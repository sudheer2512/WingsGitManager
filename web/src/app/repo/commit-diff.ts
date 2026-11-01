import { Component, effect, inject, input, signal } from '@angular/core';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatIconModule } from '@angular/material/icon';
import { catchError, forkJoin, map, of } from 'rxjs';
import { ApiService } from '../core/api.service';
import { FileChangeKind } from '../core/models';
import { DiffViewer } from '../shared/monaco';
import { UI } from '../shared/ui';

export interface DiffFile { path: string; oldPath?: string | null; kind: FileChangeKind; additions: number; deletions: number; isBinary?: boolean; patch?: string | null; }

/**
 * File list + Monaco diff for any pair of revisions. Old/new contents are loaded with files/content at `fromRef` / `toRef`.
 * When toRef is null the "new" side is the working tree, which the API cannot serve; the unified patch is shown instead.
 */
@Component({
  selector: 'app-diff-browser',
  imports: [MatButtonToggleModule, MatIconModule, DiffViewer, ...UI],
  template: `
    <div class="wrap">
      <aside class="files">
        <div class="row small muted" style="padding:2px 4px 6px">{{ files().length }} files · <span class="add">+{{ sum('additions') }}</span> <span class="del">−{{ sum('deletions') }}</span></div>
        @for (f of files(); track f.path) {
          <div class="f" [class.sel]="f.path === selected()?.path" (click)="pick(f)" [title]="f.path">
            <app-chip [value]="f.kind" [label]="f.kind.charAt(0)" />
            <span class="ellipsis mono">{{ f.path }}</span><span class="spacer"></span>
            <span class="small add">+{{ f.additions }}</span><span class="small del">−{{ f.deletions }}</span>
          </div>
        } @empty { <div class="muted small" style="padding:8px">No file changes</div> }
      </aside>
      <section style="min-width:0">
        @if (selected(); as f) {
          <div class="row" style="margin-bottom:6px">
            <b class="mono ellipsis">{{ f.oldPath && f.oldPath !== f.path ? f.oldPath + ' → ' : '' }}{{ f.path }}</b><span class="spacer"></span>
            @if (useToRef()) {
              <mat-button-toggle-group [value]="inline() ? 'inline' : 'side'" (change)="inline.set($event.value === 'inline')" hideSingleSelectionIndicator>
                <mat-button-toggle value="side">Side by side</mat-button-toggle><mat-button-toggle value="inline">Inline</mat-button-toggle>
              </mat-button-toggle-group>
            } @else { <span class="small muted">uncommitted (working tree) — unified diff</span> }
          </div>
          <app-loading [on]="loading()" />
          <app-error [error]="error()" />
          <div class="code-box" [class.short]="short()">
            @if (f.isBinary) { <app-empty icon="hide_source" text="Binary file changed" /> }
            @else if (useToRef()) {
              @if (pair(); as p) { <app-diff-viewer [original]="p.old" [modified]="p.new" [path]="f.path" [inline]="inline()" [patch]="f.patch ?? null" /> }
            } @else { <pre class="fallback">{{ f.patch ?? 'No patch available' }}</pre> }
          </div>
        } @else { <app-empty icon="difference" text="Select a file to see its changes" /> }
      </section>
    </div>`,
  styles: [`
    .wrap { display: grid; grid-template-columns: minmax(220px, 300px) 1fr; gap: 10px; }
    @media (max-width: 900px) { .wrap { grid-template-columns: 1fr; } }
    .files { max-height: 62vh; overflow: auto; border: 1px solid var(--border); border-radius: 6px; padding: 4px; background: var(--surface); }
    .f { display: flex; gap: 6px; align-items: center; padding: 3px 4px; border-radius: 4px; cursor: pointer; min-width: 0; }
    .f:hover { background: var(--hover); } .f.sel { background: var(--selected); }
    .add { color: var(--good); } .del { color: var(--bad); }`],
})
export class DiffBrowser {
  private api = inject(ApiService);
  repoId = input.required<string>();
  files = input<DiffFile[]>([]);
  /** Revision for the "before" side (null = the file did not exist / empty tree). */
  fromRef = input<string | null>(null);
  /** Revision for the "after" side (null = working tree; the unified patch is shown). */
  toRef = input<string | null>(null);
  short = input(false);
  selected = signal<DiffFile | null>(null);
  pair = signal<{ old: string; new: string } | null>(null);
  loading = signal(false);
  error = signal<unknown>(null);
  inline = signal(false);

  constructor() {
    effect(() => {
      const files = this.files();
      this.fromRef(); this.toRef();
      const cur = this.selected();
      const next = files.find(f => f.path === cur?.path) ?? files[0] ?? null;
      queueMicrotask(() => { if (next) this.pick(next); else { this.selected.set(null); this.pair.set(null); } });
    });
  }

  useToRef() { return !!this.toRef(); }
  sum(k: 'additions' | 'deletions') { return this.files().reduce((s, f) => s + (f[k] ?? 0), 0); }

  pick(f: DiffFile) {
    this.selected.set(f);
    this.pair.set(null);
    this.error.set(null);
    if (f.isBinary || !this.toRef()) return;
    const id = this.repoId(), from = this.fromRef(), to = this.toRef()!;
    const content = (ref: string | null, path: string | null | undefined) =>
      !ref || !path ? of('') : this.api.fileContent(id, path, ref).pipe(map(c => c.content ?? ''), catchError(() => of('')));
    this.loading.set(true);
    forkJoin({
      old: f.kind === 'Added' ? of('') : content(from, f.oldPath || f.path),
      new: f.kind === 'Deleted' ? of('') : content(to, f.path),
    }).subscribe({
      next: p => { if (this.selected() === f) this.pair.set(p); this.loading.set(false); },
      error: e => { this.error.set(e); this.loading.set(false); },
    });
  }
}
