import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { interval, merge } from 'rxjs';
import { ApiService } from '../core/api.service';
import { Company, DashboardSummary, Project } from '../core/models';
import { RealtimeService } from '../core/realtime.service';
import { BarChart } from '../shared/widgets';
import { Tone, UI } from '../shared/ui';

interface Card { label: string; value: string | number; icon: string; tone: Tone; hint?: string; link?: string; query?: Record<string, string>; }

@Component({
  selector: 'app-dashboard',
  imports: [FormsModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatButtonModule, MatIconModule, MatTooltipModule, RouterLink, BarChart, ...UI],
  template: `
    <div class="page">
      <div class="page-head">
        <h1>Dashboard</h1><span class="spacer"></span>
        <button mat-stroked-button (click)="load()"><mat-icon>refresh</mat-icon>Refresh</button>
      </div>
      <div class="filters">
        <mat-form-field>
          <mat-label>Company</mat-label>
          <mat-select [(ngModel)]="companyId" (selectionChange)="projectId = ''; loadProjects(); load()">
            <mat-option value="">All companies</mat-option>
            @for (c of companies(); track c.id) { <mat-option [value]="c.id">{{ c.name }}</mat-option> }
          </mat-select>
        </mat-form-field>
        <mat-form-field>
          <mat-label>Project</mat-label>
          <mat-select [(ngModel)]="projectId" (selectionChange)="load()">
            <mat-option value="">All projects</mat-option>
            @for (p of projects(); track p.id) { <mat-option [value]="p.id">{{ p.name }}</mat-option> }
          </mat-select>
        </mat-form-field>
        <mat-form-field>
          <mat-label>User / agent</mat-label>
          <input matInput [(ngModel)]="user" (keyup.enter)="load()" (blur)="load()" placeholder="e.g. wings-code-agent">
        </mat-form-field>
        <mat-form-field>
          <mat-label>From</mat-label>
          <input matInput type="date" [(ngModel)]="from" (change)="load()">
        </mat-form-field>
        <mat-form-field>
          <mat-label>To</mat-label>
          <input matInput type="date" [(ngModel)]="to" (change)="load()">
        </mat-form-field>
        @if (companyId || projectId || user || from || to) { <button mat-button (click)="clear()">Clear filters</button> }
      </div>
      <app-loading [on]="loading()" />
      <app-error [error]="error()" />

      @if (data(); as d) {
        <div class="cards">
          @for (c of cards(); track c.label) {
            <a class="card" [attr.data-tone]="c.tone" [routerLink]="c.link ?? null" [queryParams]="c.query ?? null" [matTooltip]="c.hint ?? ''">
              <mat-icon>{{ c.icon }}</mat-icon>
              <div><div class="v">{{ c.value }}</div><div class="l">{{ c.label }}</div></div>
            </a>
          }
        </div>

        <div class="grid-2" style="margin-top:12px">
          <section class="panel">
            <div class="panel-head"><h2>Commits per day</h2><span class="spacer"></span><span class="small muted">{{ totalCommits() }} commits</span></div>
            <app-bar-chart [data]="d.commitsPerDay" />
          </section>
          <section class="panel">
            <div class="panel-head"><h2>Recent alerts</h2><span class="spacer"></span><a routerLink="/alerts" class="small">All alerts</a></div>
            @for (a of d.recentAlerts; track a.id) {
              <div class="alert-row">
                <app-chip [value]="a.severity" />
                <div style="flex:1;min-width:0">
                  <div>{{ a.message }}</div>
                  <div class="small muted">{{ a.category }} · @if (a.repositoryId) {<a [routerLink]="['/repositories', a.repositoryId]">{{ a.repositoryName ?? 'repository' }}</a> · } {{ a.lastSeenAt | ago }}</div>
                </div>
              </div>
            } @empty { <app-empty icon="task_alt" text="No open alerts" hint="Push failures, backup problems and storage warnings appear here." /> }
          </section>
        </div>
      } @else if (!loading() && !error()) {
        <app-empty icon="space_dashboard" text="No data" />
      }
    </div>`,
  styles: [`
    .cards { display: grid; grid-template-columns: repeat(auto-fill, minmax(210px, 1fr)); gap: 10px; }
    .card { display: flex; gap: 12px; align-items: center; padding: 12px; background: var(--surface); border: 1px solid var(--border); border-left: 4px solid var(--off);
      border-radius: 8px; color: var(--text); text-decoration: none; min-height: 72px; }
    a.card[href]:hover { background: var(--hover); }
    .card mat-icon { color: var(--off); }
    .card[data-tone=good] { border-left-color: var(--good); } .card[data-tone=good] mat-icon { color: var(--good); }
    .card[data-tone=warn] { border-left-color: var(--warn); } .card[data-tone=warn] mat-icon { color: var(--warn); }
    .card[data-tone=bad] { border-left-color: var(--bad); } .card[data-tone=bad] mat-icon { color: var(--bad); }
    .card[data-tone=info] { border-left-color: var(--info); } .card[data-tone=info] mat-icon { color: var(--info); }
    .v { font-size: 22px; font-weight: 600; font-variant-numeric: tabular-nums; line-height: 1.1; }
    .l { color: var(--muted); font-size: 12px; }
    .alert-row { display: flex; gap: 8px; align-items: flex-start; padding: 6px 0; border-bottom: 1px solid var(--border); }
    .alert-row:last-child { border-bottom: 0; }`],
})
export class Dashboard implements OnInit {
  private api = inject(ApiService);
  private rt = inject(RealtimeService);
  private destroyRef = inject(DestroyRef);
  companies = signal<Company[]>([]);
  projects = signal<Project[]>([]);
  data = signal<DashboardSummary | null>(null);
  loading = signal(false);
  error = signal<unknown>(null);
  companyId = ''; projectId = ''; user = ''; from = ''; to = '';
  private lastQuery = '';

