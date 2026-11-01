import { Component, Pipe, PipeTransform, computed, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { toProblem } from '../core/errors';
import { ApiProblem, DurabilityState } from '../core/models';

export type Tone = 'good' | 'warn' | 'bad' | 'info' | 'maint' | 'off';

// ---------------------------------------------------------------- formatting

export function formatBytes(n: number | null | undefined): string {
  if (n === null || n === undefined) return '—';
  if (n < 1024) return `${n} B`;
  const u = ['KB', 'MB', 'GB', 'TB'];
  let v = n / 1024, i = 0;
  while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
  return `${v.toFixed(v < 10 ? 1 : 0)} ${u[i]}`;
}

export function timeAgo(iso: string | null | undefined): string {
  if (!iso) return '—';
  const t = new Date(iso).getTime();
  if (isNaN(t)) return '—';
  const s = Math.round((Date.now() - t) / 1000);
  const abs = Math.abs(s), suffix = s >= 0 ? 'ago' : 'from now';
  if (abs < 45) return s >= 0 ? 'just now' : 'in a moment';
  if (abs < 3600) return `${Math.round(abs / 60)} min ${suffix}`;
  if (abs < 86400) return `${Math.round(abs / 3600)} h ${suffix}`;
  if (abs < 86400 * 30) return `${Math.round(abs / 86400)} d ${suffix}`;
  return new Date(iso).toLocaleDateString();
}

export function fmtDate(iso: string | null | undefined): string {
  return iso ? new Date(iso).toLocaleString() : '—';
}

export function durationText(fromIso: string | null | undefined, toIso: string | null | undefined): string {
  if (!fromIso || !toIso) return '—';
  const ms = new Date(toIso).getTime() - new Date(fromIso).getTime();
  if (ms < 1000) return `${ms} ms`;
  if (ms < 60000) return `${(ms / 1000).toFixed(1)} s`;
  if (ms < 3600000) return `${Math.round(ms / 60000)} min`;
  return `${(ms / 3600000).toFixed(1)} h`;
}

@Pipe({ name: 'bytes' })
export class BytesPipe implements PipeTransform { transform(v: number | null | undefined) { return formatBytes(v); } }

@Pipe({ name: 'ago', pure: false })
export class AgoPipe implements PipeTransform { transform(v: string | null | undefined) { return timeAgo(v); } }

@Pipe({ name: 'dt' })
export class DatePipeLite implements PipeTransform { transform(v: string | null | undefined) { return fmtDate(v); } }

@Pipe({ name: 'sha' })
export class ShaPipe implements PipeTransform { transform(v: string | null | undefined, n = 8) { return v ? v.substring(0, n) : '—'; } }

@Pipe({ name: 'human' })
export class HumanizePipe implements PipeTransform {
  /** "RemotePushPending" -> "Remote push pending" */
  transform(v: string | null | undefined) {
    if (!v) return '—';
    const s = v.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/([A-Z])([A-Z][a-z])/g, '$1 $2');
    return s.charAt(0).toUpperCase() + s.slice(1).toLowerCase().replace(/\bai\b/g, 'AI');
  }
}

// ---------------------------------------------------------------- tone mapping

const TONES: Record<string, Tone> = {
  // durability
  RemoteSynchronized: 'good', IndependentlyBackedUp: 'good', LocalCommit: 'warn', RemotePushPending: 'warn', WorkingTreeOnly: 'warn',
  Failed: 'bad', RemoteDiverged: 'bad',
  // remote status
  InSync: 'good', Ahead: 'warn', Behind: 'info', Diverged: 'bad', Unreachable: 'bad', Unknown: 'off',
  // jobs/push
  Pending: 'warn', Processing: 'info', Succeeded: 'good', RetryScheduled: 'warn', Conflict: 'bad', AuthenticationFailed: 'bad', PermanentFailure: 'bad',
  Queued: 'warn', Running: 'info', Cancelled: 'off',
  // operations / sessions
  Active: 'info', Committing: 'info', Completed: 'good', Aborted: 'off', Interrupted: 'warn', PolicyBlocked: 'bad',
  Merged: 'good', Reverted: 'maint', Abandoned: 'off',
  // recovery
  Previewed: 'info', Executing: 'info', Expired: 'off',
  // backups
  Verified: 'good', VerificationFailed: 'bad',
  // repo
  Provisioning: 'info', Archived: 'off', Deleted: 'bad',
  // worktrees / merges
  Released: 'off', Missing: 'bad', AwaitingApproval: 'warn', Mergeable: 'good', Rejected: 'off',
  // risk
  Low: 'good', Medium: 'warn', High: 'bad',
  // alerts
  Info: 'info', Warning: 'warn', Critical: 'bad',
  // health
  Healthy: 'good', Degraded: 'warn', Unhealthy: 'bad', Ok: 'good', OK: 'good', Low_: 'warn',
  // outcomes
  Success: 'good', success: 'good', Failure: 'bad', failure: 'bad', Denied: 'bad', denied: 'bad',
  // file kinds
  Added: 'good', Modified: 'info', Renamed: 'maint', Copied: 'maint', TypeChanged: 'maint',
};
const ICONS: Partial<Record<Tone, string>> = { good: 'check_circle', warn: 'schedule', bad: 'error', info: 'info', maint: 'build', off: 'remove_circle_outline' };

export function toneOf(v: string | null | undefined): Tone {
  if (!v) return 'off';
  return TONES[v] ?? (/(fail|error|unhealthy|critical)/i.test(v) ? 'bad' : /(pass|succe|ok|healthy)/i.test(v) ? 'good' : /(warn|degrad|pending)/i.test(v) ? 'warn' : 'off');
}

