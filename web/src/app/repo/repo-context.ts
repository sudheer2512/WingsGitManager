import { Injectable, inject, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { Subject } from 'rxjs';
import { ApiService } from '../core/api.service';
import { RecoveryType, RepositoryDetail } from '../core/models';

export interface RecoveryPrefill {
  type?: RecoveryType;
  recoveryPoint?: string;
  sessionId?: string;
  commitShas?: string[];
  paths?: string[];
  label?: string;
}

/** Shared state for the repository-details page and its tabs (provided per page instance). */
@Injectable()
export class RepoContext {
  private api = inject(ApiService);
  private router = inject(Router);
  private route = inject(ActivatedRoute);

  readonly id = signal<string>('');
  readonly repo = signal<RepositoryDetail | null>(null);
  readonly error = signal<unknown>(null);
  /** Fires (debounced) when a real-time event concerns this repository. */
  readonly refresh$ = new Subject<void>();
  /** Pending pre-fill for the recovery wizard (consumed by the Recovery tab). */
  readonly recoveryPrefill = signal<RecoveryPrefill | null>(null);

  load(id: string) {
    this.id.set(id);
    this.api.repository(id).subscribe({
      next: r => { this.repo.set(r); this.error.set(null); },
      error: e => this.error.set(e),
    });
  }

  reload() { if (this.id()) this.load(this.id()); }

  tab(tab: string, extra: Record<string, string | null> = {}) {
    this.router.navigate([], { relativeTo: this.route, queryParams: { tab, ...extra }, queryParamsHandling: 'merge' });
  }

  /** Opens the recovery wizard pre-filled (e.g. "revert this session", "restore to this checkpoint"). */
  recover(prefill: RecoveryPrefill) {
    this.recoveryPrefill.set(prefill);
    this.router.navigate([], { relativeTo: this.route, queryParams: { tab: 'recovery' } });
  }
}
