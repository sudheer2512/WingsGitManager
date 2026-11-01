import { Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatSelectModule } from '@angular/material/select';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ApiService } from '../core/api.service';
import { CommitSummary, Diff, Session } from '../core/models';
import { UI } from '../shared/ui';
import { confirm } from '../shared/widgets';
import { DiffBrowser } from './commit-diff';
import { RepoContext } from './repo-context';

@Component({
  selector: 'app-sessions-tab',
  imports: [FormsModule, MatFormFieldModule, MatSelectModule, MatButtonModule, MatButtonToggleModule, MatIconModule, MatTooltipModule, DiffBrowser, ...UI],
  template: `
    <app-error [error]="error()" [dismissable]="true" (dismiss)="error.set(null)" />
    @if (msg()) { <div class="callout good">{{ msg() }}</div> }
    <div class="filters">
      <mat-form-field><mat-label>State</mat-label>
        <mat-select [(ngModel)]="state" (selectionChange)="load()">
          <mat-option value="">Any</mat-option>
          @for (s of states; track s) { <mat-option [value]="s">{{ s }}</mat-option> }
        </mat-select></mat-form-field>
    </div>
    @if (sessions().length) {
      <div class="table-wrap">
        <table class="data">
          <thead><tr><th>Session</th><th>Agent / user</th><th>State</th><th>Commits</th><th class="num">Files</th><th class="num">Commits</th><th>Push status</th><th>Tests</th><th>Recovery</th><th>Last activity</th></tr></thead>
          <tbody>
            @for (s of sessions(); track s.id) {
              <tr class="clickable" [class.selected]="sel()?.id === s.id" (click)="select(s)">
                <td><code>{{ s.id | sha }}</code>@if (s.title) {<div class="small ellipsis" style="max-width:220px">{{ s.title }}</div>}<div class="small muted"><code>{{ s.branch }}</code></div></td>
                <td><span class="tag ai">{{ s.agentId }}</span>@if (s.initiatingUser) {<div class="small muted">for {{ s.initiatingUser }}</div>}</td>
                <td><app-chip [value]="s.state" /><div class="small muted">{{ s.isolation === 'Worktree' ? 'isolated worktree' : 'shared directory' }}</div></td>
                <td class="nowrap"><code>{{ s.startingCommitSha | sha }}</code> → <code>{{ s.endingCommitSha | sha }}</code></td>
                <td class="num">{{ s.filesModified }}</td>
                <td class="num">{{ s.commitCount }}</td>
                <td><app-durability [state]="s.pushStatus" /></td>
                <td>@if (s.lastTestStatus) { <app-chip [value]="s.lastTestStatus" /> }<div class="small muted">{{ s.testsExecuted.length }} executed</div></td>
                <td>@if (s.recoveryAvailable) { <app-chip label="Available" tone="good" icon="restore" /> } @else { <span class="muted">—</span> }</td>
                <td class="nowrap">{{ s.lastActivityAt | ago }}</td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    } @else if (!loading()) { <app-empty icon="smart_toy" text="No AI sessions" hint="Wings Code starts a session (POST /ai/sessions) for each AI conversation." /> }

    @if (sel(); as s) {
      <section class="panel" style="margin-top:12px">
        <div class="panel-head">
          <h2>Session {{ s.id | sha }}</h2>@if (s.title) { <span class="muted">{{ s.title }}</span> }
          <span class="spacer"></span>
          <button mat-stroked-button (click)="ctx.tab('history', { session: s.id })"><mat-icon>history</mat-icon>Commits in history</button>
          @if (s.state === 'Active') { <button mat-stroked-button (click)="end(s)"><mat-icon>stop_circle</mat-icon>End session</button> }
          <button mat-flat-button [disabled]="!s.recoveryAvailable" (click)="revert(s)"><mat-icon>undo</mat-icon>Revert this session</button>
        </div>
        <dl class="kv small">
          <dt>Working directory</dt><dd class="mono">{{ s.workingDirectory }}</dd>
          <dt>Base branch</dt><dd><code>{{ s.baseBranch }}</code></dd>
          <dt>Started / ended</dt><dd>{{ s.createdAt | dt }} — {{ s.endedAt | dt }}</dd>
          @if (s.userRequestRef) { <dt>User request</dt><dd>{{ s.userRequestRef }}</dd> }
          <dt>Tests executed</dt><dd>@for (t of s.testsExecuted; track t) { <span class="tag">{{ t }}</span> } @empty { none }</dd>
        </dl>
        <mat-button-toggle-group [value]="view()" (change)="view.set($event.value)" hideSingleSelectionIndicator style="margin:8px 0">
          <mat-button-toggle value="commits">Commits ({{ commits().length }})</mat-button-toggle>
          <mat-button-toggle value="changes">All changes</mat-button-toggle>
        </mat-button-toggle-group>
        @if (view() === 'commits') {
          <div class="table-wrap">
            <table class="data">
              <thead><tr><th>Commit</th><th>Message</th><th>When</th><th>Durability</th></tr></thead>
              <tbody>
                @for (c of commits(); track c.sha) {
                  <tr class="clickable" (click)="ctx.tab('history', { sha: c.sha })">
                    <td><code>{{ c.sha | sha }}</code></td><td>{{ c.subject }}</td><td class="nowrap">{{ c.committedAt | ago }}</td><td><app-durability [state]="c.durability" /></td>
                  </tr>
                } @empty { <tr><td colspan="4" class="muted">No commits yet</td></tr> }
              </tbody>
            </table>
          </div>
        } @else {
          @if (changes(); as d) { <app-diff-browser [repoId]="ctx.id()" [files]="d.files" [fromRef]="d.fromRef" [toRef]="d.toRef" /> }
          @else { <app-loading [on]="true" /> }
        }
      </section>
    }`,
})
export class SessionsTab implements OnInit {
  ctx = inject(RepoContext);
  private api = inject(ApiService);
  private dialog = inject(MatDialog);
  private destroyRef = inject(DestroyRef);
  states = ['Active', 'Completed', 'Merged', 'Reverted', 'Abandoned'];
  state = '';
  sessions = signal<Session[]>([]);
  sel = signal<Session | null>(null);
  commits = signal<CommitSummary[]>([]);
  changes = signal<Diff | null>(null);
  view = signal<'commits' | 'changes'>('commits');
  loading = signal(false);
  error = signal<unknown>(null);
  msg = signal<string | null>(null);

