import { Component, DestroyRef, OnInit, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatIconModule } from '@angular/material/icon';
import { RouterLink } from '@angular/router';
import { interval, merge } from 'rxjs';
import { ApiService } from '../core/api.service';
import { PUSH_JOB_STATES, PushJob } from '../core/models';
import { RealtimeService } from '../core/realtime.service';
import { UI } from '../shared/ui';

@Component({
  selector: 'app-sync-monitor',
  imports: [FormsModule, RouterLink, MatButtonModule, MatButtonToggleModule, MatIconModule, ...UI],
  template: `
    <div class="page">
      <div class="page-head"><h1>Sync monitor</h1><span class="muted">push queue to the central Git server</span><span class="spacer"></span>
        <button mat-stroked-button (click)="load()"><mat-icon>refresh</mat-icon>Refresh</button></div>
      <div class="filters">
        <mat-button-toggle-group [value]="filter()" (change)="filter.set($event.value); load()" hideSingleSelectionIndicator>
          <mat-button-toggle value="">All</mat-button-toggle>
          @for (s of states; track s) { <mat-button-toggle [value]="s">{{ s | human }}</mat-button-toggle> }
        </mat-button-toggle-group>
      </div>
      <app-loading [on]="loading()" /><app-error [error]="error()" [dismissable]="true" (dismiss)="error.set(null)" />
      @if (msg()) { <div class="callout good">{{ msg() }}</div> }
      @if (items().length) {
        <div class="table-wrap">
          <table class="data">
            <thead><tr><th>Queued</th><th>Repository</th><th>Branch</th><th>Commit</th><th>State</th><th class="num">Retries</th><th>Next attempt</th><th>Last error</th><th></th></tr></thead>
            <tbody>
              @for (j of items(); track j.id) {
                <tr>
                  <td class="nowrap">{{ j.createdAt | ago }}</td>
                  <td><a [routerLink]="['/repositories', j.repositoryId]">{{ j.repositoryName ?? (j.repositoryId | sha) }}</a></td>
                  <td><code>{{ j.branch }}</code></td><td><code>{{ j.commitSha | sha }}</code></td>
                  <td><app-chip [value]="j.state" /></td><td class="num">{{ j.retryCount }}</td>
                  <td class="nowrap">{{ j.nextAttemptAt | ago }}</td>
                  <td class="small error ellipsis" style="max-width:320px" [title]="j.lastError ?? ''">{{ j.lastError ?? '' }}</td>
                  <td>@if (j.state !== 'Succeeded' && j.state !== 'Processing') { <button mat-button (click)="retry(j)">Retry</button> }</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      } @else if (!loading()) { <app-empty icon="cloud_done" text="Nothing in the queue" hint="Every commit has been pushed and verified." /> }
    </div>`,
})
export class SyncMonitor implements OnInit {
  private api = inject(ApiService);
  private rt = inject(RealtimeService);
  private destroyRef = inject(DestroyRef);
  state = input<string>();
  states = PUSH_JOB_STATES;
  filter = signal('');
  items = signal<PushJob[]>([]);
  loading = signal(false); error = signal<unknown>(null); msg = signal<string | null>(null);

  ngOnInit() {
    this.filter.set(this.state() ?? '');
    this.load();
    merge(this.rt.changes(null, 1500), interval(15000)).pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => this.load(true));
  }
  load(silent = false) {
    if (!silent) this.loading.set(true);
    this.api.queue(this.filter() || undefined).subscribe({
      next: q => { this.items.set(q); this.loading.set(false); },
      error: e => { this.error.set(e); this.loading.set(false); },
    });
  }
  retry(j: PushJob) {
    this.api.retry(j.repositoryId, j.id).subscribe({ next: r => { this.msg.set(`${r.retried} push job(s) rescheduled.`); this.load(); }, error: e => this.error.set(e) });
  }
}
