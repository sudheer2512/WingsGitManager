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
import { ApiToken, User } from '../core/models';
import { UI } from '../shared/ui';
import { SecretDialog, confirm } from '../shared/widgets';

const SCOPES = ['repos:read', 'repos:write', 'changes:write', 'recovery:execute', 'backups:manage', 'admin'];

@Component({
  selector: 'app-tokens',
  imports: [FormsModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatCheckboxModule, MatButtonModule, MatIconModule, ...UI],
  template: `
    <div class="page">
      <div class="page-head"><h1>API tokens</h1><span class="muted small">Bearer tokens (wgm_…) for Wings Code agents, automation and this dashboard</span></div>
      <app-error [error]="error()" [dismissable]="true" (dismiss)="error.set(null)" />
      <section class="panel">
        <div class="panel-head"><h2>Create token</h2></div>
        <div class="filters">
          <mat-form-field class="wide"><mat-label>Name</mat-label><input matInput [(ngModel)]="name" placeholder="e.g. wings-code-agent-prod"></mat-form-field>
          <mat-form-field class="wide"><mat-label>For user (default: you)</mat-label><mat-select [(ngModel)]="userId"><mat-option value="">Me</mat-option>
            @for (u of users(); track u.id) { <mat-option [value]="u.id">{{ u.displayName }} ({{ u.email }})</mat-option> }</mat-select></mat-form-field>
          <mat-form-field><mat-label>Expires in (days)</mat-label><input matInput type="number" min="1" [(ngModel)]="days"></mat-form-field>
        </div>
        <div class="wrap-row" style="margin-bottom:8px"><span class="small muted">Scopes:</span>
          @for (s of scopes; track s) { <mat-checkbox [checked]="sel.has(s)" (change)="toggle(s)">{{ s }}</mat-checkbox> }</div>
        <button mat-flat-button [disabled]="!name.trim() || !sel.size" (click)="create()"><mat-icon>key</mat-icon>Create token</button>
      </section>
      <section class="panel">
        @if (items().length) {
          <div class="table-wrap"><table class="data">
            <thead><tr><th>Name</th><th>Prefix</th><th>User</th><th>Scopes</th><th>Created</th><th>Expires</th><th>Last used</th><th>Status</th><th></th></tr></thead>
            <tbody>@for (t of items(); track t.id) {
              <tr [style.opacity]="t.revokedAt ? .55 : 1">
                <td><b>{{ t.name }}</b></td><td><code>{{ t.prefix }}…</code></td><td>{{ userName(t.userId) }}</td>
                <td>@for (s of t.scopes; track s) { <span class="tag">{{ s }}</span> }</td>
                <td class="nowrap">{{ t.createdAt | ago }}</td><td class="nowrap">{{ t.expiresAt ? (t.expiresAt | dt) : 'never' }}</td><td class="nowrap">{{ t.lastUsedAt | ago }}</td>
                <td>@if (t.revokedAt) { <app-chip label="Revoked" tone="off" /> } @else if (t.expiresAt && expired(t.expiresAt)) { <app-chip label="Expired" tone="warn" /> } @else { <app-chip label="Active" tone="good" icon="check" /> }</td>
                <td>@if (!t.revokedAt) { <button mat-button class="danger" (click)="revoke(t)">Revoke</button> }</td>
              </tr>
            }</tbody>
          </table></div>
        } @else { <app-empty icon="key" text="No tokens" /> }
      </section>
    </div>`,
})
export class Tokens implements OnInit {
  private api = inject(ApiService);
  private dialog = inject(MatDialog);
  scopes = SCOPES;
  items = signal<ApiToken[]>([]); users = signal<User[]>([]);
  error = signal<unknown>(null);
  name = ''; userId = ''; days: number | null = 90; sel = new Set<string>(['repos:read']);
  ngOnInit() { this.load(); this.api.users().subscribe({ next: u => this.users.set(u), error: () => undefined }); }
  load() { this.api.tokens().subscribe({ next: t => this.items.set(t), error: e => this.error.set(e) }); }
  toggle(s: string) { if (this.sel.has(s)) this.sel.delete(s); else this.sel.add(s); }
  userName(id: string) { const u = this.users().find(x => x.id === id); return u ? u.email : id.substring(0, 8); }
  expired(iso: string) { return new Date(iso).getTime() < Date.now(); }
  create() {
    this.api.createToken({ name: this.name.trim(), userId: this.userId || null, scopes: [...this.sel], expiresInDays: this.days || null }).subscribe({
      next: r => {
        this.dialog.open(SecretDialog, { width: '560px', maxWidth: '94vw', disableClose: true, data: {
          title: `Token "${r.token.name}" created`, secret: r.secret,
          message: 'Copy the token now. It is shown only once and cannot be retrieved later (only its hash is stored).' } });
        this.name = ''; this.load();
      },
      error: e => this.error.set(e),
    });
  }
  revoke(t: ApiToken) {
    confirm(this.dialog, { title: 'Revoke token', message: `Revoke "${t.name}"? Clients using it lose access immediately.`, danger: true, confirmText: 'Revoke' })
      .subscribe(ok => ok && this.api.revokeToken(t.id).subscribe({ next: () => this.load(), error: e => this.error.set(e) }));
  }
}
