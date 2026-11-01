import { Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatSelectModule } from '@angular/material/select';
import { RouterLink } from '@angular/router';
import { ApiService } from '../core/api.service';
import { RepositorySummary, Session } from '../core/models';
import { RealtimeService } from '../core/realtime.service';
import { UI } from '../shared/ui';

@Component({
  selector: 'app-all-sessions',
  imports: [FormsModule, RouterLink, MatFormFieldModule, MatSelectModule, MatButtonModule, MatIconModule, MatPaginatorModule, ...UI],
  template: `
    <div class="page">
      <div class="page-head"><h1>AI sessions</h1><span class="muted">{{ total() }} sessions</span><span class="spacer"></span>
        <button mat-stroked-button (click)="load(page())"><mat-icon>refresh</mat-icon>Refresh</button></div>
      <div class="filters">
        <mat-form-field><mat-label>State</mat-label>
          <mat-select [(ngModel)]="state" (selectionChange)="load(1)"><mat-option value="">Any</mat-option>
            @for (s of states; track s) { <mat-option [value]="s">{{ s }}</mat-option> }</mat-select></mat-form-field>
      </div>
      <app-loading [on]="loading()" /><app-error [error]="error()" />
      @if (items().length) {
        <div class="table-wrap">
          <table class="data">
            <thead><tr><th>Session</th><th>Repository</th><th>Agent / user</th><th>State</th><th class="num">Commits</th><th class="num">Files</th><th>Push status</th><th>Tests</th><th>Last activity</th></tr></thead>
            <tbody>
              @for (s of items(); track s.id) {
                <tr>
                  <td><a [routerLink]="['/repositories', s.repositoryId]" [queryParams]="{ tab: 'sessions' }"><code>{{ s.id | sha }}</code></a>@if (s.title) {<div class="small">{{ s.title }}</div>}</td>
                  <td>{{ repoName(s.repositoryId) }}</td>
                  <td><span class="tag ai">{{ s.agentId }}</span>@if (s.initiatingUser) {<div class="small muted">for {{ s.initiatingUser }}</div>}</td>
                  <td><app-chip [value]="s.state" /></td><td class="num">{{ s.commitCount }}</td><td class="num">{{ s.filesModified }}</td>
                  <td><app-durability [state]="s.pushStatus" /></td>
                  <td>@if (s.lastTestStatus) { <app-chip [value]="s.lastTestStatus" /> } @else { — }</td>
                  <td class="nowrap">{{ s.lastActivityAt | ago }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
        <mat-paginator [length]="total()" [pageIndex]="page() - 1" [pageSize]="50" [pageSizeOptions]="[]" (page)="onPage($event)" />
      } @else if (!loading()) { <app-empty icon="smart_toy" text="No AI sessions" /> }
    </div>`,
})
export class AllSessions implements OnInit {
  private api = inject(ApiService);
  private rt = inject(RealtimeService);
  private destroyRef = inject(DestroyRef);
  states = ['Active', 'Completed', 'Merged', 'Reverted', 'Abandoned'];
  state = '';
  items = signal<Session[]>([]);
  repos = signal<RepositorySummary[]>([]);
  total = signal(0); page = signal(1);
  loading = signal(false); error = signal<unknown>(null);

  ngOnInit() {
    this.api.repositories({ pageSize: 500 }).subscribe({ next: r => this.repos.set(r.items), error: () => undefined });
    this.load(1);
    this.rt.changes(null, 1500).pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => this.load(this.page(), true));
  }
  repoName(id: string) { return this.repos().find(r => r.id === id)?.name ?? id.substring(0, 8); }
  load(page: number, silent = false) {
    this.page.set(page); if (!silent) this.loading.set(true);
    this.api.sessions({ state: this.state || undefined, page, pageSize: 50 }).subscribe({
      next: r => { this.items.set(r.items); this.total.set(r.total); this.loading.set(false); },
      error: e => { this.error.set(e); this.loading.set(false); },
    });
  }
  onPage(e: PageEvent) { this.load(e.pageIndex + 1); }
}