/** Generic state chip: icon + label + tone (never colour alone). */
@Component({
  selector: 'app-chip',
  imports: [MatIconModule, MatTooltipModule, HumanizePipe],
  template: `<span class="chip" [attr.data-tone]="t()" [matTooltip]="tip() ?? ''"><mat-icon>{{ icon() ?? ic() }}</mat-icon>{{ label() ?? (value() | human) }}</span>`,
})
export class StateChip {
  value = input<string | null | undefined>();
  label = input<string | null>();
  tone = input<Tone | null>();
  icon = input<string | null>();
  tip = input<string | null>();
  t = computed(() => this.tone() ?? toneOf(this.value()));
  ic = computed(() => ICONS[this.t()] ?? 'circle');
}

const DURABILITY: Record<DurabilityState, { label: string; icon: string; tone: Tone; tip: string; safe: boolean }> = {
  WorkingTreeOnly: { label: 'Working tree only', icon: 'edit_note', tone: 'warn', safe: false, tip: 'Not committed. Exists only in the local working directory.' },
  LocalCommit: { label: 'Local only', icon: 'laptop', tone: 'warn', safe: false, tip: 'Committed locally but NOT on the central server yet.' },
  RemotePushPending: { label: 'Push pending', icon: 'cloud_upload', tone: 'warn', safe: false, tip: 'Committed locally; the push to the central server is queued. NOT yet safe on the remote.' },
  RemoteSynchronized: { label: 'On remote', icon: 'cloud_done', tone: 'good', safe: true, tip: 'Pushed and verified on the central Git server.' },
  IndependentlyBackedUp: { label: 'Backed up', icon: 'verified', tone: 'good', safe: true, tip: 'Verified on the central server and in an independent encrypted backup.' },
  Failed: { label: 'Push failed', icon: 'cloud_off', tone: 'bad', safe: false, tip: 'The push failed. This checkpoint exists only locally.' },
  RemoteDiverged: { label: 'Diverged', icon: 'call_split', tone: 'bad', safe: false, tip: 'Local and remote histories differ. This checkpoint is NOT safe on the remote.' },
};

/** Durability badge — local-only states are amber/red with a dashed outline so they never look like "safe on the remote". */
@Component({
  selector: 'app-durability',
  imports: [MatIconModule, MatTooltipModule],
  template: `
    @if (d(); as d) {
      <span class="chip" [attr.data-tone]="d.tone" [attr.data-outline]="d.safe ? null : ''" [matTooltip]="d.tip">
        <mat-icon>{{ d.icon }}</mat-icon>{{ prefix() }}{{ d.label }}
      </span>
    } @else { <span class="muted">—</span> }`,
})
export class DurabilityBadge {
  state = input<DurabilityState | null | undefined>();
  prefix = input('');
  d = computed(() => { const s = this.state(); return s ? DURABILITY[s] ?? { label: s, icon: 'help', tone: 'off' as Tone, tip: s, safe: false } : null; });
}

export function isRemoteSafe(s: DurabilityState | null | undefined) { return s === 'RemoteSynchronized' || s === 'IndependentlyBackedUp'; }

// ---------------------------------------------------------------- banners & states

@Component({
  selector: 'app-error',
  imports: [MatIconModule, MatButtonModule],
  template: `
    @if (p(); as p) {
      <div class="callout bad row" role="alert" style="align-items:flex-start">
        <mat-icon style="color:var(--bad)">error</mat-icon>
        <div style="flex:1">
          <div><b>{{ title() }}</b> {{ p.detail }}</div>
          <div class="small muted">
            @if (p.status > 0) { HTTP {{ p.status }} }
            @if (p.code) { · {{ p.code }} }
            @if (p.correlationId) { · correlation id <code>{{ p.correlationId }}</code> }
          </div>
        </div>
        @if (dismissable()) { <button mat-icon-button (click)="dismiss.emit()" aria-label="Dismiss"><mat-icon>close</mat-icon></button> }
      </div>
    }`,
})
export class ErrorBanner {
  error = input<unknown>();
  title = input('Error:');
  dismissable = input(false);
  dismiss = output<void>();
  p = computed<ApiProblem | null>(() => { const e = this.error(); return e ? toProblem(e) : null; });
}

@Component({
  selector: 'app-empty',
  imports: [MatIconModule],
  template: `<div class="empty-state"><mat-icon>{{ icon() }}</mat-icon><div class="t">{{ text() }}</div>@if (hint()) {<div class="small muted">{{ hint() }}</div>}<ng-content /></div>`,
  styles: [`.empty-state { padding: 28px 16px; text-align: center; color: var(--muted); }
    .empty-state mat-icon { font-size: 36px; width: 36px; height: 36px; opacity: .6; }
    .empty-state .t { font-weight: 600; margin: 4px 0 2px; color: var(--text); }`],
})
export class EmptyState {
  icon = input('inbox');
  text = input('Nothing here yet');
  hint = input<string | null>(null);
}

@Component({
  selector: 'app-loading',
  imports: [MatProgressBarModule],
  template: `@if (on()) { <mat-progress-bar mode="indeterminate" style="margin-bottom:8px" /> }`,
})
export class LoadingBar { on = input(false); }

export const UI = [BytesPipe, AgoPipe, DatePipeLite, ShaPipe, HumanizePipe, StateChip, DurabilityBadge, ErrorBanner, EmptyState, LoadingBar] as const;
