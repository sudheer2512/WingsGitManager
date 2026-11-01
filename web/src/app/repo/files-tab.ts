import { Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ActivatedRoute, Router } from '@angular/router';
import { ApiService } from '../core/api.service';
import { Branch, CommitSummary, FileContent, GrepHit, TreeEntry } from '../core/models';
import { CodeViewer, DiffViewer } from '../shared/monaco';
import { UI } from '../shared/ui';
import { RepoContext } from './repo-context';

interface Node { entry: TreeEntry; depth: number; }

@Component({
  selector: 'app-files-tab',
  imports: [FormsModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatButtonModule, MatButtonToggleModule, MatIconModule, MatTooltipModule,
    CodeViewer, DiffViewer, ...UI],
  template: `
    <div class="filters">
      <mat-form-field>
        <mat-label>Branch / ref</mat-label>
        <mat-select [ngModel]="ref()" (ngModelChange)="setRef($event)">
          @for (b of branches(); track b.name) { <mat-option [value]="b.name">{{ b.name }}@if (b.isDefault) { (default) }</mat-option> }
          @if (customRef()) { <mat-option [value]="customRef()">{{ customRef() }}</mat-option> }
        </mat-select>
      </mat-form-field>
      <mat-form-field class="wide">
        <mat-label>{{ searchMode() === 'name' ? 'Find file by name' : 'Search file contents' }}</mat-label>
        <mat-icon matPrefix>search</mat-icon>
        <input matInput [(ngModel)]="query" (keyup.enter)="runSearch()" placeholder="type and press Enter">
        @if (query) { <button mat-icon-button matSuffix (click)="query = ''; hits.set(null); grepHits.set(null)" aria-label="Clear"><mat-icon>close</mat-icon></button> }
      </mat-form-field>
      <mat-button-toggle-group [value]="searchMode()" (change)="searchMode.set($event.value)" hideSingleSelectionIndicator>
        <mat-button-toggle value="name">File names</mat-button-toggle>
        <mat-button-toggle value="content">Contents</mat-button-toggle>
      </mat-button-toggle-group>
    </div>
    <app-error [error]="error()" [dismissable]="true" (dismiss)="error.set(null)" />

    <div class="split">
      <aside class="panel side">
        @if (hits(); as h) {
          <div class="panel-head"><h3>{{ h.length }} matching files</h3></div>
          @for (p of h; track p) { <div class="row-item" [class.sel]="p === file()?.path" (click)="openFile(p)"><mat-icon>description</mat-icon><span class="ellipsis">{{ p }}</span></div> }
          @empty { <div class="muted small">No files found</div> }
        } @else if (grepHits(); as g) {
          <div class="panel-head"><h3>{{ g.length }} matches</h3></div>
          @for (m of g; track $index) {
            <div class="row-item col" (click)="openFile(m.path, m.line)"><span class="ellipsis small"><b>{{ m.path }}</b>:{{ m.line }}</span><code class="ellipsis small muted">{{ m.text }}</code></div>
          } @empty { <div class="muted small">No matches</div> }
        } @else {
          <app-loading [on]="treeLoading()" />
          @for (n of visible(); track n.entry.path) {
            <div class="row-item" [style.padding-left.px]="6 + n.depth * 14" [class.sel]="n.entry.path === file()?.path" (click)="click(n.entry)">
              @if (n.entry.type === 'dir') { <mat-icon>{{ expanded().has(n.entry.path) ? 'expand_more' : 'chevron_right' }}</mat-icon><mat-icon class="dir">folder</mat-icon> }
              @else { <span style="width:18px"></span><mat-icon>{{ n.entry.type === 'submodule' ? 'link' : 'description' }}</mat-icon> }
              <span class="ellipsis">{{ n.entry.name }}</span>
            </div>
          } @empty { @if (!treeLoading()) { <app-empty icon="folder_off" text="Empty repository" /> } }
        }
      </aside>

      <section style="min-width:0">
        @if (file(); as f) {
          <div class="panel" style="padding:8px 12px">
            <div class="panel-head">
              <mat-icon>description</mat-icon><b class="mono ellipsis">{{ f.path }}</b>
              <button mat-icon-button (click)="copy(f.path)" matTooltip="Copy path"><mat-icon>content_copy</mat-icon></button>
              <span class="spacer"></span>
              <button mat-stroked-button (click)="toggleHistory()"><mat-icon>history</mat-icon>{{ showHistory() ? 'Hide versions' : 'Versions' }}</button>
              <button mat-stroked-button (click)="download(f)"><mat-icon>download</mat-icon>Download</button>
            </div>
            <div class="meta small muted">
              {{ f.size | bytes }} · {{ f.language ?? 'plaintext' }} · at <code>{{ f.commitSha | sha }}</code>
              @if (f.lastCommit; as c) {
                · last changed by <b>{{ c.authorName }}</b> {{ c.committedAt | ago }} in
                <a href="" (click)="$event.preventDefault(); goCommit(c.sha)"><code>{{ c.sha | sha }}</code> {{ c.subject }}</a>
                @if (c.agentId) { <span class="tag ai">AI · {{ c.agentId }}</span> }
              }
              @if (f.truncated) { · <span class="error">content truncated</span> }
              @if (copied()) { · <span>copied</span> }
            </div>
          </div>

          @if (showHistory()) {
            <div class="panel">
              <div class="panel-head"><h2>Versions of this file</h2><span class="spacer"></span>
                @if (compareSha()) {
                  <mat-button-toggle-group [value]="inline() ? 'inline' : 'side'" (change)="inline.set($event.value === 'inline')" hideSingleSelectionIndicator>
                    <mat-button-toggle value="side">Side by side</mat-button-toggle><mat-button-toggle value="inline">Inline</mat-button-toggle>
                  </mat-button-toggle-group>
                  <button mat-button (click)="compareSha.set(null)">Close compare</button>
                }
              </div>
              <div class="table-wrap" style="max-height:220px">
                <table class="data">
                  <thead><tr><th>Commit</th><th>When</th><th>Author</th><th>Message</th><th></th></tr></thead>
                  <tbody>
                    @for (c of history(); track c.sha) {
                      <tr [class.selected]="c.sha === compareSha()">
                        <td><a href="" (click)="$event.preventDefault(); goCommit(c.sha)"><code>{{ c.sha | sha }}</code></a></td>
                        <td class="nowrap" [title]="c.committedAt | dt">{{ c.committedAt | ago }}</td>
                        <td>{{ c.authorName }} @if (c.agentId) { <span class="tag ai">AI</span> }</td>
                        <td class="ellipsis" style="max-width:340px">{{ c.subject }}</td>
                        <td class="nowrap">
                          <button mat-button (click)="compare(c)" [disabled]="c.sha === f.commitSha && !compareSha()">Compare with current</button>
                          <button mat-button (click)="restore(c)">Restore this version</button>
                        </td>
                      </tr>
                    } @empty { <tr><td colspan="5" class="muted">No history</td></tr> }
                  </tbody>
                </table>
              </div>
            </div>
          }

          <div class="code-box" style="margin-top:8px">
            @if (compareSha()) {
              @if (compareOld() !== null) {
                <app-diff-viewer [original]="compareOld()!" [modified]="f.content ?? ''" [path]="f.path" [language]="f.language" [inline]="inline()" />
              }
            } @else if (f.isBinary) {
              <app-empty icon="hide_source" text="Binary file" hint="Use Download to get the file." />
            } @else {
              <app-code-viewer [content]="f.content ?? ''" [path]="f.path" [language]="f.language" [revealLine]="line()" />
            }
          </div>
          @if (compareSha()) { <div class="small muted" style="margin-top:4px">Left: version at <code>{{ compareSha() | sha }}</code> · Right: current version at <code>{{ ref() }}</code></div> }
        } @else {
          <div class="panel"><app-loading [on]="fileLoading()" /><app-empty icon="code" text="Select a file" hint="Browse the tree or search to open a file in the read-only viewer." /></div>
        }
      </section>
    </div>`,
  styles: [`
    .side { padding: 6px; max-height: 72vh; overflow: auto; }
    .row-item { display: flex; align-items: center; gap: 4px; padding: 3px 6px; border-radius: 5px; cursor: pointer; min-width: 0; }
    .row-item.col { flex-direction: column; align-items: flex-start; gap: 0; }
    .row-item:hover { background: var(--hover); } .row-item.sel { background: var(--selected); }
    .row-item mat-icon { font-size: 18px; width: 18px; height: 18px; color: var(--muted); flex: none; } .row-item mat-icon.dir { color: var(--series-4); }
    .meta { margin-top: 2px; }`],
})
export class FilesTab implements OnInit {
  private api = inject(ApiService);
  private ctx = inject(RepoContext);
  private route = inject(ActivatedRoute);
  private router = inject(Router);
  private destroyRef = inject(DestroyRef);

