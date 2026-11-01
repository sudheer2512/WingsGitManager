import { AfterViewInit, Component, ElementRef, Injectable, NgZone, OnDestroy, effect, inject, input, signal, viewChild } from '@angular/core';

/* Monaco is shipped as static assets (node_modules/monaco-editor/min/vs -> /assets/monaco/vs) and loaded at runtime with
   its own AMD loader, so it is not part of the Angular bundle. Components fall back to a <pre> when loading fails. */

declare global {
  interface Window { monaco?: any; require?: any; MonacoEnvironment?: any; }
}

@Injectable({ providedIn: 'root' })
export class MonacoLoader {
  private promise: Promise<any> | null = null;
  readonly failed = signal(false);

  load(): Promise<any> {
    if (window.monaco) return Promise.resolve(window.monaco);
    if (this.promise) return this.promise;
    const base = new URL('assets/monaco/vs', document.baseURI).href;
    this.promise = new Promise((resolve, reject) => {
      const timeout = setTimeout(() => reject(new Error('Monaco load timed out')), 20000);
      const boot = () => {
        const req = window.require;
        if (!req?.config) { clearTimeout(timeout); reject(new Error('Monaco AMD loader not available')); return; }
        req.config({ paths: { vs: base } });
        window.MonacoEnvironment = {
          getWorkerUrl: () =>
            `data:text/javascript;charset=utf-8,${encodeURIComponent(`self.MonacoEnvironment={baseUrl:'${base}/../'};importScripts('${base}/base/worker/workerMain.js');`)}`,
        };
        req(['vs/editor/editor.main'], () => { clearTimeout(timeout); resolve(window.monaco); }, (e: unknown) => { clearTimeout(timeout); reject(e); });
      };
      if (window.require?.config) { boot(); return; }
      const s = document.createElement('script');
      s.src = `${base}/loader.js`;
      s.onload = boot;
      s.onerror = () => { clearTimeout(timeout); reject(new Error('Could not load Monaco loader.js')); };
      document.head.appendChild(s);
    }).catch(e => { this.failed.set(true); this.promise = null; throw e; });
    return this.promise;
  }

  theme(): string {
    return window.matchMedia?.('(prefers-color-scheme: dark)').matches ? 'vs-dark' : 'vs';
  }
}

/** Language id for Monaco from the API's `language` or the file extension. */
export function monacoLanguage(path: string | null | undefined, apiLanguage?: string | null): string {
  if (apiLanguage && apiLanguage !== 'plaintext') return apiLanguage;
  const ext = (path ?? '').split('.').pop()?.toLowerCase() ?? '';
  const map: Record<string, string> = {
    ts: 'typescript', tsx: 'typescript', js: 'javascript', jsx: 'javascript', mjs: 'javascript', json: 'json', cs: 'csharp', csproj: 'xml',
    xml: 'xml', html: 'html', htm: 'html', css: 'css', scss: 'scss', less: 'less', md: 'markdown', py: 'python', java: 'java', go: 'go',
    rs: 'rust', sql: 'sql', yml: 'yaml', yaml: 'yaml', sh: 'shell', ps1: 'powershell', rb: 'ruby', php: 'php', kt: 'kotlin', swift: 'swift',
    c: 'c', h: 'c', cpp: 'cpp', hpp: 'cpp', dockerfile: 'dockerfile', razor: 'razor', cshtml: 'razor', ini: 'ini', toml: 'ini',
  };
  if ((path ?? '').toLowerCase().endsWith('dockerfile')) return 'dockerfile';
  return map[ext] ?? 'plaintext';
}

/** Read-only, syntax-highlighted code viewer. */
@Component({
  selector: 'app-code-viewer',
  template: `
    @if (fallback()) { <pre class="fallback">{{ content() }}</pre> }
    <div #host class="host" [style.display]="fallback() ? 'none' : 'block'"></div>`,
  styles: [`:host { display: block; height: 100%; } .host { height: 100%; width: 100%; }`],
})
export class CodeViewer implements AfterViewInit, OnDestroy {
  private loader = inject(MonacoLoader);
  private zone = inject(NgZone);
  content = input<string>('');
  path = input<string | null>(null);
  language = input<string | null | undefined>(null);
  revealLine = input<number | null>(null);
  host = viewChild.required<ElementRef<HTMLDivElement>>('host');
  fallback = signal(false);
  private editor: any;
  private model: any;