  totalCommits = computed(() => (this.data()?.commitsPerDay ?? []).reduce((s, d) => s + d.count, 0));

  cards = computed<Card[]>(() => {
    const d = this.data();
    if (!d) return [];
    const backupAge = d.lastVerifiedBackupAt ? (Date.now() - new Date(d.lastVerifiedBackupAt).getTime()) / 3600000 : null;
    return [
      { label: 'Repositories', value: d.totalRepositories, icon: 'source', tone: 'info', link: '/repositories' },
      { label: 'Active repositories', value: d.activeRepositories, icon: 'check_circle', tone: 'good', link: '/repositories' },
      { label: 'Commits today', value: d.commitsToday, icon: 'commit', tone: 'info' },
      { label: 'Pending pushes', value: d.pendingPushes, icon: 'cloud_upload', tone: d.pendingPushes ? 'warn' : 'good', link: '/sync', query: { state: 'Pending' } },
      { label: 'Failed pushes', value: d.failedPushes, icon: 'cloud_off', tone: d.failedPushes ? 'bad' : 'good', link: '/sync', query: { state: 'PermanentFailure' } },
      { label: 'Repos with local-only changes', value: d.repositoriesWithLocalOnlyChanges, icon: 'laptop', tone: d.repositoriesWithLocalOnlyChanges ? 'warn' : 'good',
        hint: 'Commits or files that exist only on the local machine and are not yet verified on the central server.', link: '/repositories' },
      { label: 'Repos with remote divergence', value: d.repositoriesWithRemoteDivergence, icon: 'call_split', tone: d.repositoriesWithRemoteDivergence ? 'bad' : 'good', link: '/repositories' },
      { label: 'Last verified backup', value: d.lastVerifiedBackupAt ? this.ago(d.lastVerifiedBackupAt) : 'Never', icon: 'verified',
        tone: backupAge === null ? 'bad' : backupAge > 48 ? 'warn' : 'good',
        hint: `${d.repositoriesWithoutVerifiedBackup} repositor${d.repositoriesWithoutVerifiedBackup === 1 ? 'y has' : 'ies have'} no verified backup` },
      { label: 'Storage used', value: this.bytes(d.storageBytes), icon: 'storage', tone: 'info', link: '/system' },
      { label: 'High-risk operations', value: `${d.highRiskOperations} (${d.blockedHighRiskOperations} blocked)`, icon: 'warning',
        tone: d.blockedHighRiskOperations ? 'bad' : d.highRiskOperations ? 'warn' : 'good', link: '/audit' },
      { label: 'Active AI sessions', value: d.activeAISessions, icon: 'smart_toy', tone: 'info', link: '/sessions' },
      { label: 'Open alerts', value: d.openAlerts, icon: 'notification_important', tone: d.openAlerts ? 'bad' : 'good', link: '/alerts' },
    ];
  });

  ngOnInit() {
    this.api.companies().subscribe({ next: c => this.companies.set(c), error: () => undefined });
    this.loadProjects();
    this.load();
    merge(this.rt.changes(null, 2000), interval(60000)).pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => this.load(true));
  }

  loadProjects() { this.api.projects(this.companyId || undefined).subscribe({ next: p => this.projects.set(p), error: () => undefined }); }

  clear() { this.companyId = this.projectId = this.user = this.from = this.to = ''; this.loadProjects(); this.load(); }

  load(silent = false) {
    const q = {
      companyId: this.companyId || undefined, projectId: this.projectId || undefined, user: this.user.trim() || undefined,
      from: this.from ? new Date(this.from + 'T00:00:00').toISOString() : undefined,
      to: this.to ? new Date(this.to + 'T23:59:59').toISOString() : undefined,
    };
    const key = JSON.stringify(q);
    if (silent && key !== this.lastQuery) return;
    this.lastQuery = key;
    if (!silent) this.loading.set(true);
    this.api.dashboard(q).subscribe({
      next: d => { this.data.set(d); this.error.set(null); this.loading.set(false); },
      error: e => { this.error.set(e); this.loading.set(false); },
    });
  }

  private ago(iso: string) { const h = (Date.now() - new Date(iso).getTime()) / 3600000; return h < 1 ? `${Math.max(1, Math.round(h * 60))} min ago` : h < 48 ? `${Math.round(h)} h ago` : `${Math.round(h / 24)} d ago`; }
  private bytes(n: number) { const u = ['B', 'KB', 'MB', 'GB', 'TB']; let i = 0; while (n >= 1024 && i < 4) { n /= 1024; i++; } return `${n.toFixed(i ? 1 : 0)} ${u[i]}`; }
}
