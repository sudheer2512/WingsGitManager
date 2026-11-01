import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { Company, Membership, User, WORKSPACE_ROLES, WorkspaceRole } from '../core/models';
import { UI } from '../shared/ui';
import { confirm } from '../shared/widgets';

@Component({
  selector: 'app-users',
  imports: [FormsModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatCheckboxModule, MatButtonModule, MatIconModule, ...UI],
  template: `
    <div class="page">
      <div class="page-head"><h1>Users &amp; memberships</h1></div>
      <app-error [error]="error()" [dismissable]="true" (dismiss)="error.set(null)" />
      @if (msg()) { <div class="callout good">{{ msg() }}</div> }
      <section class="panel">
        <div class="panel-head"><h2>New user</h2></div>
        <div class="filters">
          <mat-form-field class="wide"><mat-label>Email</mat-label><input matInput type="email" [(ngModel)]="n.email"></mat-form-field>
          <mat-form-field class="wide"><mat-label>Display name</mat-label><input matInput [(ngModel)]="n.displayName"></mat-form-field>
          <mat-form-field><mat-label>Company</mat-label><mat-select [(ngModel)]="n.companyId"><mat-option value="">None</mat-option>
            @for (c of companies(); track c.id) { <mat-option [value]="c.id">{{ c.name }}</mat-option> }</mat-select></mat-form-field>
          <mat-form-field><mat-label>Role</mat-label><mat-select [(ngModel)]="n.role">@for (r of roles; track r) { <mat-option [value]="r">{{ r | human }}</mat-option> }</mat-select></mat-form-field>
          <mat-checkbox [(ngModel)]="n.isServiceAccount">Service account (AI agent)</mat-checkbox>
          @if (auth.isAdmin()) { <mat-checkbox [(ngModel)]="n.isSystemAdministrator">System administrator</mat-checkbox> }
          <button mat-flat-button [disabled]="!n.email.trim() || !n.displayName.trim()" (click)="create()"><mat-icon>person_add</mat-icon>Create</button>
        </div>
      </section>
      <section class="panel">
        @if (users().length) {
          <div class="table-wrap"><table class="data">
            <thead><tr><th>User</th><th>Type</th><th>Status</th><th>Memberships</th><th>Add membership</th></tr></thead>
            <tbody>@for (u of users(); track u.id) {
              <tr>
                <td><b>{{ u.displayName }}</b><div class="small muted">{{ u.email }}</div></td>
                <td>@if (u.isSystemAdministrator) { <span class="tag">system admin</span> } @if (u.isServiceAccount) { <span class="tag ai">service account</span> } @if (!u.isSystemAdministrator && !u.isServiceAccount) { user }</td>
                <td>@if (u.isActive) { <app-chip label="Active" tone="good" icon="check" /> } @else { <app-chip label="Disabled" tone="off" /> }</td>
                <td>@for (m of u.memberships; track m.companyId) {
                  <div class="row small"><span>{{ m.companyName }}</span>
                    <select class="native" [value]="m.role" (change)="setRole(u, m.companyId, $any($event.target).value)">@for (r of roles; track r) { <option [value]="r">{{ r | human }}</option> }</select>
                    <a href="" (click)="$event.preventDefault(); remove(u, m)">remove</a></div>
                } @empty { <span class="muted small">none</span> }</td>
                <td><div class="row small">
                  <select class="native" #c><option value="">company…</option>@for (co of companies(); track co.id) { <option [value]="co.id">{{ co.name }}</option> }</select>
                  <select class="native" #r>@for (ro of roles; track ro) { <option [value]="ro" [selected]="ro === 'Developer'">{{ ro | human }}</option> }</select>
                  <button mat-button [disabled]="!c.value" (click)="setRole(u, c.value, $any(r.value))">Add</button></div></td>
              </tr>
            }</tbody>
          </table></div>
        } @else { <app-empty icon="group" text="No users" /> }
      </section>
    </div>`,
  styles: [`select.native { font: inherit; padding: 2px 4px; border: 1px solid var(--border); border-radius: 4px; background: var(--surface); color: var(--text); }`],
})
export class Users implements OnInit {
  private api = inject(ApiService);
  private dialog = inject(MatDialog);
  auth = inject(AuthService);
  roles = WORKSPACE_ROLES;
  users = signal<User[]>([]); companies = signal<Company[]>([]);
  error = signal<unknown>(null); msg = signal<string | null>(null);
  n = { email: '', displayName: '', companyId: '', role: 'Developer' as WorkspaceRole, isServiceAccount: false, isSystemAdministrator: false };
  ngOnInit() { this.load(); this.api.companies().subscribe({ next: c => this.companies.set(c), error: () => undefined }); }
  load() { this.api.users().subscribe({ next: u => this.users.set(u), error: e => this.error.set(e) }); }
  create() {
    const n = this.n;
    this.api.createUser({ email: n.email.trim(), displayName: n.displayName.trim(), isServiceAccount: n.isServiceAccount, isSystemAdministrator: n.isSystemAdministrator,
      companyId: n.companyId || null, role: n.companyId ? n.role : null }).subscribe({
      next: u => { this.msg.set(`User ${u.email} created. Create an API token for them under API tokens.`); this.n = { ...this.n, email: '', displayName: '' }; this.load(); },
      error: e => this.error.set(e),
    });
  }
  setRole(u: User, companyId: string, role: WorkspaceRole) {
    if (!companyId) return;
    this.api.setMembership(u.id, companyId, role).subscribe({ next: () => { this.msg.set(`Membership updated for ${u.email}.`); this.load(); }, error: e => this.error.set(e) });
  }
  remove(u: User, m: Membership) {
    confirm(this.dialog, { title: 'Remove membership', message: `Remove ${u.email} from ${m.companyName}?`, danger: true, confirmText: 'Remove' })
      .subscribe(ok => ok && this.api.removeMembership(u.id, m.companyId).subscribe({ next: () => this.load(), error: e => this.error.set(e) }));
  }
}