  branches = signal<Branch[]>([]);
  ref = signal('');
  customRef = signal<string | null>(null);
  children = signal(new Map<string, TreeEntry[]>());
  expanded = signal(new Set<string>());
  visible = signal<Node[]>([]);
  treeLoading = signal(false);
  file = signal<FileContent | null>(null);
  fileLoading = signal(false);
  line = signal<number | null>(null);
  error = signal<unknown>(null);
  query = '';
  searchMode = signal<'name' | 'content'>('name');
  hits = signal<string[] | null>(null);
  grepHits = signal<GrepHit[] | null>(null);
  showHistory = signal(false);
  history = signal<CommitSummary[]>([]);
  compareSha = signal<string | null>(null);
  compareOld = signal<string | null>(null);
  inline = signal(false);
  copied = signal(false);

  ngOnInit() {
    const qp = this.route.snapshot.queryParamMap;
    const repo = this.ctx.repo();
    const ref = qp.get('ref') || repo?.summary.defaultBranch || 'main';
    this.ref.set(ref);
    this.api.branches(this.ctx.id()).subscribe({
      next: b => { this.branches.set(b); if (!b.some(x => x.name === ref)) this.customRef.set(ref); },
      error: () => this.customRef.set(ref),
    });
    this.loadDir('');
    const path = qp.get('path');
    if (path) this.openFile(path);
    this.ctx.refresh$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => { this.children.set(new Map()); this.loadDir(''); for (const d of this.expanded()) this.loadDir(d); });
  }

  setRef(ref: string) {
    this.ref.set(ref);
    this.children.set(new Map()); this.expanded.set(new Set()); this.visible.set([]);
    this.loadDir('');
    const f = this.file();
    if (f) this.openFile(f.path);
    this.router.navigate([], { queryParams: { ref }, queryParamsHandling: 'merge', replaceUrl: true });
  }

  private loadDir(path: string) {
    if (!path) this.treeLoading.set(true);
    this.api.tree(this.ctx.id(), this.ref(), path || undefined).subscribe({
      next: entries => {
        const m = new Map(this.children()); m.set(path, entries); this.children.set(m);
        this.treeLoading.set(false); this.rebuild();
      },
      error: e => { this.treeLoading.set(false); this.error.set(e); },
    });
  }

  private rebuild() {
    const out: Node[] = [];
    const walk = (dir: string, depth: number) => {
      for (const e of this.children().get(dir) ?? []) {
        out.push({ entry: e, depth });
        if (e.type === 'dir' && this.expanded().has(e.path)) walk(e.path, depth + 1);
      }
    };
    walk('', 0);
    this.visible.set(out);
  }

  click(e: TreeEntry) {
    if (e.type === 'dir') {
      const s = new Set(this.expanded());
      if (s.has(e.path)) s.delete(e.path); else { s.add(e.path); if (!this.children().has(e.path)) this.loadDir(e.path); }
      this.expanded.set(s); this.rebuild();
    } else if (e.type === 'file') this.openFile(e.path);
  }

  openFile(path: string, line?: number) {
    this.fileLoading.set(true);
    this.compareSha.set(null);
    this.line.set(line ?? null);
    this.api.fileContent(this.ctx.id(), path, this.ref()).subscribe({
      next: f => {
        this.file.set(f); this.fileLoading.set(false);
        if (this.showHistory()) this.loadHistory();
        this.router.navigate([], { queryParams: { path }, queryParamsHandling: 'merge', replaceUrl: true });
      },
      error: e => { this.fileLoading.set(false); this.error.set(e); },
    });
  }

  runSearch() {
    const q = this.query.trim();
    if (!q) { this.hits.set(null); this.grepHits.set(null); return; }
    if (this.searchMode() === 'name') {
      this.grepHits.set(null);
      this.api.searchFiles(this.ctx.id(), q, this.ref()).subscribe({ next: h => this.hits.set(h), error: e => this.error.set(e) });
    } else {
      this.hits.set(null);
      this.api.grep(this.ctx.id(), q, this.ref()).subscribe({ next: h => this.grepHits.set(h), error: e => this.error.set(e) });
    }
  }

  toggleHistory() { this.showHistory.set(!this.showHistory()); if (this.showHistory()) this.loadHistory(); else this.compareSha.set(null); }

  private loadHistory() {
    const f = this.file(); if (!f) return;
    this.api.fileHistory(this.ctx.id(), f.path, this.ref()).subscribe({ next: h => this.history.set(h.items), error: e => this.error.set(e) });
  }

  compare(c: CommitSummary) {
    const f = this.file(); if (!f) return;
    this.compareSha.set(c.sha); this.compareOld.set(null);
    this.api.fileContent(this.ctx.id(), f.path, c.sha).subscribe({
      next: old => this.compareOld.set(old.content ?? ''),
      error: e => { this.compareSha.set(null); this.error.set(e); },
    });
  }

  restore(c: CommitSummary) {
    const f = this.file(); if (!f) return;
    this.ctx.recover({ type: 'RestoreFile', recoveryPoint: c.sha, paths: [f.path], label: `Restore ${f.path} to ${c.sha.substring(0, 8)}` });
  }

  goCommit(sha: string) { this.ctx.tab('history', { sha }); }

  copy(path: string) { navigator.clipboard?.writeText(path).then(() => { this.copied.set(true); setTimeout(() => this.copied.set(false), 1500); }); }

  download(f: FileContent) {
    this.api.rawFile(this.ctx.id(), f.path, this.ref()).subscribe({
      next: blob => {
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url; a.download = f.path.split('/').pop() || 'file';
        document.body.appendChild(a); a.click(); a.remove();
        setTimeout(() => URL.revokeObjectURL(url), 2000);
      },
      error: e => this.error.set(e),
    });
  }
}
