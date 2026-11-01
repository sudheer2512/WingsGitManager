import { Component, DestroyRef, OnInit, computed, effect, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTabsModule } from '@angular/material/tabs';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Router, RouterLink } from '@angular/router';
import { ApiService } from '../core/api.service';
import { RealtimeService } from '../core/realtime.service';
import { UI } from '../shared/ui';
import { problemText } from '../core/errors';
import { RepoContext } from './repo-context';
import { FilesTab } from './files-tab';
import { HistoryTab } from './history-tab';
import { ChangesTab } from './changes-tab';
import { BranchesTab } from './branches-tab';
import { SessionsTab } from './sessions-tab';
import { CheckpointsTab } from './checkpoints-tab';
import { RecoveryTab } from './recovery-tab';
import { BackupsTab } from './backups-tab';
import { ActivityTab } from './activity-tab';
import { SettingsTab } from './settings-tab';

const TABS = ['files', 'history', 'changes', 'branches', 'sessions', 'checkpoints', 'recovery', 'backups', 'activity', 'settings'] as const;

@Component({
  selector: 'app-repo-detail',
  providers: [RepoContext],
  imports: [RouterLink, MatTabsModule, MatButtonModule, MatIconModule, MatTooltipModule, ...UI,
    FilesTab, HistoryTab, ChangesTab, BranchesTab, SessionsTab, CheckpointsTab, RecoveryTab, BackupsTab, ActivityTab, SettingsTab],
  template: `
    <div class="page">
      <div class="page-head">
        <a routerLink="/repositories" class="crumb">Repositories</a><span class="muted">/</span>
        @if (ctx.repo(); as r) {
          <span class="muted">{{ r.summary.companyName }} / {{ r.summary.projectName }} /</span>
          <h1>{{ r.summary.name }}</h1>
          @if (r.summary.state !== 'Active') { <app-chip [value]="r.summary.state" /> }
          <span class="spacer"></span>
          <button mat-stroked-button (click)="fetch()" [disabled]="busy()" matTooltip="Fetch from the central server and compare"><mat-icon>cloud_download</mat-icon>Fetch</button>
          <button mat-stroked-button (click)="push()" [disabled]="busy()" matTooltip="Push and verify on the central server"><mat-icon>cloud_upload</mat-icon>Push</button>
          <button mat-flat-button (click)="ctx.tab('recovery')"><mat-icon>restore</mat-icon>Recover code</button>
        } @else { <h1>Repository</h1> }
      </div>
      <app-error [error]="ctx.error()" />
      @if (actionMsg()) { <div class="callout" [class.bad]="actionBad()" [class.good]="!actionBad()">{{ actionMsg() }}</div> }

      @if (ctx.repo(); as r) {
        <div class="summary panel">
          <div class="s"><span class="k">Default branch</span><code>{{ r.summary.defaultBranch }}</code></div>
          <div class="s"><span class="k">Last commit</span><span><code>{{ r.summary.lastCommitSha | sha }}</code> {{ r.summary.lastCommitAt | ago }}</span></div>
          <div class="s"><span class="k">Last push</span><span>{{ r.summary.lastPushAt | ago }}</span></div>
          <div class="s"><span class="k">Remote</span><app-chip [value]="r.summary.remoteStatus" /></div>
          <div class="s"><span class="k">Latest checkpoint</span><app-durability [state]="r.summary.latestCheckpointDurability" /></div>
          <div class="s"><span class="k">Pending pushes</span><span>{{ r.summary.pendingPushes }}</span></div>
          <div class="s"><span class="k">Working tree</span>
            @if (r.summary.hasUncommittedChanges) { <app-chip value="WorkingTreeOnly" label="Uncommitted changes" tone="warn" icon="edit_note" /> }
            @else { <app-chip label="Clean" tone="good" icon="check" /> }</div>
          <div class="s"><span class="k">Storage</span><span>{{ r.summary.storageBytes | bytes }}</span></div>
          <div class="s wide"><span class="k">Remote URL</span><a [href]="r.remoteUrl" target="_blank" rel="noopener" class="mono ellipsis">{{ r.remoteUrl }}</a></div>
        </div>

        <mat-tab-group [selectedIndex]="index()" (selectedIndexChange)="select($event)" animationDuration="0ms" mat-stretch-tabs="false" class="tabs">
          <mat-tab label="Files"><ng-template matTabContent><app-files-tab /></ng-template></mat-tab>
          <mat-tab label="Commit history"><ng-template matTabContent><app-history-tab /></ng-template></mat-tab>
          <mat-tab label="Changes"><ng-template matTabContent><app-changes-tab /></ng-template></mat-tab>
          <mat-tab label="Branches"><ng-template matTabContent><app-branches-tab /></ng-template></mat-tab>
          <mat-tab label="AI sessions"><ng-template matTabContent><app-sessions-tab /></ng-template></mat-tab>
          <mat-tab label="Checkpoints"><ng-template matTabContent><app-checkpoints-tab /></ng-template></mat-tab>
          <mat-tab label="Recovery"><ng-template matTabContent><app-recovery-tab /></ng-template></mat-tab>
          <mat-tab label="Backups"><ng-template matTabContent><app-backups-tab /></ng-template></mat-tab>
          <mat-tab label="Activity"><ng-template matTabContent><app-activity-tab /></ng-template></mat-tab>
          <mat-tab label="Settings"><ng-template matTabContent><app-settings-tab /></ng-template></mat-tab>
        </mat-tab-group>
      }
    </div>`,
  styles: [`
    .summary { display: flex; flex-wrap: wrap; gap: 8px 22px; margin-bottom: 8px; align-items: center; }
    .s { display: flex; flex-direction: column; gap: 2px; min-width: 0; } .s.wide { max-width: 420px; }
    .k { font-size: 11px; color: var(--muted); text-transform: uppercase; letter-spacing: .03em; }
    .tabs { margin-top: 4px; }`],
})
export class RepoDetail implements OnInit {
  ctx = inject(RepoContext);
  private api = inject(ApiService);
  private rt = inject(RealtimeService);
  private router = inject(Router);
  private destroyRef = inject(DestroyRef);
  id = input.required<string>();
  tab = input<string>();
  index = computed(() => Math.max(0, TABS.indexOf((this.tab() ?? 'files') as (typeof TABS)[number])));
  busy = signal(false);
  actionMsg = signal<string | null>(null);
  actionBad = signal(false);

