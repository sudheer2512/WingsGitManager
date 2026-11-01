import { Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ApiService } from '../core/api.service';
import { Branch, Diff, MergeRequest, MergeResult, Worktree } from '../core/models';
import { UI } from '../shared/ui';
import { confirm } from '../shared/widgets';
import { DiffBrowser } from './commit-diff';
import { RepoContext } from './repo-context';

@Component({
  selector: 'app-branches-tab',
  imports: [FormsModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatButtonModule, MatCheckboxModule, MatIconModule, MatTooltipModule, DiffBrowser, ...UI],
  template: `
    <app-error [error]="error()" [dismissable]="true" (dismiss)="error.set(null)" />
    @if (msg()) { <div class="callout good">{{ msg() }}</div> }
    <div class="grid-2">
      <section class="panel">
        <div class="panel-head"><h2>Branches</h2><span class="spacer"></span><button mat-icon-button (click)="load()" aria-label="Refresh"><mat-icon>refresh</mat-icon></button></div>
        <div class="table-wrap">
          <table class="data">
            <thead><tr><th>Name</th><th>Head</th><th class="num">Ahead</th><th class="num">Behind</th><th>Flags</th><th>Last commit</th></tr></thead>
            <tbody>
              @for (b of branches(); track b.name) {
                <tr>
                  <td><code>{{ b.name }}</code></td>
                  <td><code>{{ b.headSha | sha }}</code>@if (b.remoteHeadSha && b.remoteHeadSha !== b.headSha) { <div class="small muted">remote <code>{{ b.remoteHeadSha | sha }}</code></div> }</td>
                  <td class="num" [class.error]="b.ahead > 0">{{ b.ahead }}</td>
                  <td class="num">{{ b.behind }}</td>
                  <td>
                    @if (b.isDefault) { <span class="tag">default</span> }
                    @if (b.isProtected) { <span class="tag"><span class="material-icons" style="font-size:11px;vertical-align:-1px">lock</span> protected</span> }
                    @if (b.isCheckedOut) { <span class="tag">checked out</span> }
                    @if (b.sessionId) { <span class="tag ai">AI session</span> }
                    @if (!b.remoteHeadSha) { <span class="tag" matTooltip="Not on the central server">local only</span> }
                  </td>
                  <td class="nowrap">{{ b.lastCommitAt | ago }}</td>
                </tr>
              } @empty { <tr><td colspan="6" class="muted">No branches</td></tr> }
            </tbody>
          </table>
        </div>
      </section>

      <section class="panel">
        <div class="panel-head"><h2>Worktrees</h2><span class="small muted">isolated directories for concurrent AI sessions</span><span class="spacer"></span>
          <mat-checkbox [(ngModel)]="includeReleased" (change)="loadWorktrees()">Show released</mat-checkbox></div>
        @if (worktrees().length) {
          <div class="table-wrap">
            <table class="data">
              <thead><tr><th>Branch</th><th>Owner</th><th>State</th><th>Head</th><th>Created</th><th></th></tr></thead>
              <tbody>
                @for (w of worktrees(); track w.id) {
                  <tr>
                    <td><code>{{ w.branch }}</code><div class="small muted mono ellipsis" style="max-width:260px" [title]="w.path">{{ w.path }}</div></td>
                    <td>{{ w.ownerAgentId ?? '—' }}</td>
                    <td><app-chip [value]="w.state" />@if (w.hasUncommittedChanges) { <app-chip label="uncommitted" tone="warn" icon="edit_note" /> }</td>
                    <td><code>{{ w.headSha | sha }}</code></td>
                    <td class="nowrap">{{ w.createdAt | ago }}</td>
                    <td>@if (w.state === 'Active') { <button mat-button (click)="release(w)">Release</button> }</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        } @else { <app-empty icon="account_tree" text="No active worktrees" /> }
      </section>
    </div>

    <section class="panel">
      <div class="panel-head"><h2>Compare &amp; merge</h2></div>
      <div class="filters">
        <mat-form-field><mat-label>Base (target)</mat-label>
          <mat-select [(ngModel)]="base">@for (b of branches(); track b.name) { <mat-option [value]="b.name">{{ b.name }}</mat-option> }</mat-select></mat-form-field>
        <mat-icon>arrow_back</mat-icon>
        <mat-form-field><mat-label>Head (source)</mat-label>
          <mat-select [(ngModel)]="head">@for (b of branches(); track b.name) { <mat-option [value]="b.name">{{ b.name }}</mat-option> }</mat-select></mat-form-field>
        <button mat-stroked-button (click)="compare()" [disabled]="!base || !head || base === head"><mat-icon>difference</mat-icon>Compare</button>
        <button mat-stroked-button (click)="merge(true)" [disabled]="!base || !head || base === head"><mat-icon>preview</mat-icon>Merge dry run</button>
        <button mat-flat-button (click)="merge(false)" [disabled]="!dry() || dry()!.conflicts.length > 0"><mat-icon>merge</mat-icon>Merge</button>
      </div>
      <app-loading [on]="busy()" />
      @if (dry(); as m) {
        <div class="callout" [class.bad]="m.conflicts.length" [class.good]="!m.conflicts.length">
          <b>{{ m.dryRun ? 'Dry run' : 'Merge' }}:</b>
          @if (m.dryRun) {
            @if (m.conflicts.length) { <app-chip value="Conflict" label="Conflicts" /> } @else { <app-chip label="Mergeable — no conflicts" tone="good" icon="check" /> }
          } @else { <app-chip [value]="m.state" /> }
          {{ m.files.length }} file(s) differ between the branches.
          @if (m.approvalRequired) { <b>Approval is required</b> before the merge is applied. }
          @if (m.resultCommitSha) { Result <code>{{ m.resultCommitSha | sha }}</code> <app-durability [state]="m.durability" /> }
          @if (m.conflicts.length) { <div>Conflicts (merge is blocked):</div><ul>@for (c of m.conflicts; track c) { <li class="mono">{{ c }}</li> }</ul> }
        </div>
      }
      @if (diff(); as d) {
        <div class="small muted" style="margin-bottom:6px">{{ d.files.length }} files · +{{ d.totalAdditions }} −{{ d.totalDeletions }} @if (d.truncated) { · truncated }</div>
        <app-diff-browser [repoId]="ctx.id()" [files]="d.files" [fromRef]="cmpBase()" [toRef]="cmpHead()" />
      }
    </section>

    <section class="panel">
      <div class="panel-head"><h2>Merge requests</h2></div>
      @if (mrs().length) {
        <div class="table-wrap">
          <table class="data">
            <thead><tr><th>Created</th><th>Source → target</th><th>State</th><th>Requested by</th><th>Conflicts</th><th>Result</th><th></th></tr></thead>
            <tbody>
              @for (m of mrs(); track m.id) {
                <tr>
                  <td class="nowrap">{{ m.createdAt | ago }}</td>
                  <td><code>{{ m.sourceBranch }}</code> → <code>{{ m.targetBranch }}</code>@if (m.message) {<div class="small muted">{{ m.message }}</div>}</td>
                  <td><app-chip [value]="m.state" /></td>
                  <td>{{ m.requestedBy ?? '—' }}@if (m.approvedBy) {<div class="small muted">decided by {{ m.approvedBy }}</div>}</td>
                  <td>{{ m.conflicts.length || '—' }}</td>
                  <td><code>{{ m.resultSha | sha }}</code></td>
                  <td class="nowrap">@if (m.state === 'AwaitingApproval') {
                    <button mat-flat-button (click)="decide(m, true)">Approve</button>
                    <button mat-button class="danger" (click)="decide(m, false)">Reject</button> }</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      } @else { <app-empty icon="merge_type" text="No merge requests" hint="Merges into protected branches can require approval (repository settings)." /> }
    </section>`,
})
export class BranchesTab implements OnInit {
  ctx = inject(RepoContext);
  private api = inject(ApiService);
  private dialog = inject(MatDialog);
  private destroyRef = inject(DestroyRef);
  branches = signal<Branch[]>([]);
  worktrees = signal<Worktree[]>([]);
  mrs = signal<MergeRequest[]>([]);
  diff = signal<Diff | null>(null);
  dry = signal<MergeResult | null>(null);
  cmpBase = signal<string | null>(null);
  cmpHead = signal<string | null>(null);
  busy = signal(false);
  error = signal<unknown>(null);
  msg = signal<string | null>(null);
  includeReleased = false;
  base = ''; head = '';

