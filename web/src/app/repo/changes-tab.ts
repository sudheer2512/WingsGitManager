import { Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatSelectModule } from '@angular/material/select';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ApiService } from '../core/api.service';
import { ChangeOperation, Diff, RepositoryStatus } from '../core/models';
import { UI, durationText } from '../shared/ui';
import { DiffBrowser } from './commit-diff';
import { RepoContext } from './repo-context';

@Component({
  selector: 'app-changes-tab',
  imports: [FormsModule, MatFormFieldModule, MatSelectModule, MatButtonModule, MatIconModule, MatTooltipModule, DiffBrowser, ...UI],
  template: `
    <app-error [error]="error()" />
    <section class="panel">
      <div class="panel-head"><h2>Working directory</h2><span class="spacer"></span>
        <button mat-stroked-button (click)="loadStatus()"><mat-icon>refresh</mat-icon>Refresh</button></div>
      @if (status(); as s) {
        <div class="wrap-row small" style="margin-bottom:8px">
          <span class="mono">{{ s.workingDirectory }}</span>
          @if (!s.workingDirectoryExists) { <app-chip value="Failed" label="Directory missing" icon="folder_off" /> }
          <span>branch <code>{{ s.branch ?? '—' }}</code></span>
          <span>HEAD <code>{{ s.headSha | sha }}</code> · remote <code>{{ s.remoteHeadSha | sha }}</code></span>
          <app-chip [value]="s.remoteStatus" /> <span class="muted">ahead {{ s.ahead }} · behind {{ s.behind }}</span>
          @if (s.activeOperationId) { <app-chip value="Active" label="AI change in progress" icon="smart_toy" /> }
          @if (s.isClean) { <app-chip label="Clean — nothing uncommitted" tone="good" icon="check" /> }
          @else { <app-chip label="{{ s.changes.length }} uncommitted change(s) — not yet safe" tone="warn" icon="edit_note" /> }
        </div>
        @if (!s.isClean) {
          @if (wt(); as d) { <app-diff-browser [repoId]="ctx.id()" [files]="d.files" [toRef]="null" [short]="true" /> }
          <div class="table-wrap" style="margin-top:8px">
            <table class="data compact">
              <thead><tr><th>Path</th><th>Change</th><th>Staged</th><th>Untracked</th></tr></thead>
              <tbody>@for (c of s.changes; track c.path) {
                <tr><td class="mono">{{ c.oldPath ? c.oldPath + ' → ' : '' }}{{ c.path }}</td><td><app-chip [value]="c.kind" /></td><td>{{ c.staged ? 'yes' : '' }}</td><td>{{ c.untracked ? 'yes' : '' }}</td></tr>
              }</tbody>
            </table>
          </div>
        }
      } @else { <app-loading [on]="true" /> }
    </section>

    <section class="panel">
      <div class="panel-head"><h2>Change operations</h2><span class="muted small">begin/complete calls from AI agents</span><span class="spacer"></span>
        <mat-form-field style="width:180px">
          <mat-label>State</mat-label>
          <mat-select [(ngModel)]="state" (selectionChange)="loadOps()">
            <mat-option value="">Any</mat-option>
            @for (s of states; track s) { <mat-option [value]="s">{{ s }}</mat-option> }
          </mat-select>
        </mat-form-field>
      </div>
      @if (ops().length) {
        <div class="table-wrap">
          <table class="data">
            <thead><tr><th>Started</th><th>Description</th><th>Agent</th><th>State</th><th>Risk</th><th>Commits</th><th>Duration</th><th></th></tr></thead>
            <tbody>
              @for (o of ops(); track o.id) {
                <tr class="clickable" (click)="open.set(open() === o.id ? null : o.id)">
                  <td class="nowrap" [title]="o.createdAt | dt">{{ o.createdAt | ago }}</td>
                  <td style="max-width:360px"><div class="ellipsis">{{ o.description }}</div>@if (o.userRequestRef) {<div class="small muted ellipsis">{{ o.userRequestRef }}</div>}</td>
                  <td>{{ o.agentId }}@if (o.initiatingUser) {<div class="small muted">for {{ o.initiatingUser }}</div>}@if (o.sessionId) {<div><span class="tag ai">session {{ o.sessionId | sha }}</span></div>}</td>
                  <td><app-chip [value]="o.state" />@if (o.overrideApplied) { <span class="tag" matTooltip="Policy override applied by an administrator">override</span> }</td>
                  <td><app-chip [value]="o.riskLevel" [tip]="o.riskReasons.join('; ')" />@if (o.riskReasons.length) {<div class="small muted ellipsis" style="max-width:220px">{{ o.riskReasons[0] }}</div>}</td>
                  <td class="nowrap"><code>{{ o.startCommitSha | sha }}</code> → <code>{{ o.resultCommitSha | sha }}</code></td>
                  <td class="nowrap">{{ dur(o.createdAt, o.completedAt) }}</td>
                  <td>@if (o.resultCommitSha) { <button mat-button (click)="$event.stopPropagation(); ctx.tab('history', { sha: o.resultCommitSha })">Commit</button> }</td>
                </tr>
                @if (open() === o.id) {
                  <tr><td colspan="8">
                    <dl class="kv small">
                      <dt>Operation id</dt><dd><code>{{ o.id }}</code></dd>
                      <dt>Branch / directory</dt><dd><code>{{ o.branch }}</code> · <span class="mono">{{ o.workingDirectory }}</span></dd>
                      <dt>Risk reasons</dt><dd>@for (r of o.riskReasons; track r) { <div>• {{ r }}</div> } @empty { none }</dd>
                      <dt>Lease expires</dt><dd>{{ o.leaseExpiresAt | dt }}</dd>
                      <dt>Completed</dt><dd>{{ o.completedAt | dt }}</dd>
                      @if (o.failureReason) { <dt>Failure</dt><dd class="error">{{ o.failureReason }}</dd> }
                    </dl>
                  </td></tr>
                }
              }
            </tbody>
          </table>
        </div>
      } @else { <app-empty icon="pending_actions" text="No change operations" hint="AI agents call changes/begin and changes/complete around every edit." /> }
    </section>`,
})
export class ChangesTab implements OnInit {
  ctx = inject(RepoContext);
  private api = inject(ApiService);
  private destroyRef = inject(DestroyRef);
  states = ['Active', 'Committing', 'Completed', 'Aborted', 'Interrupted', 'PolicyBlocked', 'Failed'];
  state = '';
  status = signal<RepositoryStatus | null>(null);
  wt = signal<Diff | null>(null);
  ops = signal<ChangeOperation[]>([]);
  open = signal<string | null>(null);
  error = signal<unknown>(null);
  dur = durationText;

  ngOnInit() {
    this.loadStatus(); this.loadOps();
    this.ctx.refresh$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => { this.loadStatus(); this.loadOps(); });
  }

  loadStatus() {
    this.api.status(this.ctx.id()).subscribe({
      next: s => {
        this.status.set(s);
        if (!s.isClean) this.api.workingTreeDiff(this.ctx.id()).subscribe({ next: d => this.wt.set(d), error: e => this.error.set(e) });
        else this.wt.set(null);
      },
      error: e => this.error.set(e),
    });
  }

  loadOps() {
    this.api.changes(this.ctx.id(), { state: this.state || undefined, pageSize: 100 }).subscribe({ next: r => this.ops.set(r.items), error: e => this.error.set(e) });
  }
}
