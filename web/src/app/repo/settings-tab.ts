import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { ApiService } from '../core/api.service';
import { RepositoryPermission, RepositorySettings, User, WORKSPACE_ROLES, WorkspaceRole } from '../core/models';
import { UI } from '../shared/ui';
import { confirm } from '../shared/widgets';
import { RepoContext } from './repo-context';

@Component({
  selector: 'app-settings-tab',
  imports: [FormsModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatSlideToggleModule, MatButtonModule, MatIconModule, ...UI],
  template: `
    <app-error [error]="error()" [dismissable]="true" (dismiss)="error.set(null)" />
    @if (msg()) { <div class="callout good">{{ msg() }}</div> }
    @if (s) {
      <section class="panel">
        <div class="panel-head"><h2>Repository settings</h2></div>
        <div class="grid-2">
          <div>
            <mat-form-field class="full"><mat-label>Description</mat-label><textarea matInput rows="2" [(ngModel)]="description"></textarea></mat-form-field>
            <mat-form-field class="full"><mat-label>Default branch</mat-label><input matInput [(ngModel)]="defaultBranch"></mat-form-field>
            <h3 class="sub">Synchronisation</h3>
            <mat-slide-toggle [(ngModel)]="s.autoPush">Push every commit automatically</mat-slide-toggle>
            <mat-slide-toggle [(ngModel)]="s.requireRemoteCheckpointBeforeChanges">Block AI changes until the pre-change checkpoint is verified on the remote</mat-slide-toggle>
            <mat-slide-toggle [(ngModel)]="s.commitUncommittedChangesOnBegin">Checkpoint uncommitted changes before an AI change starts</mat-slide-toggle>
            <mat-slide-toggle [(ngModel)]="s.watchFileSystem">Watch the working directory for external changes</mat-slide-toggle>
            <mat-slide-toggle [(ngModel)]="s.autoCommitExternalChanges">Auto-commit external changes</mat-slide-toggle>
          </div>
          <div>
            <h3 class="sub">Safety</h3>
            <mat-form-field class="full"><mat-label>Secret scanning</mat-label>
              <mat-select [(ngModel)]="s.secretScanMode">
                <mat-option value="Block">Block commits containing secrets</mat-option><mat-option value="Warn">Warn only</mat-option><mat-option value="Off">Off</mat-option>
              </mat-select></mat-form-field>
            <mat-form-field class="full"><mat-label>Protected branches (one pattern per line)</mat-label><textarea matInput rows="3" [(ngModel)]="protectedBranches"></textarea></mat-form-field>
            <mat-slide-toggle [(ngModel)]="s.requireMergeApproval">Require approval for merges into protected branches</mat-slide-toggle>
            <mat-form-field class="full" style="margin-top:8px"><mat-label>Excluded path patterns (one per line)</mat-label><textarea matInput rows="3" [(ngModel)]="excluded"></textarea></mat-form-field>
            <h3 class="sub">Retention &amp; backups</h3>
            <div class="row">
              <mat-form-field><mat-label>Worktree retention (days)</mat-label><input matInput type="number" min="1" [(ngModel)]="s.worktreeRetentionDays"></mat-form-field>
              <mat-form-field><mat-label>Backup every (hours)</mat-label><input matInput type="number" min="1" [(ngModel)]="s.backupIntervalHours"></mat-form-field>
              <mat-form-field><mat-label>Keep backups</mat-label><input matInput type="number" min="1" [(ngModel)]="s.backupRetentionCount"></mat-form-field>
            </div>
          </div>
        </div>
        <div class="row" style="margin-top:8px">
          <button mat-flat-button (click)="save()" [disabled]="saving()"><mat-icon>save</mat-icon>Save settings</button>
          <button mat-button (click)="reset()">Discard changes</button>
        </div>
      </section>
    }

    <section class="panel">
      <div class="panel-head"><h2>Permissions</h2><span class="small muted">Repository-level roles in addition to company memberships</span></div>
      @if (perms().length) {
        <div class="table-wrap">
          <table class="data">
            <thead><tr><th>User</th><th>Role</th><th></th></tr></thead>
            <tbody>
              @for (p of perms(); track p.id) {
                <tr>
                  <td>{{ p.userEmail }}</td>
                  <td><mat-select [value]="p.role" (selectionChange)="setPerm(p.userId, $event.value)" style="width:220px">
                    @for (r of roles; track r) { <mat-option [value]="r">{{ r | human }}</mat-option> }</mat-select></td>
                  <td><button mat-button class="danger" (click)="removePerm(p)">Remove</button></td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      } @else { <div class="muted small" style="padding:8px 0">No repository-specific permissions.</div> }
      <div class="row" style="margin-top:8px;flex-wrap:wrap">
        <mat-form-field style="width:300px"><mat-label>User</mat-label>
          <mat-select [(ngModel)]="newUser">@for (u of users(); track u.id) { <mat-option [value]="u.id">{{ u.displayName }} ({{ u.email }})</mat-option> }</mat-select></mat-form-field>
        <mat-form-field style="width:220px"><mat-label>Role</mat-label>
          <mat-select [(ngModel)]="newRole">@for (r of roles; track r) { <mat-option [value]="r">{{ r | human }}</mat-option> }</mat-select></mat-form-field>
        <button mat-stroked-button [disabled]="!newUser" (click)="setPerm(newUser, newRole)"><mat-icon>person_add</mat-icon>Grant</button>
      </div>
    </section>

    @if (ctx.repo(); as r) {
      <section class="panel">
        <div class="panel-head"><h2>Details</h2></div>
        <dl class="kv small">
          <dt>Repository id</dt><dd><code>{{ r.summary.id }}</code></dd>
          <dt>Provider</dt><dd>{{ r.provider }}</dd>
          <dt>Remote URL</dt><dd class="mono">{{ r.remoteUrl }}</dd>
          <dt>Main working directory</dt><dd class="mono">{{ r.mainWorkingDirectory ?? '—' }}</dd>
          <dt>Wings Code project</dt><dd>{{ r.summary.projectExternalId ?? '—' }}</dd>
          <dt>Created</dt><dd>{{ r.summary.createdAt | dt }}</dd>
        </dl>
      </section>
      <section class="panel danger-zone">
        <div class="panel-head"><h2>Archive repository</h2></div>
        <p class="small">Archiving stops automation for this repository and archives the remote. History is kept and the repository can still be recovered.</p>
        <button mat-stroked-button class="danger" [disabled]="r.summary.state === 'Archived'" (click)="archive()"><mat-icon>archive</mat-icon>Archive…</button>
      </section>
    }`,
  styles: [`
    mat-slide-toggle { display: block; margin: 6px 0; }
    .sub { margin: 10px 0 6px; }
    .danger-zone { border-color: var(--bad); }`],
})
export class SettingsTab implements OnInit {
  ctx = inject(RepoContext);
  private api = inject(ApiService);
  private dialog = inject(MatDialog);
  roles = WORKSPACE_ROLES;
  s: RepositorySettings | null = null;
  description = ''; defaultBranch = ''; protectedBranches = ''; excluded = '';
  perms = signal<RepositoryPermission[]>([]);
  users = signal<User[]>([]);
  newUser = ''; newRole: WorkspaceRole = 'Developer';
  saving = signal(false);
  error = signal<unknown>(null);
  msg = signal<string | null>(null);