  ngOnInit() {
    this.load();
    this.ctx.refresh$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => this.load());
  }

  load() {
    this.api.branches(this.ctx.id()).subscribe({
      next: b => {
        this.branches.set(b);
        if (!this.base) this.base = b.find(x => x.isDefault)?.name ?? b[0]?.name ?? '';
        if (!this.head) this.head = b.find(x => !x.isDefault)?.name ?? '';
      },
      error: e => this.error.set(e),
    });
    this.loadWorktrees();
    this.api.mergeRequests(this.ctx.id()).subscribe({ next: m => this.mrs.set(m), error: e => this.error.set(e) });
  }

  loadWorktrees() { this.api.worktrees(this.ctx.id(), this.includeReleased).subscribe({ next: w => this.worktrees.set(w), error: e => this.error.set(e) }); }

  compare() {
    this.busy.set(true); this.dry.set(null);
    this.api.compare(this.ctx.id(), this.base, this.head).subscribe({
      next: d => { this.cmpBase.set(this.base); this.cmpHead.set(this.head); this.diff.set(d); this.busy.set(false); },
      error: e => { this.error.set(e); this.busy.set(false); },
    });
  }

  merge(dryRun: boolean) {
    const run = () => {
      this.busy.set(true);
      this.api.merge(this.ctx.id(), { sourceBranch: this.head, targetBranch: this.base, dryRun }).subscribe({
        next: m => { this.dry.set(m); this.busy.set(false); if (!dryRun) { this.msg.set(`Merge ${m.state}.`); this.load(); } },
        error: e => { this.error.set(e); this.busy.set(false); },
      });
    };
    if (dryRun) { run(); return; }
    confirm(this.dialog, { title: 'Merge branches', message: `Merge ${this.head} into ${this.base}? A merge commit is created and pushed.`, confirmText: 'Merge' })
      .subscribe(ok => ok && run());
  }

  decide(m: MergeRequest, approve: boolean) {
    confirm(this.dialog, { title: approve ? 'Approve merge' : 'Reject merge', message: `${approve ? 'Approve and apply' : 'Reject'} merging ${m.sourceBranch} into ${m.targetBranch}?`, danger: !approve, confirmText: approve ? 'Approve' : 'Reject' })
      .subscribe(ok => ok && this.api.approveMerge(this.ctx.id(), m.id, approve).subscribe({
        next: r => { this.msg.set(`Merge request ${r.state}.`); this.load(); },
        error: e => this.error.set(e),
      }));
  }

  release(w: Worktree) {
    confirm(this.dialog, {
      title: 'Release worktree', message: `Release the worktree for ${w.branch}? The directory is removed; uncommitted changes are saved to a recovery branch first.`,
      confirmText: 'Release',
    }).subscribe(ok => ok && this.api.releaseWorktree(this.ctx.id(), w.id, true).subscribe({
      next: () => { this.msg.set('Worktree released.'); this.loadWorktrees(); },
      error: e => this.error.set(e),
    }));
  }
}
