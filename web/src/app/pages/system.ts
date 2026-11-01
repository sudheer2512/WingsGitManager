import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { ApiService } from '../core/api.service';
import { Job, StorageReport, SystemHealth } from '../core/models';
import { UI } from '../shared/ui';

@Component({
  selector: 'app-system',
  imports: [RouterLink, MatButtonModule, MatIconModule, MatProgressBarModule, ...UI],
  template: `
    <div class="page">
      <div class="page-head"><h1>System health</h1>@if (health(); as h) { <app-chip [value]="h.status" /> <span class="small muted">checked {{ h.checkedAt | ago }}</span> }
        <span class="spacer"></span><button mat-stroked-button (click)="load()"><mat-icon>refresh</mat-icon>Refresh</button></div>
      <app-loading [on]="loading()" /><app-error [error]="error()" />
      <div class="grid-2">
        <section class="panel">
          <div class="panel-head"><h2>Components</h2><span class="small muted">database, Git server (Gitea), storage, workers…</span></div>
          @if (health(); as h) {
            <div class="table-wrap"><table class="data">
              <thead><tr><th>Component</th><th>Status</th><th>Detail</th><th class="num">Time</th></tr></thead>
              <tbody>@for (c of h.components; track c.name) {
                <tr><td><b>{{ c.name }}</b></td><td><app-chip [value]="c.status" /></td><td class="small">{{ c.detail ?? '' }}</td><td class="num">{{ c.durationMs != null ? c.durationMs.toFixed(0) + ' ms' : '' }}</td></tr>
              }</tbody>
            </table></div>
          }
        </section>
        <section class="panel">
          <div class="panel-head"><h2>Storage volumes</h2></div>
          @for (v of storage()?.volumes ?? []; track v.path) {
            <div class="vol">
              <div class="row"><b>{{ v.purpose }}</b><app-chip [value]="v.status" /><span class="spacer"></span><span class="small">{{ (v.totalBytes - v.freeBytes) | bytes }} / {{ v.totalBytes | bytes }} ({{ v.usedPercent.toFixed(0) }}%)</span></div>
              <mat-progress-bar mode="determinate" [value]="v.usedPercent" [class.hot]="v.usedPercent > 85" />
              <div class="small muted mono">{{ v.path }} · {{ v.freeBytes | bytes }} free</div>
            </div>
          } @empty { <div class="muted small">No volume data</div> }
        </section>
      </div>
      <section class="panel">
        <div class="panel-head"><h2>Repository storage</h2><span class="spacer"></span><span class="small muted">total {{ repoTotal() | bytes }}</span></div>
        <div class="table-wrap"><table class="data">
          <thead><tr><th>Repository</th><th class="num">Working directory</th><th class="num">Git objects</th><th class="num">Total</th></tr></thead>
          <tbody>@for (r of storage()?.repositories ?? []; track r.repositoryId) {
            <tr><td><a [routerLink]="['/repositories', r.repositoryId]">{{ r.name }}</a></td><td class="num">{{ r.workingDirectoryBytes | bytes }}</td><td class="num">{{ r.gitObjectBytes | bytes }}</td><td class="num">{{ (r.workingDirectoryBytes + r.gitObjectBytes) | bytes }}</td></tr>
          } @empty { <tr><td colspan="4" class="muted">No repositories</td></tr> }</tbody>
        </table></div>
      </section>
      <section class="panel">
        <div class="panel-head"><h2>Background jobs</h2><span class="small muted">latest 50</span></div>
        @if (jobs().length) {
          <div class="table-wrap" style="max-height:420px"><table class="data">
            <thead><tr><th>Created</th><th>Type</th><th>State</th><th>Repository</th><th class="num">Attempts</th><th>Progress / error</th></tr></thead>
            <tbody>@for (j of jobs(); track j.id) {
              <tr><td class="nowrap">{{ j.createdAt | ago }}</td><td><code>{{ j.type }}</code></td><td><app-chip [value]="j.state" /></td>
                <td>@if (j.repositoryId) { <a [routerLink]="['/repositories', j.repositoryId]">open</a> }</td><td class="num">{{ j.attempts }}</td>
                <td class="small ellipsis" style="max-width:420px" [class.error]="j.error" [title]="j.error ?? j.progress ?? ''">{{ j.error ?? j.progress ?? '' }}</td></tr>
            }</tbody>
          </table></div>
        } @else { <app-empty icon="work_history" text="No background jobs" /> }
      </section>
    </div>`,
  styles: [`.vol { padding: 6px 0; border-bottom: 1px solid var(--border); } .vol:last-child { border-bottom: 0; }
    .hot { --mat-progress-bar-active-indicator-color: var(--bad); --mdc-linear-progress-active-indicator-color: var(--bad); }`],
})
export class SystemPage implements OnInit {
  private api = inject(ApiService);
  health = signal<SystemHealth | null>(null);
  storage = signal<StorageReport | null>(null);
  jobs = signal<Job[]>([]);
  loading = signal(false); error = signal<unknown>(null);
  repoTotal = computed(() => (this.storage()?.repositories ?? []).reduce((s, r) => s + r.workingDirectoryBytes + r.gitObjectBytes, 0));
  ngOnInit() { this.load(); }
  load() {
    this.loading.set(true);
    this.api.health().subscribe({ next: h => { this.health.set(h); this.loading.set(false); }, error: e => { this.error.set(e); this.loading.set(false); } });
    this.api.storage().subscribe({ next: s => this.storage.set(s), error: e => this.error.set(e) });
    this.api.jobs({ take: 50 }).subscribe({ next: j => this.jobs.set(j), error: () => undefined });
  }
}