  constructor() {
    effect(() => {
      const content = this.content(), lang = monacoLanguage(this.path(), this.language()), line = this.revealLine();
      if (!this.editor) return;
      const m = window.monaco;
      this.model?.dispose();
      this.model = m.editor.createModel(content ?? '', lang);
      this.editor.setModel(this.model);
      if (line) { this.editor.revealLineInCenter(line); this.editor.setSelection(new m.Range(line, 1, line, 1 + 9999)); }
    });
  }

  ngAfterViewInit() {
    this.loader.load().then(m => this.zone.runOutsideAngular(() => {
      this.model = m.editor.createModel(this.content() ?? '', monacoLanguage(this.path(), this.language()));
      this.editor = m.editor.create(this.host().nativeElement, {
        model: this.model, readOnly: true, automaticLayout: true, minimap: { enabled: false }, scrollBeyondLastLine: false,
        fontSize: 12.5, theme: this.loader.theme(), renderWhitespace: 'selection', domReadOnly: true,
      });
      const line = this.revealLine();
      if (line) this.editor.revealLineInCenter(line);
    })).catch(() => this.fallback.set(true));
  }

  ngOnDestroy() { this.editor?.dispose(); this.model?.dispose(); }
}

/** Monaco diff editor (side-by-side or inline) with a unified-patch <pre> fallback. */
@Component({
  selector: 'app-diff-viewer',
  template: `
    @if (fallback()) {
      @if (patch()) { <pre class="fallback">{{ patch() }}</pre> }
      @else {
        <div class="fb2"><div><div class="small muted">Before</div><pre class="fallback">{{ original() }}</pre></div>
        <div><div class="small muted">After</div><pre class="fallback">{{ modified() }}</pre></div></div>
      }
    }
    <div #host class="host" [style.display]="fallback() ? 'none' : 'block'"></div>`,
  styles: [`:host { display: block; height: 100%; } .host { height: 100%; width: 100%; }
    .fb2 { display: grid; grid-template-columns: 1fr 1fr; gap: 6px; height: 100%; } .fb2 pre { height: calc(100% - 20px); }`],
})
export class DiffViewer implements AfterViewInit, OnDestroy {
  private loader = inject(MonacoLoader);
  private zone = inject(NgZone);
  original = input<string>('');
  modified = input<string>('');
  path = input<string | null>(null);
  language = input<string | null | undefined>(null);
  inline = input(false);
  patch = input<string | null>(null);
  host = viewChild.required<ElementRef<HTMLDivElement>>('host');
  fallback = signal(false);
  private editor: any;
  private models: any[] = [];

  constructor() {
    effect(() => {
      const o = this.original(), mo = this.modified(), lang = monacoLanguage(this.path(), this.language());
      if (this.editor) this.setModels(o, mo, lang);
    });
    effect(() => {
      const inline = this.inline();
      this.editor?.updateOptions({ renderSideBySide: !inline });
    });
  }

  private setModels(o: string, mo: string, lang: string) {
    const m = window.monaco;
    const old = this.models;
    this.models = [m.editor.createModel(o ?? '', lang), m.editor.createModel(mo ?? '', lang)];
    this.editor.setModel({ original: this.models[0], modified: this.models[1] });
    old.forEach(x => x.dispose());
  }

  ngAfterViewInit() {
    this.loader.load().then(m => this.zone.runOutsideAngular(() => {
      this.editor = m.editor.createDiffEditor(this.host().nativeElement, {
        readOnly: true, originalEditable: false, automaticLayout: true, renderSideBySide: !this.inline(), minimap: { enabled: false },
        scrollBeyondLastLine: false, fontSize: 12.5, theme: this.loader.theme(), ignoreTrimWhitespace: false,
        hideUnchangedRegions: { enabled: true, contextLineCount: 4, minimumLineCount: 6 }, renderSideBySideInlineBreakpoint: 560,
      });
      this.setModels(this.original(), this.modified(), monacoLanguage(this.path(), this.language()));
    })).catch(() => this.fallback.set(true));
  }

  ngOnDestroy() { this.editor?.dispose(); this.models.forEach(x => x.dispose()); }
}