  constructor() {
    effect(() => { const id = this.id(); if (id && id !== this.ctx.id()) { this.ctx.repo.set(null); this.ctx.load(id); } });
  }

  ngOnInit() {
    this.rt.changes(null, 1000).pipe(takeUntilDestroyed(this.destroyRef)).subscribe(n => {
      if (n.repositoryId === this.ctx.id()) { this.ctx.reload(); this.ctx.refresh$.next(); }
    });
  }

  select(i: number) {
    if (i === this.index()) return;
    this.router.navigate([], { queryParams: { tab: TABS[i] } });
  }

  push() {
    this.busy.set(true); this.actionMsg.set(null);
    this.api.push(this.ctx.id()).subscribe({
      next: s => { this.busy.set(false); this.actionBad.set(s.failed > 0); this.actionMsg.set(`Push finished: remote ${s.remoteStatus}, ${s.pending} pending, ${s.failed} failed, ${s.conflicts} conflicts.`); this.ctx.reload(); this.ctx.refresh$.next(); },
      error: e => { this.busy.set(false); this.actionBad.set(true); this.actionMsg.set(problemText(e)); },
    });
  }

  fetch() {
    this.busy.set(true); this.actionMsg.set(null);
    this.api.fetch(this.ctx.id()).subscribe({
      next: r => { this.busy.set(false); this.actionBad.set(false); this.actionMsg.set('Fetched: ' + (r.branches.map(b => `${b.branch} ${b.status} (+${b.ahead}/-${b.behind})`).join(', ') || 'no branches')); this.ctx.reload(); },
      error: e => { this.busy.set(false); this.actionBad.set(true); this.actionMsg.set(problemText(e)); },
    });
  }
}
