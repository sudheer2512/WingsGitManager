import { Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatRadioModule } from '@angular/material/radio';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ApiService } from '../core/api.service';
import { Backup, Job } from '../core/models';
import { UI } from '../shared/ui';
import { JobTracker } from '../shared/widgets';
import { RepoContext } from './repo-context';

@Component({
  selector: 'app-backups-tab',
  imports: [FormsModule, MatButtonModule, MatIconModule, MatFormFieldModule, MatInputModule, MatRadioModule, MatCheckboxModule, MatTooltipModule, JobTracker, ...UI],
  template: `
    <div class="filters">
      <button mat-flat-button (click)="create()" [disabled]="busy()"><mat-icon>backup</mat-icon>Create backup now</button>
      <button mat-stroked-button (click)="load()"><mat-icon>refresh</mat-icon>Refresh</button>
      <span class="small muted">Independent, encrypted Git bundles stored outside the Git server. Verification checks checksum, bundle, fsck, refs and a test restore.</span>
    </div>
    <app-error [error]="error()" [dismissable]="true" (dismiss)="error.set(null)" />
    @for (j of jobs(); track j) { <app-job [jobId]="j" (done)="jobDone($event)" /> }

    @if (items().length) {
      <div class="table-wrap">
        <table class="data">
          <thead><tr><th>Created</th><th>State</th><th>Kind / trigger</th><th class="num">Size</th><th>Encrypted</th><th>Head</th><th>Verified</th><th>Verification</th><th></th></tr></thead>
          <tbody>
            @for (b of items(); track b.id) {
              <tr [class.selected]="restoreFor()?.id === b.id">
                <td class="nowrap" [title]="b.createdAt | dt">{{ b.createdAt | ago }}</td>
                <td><app-chip [value]="b.state" />@if (b.error) {<div class="small error ellipsis" style="max-width:240px" [title]="b.error">{{ b.error }}</div>}</td>
                <td>{{ b.kind }}<div class="small muted">{{ b.trigger }} · {{ b.storageProvider }}</div></td>
                <td class="num">{{ b.sizeBytes | bytes }}<div class="small muted">{{ b.refCount }} refs</div></td>
                <td>@if (b.encrypted) { <app-chip label="Encrypted" tone="good" icon="lock" /> } @else { <app-chip label="Not encrypted" tone="warn" icon="lock_open" /> }</td>
                <td><code>{{ b.headSha | sha }}</code></td>
                <td class="nowrap">{{ b.verifiedAt | ago }}</td>
                <td>
                  @if (b.lastVerification; as v) {
                    <div class="checks">
                      <app-chip [tone]="v.checksumOk ? 'good' : 'bad'" label="checksum" [icon]="v.checksumOk ? 'check' : 'close'" />
                      <app-chip [tone]="v.bundleOk ? 'good' : 'bad'" label="bundle" [icon]="v.bundleOk ? 'check' : 'close'" />
                      <app-chip [tone]="v.fsckOk ? 'good' : 'bad'" label="fsck" [icon]="v.fsckOk ? 'check' : 'close'" />
                      <app-chip [tone]="v.refsMatch ? 'good' : 'bad'" label="refs" [icon]="v.refsMatch ? 'check' : 'close'" />
                      <app-chip [tone]="v.testRestoreOk ? 'good' : 'bad'" label="test restore" [icon]="v.testRestoreOk ? 'check' : 'close'" />
                    </div>
                    @if (v.details) { <div class="small muted ellipsis" style="max-width:300px" [title]="v.details">{{ v.details }}</div> }
                  } @else { <span class="muted">not verified</span> }
                </td>
                <td class="nowrap">
                  <button mat-button (click)="verify(b)" [disabled]="b.state === 'Pending' || b.state === 'Running'">Verify</button>
                  <button mat-button (click)="openRestore(b)" [disabled]="b.state !== 'Verified' && b.state !== 'Completed'">Restore…</button>
                </td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    } @else if (!busy()) { <app-empty icon="backup" text="No backups yet" hint="Create one now or wait for the scheduled backup (repository settings)." /> }

    @if (restoreFor(); as b) {
      <section class="panel" style="margin-top:12px">
        <div class="panel-head"><h2>Restore backup from {{ b.createdAt | dt }}</h2><span class="spacer"></span><button mat-icon-button (click)="restoreFor.set(null)" aria-label="Close"><mat-icon>close</mat-icon></button></div>
        <mat-radio-group [(ngModel)]="target" style="display:flex;flex-direction:column;gap:6px">
          <mat-radio-button value="directory"><b>Into a new directory</b><div class="small muted">Safe: unpacks the backup into a new folder for inspection.</div></mat-radio-button>
          <mat-radio-button value="remote"><b>Re-create the central repository</b><div class="small muted">Disaster recovery after losing the Git server. Pushes all refs from the backup to the remote.</div></mat-radio-button>
        </mat-radio-group>
        @if (target === 'directory') {
          <mat-form-field style="width:520px;max-width:100%;margin-top:8px"><mat-label>Target directory</mat-label><input matInput [(ngModel)]="targetDirectory" placeholder="inside the authorised workspace root (empty or missing)"></mat-form-field>
        } @else { <div class="callout warn">This overwrites the central repository's refs with the backup's state (head <code>{{ b.headSha | sha }}</code>).</div> }
        <mat-checkbox [(ngModel)]="ack">I understand what this restore does and approve it.</mat-checkbox>
        <div class="row" style="margin-top:8px">
          <button mat-flat-button [class.danger]="target === 'remote'" [disabled]="!ack || (target === 'directory' && !targetDirectory.trim())" (click)="restore(b)"><mat-icon>settings_backup_restore</mat-icon>Restore</button>
        </div>
      </section>
    }`,
  styles: [`.checks { display: flex; flex-wrap: wrap; gap: 2px; }`],
})
export class BackupsTab implements OnInit {
  private ctx = inject(RepoContext);
  private api = inject(ApiService);
  private destroyRef = inject(DestroyRef);
  items = signal<Backup[]>([]);
  jobs = signal<string[]>([]);
  busy = signal(false);
  error = signal<unknown>(null);
  restoreFor = signal<Backup | null>(null);
  target: 'directory' | 'remote' = 'directory';
  targetDirectory = '';
  ack = false;

  ngOnInit() {
    this.load();
    this.ctx.refresh$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => this.load());
  }

  load() {
    this.busy.set(true);
    this.api.backups(this.ctx.id()).subscribe({
      next: b => { this.items.set(b); this.busy.set(false); },
      error: e => { this.error.set(e); this.busy.set(false); },
    });
  }

  private track(jobId: string) { this.jobs.set([jobId, ...this.jobs().filter(j => j !== jobId)].slice(0, 4)); }

  create() { this.api.createBackup(this.ctx.id()).subscribe({ next: j => { this.track(j.jobId); this.load(); }, error: e => this.error.set(e) }); }
  verify(b: Backup) { this.api.verifyBackup(this.ctx.id(), b.id).subscribe({ next: j => this.track(j.jobId), error: e => this.error.set(e) }); }
  jobDone(_j: Job) { this.load(); }

  openRestore(b: Backup) { this.restoreFor.set(b); this.ack = false; this.target = 'directory'; this.targetDirectory = ''; }

  restore(b: Backup) {
    this.api.restoreBackup(this.ctx.id(), b.id, { target: this.target, targetDirectory: this.target === 'directory' ? this.targetDirectory.trim() : null, approved: true }).subscribe({
      next: j => { this.track(j.jobId); this.restoreFor.set(null); },
      error: e => this.error.set(e),
    });
  }
}
