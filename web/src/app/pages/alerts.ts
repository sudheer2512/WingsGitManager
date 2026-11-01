import { Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatIconModule } from '@angular/material/icon';
import { RouterLink } from '@angular/router';
import { ApiService } from '../core/api.service';
import { Alert } from '../core/models';
import { RealtimeService } from '../core/realtime.service';
import { UI } from '../shared/ui';

@Component({
  selector: 'app-alerts',
  imports: [FormsModule, RouterLink, MatButtonModule, MatCheckboxModule, MatIconModule, ...UI],
  template: `
    <div class="page">
      <div class="page-head"><h1>Alerts</h1><span class="spacer"></span>
        <mat-checkbox [(ngModel)]="includeResolved" (change)="load()">Show resolved</mat-checkbox>
        <button mat-stroked-button (click)="load()"><mat-icon>refresh</mat-icon>Refresh</button></div>
      <app-loading [on]="loading()" /><app-error [error]="error()" [dismissable]="true" (dismiss)="error.set(null)" />
      @if (items().length) {
        <div class="table-wrap">
          <table class="data">
            <thead><tr><th>Severity</th><th>Message</th><th>Category</th><th>Repository</th><th>First seen</th><th>Last seen</th><th></th></tr></thead>
            <tbody>
              @for (a of items(); track a.id) {
                <tr [style.opacity]="a.resolvedAt ? .6 : 1">
                  <td><app-chip [value]="a.severity" /></td>
                  <td>{{ a.message }}</td>
                  <td><span class="tag">{{ a.category }}</span></td>
                  <td>@if (a.repositoryId) { <a [routerLink]="['/repositories', a.repositoryId]">{{ a.repositoryName ?? 'open' }}</a> } @else { — }</td>
                  <td class="nowrap" [title]="a.createdAt | dt">{{ a.createdAt | ago }}</td>
                  <td class="nowrap" [title]="a.lastSeenAt | dt">{{ a.lastSeenAt | ago }}</td>
                  <td>@if (!a.resolvedAt) { <button mat-button (click)="resolve(a)">Resolve</button> } @else { <span class="small muted">resolved {{ a.resolvedAt | ago }}</span> }</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      } @else if (!loading()) { <app-empty icon="task_alt" text="No alerts" hint="Push failures, backup verification failures, storage and divergence problems appear here." /> }
    </div>`,
})
export class Alerts implements OnInit {
  private api = inject(ApiService);
  private rt = inject(RealtimeService);
  private destroyRef = inject(DestroyRef);
  includeResolved = false;
  items = signal<Alert[]>([]);
  loading = signal(false); error = signal<unknown>(null);
  ngOnInit() {
    this.load();
    this.rt.changes(null, 1500).pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => this.load(true));
  }
  load(silent = false) {
    if (!silent) this.loading.set(true);
    this.api.alerts(this.includeResolved).subscribe({ next: a => { this.items.set(a); this.loading.set(false); }, error: e => { this.error.set(e); this.loading.set(false); } });
  }
  resolve(a: Alert) { this.api.resolveAlert(a.id).subscribe({ next: () => this.load(), error: e => this.error.set(e) }); }
}
