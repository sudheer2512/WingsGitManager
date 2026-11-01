import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { ApiService } from '../core/api.service';
import { Company, DEFAULT_HIGH_RISK_SETTINGS, Policy, Project, RepositorySummary } from '../core/models';
import { UI } from '../shared/ui';

type Scope = 'global' | 'company' | 'project' | 'repository';

@Component({
  selector: 'app-policies',
  imports: [FormsModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatSlideToggleModule, MatButtonModule, MatIconModule, ...UI],
  template: `
    <div class="page">
      <div class="page-head"><h1>Policies</h1><span class="muted small">Most specific scope wins: repository → project → company → global</span><span class="spacer"></span>
        <button mat-flat-button (click)="newPolicy()"><mat-icon>add</mat-icon>New policy</button></div>
      <app-error [error]="error()" [dismissable]="true" (dismiss)="error.set(null)" />
      @if (msg()) { <div class="callout good">{{ msg() }}</div> }
      <div class="split">
        <aside class="panel" style="padding:6px">
          @for (p of items(); track p.id) {
            <div class="item" [class.sel]="editing()?.id === p.id" (click)="edit(p)">
              <div class="row"><b>{{ p.key }}</b><span class="spacer"></span>@if (p.enabled) { <app-chip label="on" tone="good" icon="check" /> } @else { <app-chip label="off" tone="off" /> }</div>
              <div class="small muted">{{ p.scope }}@if (p.scopeId) { · {{ scopeName(p.scope, p.scopeId) }} }</div>
              <div class="small muted">updated {{ p.updatedAt | ago }} by {{ p.updatedBy ?? '—' }}</div>
            </div>
          } @empty { <app-empty icon="policy" text="No policies" hint="Defaults apply until a policy is saved." /> }
        </aside>
        <section class="panel">
          @if (form(); as f) {
            <div class="filters">
              <mat-form-field><mat-label>Scope</mat-label><mat-select [(ngModel)]="f.scope" (selectionChange)="f.scopeId = ''">
                @for (s of scopes; track s) { <mat-option [value]="s">{{ s }}</mat-option> }</mat-select></mat-form-field>
              @if (f.scope !== 'global') {
                <mat-form-field class="wide"><mat-label>{{ f.scope }}</mat-label><mat-select [(ngModel)]="f.scopeId">
                  @for (o of options(f.scope); track o.id) { <mat-option [value]="o.id">{{ o.name }}</mat-option> }</mat-select></mat-form-field>
              }
              <mat-form-field><mat-label>Key</mat-label><input matInput [(ngModel)]="f.key"></mat-form-field>
              <mat-slide-toggle [(ngModel)]="f.enabled">Enabled</mat-slide-toggle>
            </div>
            <div class="row" style="margin-bottom:4px"><h3>Settings JSON</h3><span class="spacer"></span>
              @if (f.key === 'high-risk') { <button mat-button (click)="loadDefaults()">Insert defaults</button> }
              <button mat-button (click)="format()">Format</button></div>
            <textarea class="json" [(ngModel)]="f.json" (ngModelChange)="validate()" spellcheck="false"></textarea>
            @if (jsonError()) { <div class="callout bad small">{{ jsonError() }}</div> }
            @else if (unknownKeys().length) { <div class="callout warn small">Unknown keys for the high-risk policy (ignored by the server): {{ unknownKeys().join(', ') }}</div> }
            @if (f.key === 'high-risk') {
              <details class="small"><summary>High-risk policy fields</summary>
                <ul>
                  <li><code>requireVerifiedRemoteCheckpoint</code> — high-risk AI changes wait until the pre-change checkpoint is verified on the remote.</li>
                  <li><code>allowAdministratorOverride</code> — administrators may override a block (audited).</li>
                  <li><code>multipleFileDeleteThreshold</code>, <code>largeChangeFileThreshold</code>, <code>mediumChangeFileThreshold</code>, <code>renameThreshold</code> — file-count thresholds.</li>
                  <li><code>sourceDirectoryDeletionIsHighRisk</code>; glob lists: <code>migrationPatterns</code>, <code>authPatterns</code>, <code>deploymentPatterns</code>, <code>sharedLibraryPatterns</code>, <code>dependencyManifests</code>; <code>highRiskCategories</code>.</li>
                </ul></details>
            }
            <div class="row" style="margin-top:8px">
              <button mat-flat-button [disabled]="!!jsonError() || !f.key.trim() || (f.scope !== 'global' && !f.scopeId)" (click)="save()"><mat-icon>save</mat-icon>Save policy</button>
              <button mat-button (click)="form.set(null); editing.set(null)">Cancel</button>
            </div>
          } @else { <app-empty icon="edit_note" text="Select a policy to edit" hint="or create a new one for a company, project or repository" /> }
        </section>
      </div>
    </div>`,
  styles: [`
    .item { padding: 8px; border-radius: 6px; cursor: pointer; border-bottom: 1px solid var(--border); } .item:hover { background: var(--hover); } .item.sel { background: var(--selected); }
    textarea.json { width: 100%; min-height: 420px; font-family: var(--mono); font-size: 12px; padding: 8px; border: 1px solid var(--border); border-radius: 6px; background: var(--surface-2); color: var(--text); resize: vertical; }`],
})
export class Policies implements OnInit {
  private api = inject(ApiService);
  scopes: Scope[] = ['global', 'company', 'project', 'repository'];
  items = signal<Policy[]>([]);
  companies = signal<Company[]>([]); projects = signal<Project[]>([]); repos = signal<RepositorySummary[]>([]);
  editing = signal<Policy | null>(null);
  form = signal<{ scope: Scope; scopeId: string; key: string; enabled: boolean; json: string } | null>(null);
  jsonError = signal<string | null>(null);
  unknownKeys = signal<string[]>([]);
  error = signal<unknown>(null); msg = signal<string | null>(null);
  private known = computed(() => new Set(Object.keys(DEFAULT_HIGH_RISK_SETTINGS).map(k => k.toLowerCase())));

