import { Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { ApiService } from '../core/api.service';
import { AuditLog, ChangeSummary } from '../core/models';
import { UI } from '../shared/ui';
import { AuditTable } from '../shared/widgets';
import { RepoContext } from './repo-context';

@Component({
  selector: 'app-activity-tab',
  imports: [FormsModule, MatFormFieldModule, MatInputModule, MatButtonModule, MatIconModule, MatPaginatorModule, AuditTable, ...UI],
  template: `
    @if (summary(); as s) {
      <section class="panel">
        <div class="panel-head"><h2>Last 24 hours</h2><span class="spacer"></span>
          <span class="small muted">{{ s.commitCount }} commits · {{ s.filesChanged }} files · +{{ s.additions }} −{{ s.deletions }}</span></div>
        <div>{{ s.narrative }}</div>
        @if (s.agents.length) { <div style="margin-top:4px">@for (a of s.agents; track a) { <span class="tag ai">{{ a }}</span> }</div> }
      </section>
    }
    <section class="panel">
      <div class="filters">
        <mat-form-field><mat-label>Action</mat-label><input matInput [(ngModel)]="action" (keyup.enter)="load(1)" placeholder="e.g. recovery"></mat-form-field>
        <mat-form-field><mat-label>Actor</mat-label><input matInput [(ngModel)]="actor" (keyup.enter)="load(1)"></mat-form-field>
        <button mat-stroked-button (click)="load(1)"><mat-icon>filter_list</mat-icon>Apply</button>
      </div>
      <app-loading [on]="loading()" />
      <app-error [error]="error()" />
      <app-audit-table [items]="items()" [showRepo]="false" />
      <mat-paginator [length]="total()" [pageIndex]="page() - 1" [pageSize]="50" [pageSizeOptions]="[]" (page)="onPage($event)" />
    </section>`,
})
export class ActivityTab implements OnInit {
  private ctx = inject(RepoContext);
  private api = inject(ApiService);
  private destroyRef = inject(DestroyRef);
  items = signal<AuditLog[]>([]);
  summary = signal<ChangeSummary | null>(null);
  total = signal(0);
  page = signal(1);
  loading = signal(false);
  error = signal<unknown>(null);
  action = ''; actor = '';

  ngOnInit() {
    this.load(1);
    this.api.changesSummary(this.ctx.id(), 24).subscribe({ next: s => this.summary.set(s), error: () => undefined });
    this.ctx.refresh$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => this.load(this.page(), true));
  }

  load(page: number, silent = false) {
    this.page.set(page);
    if (!silent) this.loading.set(true);
    this.api.audit({ repositoryId: this.ctx.id(), action: this.action.trim() || undefined, actor: this.actor.trim() || undefined, page, pageSize: 50 }).subscribe({
      next: r => { this.items.set(r.items); this.total.set(r.total); this.loading.set(false); },
      error: e => { this.error.set(e); this.loading.set(false); },
    });
  }

  onPage(e: PageEvent) { this.load(e.pageIndex + 1); }
}
