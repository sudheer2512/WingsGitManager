import { Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ActivatedRoute, Router } from '@angular/router';
import { ApiService } from '../core/api.service';
import { CommitDetail, CommitSummary } from '../core/models';
import { UI } from '../shared/ui';
import { DiffBrowser } from './commit-diff';
import { RepoContext } from './repo-context';

const EMPTY_TREE = '4b825dc642cb6eb9a060e54bf8d69288fbee4904';

@Component({
  selector: 'app-history-tab',
  imports: [FormsModule, MatFormFieldModule, MatInputModule, MatButtonModule, MatIconModule, MatPaginatorModule, MatTooltipModule, DiffBrowser, ...UI],
  template: `
    @if (detail(); as d) {
      <div class="panel">
        <div class="panel-head">
          <button mat-button (click)="close()"><mat-icon>arrow_back</mat-icon>Back to history</button>
          <span class="spacer"></span>
          <button mat-stroked-button (click)="browseAt(d.commit.sha)"><mat-icon>folder_open</mat-icon>Browse files at this commit</button>
          <button mat-stroked-button (click)="restoreTo(d.commit)"><mat-icon>restore</mat-icon>Restore project to here</button>
          <button mat-stroked-button (click)="revert(d.commit)"><mat-icon>undo</mat-icon>Undo this commit</button>
        </div>
        <h2 style="margin:4px 0">{{ d.commit.subject }}</h2>
        <div class="wrap-row small" style="margin-bottom:8px">
          <code>{{ d.commit.sha }}</code>
          <span>by <b>{{ d.commit.authorName }}</b> &lt;{{ d.commit.authorEmail }}&gt;</span>
          <span [title]="d.commit.committedAt | dt">{{ d.commit.committedAt | ago }}</span>
          @if (d.commit.agentId) { <span class="tag ai">AI agent · {{ d.commit.agentId }}</span> }
          @if (d.commit.sessionId) { <span class="tag ai">session {{ d.commit.sessionId | sha }}</span> }
          @if (d.commit.isAutomated) { <span class="tag">automated</span> }
          @if (d.commit.initiatingUser) { <span class="tag">for {{ d.commit.initiatingUser }}</span> }
          <app-durability [state]="d.commit.durability" />
          @if (d.buildStatus) { <app-chip [value]="d.buildStatus" [label]="'Build: ' + d.buildStatus" /> }
          @if (d.testStatus) { <app-chip [value]="d.testStatus" [label]="'Tests: ' + d.testStatus" /> }
          @if (d.commit.parents.length) { <span class="muted">parent{{ d.commit.parents.length > 1 ? 's' : '' }}:
            @for (p of d.commit.parents; track p) { <a href="" (click)="$event.preventDefault(); openSha(p)"><code>{{ p | sha }}</code></a> } </span> }
        </div>
        @if (d.message.trim() !== d.commit.subject.trim()) { <pre class="pre small msg">{{ d.message }}</pre> }
        @if (d.userRequestRef) { <div class="small muted">User request: {{ d.userRequestRef }}</div> }
        <app-diff-browser [repoId]="ctx.id()" [files]="d.files" [fromRef]="d.commit.parents[0] ?? emptyTree" [toRef]="d.commit.sha" />
      </div>
    } @else {
      <div class="filters">
        <mat-form-field><mat-label>Branch / ref</mat-label><input matInput [(ngModel)]="f.ref" (keyup.enter)="load(1)" [placeholder]="ctx.repo()?.summary?.defaultBranch ?? 'main'"></mat-form-field>
        <mat-form-field><mat-label>Path</mat-label><input matInput [(ngModel)]="f.path" (keyup.enter)="load(1)" placeholder="src/…"></mat-form-field>
        <mat-form-field><mat-label>Author</mat-label><input matInput [(ngModel)]="f.author" (keyup.enter)="load(1)"></mat-form-field>
        <mat-form-field><mat-label>AI session id</mat-label><input matInput [(ngModel)]="f.sessionId" (keyup.enter)="load(1)"></mat-form-field>
        <mat-form-field><mat-label>Since</mat-label><input matInput type="date" [(ngModel)]="f.since" (change)="load(1)"></mat-form-field>
        <mat-form-field><mat-label>Until</mat-label><input matInput type="date" [(ngModel)]="f.until" (change)="load(1)"></mat-form-field>
        <button mat-stroked-button (click)="load(1)"><mat-icon>filter_list</mat-icon>Apply</button>
      </div>
      <app-loading [on]="loading()" />
      <app-error [error]="error()" />
      @if (items().length) {
        <div class="table-wrap">
          <table class="data">
            <thead><tr><th>Commit</th><th>Message</th><th>Author</th><th>When</th><th>Durability</th></tr></thead>
            <tbody>
              @for (c of items(); track c.sha) {
                <tr class="clickable" (click)="openSha(c.sha)">
                  <td><code>{{ c.sha | sha }}</code></td>
                  <td style="max-width:520px">
                    <div class="ellipsis">{{ c.subject }}</div>
                    <div>
                      @if (c.agentId) { <span class="tag ai" matTooltip="Committed by an AI agent">AI · {{ c.agentId }}</span> }
                      @if (c.sessionId) { <span class="tag ai">session {{ c.sessionId | sha }}</span> }
                      @if (c.isAutomated) { <span class="tag">automated</span> }
                      @if (c.parents.length > 1) { <span class="tag">merge</span> }
                    </div>
                  </td>
                  <td>{{ c.authorName }}@if (c.initiatingUser) { <div class="small muted">for {{ c.initiatingUser }}</div> }</td>
                  <td class="nowrap" [title]="c.committedAt | dt">{{ c.committedAt | ago }}</td>
                  <td><app-durability [state]="c.durability" /></td>
                </tr>
              }
            </tbody>
          </table>
        </div>
        <mat-paginator [length]="total()" [pageIndex]="page() - 1" [pageSize]="pageSize" [pageSizeOptions]="[]" (page)="onPage($event)" />
      } @else if (!loading()) { <app-empty icon="commit" text="No commits match" /> }
    }`,
  styles: [`.msg { background: var(--surface-2); padding: 8px; border-radius: 6px; margin: 0 0 8px; }`],
})
export class HistoryTab implements OnInit {
  ctx = inject(RepoContext);
  private api = inject(ApiService);
  private route = inject(ActivatedRoute);
  private router = inject(Router);
  private destroyRef = inject(DestroyRef);
  emptyTree = EMPTY_TREE;
  items = signal<CommitSummary[]>([]);
  total = signal(0);
  page = signal(1);
  pageSize = 50;
  loading = signal(false);
  error = signal<unknown>(null);
  detail = signal<CommitDetail | null>(null);
  f = { ref: '', path: '', author: '', sessionId: '', since: '', until: '' };

