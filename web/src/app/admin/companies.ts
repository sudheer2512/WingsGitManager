import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { ApiService } from '../core/api.service';
import { Company, Project } from '../core/models';
import { UI } from '../shared/ui';

@Component({
  selector: 'app-companies',
  imports: [FormsModule, MatFormFieldModule, MatInputModule, MatButtonModule, MatIconModule, ...UI],
  template: `
    <div class="page">
      <div class="page-head"><h1>Companies</h1></div>
      <app-error [error]="error()" [dismissable]="true" (dismiss)="error.set(null)" />
      @if (msg()) { <div class="callout good">{{ msg() }}</div> }
      <section class="panel">
        <div class="panel-head"><h2>New company</h2></div>
        <div class="filters">
          <mat-form-field class="wide"><mat-label>Name</mat-label><input matInput [(ngModel)]="name"></mat-form-field>
          <mat-form-field><mat-label>Slug (optional)</mat-label><input matInput [(ngModel)]="slug"></mat-form-field>
          <mat-form-field><mat-label>External id (optional)</mat-label><input matInput [(ngModel)]="externalId"></mat-form-field>
          <button mat-flat-button [disabled]="!name.trim()" (click)="create()"><mat-icon>add</mat-icon>Create</button>
        </div>
      </section>
      <section class="panel">
        @if (items().length) {
          <div class="table-wrap"><table class="data">
            <thead><tr><th>Name</th><th>Slug</th><th>Git namespace</th><th>External id</th><th>Active</th><th class="num">Repositories</th><th>Projects</th></tr></thead>
            <tbody>@for (c of items(); track c.id) {
              <tr><td><b>{{ c.name }}</b></td><td><code>{{ c.slug }}</code></td><td><code>{{ c.gitNamespace ?? '—' }}</code></td><td>{{ c.externalId ?? '—' }}</td>
                <td>@if (c.isActive) { <app-chip label="Active" tone="good" icon="check" /> } @else { <app-chip label="Inactive" tone="off" /> }</td>
                <td class="num">{{ c.repositoryCount }}</td>
                <td>@for (p of projectsOf(c.id); track p.id) { <span class="tag">{{ p.name }}</span> } @empty { — }</td></tr>
            }</tbody>
          </table></div>
        } @else { <app-empty icon="domain" text="No companies" /> }
      </section>
    </div>`,
})
export class Companies implements OnInit {
  private api = inject(ApiService);
  items = signal<Company[]>([]); projects = signal<Project[]>([]);
  error = signal<unknown>(null); msg = signal<string | null>(null);
  name = ''; slug = ''; externalId = '';
  ngOnInit() { this.load(); }
  load() {
    this.api.companies().subscribe({ next: c => this.items.set(c), error: e => this.error.set(e) });
    this.api.projects().subscribe({ next: p => this.projects.set(p), error: () => undefined });
  }
  projectsOf(id: string) { return this.projects().filter(p => p.companyId === id); }
  create() {
    this.api.createCompany({ name: this.name.trim(), slug: this.slug.trim() || null, externalId: this.externalId.trim() || null }).subscribe({
      next: c => { this.msg.set(`Company ${c.name} created.`); this.name = this.slug = this.externalId = ''; this.load(); },
      error: e => this.error.set(e),
    });
  }
}
