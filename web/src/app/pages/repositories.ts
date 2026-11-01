import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatMenuModule } from '@angular/material/menu';
import { MatSelectModule } from '@angular/material/select';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Router, RouterLink } from '@angular/router';
import { Subject, debounceTime, forkJoin } from 'rxjs';
import { ApiService } from '../core/api.service';
import { Company, Project, RepositorySummary } from '../core/models';
import { RealtimeService } from '../core/realtime.service';
import { UI } from '../shared/ui';

interface CompanyNode { company: Company; projects: { project: Project; repos: RepositorySummary[] }[]; }

@Component({
  selector: 'app-repositories',
  imports: [FormsModule, RouterLink, MatFormFieldModule, MatInputModule, MatButtonModule, MatIconModule, MatMenuModule, MatSelectModule, MatTooltipModule, ...UI],
  template: `
    <div class="page">
      <div class="page-head"><h1>Repositories</h1><span class="muted">{{ total() }} total</span><span class="spacer"></span>
        <button mat-stroked-button (click)="load()"><mat-icon>refresh</mat-icon>Refresh</button>
      </div>
      <div class="split">
        <aside class="panel tree">
          <h3 style="margin-bottom:6px">Companies &amp; projects</h3>
          <div class="node" [class.sel]="!companyId() && !projectId()" (click)="select(null, null)"><mat-icon>apps</mat-icon>All repositories</div>
          @for (c of tree(); track c.company.id) {
            <div class="node" [class.sel]="companyId() === c.company.id && !projectId()" (click)="toggle(c.company.id); select(c.company.id, null)">
              <mat-icon>{{ expanded().has(c.company.id) ? 'expand_more' : 'chevron_right' }}</mat-icon><mat-icon>domain</mat-icon>
              <span class="ellipsis">{{ c.company.name }}</span><span class="spacer"></span><span class="small muted">{{ c.company.repositoryCount }}</span>
            </div>
            @if (expanded().has(c.company.id)) {
              @for (p of c.projects; track p.project.id) {
                <div class="node l2" [class.sel]="projectId() === p.project.id" (click)="select(c.company.id, p.project.id)">
                  <mat-icon>folder</mat-icon><span class="ellipsis">{{ p.project.name }}</span><span class="spacer"></span><span class="small muted">{{ p.repos.length }}</span>
                </div>
                @for (r of p.repos; track r.id) {
                  <a class="node l3" [routerLink]="['/repositories', r.id]"><mat-icon>source</mat-icon><span class="ellipsis">{{ r.name }}</span></a>
                }
              } @empty { <div class="node l2 muted small">No projects</div> }
            }
          } @empty { <div class="muted small" style="padding:8px">No companies visible</div> }
        </aside>

        <section style="min-width:0">
          <div class="filters">
            <mat-form-field class="wide">
              <mat-label>Search repositories</mat-label>
              <mat-icon matPrefix>search</mat-icon>
              <input matInput [ngModel]="search()" (ngModelChange)="search.set($event); search$.next()" placeholder="name or project">
            </mat-form-field>
            <mat-form-field>
              <mat-label>State</mat-label>
              <mat-select [ngModel]="state()" (ngModelChange)="state.set($event); load()">
                <mat-option value="">Any state</mat-option>
                @for (s of states; track s) { <mat-option [value]="s">{{ s }}</mat-option> }
              </mat-select>
            </mat-form-field>
          </div>
          <app-loading [on]="loading()" />
          <app-error [error]="error()" />
          @if (repos().length) {
            <div class="table-wrap">
              <table class="data">
                <thead><tr>
                  <th>Name</th><th>Company</th><th>Project</th><th>Default branch</th><th>Last commit</th><th>Last push</th>
                  <th>Remote status</th><th>Latest checkpoint</th><th class="num">Storage</th><th></th>
                </tr></thead>
                <tbody>
                  @for (r of repos(); track r.id) {
                    <tr class="clickable" (click)="open(r)">
                      <td><a [routerLink]="['/repositories', r.id]" (click)="$event.stopPropagation()"><b>{{ r.name }}</b></a>
                        @if (r.state !== 'Active') { <app-chip [value]="r.state" /> }
                        @if (r.hasUncommittedChanges) { <mat-icon class="flag" matTooltip="Uncommitted changes in the working directory">edit_note</mat-icon> }
                      </td>
                      <td>{{ r.companyName }}</td>
                      <td>{{ r.projectName }}</td>
                      <td><code>{{ r.defaultBranch }}</code></td>
                      <td class="nowrap" [title]="r.lastCommitAt | dt"><code>{{ r.lastCommitSha | sha }}</code> · {{ r.lastCommitAt | ago }}</td>
                      <td class="nowrap" [title]="r.lastPushAt | dt">{{ r.lastPushAt | ago }}@if (r.pendingPushes) { <span class="tag">{{ r.pendingPushes }} pending</span> }</td>
                      <td><app-chip [value]="r.remoteStatus" /></td>
                      <td><app-durability [state]="r.latestCheckpointDurability" /></td>
                      <td class="num">{{ r.storageBytes | bytes }}</td>
                      <td class="nowrap" (click)="$event.stopPropagation()">
                        <button mat-icon-button [matMenuTriggerFor]="menu" aria-label="Actions"><mat-icon>more_vert</mat-icon></button>
                        <mat-menu #menu="matMenu">
                          <a mat-menu-item [routerLink]="['/repositories', r.id]" [queryParams]="{ tab: 'files' }"><mat-icon>folder_open</mat-icon>Browse files</a>
                          <a mat-menu-item [routerLink]="['/repositories', r.id]" [queryParams]="{ tab: 'history' }"><mat-icon>history</mat-icon>Commit history</a>
                          <a mat-menu-item [routerLink]="['/repositories', r.id]" [queryParams]="{ tab: 'recovery' }"><mat-icon>restore</mat-icon>Recover code</a>
                          <a mat-menu-item [routerLink]="['/repositories', r.id]" [queryParams]="{ tab: 'backups' }"><mat-icon>backup</mat-icon>Backups</a>
                          <button mat-menu-item (click)="push(r)"><mat-icon>cloud_upload</mat-icon>Push now</button>
                          <a mat-menu-item [routerLink]="['/repositories', r.id]" [queryParams]="{ tab: 'settings' }"><mat-icon>settings</mat-icon>Settings</a>
                        </mat-menu>
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          } @else if (!loading() && !error()) {
            <app-empty icon="source" text="No repositories match" hint="Repositories are created automatically when Wings Code creates a project." />
          }
          @if (message()) { <div class="callout good">{{ message() }}</div> }
        </section>
      </div>
    </div>`,
  styles: [`
    .tree { padding: 8px; max-height: calc(100vh - 140px); overflow: auto; }
    .node { display: flex; align-items: center; gap: 4px; padding: 4px 6px; border-radius: 6px; cursor: pointer; color: var(--text); text-decoration: none; min-width: 0; }
    .node:hover { background: var(--hover); } .node.sel { background: var(--selected); font-weight: 600; }
    .node mat-icon { font-size: 18px; width: 18px; height: 18px; color: var(--muted); flex: none; }
    .l2 { padding-left: 26px; } .l3 { padding-left: 48px; font-size: 12px; }
    .flag { font-size: 16px; width: 16px; height: 16px; vertical-align: middle; color: var(--warn); }`],
})
export class Repositories implements OnInit {
  private api = inject(ApiService);
  private router = inject(Router);
  private rt = inject(RealtimeService);
  private destroyRef = inject(DestroyRef);
  states = ['Provisioning', 'Active', 'Archived', 'Deleted', 'Failed'];
  companies = signal<Company[]>([]);
  projects = signal<Project[]>([]);
  allRepos = signal<RepositorySummary[]>([]);
  repos = signal<RepositorySummary[]>([]);
  total = signal(0);
  companyId = signal<string | null>(null);
  projectId = signal<string | null>(null);
  search = signal('');
  state = signal('');
  expanded = signal(new Set<string>());
  loading = signal(false);
  error = signal<unknown>(null);
  message = signal<string | null>(null);
  search$ = new Subject<void>();