  ngOnInit() {
    this.route.queryParamMap.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(q => {
      const sha = q.get('sha');
      if (q.get('session') && !this.f.sessionId) { this.f.sessionId = q.get('session')!; this.load(1); }
      if (sha && sha !== this.detail()?.commit.sha) this.loadDetail(sha);
      if (!sha) this.detail.set(null);
    });
    this.load(1);
    this.ctx.refresh$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => { if (!this.detail()) this.load(this.page(), true); });
  }

  load(page: number, silent = false) {
    this.page.set(page);
    if (!silent) this.loading.set(true);
    const iso = (d: string, end = false) => d ? new Date(d + (end ? 'T23:59:59' : 'T00:00:00')).toISOString() : undefined;
    this.api.commits(this.ctx.id(), {
      ref: this.f.ref.trim() || undefined, path: this.f.path.trim() || undefined, author: this.f.author.trim() || undefined,
      sessionId: this.f.sessionId.trim() || undefined, since: iso(this.f.since), until: iso(this.f.until, true), page, pageSize: this.pageSize,
    }).subscribe({
      next: r => { this.items.set(r.items); this.total.set(r.total); this.loading.set(false); this.error.set(null); },
      error: e => { this.error.set(e); this.loading.set(false); },
    });
  }

  onPage(e: PageEvent) { this.load(e.pageIndex + 1); }

  openSha(sha: string) { this.router.navigate([], { queryParams: { sha }, queryParamsHandling: 'merge' }); }
  close() { this.router.navigate([], { queryParams: { sha: null }, queryParamsHandling: 'merge' }); }

  private loadDetail(sha: string) {
    this.loading.set(true);
    this.api.commit(this.ctx.id(), sha).subscribe({
      next: d => { this.detail.set(d); this.loading.set(false); },
      error: e => { this.error.set(e); this.loading.set(false); },
    });
  }

  browseAt(sha: string) { this.router.navigate([], { queryParams: { tab: 'files', ref: sha, sha: null, path: null }, queryParamsHandling: 'merge' }); }
  restoreTo(c: CommitSummary) { this.ctx.recover({ type: 'RestoreProject', recoveryPoint: c.sha, label: `Restore project to ${c.sha.substring(0, 8)} (${c.subject})` }); }
  revert(c: CommitSummary) { this.ctx.recover({ type: 'RevertCommits', commitShas: [c.sha], recoveryPoint: c.sha, label: `Undo commit ${c.sha.substring(0, 8)} (${c.subject})` }); }
}
