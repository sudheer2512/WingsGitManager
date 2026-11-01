import { Injectable, NgZone, effect, inject, signal } from '@angular/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Router } from '@angular/router';
import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { Observable, Subject, debounceTime, filter } from 'rxjs';
import { AuthService } from './auth.service';
import { Notification } from './models';

type Severity = 'info' | 'good' | 'warn' | 'bad';

const DESCRIBE: Record<string, { text: string; sev: Severity; toast: boolean }> = {
  'repository.created': { text: 'Repository created', sev: 'good', toast: true },
  'change.begin': { text: 'AI change started', sev: 'info', toast: false },
  'change.completed': { text: 'AI change committed', sev: 'good', toast: true },
  'change.blocked': { text: 'High-risk change blocked by policy', sev: 'bad', toast: true },
  'sync.succeeded': { text: 'Push verified on remote', sev: 'good', toast: false },
  'sync.failed': { text: 'Push failed', sev: 'bad', toast: true },
  'sync.conflict': { text: 'Push conflict (remote diverged)', sev: 'bad', toast: true },
  'recovery.succeeded': { text: 'Recovery completed', sev: 'good', toast: true },
  'backup.verified': { text: 'Backup verified', sev: 'good', toast: true },
  'backup.verification-failed': { text: 'Backup verification failed', sev: 'bad', toast: true },
  'alert.raised': { text: 'Alert raised', sev: 'warn', toast: true },
  'job.succeeded': { text: 'Background job succeeded', sev: 'good', toast: false },
  'job.failed': { text: 'Background job failed', sev: 'bad', toast: true },
  'session.started': { text: 'AI session started', sev: 'info', toast: true },
  'merge.completed': { text: 'Merge completed', sev: 'good', toast: true },
  'merge.approval-required': { text: 'Merge awaiting approval', sev: 'warn', toast: true },
  'repository.external-change': { text: 'External change detected in working directory', sev: 'warn', toast: true },
};

export function describeNotification(n: Notification): string {
  const d = DESCRIBE[n.type];
  const p = n.payload ?? {};
  const extra = p.message ?? p.description ?? p.error ?? p.repositoryName ?? p.name ?? '';
  return `${d?.text ?? n.type}${extra ? ': ' + String(extra).slice(0, 140) : ''}`;
}

/** SignalR client for /hubs/notifications: toasts + a debounced "something changed" stream for auto-refresh. */
@Injectable({ providedIn: 'root' })
export class RealtimeService {
  private auth = inject(AuthService);
  private snack = inject(MatSnackBar);
  private router = inject(Router);
  private zone = inject(NgZone);
  private hub: HubConnection | null = null;
  private events$ = new Subject<Notification>();

  readonly connected = signal(false);
  readonly recent = signal<Notification[]>([]);
  readonly toastsEnabled = signal(true);

  constructor() {
    effect(() => {
      const token = this.auth.token(), me = this.auth.me();
      if (token && me) this.start(); else this.stop();
    });
  }

  /** Emits (debounced) whenever an event concerns the given repository (or any repository when omitted). */
  changes(repositoryId?: string | null, debounceMs = 800): Observable<Notification> {
    return this.events$.pipe(filter(n => !repositoryId || n.repositoryId === repositoryId), debounceTime(debounceMs));
  }

  private async start() {
    if (this.hub) return;
    const hub = new HubConnectionBuilder()
      .withUrl('/hubs/notifications', { accessTokenFactory: () => this.auth.token() ?? '' })
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(LogLevel.Warning)
      .build();
    this.hub = hub;
    hub.on('notification', (n: Notification) => this.zone.run(() => this.onNotification(n)));
    hub.onreconnected(() => this.zone.run(() => this.connected.set(true)));
    hub.onreconnecting(() => this.zone.run(() => this.connected.set(false)));
    hub.onclose(() => this.zone.run(() => this.connected.set(false)));
    try {
      await hub.start();
      this.connected.set(true);
    } catch (e) {
      console.warn('Real-time notifications unavailable', e);
      this.connected.set(false);
      // Retry later while still signed in.
      setTimeout(() => { if (this.hub === hub && hub.state === HubConnectionState.Disconnected) { this.hub = null; if (this.auth.signedIn()) this.start(); } }, 15000);
    }
  }

  private stop() {
    const hub = this.hub;
    this.hub = null;
    this.connected.set(false);
    if (hub) hub.stop().catch(() => undefined);
  }

  private onNotification(n: Notification) {
    this.recent.update(list => [n, ...list].slice(0, 50));
    this.events$.next(n);
    const d = DESCRIBE[n.type];
    if (!this.toastsEnabled() || (d && !d.toast)) return;
    const ref = this.snack.open(describeNotification(n), n.repositoryId ? 'Open' : 'OK', {
      duration: d?.sev === 'bad' ? 9000 : 4500,
      panelClass: d?.sev === 'bad' ? 'snack-error' : d?.sev === 'warn' ? 'snack-warn' : undefined,
      horizontalPosition: 'right', verticalPosition: 'bottom',
    });
    if (n.repositoryId) ref.onAction().subscribe(() => this.router.navigate(['/repositories', n.repositoryId]));
  }
}