  tree = computed<CompanyNode[]>(() => this.companies().map(company => ({
    company,
    projects: this.projects().filter(p => p.companyId === company.id)
      .map(project => ({ project, repos: this.allRepos().filter(r => r.projectId === project.id) })),
  })));

  ngOnInit() {
    forkJoin([this.api.companies(), this.api.projects(), this.api.repositories({ pageSize: 500 })]).subscribe({
      next: ([c, p, r]) => {
        this.companies.set(c); this.projects.set(p); this.allRepos.set(r.items);
        if (c.length === 1) this.expanded.set(new Set([c[0].id]));
      },
      error: e => this.error.set(e),
    });
    this.load();
    this.search$.pipe(debounceTime(300), takeUntilDestroyed(this.destroyRef)).subscribe(() => this.load());
    this.rt.changes(null, 1500).pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => this.load(true));
  }

  toggle(id: string) {
    const s = new Set(this.expanded());
    if (s.has(id) && this.companyId() === id && !this.projectId()) s.delete(id); else s.add(id);
    this.expanded.set(s);
  }

  select(companyId: string | null, projectId: string | null) { this.companyId.set(companyId); this.projectId.set(projectId); this.load(); }

  load(silent = false) {
    if (!silent) this.loading.set(true);
    this.api.repositories({
      companyId: this.companyId() ?? undefined, projectId: this.projectId() ?? undefined, search: this.search().trim() || undefined,
      state: this.state() || undefined, pageSize: 200,
    }).subscribe({
      next: r => { this.repos.set(r.items); this.total.set(r.total); this.loading.set(false); this.error.set(null); },
      error: e => { this.error.set(e); this.loading.set(false); },
    });
  }

  open(r: RepositorySummary) { this.router.navigate(['/repositories', r.id]); }

  push(r: RepositorySummary) {
    this.message.set(null);
    this.api.push(r.id).subscribe({
      next: s => { this.message.set(`${r.name}: push finished — remote status ${s.remoteStatus}, ${s.pending} pending, ${s.failed} failed.`); this.load(true); },
      error: e => this.error.set(e),
    });
  }
}
