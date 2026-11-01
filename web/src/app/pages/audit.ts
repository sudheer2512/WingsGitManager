import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatSelectModule } from '@angular/material/select';
import { ApiService } from '../core/api.service';
import { AuditLog, Company, RepositorySummary } from '../core/models';
import { UI } from '../shared/ui';
import { AuditTable } from '../shared/widgets';

@Component({
  selector: 'app-audit',
  imports: [FormsModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatButtonModule, MatIconModule, MatPaginatorModule, AuditTable, ...UI],
  template: `
    <div class="page">
      <div class="page-head"><h1>Audit log</h1><span class="muted">{{ total() }} entries</span></div>
      <div class="filters">
        <mat-form-field><mat-label>Company</mat-label><mat-select [(ngModel)]="f.companyId" (selectionChange)="load(1)">
          <mat-option value="">All</mat-option>@for (c of companies(); track c.id) { <mat-option [value]="c.id">{{ c.name }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>Repository</mat-label><mat-select [(ngModel)]="f.repositoryId" (selectionChange)="load(1)">
          <mat-option value="">All</mat-option>@for (r of repos(); track r.id) { <mat-option [value]="r.id">{{ r.name }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>Action</mat-label><input matInput [(ngModel)]="f.action" (keyup.enter)="load(1)" placeholder="e.g. change.begin"></mat-form-field>
        <mat-form-field><mat-label>Actor</mat-label><input matInput [(ngModel)]="f.actor" (keyup.enter)="load(1)"></mat-form-field>
        <mat-form-field><mat-label>Outcome</mat-label><mat-select [(ngModel)]="f.outcome" (selectionChange)="load(1)">
          <mat-option value="">Any</mat-option><mat-option value="success">success</mat-option><mat-option value="failure">failure</mat-option><mat-option value="denied">denied</mat-option></mat-select></mat-form-field>
        <mat-form-field><mat-label>From</mat-label><input matInput type="date" [(ngModel)]="f.from" (change)="load(1)"></mat-form-field>
        <mat-form-field><mat-label>To</mat-label><input matInput type="date" [(ngModel)]="f.to" (change)="load(1)"></mat-form-field>
        <button mat-stroked-button (click)="load(1)"><mat-icon>filter_list</mat-icon>Apply</button>
      </div>
      <app-loading [on]="loading()" /><app-error [error]="error()" />
      <app-audit-table [items]="items()" />
      <mat-paginator [length]="total()" [pageIndex]="page() - 1" [pageSize]="100" [pageSizeOptions]="[]" (page)="onPage($event)" />
    </div>`,
})
export class AuditPage implements OnInit {
  private api = inject(ApiService);
  companies = signal<Company[]>([]); repos = signal<RepositorySummary[]>([]);
  items = signal<AuditLog[]>([]); total = signal(0); page = signal(1);
  loading = signal(false); error = signal<unknown>(null);
  f = { companyId: '', repositoryId: '', action: '', actor: '', outcome: '', from: '', to: '' };
  ngOnInit() {
    this.api.companies().subscribe({ next: c => this.companies.set(c), error: () => undefined });
    this.api.repositories({ pageSize: 500 }).subscribe({ next: r => this.repos.set(r.items), error: () => undefined });
    this.load(1);
  }
  load(page: number) {
    this.page.set(page); this.loading.set(true);
    const f = this.f;
    this.api.audit({
      companyId: f.companyId || undefined, repositoryId: f.repositoryId || undefined, action: f.action.trim() || undefined, actor: f.actor.trim() || undefined,
      outcome: f.outcome || undefined, from: f.from ? new Date(f.from + 'T00:00:00').toISOString() : undefined,
      to: f.to ? new Date(f.to + 'T23:59:59').toISOString() : undefined, page, pageSize: 100,
    }).subscribe({ next: r => { this.items.set(r.items); this.total.set(r.total); this.loading.set(false); }, error: e => { this.error.set(e); this.loading.set(false); } });
  }
  onPage(e: PageEvent) { this.load(e.pageIndex + 1); }
}