  ngOnInit() {
    this.reset();
    this.loadPerms();
    this.api.users(this.ctx.repo()?.summary.companyId).subscribe({ next: u => this.users.set(u), error: () => undefined });
  }

  reset() {
    const r = this.ctx.repo(); if (!r) return;
    this.s = structuredClone(r.settings);
    this.description = r.description ?? '';
    this.defaultBranch = r.summary.defaultBranch;
    this.protectedBranches = r.settings.protectedBranches.join('\n');
    this.excluded = r.settings.excludedPathPatterns.join('\n');
  }

  private lines(v: string) { return v.split('\n').map(x => x.trim()).filter(Boolean); }

  save() {
    if (!this.s) return;
    this.saving.set(true); this.msg.set(null); this.error.set(null);
    const settings: RepositorySettings = { ...this.s, protectedBranches: this.lines(this.protectedBranches), excludedPathPatterns: this.lines(this.excluded) };
    this.api.updateRepository(this.ctx.id(), { description: this.description, defaultBranch: this.defaultBranch.trim() || null, settings }).subscribe({
      next: r => { this.ctx.repo.set(r); this.reset(); this.saving.set(false); this.msg.set('Settings saved.'); },
      error: e => { this.error.set(e); this.saving.set(false); },
    });
  }

  loadPerms() { this.api.permissions(this.ctx.id()).subscribe({ next: p => this.perms.set(p), error: e => this.error.set(e) }); }

  setPerm(userId: string, role: WorkspaceRole) {
    this.api.setPermission(this.ctx.id(), userId, role).subscribe({ next: () => { this.msg.set('Permission saved.'); this.newUser = ''; this.loadPerms(); }, error: e => this.error.set(e) });
  }

  removePerm(p: RepositoryPermission) {
    confirm(this.dialog, { title: 'Remove permission', message: `Remove ${p.userEmail}'s repository role?`, danger: true, confirmText: 'Remove' })
      .subscribe(ok => ok && this.api.removePermission(this.ctx.id(), p.userId).subscribe({ next: () => this.loadPerms(), error: e => this.error.set(e) }));
  }

  archive() {
    const name = this.ctx.repo()?.summary.name ?? '';
    confirm(this.dialog, { title: 'Archive repository', message: `Archive "${name}"? Automation stops and the remote is archived.`, typeToConfirm: name, danger: true, confirmText: 'Archive' })
      .subscribe(ok => ok && this.api.archiveRepository(this.ctx.id()).subscribe({ next: () => { this.msg.set('Repository archived.'); this.ctx.reload(); }, error: e => this.error.set(e) }));
  }
}