  ngOnInit() {
    this.load();
    this.api.companies().subscribe({ next: c => this.companies.set(c), error: () => undefined });
    this.api.projects().subscribe({ next: p => this.projects.set(p), error: () => undefined });
    this.api.repositories({ pageSize: 500 }).subscribe({ next: r => this.repos.set(r.items), error: () => undefined });
  }
  load() { this.api.policies().subscribe({ next: p => this.items.set(p), error: e => this.error.set(e) }); }

  options(scope: Scope) {
    if (scope === 'company') return this.companies().map(c => ({ id: c.id, name: c.name }));
    if (scope === 'project') return this.projects().map(p => ({ id: p.id, name: p.name }));
    if (scope === 'repository') return this.repos().map(r => ({ id: r.id, name: `${r.companyName} / ${r.name}` }));
    return [];
  }
  scopeName(scope: string, id: string) { return this.options(scope as Scope).find(o => o.id === id)?.name ?? id.substring(0, 8); }

  edit(p: Policy) {
    this.editing.set(p); this.msg.set(null);
    let json = p.settingsJson; try { json = JSON.stringify(JSON.parse(json), null, 2); } catch { /* keep raw */ }
    this.form.set({ scope: p.scope as Scope, scopeId: p.scopeId ?? '', key: p.key, enabled: p.enabled, json });
    this.validate();
  }
  newPolicy() {
    this.editing.set(null); this.msg.set(null);
    this.form.set({ scope: 'company', scopeId: '', key: 'high-risk', enabled: true, json: JSON.stringify(DEFAULT_HIGH_RISK_SETTINGS, null, 2) });
    this.validate();
  }
  loadDefaults() { const f = this.form(); if (f) { f.json = JSON.stringify(DEFAULT_HIGH_RISK_SETTINGS, null, 2); this.validate(); } }
  format() { const f = this.form(); if (!f) return; try { f.json = JSON.stringify(JSON.parse(f.json), null, 2); } catch { /* validation shows error */ } this.validate(); }

  validate() {
    const f = this.form(); if (!f) return;
    try {
      const v = JSON.parse(f.json);
      if (typeof v !== 'object' || v === null || Array.isArray(v)) throw new Error('Settings must be a JSON object.');
      this.jsonError.set(null);
      this.unknownKeys.set(f.key === 'high-risk' ? Object.keys(v).filter(k => !this.known().has(k.toLowerCase())) : []);
    } catch (e) { this.jsonError.set('Invalid JSON: ' + (e as Error).message); this.unknownKeys.set([]); }
  }

  save() {
    const f = this.form(); if (!f) return;
    this.api.upsertPolicy({ scope: f.scope, scopeId: f.scope === 'global' ? null : f.scopeId, key: f.key.trim(), enabled: f.enabled, settingsJson: JSON.stringify(JSON.parse(f.json)) }).subscribe({
      next: p => { this.msg.set(`Policy ${p.key} saved for ${p.scope}.`); this.load(); this.edit(p); },
      error: e => this.error.set(e),
    });
  }
}