  ngOnInit() {
    this.load();
    this.ctx.refresh$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => this.load());
  }

  load() {
    this.loading.set(true);
    this.api.sessions({ repositoryId: this.ctx.id(), state: this.state || undefined, pageSize: 100 }).subscribe({
      next: r => {
        this.sessions.set(r.items); this.loading.set(false);
        const cur = this.sel(); if (cur) { const u = r.items.find(s => s.id === cur.id); if (u) this.sel.set(u); }
      },
      error: e => { this.error.set(e); this.loading.set(false); },
    });
  }

  select(s: Session) {
    this.sel.set(s); this.commits.set([]); this.changes.set(null);
    this.api.sessionCommits(s.id).subscribe({ next: c => this.commits.set(c), error: e => this.error.set(e) });
    this.api.sessionChanges(s.id).subscribe({ next: d => this.changes.set(d), error: e => this.error.set(e) });
  }

  revert(s: Session) {
    this.ctx.recover({ type: 'RevertSession', sessionId: s.id, label: `Revert AI session ${s.id.substring(0, 8)}${s.title ? ' (' + s.title + ')' : ''}` });
  }

  end(s: Session) {
    confirm(this.dialog, { title: 'End AI session', message: `End session ${s.id.substring(0, 8)}? Its branch is kept; the worktree is released.`, confirmText: 'End session' })
      .subscribe(ok => ok && this.api.endSession(s.id, { mergeIntoBase: false, releaseWorktree: true }).subscribe({
        next: () => { this.msg.set('Session ended.'); this.load(); },
        error: e => this.error.set(e),
      }));
  }
}
