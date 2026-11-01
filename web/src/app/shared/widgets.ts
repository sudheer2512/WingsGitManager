import { Component, DestroyRef, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { Observable, Subscription } from 'rxjs';
import { ApiService } from '../core/api.service';
import { AuditLog, DailyCount, Job } from '../core/models';
import { UI } from './ui';

// ---------------------------------------------------------------- confirm dialog

export interface ConfirmData {
  title: string; message: string; confirmText?: string; danger?: boolean;
  /** When set, the user must type this text to enable the confirm button. */
  typeToConfirm?: string;
  /** Checkbox the user must tick (e.g. "I understand this rewrites the working directory"). */
  acknowledge?: string;
  details?: string[];
}

@Component({
  selector: 'app-confirm-dialog',
  imports: [MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatCheckboxModule, FormsModule],
  template: `
    <h2 mat-dialog-title>{{ data.title }}</h2>
    <mat-dialog-content>
      <p class="pre">{{ data.message }}</p>
      @if (data.details?.length) { <ul class="small">@for (d of data.details; track $index) { <li>{{ d }}</li> }</ul> }
      @if (data.acknowledge) { <mat-checkbox [(ngModel)]="ack">{{ data.acknowledge }}</mat-checkbox> }
      @if (data.typeToConfirm) {
        <mat-form-field class="full" style="margin-top:8px">
          <mat-label>Type "{{ data.typeToConfirm }}" to confirm</mat-label>
          <input matInput [(ngModel)]="typed" autocomplete="off">
        </mat-form-field>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button [class.danger]="data.danger" [disabled]="!ok()" [mat-dialog-close]="true">{{ data.confirmText ?? 'Confirm' }}</button>
    </mat-dialog-actions>`,
})
export class ConfirmDialog {
  data = inject<ConfirmData>(MAT_DIALOG_DATA);
  typed = ''; ack = false;
  ok() { return (!this.data.typeToConfirm || this.typed.trim() === this.data.typeToConfirm) && (!this.data.acknowledge || this.ack); }
}

export function confirm(dialog: MatDialog, data: ConfirmData): Observable<boolean | undefined> {
  return dialog.open(ConfirmDialog, { data, width: '520px', maxWidth: '94vw' }).afterClosed();
}

// ---------------------------------------------------------------- secret dialog (shown once)

@Component({
  selector: 'app-secret-dialog',
  imports: [MatDialogModule, MatButtonModule, MatIconModule],
  template: `
    <h2 mat-dialog-title>{{ data.title }}</h2>
    <mat-dialog-content>
      <div class="callout warn">{{ data.message }}</div>
      <div class="row" style="align-items:stretch">
        <code class="secret">{{ data.secret }}</code>
        <button mat-stroked-button (click)="copy()"><mat-icon>content_copy</mat-icon>{{ copied() ? 'Copied' : 'Copy' }}</button>
      </div>
    </mat-dialog-content>
    <mat-dialog-actions align="end"><button mat-flat-button mat-dialog-close>I have stored it</button></mat-dialog-actions>`,
  styles: [`.secret { flex: 1; padding: 8px; background: var(--surface-2); border: 1px solid var(--border); border-radius: 4px; word-break: break-all; }`],
})
export class SecretDialog {
  data = inject<{ title: string; message: string; secret: string }>(MAT_DIALOG_DATA);
  copied = signal(false);
  copy() { navigator.clipboard?.writeText(this.data.secret).then(() => this.copied.set(true)); }
}

// ---------------------------------------------------------------- bar chart (commits per day)

@Component({
  selector: 'app-bar-chart',
  template: `
    @if (data().length) {
      <div class="chart" role="img" [attr.aria-label]="label()">
        <div class="bars">
          @for (d of data(); track d.day) {
            <div class="col" [title]="d.day + ': ' + d.count">
              <div class="val">{{ d.count || '' }}</div>
              <div class="bar" [style.height.%]="max() ? (d.count / max()) * 100 : 0"></div>
            </div>
          }
        </div>
        <div class="axis">
          @for (d of data(); track d.day; let i = $index) {
            <div class="tick">{{ i % step() === 0 ? short(d.day) : '' }}</div>
          }
        </div>
      </div>
    } @else { <div class="muted small" style="padding:24px;text-align:center">No commits in this period</div> }`,
  styles: [`
    .chart { padding: 4px 0; }
    .bars { display: flex; align-items: flex-end; gap: 3px; height: 150px; border-bottom: 1px solid var(--axis); }
    .col { flex: 1; display: flex; flex-direction: column; justify-content: flex-end; height: 100%; min-width: 4px; }
    .bar { background: var(--series-1); border-radius: 3px 3px 0 0; min-height: 1px; transition: height .3s; }
    .col:hover .bar { filter: brightness(1.15); }
    .val { font-size: 10px; color: var(--muted); text-align: center; line-height: 14px; }
    .axis { display: flex; gap: 3px; } .tick { flex: 1; font-size: 10px; color: var(--muted); text-align: center; white-space: nowrap; overflow: visible; min-width: 4px; }`],
})
export class BarChart {
  data = input<DailyCount[]>([]);
  label = input('Commits per day');
  max = computed(() => Math.max(0, ...this.data().map(d => d.count)));
  step = computed(() => Math.max(1, Math.ceil(this.data().length / 10)));
  short(day: string) { const d = new Date(day + 'T00:00:00'); return isNaN(d.getTime()) ? day : `${d.getMonth() + 1}/${d.getDate()}`; }
}

// ---------------------------------------------------------------- audit table

@Component({
  selector: 'app-audit-table',
  imports: [...UI, RouterLink],
  template: `
    @if (items().length) {
      <div class="table-wrap">
        <table class="data">
          <thead><tr><th>Time</th><th>Actor</th><th>Action</th><th>Target</th><th>Outcome</th>@if (showRepo()) {<th>Repository</th>}<th>Correlation</th></tr></thead>
          <tbody>
            @for (a of items(); track a.id) {
              <tr class="clickable" (click)="open.set(open() === a.id ? null : a.id)">
                <td class="nowrap" [title]="a.timestamp | dt">{{ a.timestamp | ago }}</td>
                <td>{{ a.actor }} <span class="tag">{{ a.actorType }}</span></td>
                <td><code>{{ a.action }}</code></td>
                <td class="ellipsis" style="max-width:280px" [title]="a.target ?? ''">{{ a.target ?? '—' }}</td>
                <td><app-chip [value]="a.outcome" /></td>
                @if (showRepo()) { <td>@if (a.repositoryId) {<a [routerLink]="['/repositories', a.repositoryId]" (click)="$event.stopPropagation()">open</a>} @else {—}</td> }
                <td><code class="small">{{ a.correlationId ?? '—' }}</code></td>
              </tr>
              @if (open() === a.id && a.details) {
                <tr><td [attr.colspan]="showRepo() ? 7 : 6"><pre class="pre small" style="margin:0">{{ pretty(a.details) }}</pre></td></tr>
              }
            }
          </tbody>
        </table>
      </div>
    } @else { <div class="muted" style="padding:16px;text-align:center">No audit entries</div> }`,
})
export class AuditTable {
  items = input<AuditLog[]>([]);
  showRepo = input(true);
  open = signal<string | null>(null);
  pretty(s: string) { try { return JSON.stringify(JSON.parse(s), null, 2); } catch { return s; } }
}

// ---------------------------------------------------------------- job tracker

/** Polls GET /jobs/{id} and shows state/progress/result; emits when the job finishes. */
@Component({
  selector: 'app-job',
  imports: [...UI, MatProgressBarModule],
  template: `
    @if (job(); as j) {
      <div class="callout" [class.good]="j.state === 'Succeeded'" [class.bad]="j.state === 'Failed' || j.state === 'Cancelled'">
        <div class="row"><b>{{ j.type }}</b> <app-chip [value]="j.state" /> <span class="spacer"></span><span class="small muted">job {{ j.id | sha }} · attempts {{ j.attempts }}</span></div>
        @if (j.state === 'Queued' || j.state === 'Running') { <mat-progress-bar mode="indeterminate" style="margin:6px 0" /> }
        @if (j.progress) { <div class="small">{{ j.progress }}</div> }
        @if (j.result) { <pre class="pre small" style="margin:4px 0 0">{{ pretty(j.result) }}</pre> }
        @if (j.error) { <div class="error small">{{ j.error }}</div> }
      </div>
    } @else if (jobId()) { <div class="small muted">Waiting for job {{ jobId() | sha }}…</div> }
    <app-error [error]="error()" />`,
})
export class JobTracker implements OnInit {
  private api = inject(ApiService);
  jobId = input.required<string>();
  done = output<Job>();
  job = signal<Job | null>(null);
  error = signal<unknown>(null);
  private sub?: Subscription;
  constructor() { inject(DestroyRef).onDestroy(() => this.sub?.unsubscribe()); }
  ngOnInit() {
    this.sub = this.api.pollJob(this.jobId()).subscribe({
      next: j => { this.job.set(j); if (j.state !== 'Queued' && j.state !== 'Running') this.done.emit(j); },
      error: e => this.error.set(e),
    });
  }
  pretty(s: string) { try { return JSON.stringify(JSON.parse(s), null, 2); } catch { return s; } }
}

