import { Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatSelectModule } from '@angular/material/select';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ApiService } from '../core/api.service';
import { Checkpoint } from '../core/models';
import { UI, isRemoteSafe } from '../shared/ui';
import { RepoContext } from './repo-context';

@Component({
  selector: 'app-checkpoints-tab',
  imports: [FormsModule, MatFormFieldModule, MatSelectModule, MatButtonModule, MatIconModule, MatPaginatorModule, MatTooltipModule, ...UI],
  template: `
    <div class="callout small">
      Checkpoints are restore points created before and after every AI change. Only <b>On remote</b> and <b>Backed up</b> checkpoints are safe if this
      machine is lost; <b>Local only</b>, <b>Push pending</b>, <b>Push failed</b> and <b>Diverged</b> checkpoints (dashed outline) exist only locally.
    </div>
    <div class="filters">
      <mat-form-field><mat-label>Type</mat-label>
        <mat-select [(ngModel)]="type" (selectionChange)="load(1)">
          <mat-option value="">Any</mat-option>
          @for (t of types; track t) { <mat-option [value]="t">{{ t }}</mat-option> }
        </mat-select></mat-form-field>
    </div>
    <app-loading [on]="loading()" />
    <app-error [error]="error()" />
    @if (items().length) {
      <div class="table-wrap">
        <table class="data">
          <thead><tr><th>Created</th><th>Type</th><th>Commit</th><th>Description</th><th>Sync state</th><th>Backup state</th><th>Build / tests</th><th>Agent</th><th></th></tr></thead>
          <tbody>
            @for (c of items(); track c.id) {
              <tr [class.unsafe]="!safe(c.syncState)">
                <td class="nowrap" [title]="c.createdAt | dt">{{ c.createdAt | ago }}</td>
                <td><span class="tag">{{ c.type | human }}</span></td>
                <td class="nowrap"><a href="" (click)="$event.preventDefault(); ctx.tab('history', { sha: c.commitSha })"><code>{{ c.commitSha | sha }}</code></a><div class="small muted"><code>{{ c.branch }}</code></div></td>
                <td style="max-width:320px"><div class="ellipsis" [title]="c.description ?? ''">{{ c.description ?? '—' }}</div></td>
                <td><app-durability [state]="c.syncState" />@if (c.remoteVerifiedAt) { <div class="small muted">verified {{ c.remoteVerifiedAt | ago }}</div> }</td>
                <td><app-durability [state]="c.backupState" /></td>
                <td>
                  @if (c.buildStatus) { <app-chip [value]="c.buildStatus" [label]="'Build ' + c.buildStatus" /> }
                  @if (c.testStatus) { <app-chip [value]="c.testStatus" [label]="'Tests ' + c.testStatus" /> }
                  @if (!c.buildStatus && !c.testStatus) { <span class="muted">—</span> }
                </td>
                <td>@if (c.agentId) { <span class="tag ai">{{ c.agentId }}</span> } @else { {{ c.userId ?? '—' }} }</td>
                <td class="nowrap"><button mat-stroked-button (click)="restore(c)" [matTooltip]="safe(c.syncState) ? 'Preview restoring the project to this checkpoint' : 'Warning: this checkpoint is not verified on the remote'">
                  <mat-icon>restore</mat-icon>Restore to here</button></td>
              </tr>
            }
          </tbody>
        </table>
      </div>
      <mat-paginator [length]="total()" [pageIndex]="page() - 1" [pageSize]="50" [pageSizeOptions]="[]" (page)="onPage($event)" />
    } @else if (!loading()) { <app-empty icon="flag" text="No checkpoints" /> }`,
  styles: [`tr.unsafe td:first-child { box-shadow: inset 3px 0 0 var(--warn); }`],
})
export class CheckpointsTab implements OnInit {
  ctx = inject(RepoContext);
  private api = inject(ApiService);
  private destroyRef = inject(DestroyRef);
  types = ['Initial', 'PreChange', 'PostChange', 'Recovery', 'Merge', 'Manual', 'External'];
  type = '';
  items = signal<Checkpoint[]>([]);
  total = signal(0);
  page = signal(1);
  loading = signal(false);
  error = signal<unknown>(null);
  safe = isRemoteSafe;

  ngOnInit() {
    this.load(1);
    this.ctx.refresh$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => this.load(this.page(), true));
  }

  load(page: number, silent = false) {
    this.page.set(page);
    if (!silent) this.loading.set(true);
    this.api.checkpoints(this.ctx.id(), { type: this.type || undefined, page, pageSize: 50 }).subscribe({
      next: r => { this.items.set(r.items); this.total.set(r.total); this.loading.set(false); },
      error: e => { this.error.set(e); this.loading.set(false); },
    });
  }

  onPage(e: PageEvent) { this.load(e.pageIndex + 1); }

  restore(c: Checkpoint) {
    this.ctx.recover({ type: 'RestoreProject', recoveryPoint: `checkpoint:${c.id}`, label: `Restore project to checkpoint ${c.commitSha.substring(0, 8)} (${c.type}${c.description ? ': ' + c.description : ''})` });
  }
}
