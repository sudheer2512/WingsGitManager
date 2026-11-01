import { Component, DestroyRef, OnInit, computed, effect, inject, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatRadioModule } from '@angular/material/radio';
import { MatSelectModule } from '@angular/material/select';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Router } from '@angular/router';
import { Subscription } from 'rxjs';
import { ApiService } from '../core/api.service';
import {
  Checkpoint, CommitSummary, DeletedFile, DurabilityState, RecoveryCandidatesResponse, RecoveryPlan, RecoveryRequest, RecoveryStrategy, RecoveryType,
  RepositorySummary, Session,
} from '../core/models';
import { UI, isRemoteSafe } from '../shared/ui';
import { RepoContext } from './repo-context';

interface Kind { type: RecoveryType; title: string; text: string; icon: string; }

const KINDS: Kind[] = [
  { type: 'RestoreFile', icon: 'description', title: 'Restore files', text: 'Bring one or more files back to an earlier version. Everything else stays as it is.' },
  { type: 'RestoreDeleted', icon: 'restore_from_trash', title: 'Restore deleted files', text: 'Recover files or folders that were deleted.' },
  { type: 'RestoreProject', icon: 'settings_backup_restore', title: 'Restore the whole project', text: 'Get the project as it was at an earlier point — side by side, as a rollback commit, or in a new folder.' },
  { type: 'RevertSession', icon: 'smart_toy', title: 'Undo an AI session', text: 'Reverse everything one AI conversation changed, keeping later unrelated work.' },
  { type: 'RevertCommits', icon: 'undo', title: 'Undo specific changes', text: 'Reverse selected commits, keeping later unrelated work.' },
  { type: 'CloneProject', icon: 'cloud_download', title: 'Recover a deleted project folder', text: 'Re-create the project from the central Git server into a folder.' },
];

const STRATEGIES: Record<RecoveryType, RecoveryStrategy[]> = {
  RestoreFile: ['RestoreFilesAsNewCommit'],
  RestoreDeleted: ['RestoreFilesAsNewCommit'],
  RestoreProject: ['RecoveryBranchWorktree', 'RollbackCommit', 'CloneToDirectory'],
  RevertSession: ['RevertCommits'],
  RevertCommits: ['RevertCommits'],
  CloneProject: ['CloneToDirectory'],
};

const STRATEGY_TEXT: Record<RecoveryStrategy, { title: string; text: string; safe: boolean }> = {
  RestoreFilesAsNewCommit: { title: 'Restore as a new commit', text: 'The selected files are restored on the current branch in a new commit. History is kept, so this can be undone.', safe: false },
  RecoveryBranchWorktree: { title: 'Side by side (recommended)', text: 'A new recovery branch in its own folder. Your current code is not touched.', safe: true },
  RollbackCommit: { title: 'Roll back the current branch', text: 'A new commit makes every file match the recovery point. Earlier commits stay in history.', safe: false },
  RevertCommits: { title: 'Revert commits', text: 'A new commit reverses the selected changes; later unrelated work is kept.', safe: false },
  CloneToDirectory: { title: 'Into a new folder', text: 'A fresh copy is created in the folder you choose; nothing existing is modified.', safe: true },
};

const STEPS = ['Repository', 'Recovery point', 'Files', 'Preview', 'Method', 'Confirm', 'Result'];

@Component({
  selector: 'app-recovery-tab',
  imports: [FormsModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatButtonModule, MatButtonToggleModule, MatCheckboxModule, MatRadioModule,
    MatIconModule, MatTooltipModule, ...UI],
  template: `
    <section class="panel wizard">
      <div class="panel-head"><h2>Recovery wizard</h2>@if (prefillLabel()) { <span class="tag ai">{{ prefillLabel() }}</span> }<span class="spacer"></span>
        <button mat-button (click)="reset()"><mat-icon>restart_alt</mat-icon>Start over</button></div>
      <ol class="steps">
        @for (s of stepNames; track s; let i = $index) {
          <li [class.cur]="step() === i + 1" [class.done]="step() > i + 1" (click)="goto(i + 1)"><span class="n">{{ step() > i + 1 ? '✓' : i + 1 }}</span>{{ s }}</li>
        }
      </ol>
      <app-error [error]="error()" [dismissable]="true" (dismiss)="error.set(null)" />

      <!-- STEP 1 -->
      @if (step() === 1) {
        <div class="row" style="flex-wrap:wrap;margin-bottom:8px">
          <mat-form-field style="width:360px">
            <mat-label>Repository</mat-label>
            <mat-select [ngModel]="ctx.id()" (ngModelChange)="switchRepo($event)">
              @for (r of repos(); track r.id) { <mat-option [value]="r.id">{{ r.companyName }} / {{ r.projectName }} / {{ r.name }}</mat-option> }
              @if (!repos().length) { <mat-option [value]="ctx.id()">{{ ctx.repo()?.summary?.name }}</mat-option> }
            </mat-select>
          </mat-form-field>
        </div>
        <h3 style="margin-bottom:6px">What do you want to recover?</h3>
        <div class="kinds">
          @for (k of kinds; track k.type) {
            <button class="kind" [class.sel]="type() === k.type" (click)="setType(k.type)">
              <mat-icon>{{ k.icon }}</mat-icon><b>{{ k.title }}</b><span class="small muted">{{ k.text }}</span>
            </button>
          }
        </div>
      }

      <!-- STEP 2 -->
      @if (step() === 2) {
        @if (type() === 'RevertSession') {
          <h3>Which AI session should be undone?</h3>
          <div class="table-wrap list-scroll">
            <table class="data">
              <thead><tr><th></th><th>Session</th><th>Agent</th><th>State</th><th class="num">Commits</th><th class="num">Files</th><th>When</th></tr></thead>
              <tbody>
                @for (s of sessions(); track s.id) {
                  <tr class="clickable" [class.selected]="sessionId() === s.id" (click)="sessionId.set(s.id)">
                    <td><mat-radio-button [checked]="sessionId() === s.id" /></td>
                    <td><code>{{ s.id | sha }}</code> {{ s.title ?? '' }}</td><td><span class="tag ai">{{ s.agentId }}</span></td>
                    <td><app-chip [value]="s.state" /></td><td class="num">{{ s.commitCount }}</td><td class="num">{{ s.filesModified }}</td><td class="nowrap">{{ s.createdAt | ago }}</td>
                  </tr>
                } @empty { <tr><td colspan="7" class="muted">No AI sessions in this repository</td></tr> }
              </tbody>
            </table>
          </div>
        } @else {
          <mat-button-toggle-group [value]="pointMode()" (change)="pointMode.set($event.value)" hideSingleSelectionIndicator style="margin-bottom:8px">
            <mat-button-toggle value="describe"><mat-icon>chat</mat-icon> Describe what you want</mat-button-toggle>
            <mat-button-toggle value="checkpoints"><mat-icon>flag</mat-icon> Checkpoints</mat-button-toggle>
            <mat-button-toggle value="commits"><mat-icon>commit</mat-icon> Commits</mat-button-toggle>
          </mat-button-toggle-group>
          @if (type() === 'RevertCommits') { <div class="small muted" style="margin-bottom:6px">Select the commits to undo (newest are reverted first).</div> }
          @if (type() === 'RestoreDeleted' || type() === 'CloneProject') { <div class="callout small">A recovery point is optional here — by default the last version before deletion (or the latest commit) is used.</div> }

          @if (pointMode() === 'describe') {
            <div class="row" style="align-items:flex-start">
              <mat-form-field class="full">
                <mat-label>Describe what you want to recover</mat-label>
                <textarea matInput rows="2" [(ngModel)]="query" (keydown.control.enter)="findCandidates()"
                  placeholder="e.g. &quot;restore the login page from yesterday before the AI refactoring&quot; or &quot;undo the last AI session&quot;"></textarea>
              </mat-form-field>
              <button mat-flat-button (click)="findCandidates()" [disabled]="!query.trim() || busy()" style="margin-top:6px"><mat-icon>search</mat-icon>Find</button>
            </div>
            @if (candidates(); as c) {
              <div class="callout small"><b>Understood as:</b> {{ c.interpretation.explanation }}
                @if (c.interpretation.suggestedType) { — suggested: <b>{{ c.interpretation.suggestedType | human }}</b> }
                @if (c.ambiguous && c.candidates.length > 1) { <div>Several points match; pick the right one below.</div> }
              </div>
              @if (c.changeSummary; as cs) { <div class="small muted" style="margin-bottom:6px">{{ cs.narrative }}</div> }
              <div class="table-wrap list-scroll">
                <table class="data">
                  <thead><tr><th></th><th>Score</th><th>When</th><th>Description</th><th>Commit</th><th>Durability</th><th>Suggested</th></tr></thead>
                  <tbody>
                    @for (k of c.candidates; track k.recoveryPoint + k.commitSha) {
                      <tr class="clickable" [class.selected]="chosenCandidate() === k.recoveryPoint + k.commitSha" (click)="pickCandidate(k)">
                        <td><mat-radio-button [checked]="chosenCandidate() === k.recoveryPoint + k.commitSha" /></td>
                        <td class="num">{{ (k.score * 100).toFixed(0) }}%</td>
                        <td class="nowrap" [title]="k.timestamp | dt">{{ k.timestamp | ago }}</td>
                        <td style="max-width:380px"><div>{{ k.description }}</div>@if (k.matchingPaths.length) {<div class="small muted ellipsis">{{ k.matchingPaths.join(', ') }}</div>}</td>
                        <td><code>{{ k.commitSha | sha }}</code></td>
                        <td><app-durability [state]="k.durability" /></td>
                        <td><span class="tag">{{ k.suggestedType | human }}</span></td>
                      </tr>
                    } @empty { <tr><td colspan="7" class="muted">No candidates found — try other words or pick from checkpoints/commits.</td></tr> }
                  </tbody>
                </table>
              </div>
            }
          }
          @if (pointMode() === 'checkpoints') {
            <div class="table-wrap list-scroll">
              <table class="data">
                <thead><tr><th></th><th>When</th><th>Type</th><th>Description</th><th>Commit</th><th>Stored</th></tr></thead>
                <tbody>
                  @for (c of checkpoints(); track c.id) {
                    <tr class="clickable" [class.selected]="point() === 'checkpoint:' + c.id" (click)="pickPoint('checkpoint:' + c.id, c.commitSha)">
                      <td><mat-radio-button [checked]="point() === 'checkpoint:' + c.id" /></td>
                      <td class="nowrap" [title]="c.createdAt | dt">{{ c.createdAt | ago }}</td><td><span class="tag">{{ c.type | human }}</span></td>
                      <td class="ellipsis" style="max-width:360px">{{ c.description ?? '—' }}</td><td><code>{{ c.commitSha | sha }}</code></td>
                      <td><app-durability [state]="c.syncState" /></td>
                    </tr>
                  } @empty { <tr><td colspan="6" class="muted">No checkpoints</td></tr> }
                </tbody>
              </table>
            </div>
          }
          @if (pointMode() === 'commits') {
            <div class="table-wrap list-scroll">
              <table class="data">
                <thead><tr><th></th><th>When</th><th>Message</th><th>Author</th><th>Commit</th><th>Stored</th></tr></thead>
                <tbody>
                  @for (c of commits(); track c.sha) {
                    <tr class="clickable" [class.selected]="isCommitSelected(c.sha)" (click)="pickCommit(c)">
                      <td>@if (type() === 'RevertCommits') { <mat-checkbox [checked]="isCommitSelected(c.sha)" (click)="$event.preventDefault()" /> }
                        @else { <mat-radio-button [checked]="point() === c.sha" /> }</td>
                      <td class="nowrap" [title]="c.committedAt | dt">{{ c.committedAt | ago }}</td>
                      <td class="ellipsis" style="max-width:380px">{{ c.subject }} @if (c.agentId) { <span class="tag ai">AI</span> }</td>
                      <td>{{ c.authorName }}</td><td><code>{{ c.sha | sha }}</code></td><td><app-durability [state]="c.durability" /></td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }
          <div class="small" style="margin-top:6px">Selected:
            @if (type() === 'RevertCommits') { {{ commitShas().length }} commit(s) } @else { <code>{{ point() || '— (default)' }}</code> }
            @if (point() && pointUnsafe()) { <app-chip label="this point is not verified on the remote" tone="warn" icon="warning" /> }
          </div>
        }
      }

      <!-- STEP 3 -->
      @if (step() === 3) {
        @switch (type()) {
          @case ('RestoreProject') { <div class="callout"><b>Entire project.</b> Every tracked file is restored to <code>{{ point() }}</code>. You choose how in step 5.</div> }
          @case ('CloneProject') {
            <div class="callout"><b>Entire project.</b> The central repository is cloned.</div>
            <mat-form-field style="width:260px"><mat-label>Branch (optional)</mat-label><input matInput [(ngModel)]="branch" [placeholder]="ctx.repo()?.summary?.defaultBranch ?? 'main'"></mat-form-field>
          }
          @case ('RestoreDeleted') {
            <div class="row"><mat-form-field class="wide" style="width:320px"><mat-label>Filter deleted files</mat-label><input matInput [(ngModel)]="deletedFilter" (keyup.enter)="loadDeleted()"></mat-form-field>
              <button mat-stroked-button (click)="loadDeleted()">Search</button></div>
            <div class="table-wrap list-scroll">
              <table class="data">
                <thead><tr><th></th><th>Path</th><th>Deleted</th><th>By</th><th>Deleted in</th><th>Last version</th></tr></thead>
                <tbody>
                  @for (d of deleted(); track d.path) {
                    <tr class="clickable" [class.selected]="paths().includes(d.path)" (click)="togglePath(d.path)">
                      <td><mat-checkbox [checked]="paths().includes(d.path)" (click)="$event.preventDefault()" /></td>
                      <td class="mono">{{ d.path }}</td><td class="nowrap">{{ d.deletedAt | ago }}</td><td>{{ d.deletedBy ?? '—' }}</td>
                      <td><code>{{ d.deletedInCommit | sha }}</code></td><td><code>{{ d.lastVersionCommit | sha }}</code></td>
                    </tr>
                  } @empty { <tr><td colspan="6" class="muted">No deleted files found</td></tr> }
                </tbody>
              </table>
            </div>
          }
          @default {
            <div class="small muted" style="margin-bottom:6px">
              @if (type() === 'RestoreFile') { Choose the files to restore (required). }
              @else { Optionally limit the undo to some paths; leave empty to undo everything in the selected {{ type() === 'RevertSession' ? 'session' : 'commits' }}. }
            </div>
            <div class="row">
              <mat-form-field style="width:360px"><mat-label>Find files at the recovery point</mat-label><input matInput [(ngModel)]="fileQuery" (keyup.enter)="searchFiles()"></mat-form-field>
              <button mat-stroked-button (click)="searchFiles()">Search</button>
              <mat-form-field style="width:300px"><mat-label>…or type a path</mat-label><input matInput [(ngModel)]="manualPath" (keyup.enter)="addManual()"></mat-form-field>
              <button mat-stroked-button (click)="addManual()" [disabled]="!manualPath.trim()">Add</button>
            </div>
            @if (fileHits().length) {
              <div class="table-wrap" style="max-height:220px">
                <table class="data compact"><tbody>
                  @for (p of fileHits(); track p) {
                    <tr class="clickable" [class.selected]="paths().includes(p)" (click)="togglePath(p)"><td style="width:30px"><mat-checkbox [checked]="paths().includes(p)" (click)="$event.preventDefault()" /></td><td class="mono">{{ p }}</td></tr>
                  }
                </tbody></table>
              </div>
            }
          }
        }
        @if (paths().length) {
          <div style="margin-top:8px"><b class="small">Selected paths:</b>
            @for (p of paths(); track p) { <span class="tag mono">{{ p }} <a href="" (click)="$event.preventDefault(); togglePath(p)">×</a></span> }</div>
        }
      }

      <!-- STEP 4 / 5 / 6 share the plan view -->
      @if (step() >= 4 && step() <= 6) {
        <app-loading [on]="busy()" />
        @if (step() === 5) {
          <h3 style="margin-bottom:6px">How should the code be recovered?</h3>
          <mat-radio-group [ngModel]="strategy()" (ngModelChange)="strategy.set($event)" class="strategies">
            @for (s of strategies(); track s) {
              <mat-radio-button [value]="s"><b>{{ st[s].title }}</b> @if (st[s].safe) { <span class="tag">non-destructive</span> }<div class="small muted">{{ st[s].text }}</div></mat-radio-button>
            }
          </mat-radio-group>
          @if (strategy() === 'CloneToDirectory') {
            <mat-form-field style="width:520px;max-width:100%;margin-top:8px">
              <mat-label>Target directory {{ type() === 'CloneProject' ? '(optional)' : '' }}</mat-label>
              <input matInput [(ngModel)]="targetDirectory" placeholder="must be inside the company's authorised workspace root">
              <mat-hint>Must not exist or be empty. Leave empty for clone recovery to use the default location.</mat-hint>
            </mat-form-field>
          }
          <div class="row" style="margin:10px 0"><button mat-stroked-button (click)="preview()" [disabled]="busy()"><mat-icon>preview</mat-icon>Update preview</button></div>
        }
        @if (plan(); as p) {
          <div class="plan">
            <div class="wrap-row" style="margin-bottom:6px">
              <app-chip [value]="p.status" /> <b>{{ p.type | human }}</b> · <span>{{ st[p.strategy].title }}</span>
              @if (p.destructive) { <app-chip label="Changes existing code" tone="warn" icon="warning" /> } @else { <app-chip label="Non-destructive" tone="good" icon="shield" /> }
              @if (p.approvalRequired) { <app-chip label="Approval required" tone="info" icon="how_to_reg" /> }
              <span class="spacer"></span><span class="small muted">preview expires {{ p.expiresAt | ago }}</span>
            </div>
            <div class="callout"><b>What will happen:</b> {{ p.expectedResult }}<div class="small">{{ p.explanation }}</div></div>
            <dl class="kv small">
              <dt>Branch</dt><dd><code>{{ p.currentBranch ?? '—' }}</code></dd>
              <dt>Current commit</dt><dd><code>{{ p.currentCommit | sha:12 }}</code></dd>
              <dt>Recovery commit</dt><dd><code>{{ p.recoveryCommit | sha:12 }}</code></dd>
              @if (p.targetDirectory) { <dt>Directory</dt><dd class="mono">{{ p.targetDirectory }}</dd> }
              <dt>Affected files</dt><dd>{{ p.affectedFiles.length }} · <span style="color:var(--good)">+{{ p.additions }}</span> <span style="color:var(--bad)">−{{ p.deletions }}</span></dd>
            </dl>
            @if (p.expectedConflicts.length) { <div class="callout bad"><b>Expected conflicts</b><ul>@for (c of p.expectedConflicts; track c) { <li>{{ c }}</li> }</ul></div> }
            @if (p.uncommittedChanges.length) { <div class="callout warn"><b>Uncommitted local changes</b> (saved in a safety checkpoint first):<ul>@for (c of p.uncommittedChanges.slice(0, 20); track c) { <li class="mono">{{ c }}</li> }</ul></div> }
            @if (p.affectedFiles.length && step() !== 6) {
              <div class="table-wrap" style="max-height:260px">
                <table class="data compact">
                  <thead><tr><th>File</th><th>Change</th><th class="num">+</th><th class="num">−</th></tr></thead>
                  <tbody>@for (f of p.affectedFiles; track f.path) {
                    <tr><td class="mono">{{ f.oldPath && f.oldPath !== f.path ? f.oldPath + ' → ' : '' }}{{ f.path }}</td><td><app-chip [value]="f.kind" /></td><td class="num">{{ f.additions }}</td><td class="num">{{ f.deletions }}</td></tr>
                  }</tbody>
                </table>
              </div>
            }
          </div>
        }
        @if (step() === 6 && plan(); as p) {
          <div class="confirm">
            @if (p.destructive || p.approvalRequired) {
              <div class="callout warn">This recovery {{ p.destructive ? 'changes existing code on ' + (p.currentBranch ?? 'the branch') : 'needs your approval' }}.
                A safety checkpoint is created first and history is preserved, so it can be undone.</div>
              <mat-checkbox [(ngModel)]="ack">I reviewed the preview and approve this recovery.</mat-checkbox>
            } @else {
              <div class="callout good">This recovery does not modify existing code.</div>
            }
            @if (p.expectedConflicts.length && p.strategy === 'RevertCommits') { <div class="callout bad">The revert would conflict; the server will refuse to execute it. Choose fewer commits or limit the paths.</div> }
          </div>
        }
      }

      <!-- STEP 7 -->
      @if (step() === 7) {
        <app-loading [on]="busy()" />
        @if (result(); as r) {
          <div class="callout" [class.good]="r.status === 'Succeeded'" [class.bad]="r.status === 'Failed' || r.status === 'Conflict'">
            <div class="row"><app-chip [value]="r.status" /><b>{{ r.type | human }}</b> · {{ st[r.strategy].title }}</div>
            @if (r.status === 'Executing') { <div class="small">Running in the background{{ r.jobId ? ' (job ' + r.jobId.substring(0, 8) + ')' : '' }} — this page updates automatically.</div> }
            @if (r.error) { <div class="error">{{ r.error }}</div> }
          </div>
          <dl class="kv">
            <dt>Operation</dt><dd><code>{{ r.operationId }}</code></dd>
            @if (r.resultCommitSha) { <dt>Result commit</dt><dd><a href="" (click)="$event.preventDefault(); ctx.tab('history', { sha: r.resultCommitSha })"><code>{{ r.resultCommitSha | sha:12 }}</code></a></dd> }
            @if (r.resultBranch) { <dt>Result branch</dt><dd><code>{{ r.resultBranch }}</code></dd> }
            <dt>Stored</dt><dd>
              @if (stored(); as d) { <app-durability [state]="d" /> @if (!safe(d)) { <span class="small muted">— not yet verified on the remote</span> } }
              @else if (r.resultCommitSha) { <span class="muted small">checking…</span> } @else { <span class="muted">—</span> }</dd>
            @if (r.targetDirectory) { <dt>Directory</dt><dd class="mono">{{ r.targetDirectory }}</dd> }
            @if (r.safetyCheckpointId) { <dt>Safety checkpoint</dt><dd><code>{{ r.safetyCheckpointId | sha }}</code> (restore this to undo the recovery)</dd> }
            <dt>Completed</dt><dd>{{ r.completedAt | dt }}</dd>
          </dl>
          @if (r.resultBranch) { <button mat-stroked-button (click)="browse(r.resultBranch)"><mat-icon>folder_open</mat-icon>Browse the recovered files</button> }
        }
      }

      <div class="nav">
        @if (step() > 1 && step() < 7) { <button mat-button (click)="back()"><mat-icon>arrow_back</mat-icon>Back</button> }
        <span class="spacer"></span>
        @if (hint()) { <span class="small muted">{{ hint() }}</span> }
        @if (step() < 6) { <button mat-flat-button (click)="next()" [disabled]="!canNext() || busy()">{{ step() === 3 ? 'Preview' : 'Next' }}<mat-icon iconPositionEnd>arrow_forward</mat-icon></button> }
        @if (step() === 6) { <button mat-flat-button [class.danger]="plan()?.destructive" (click)="execute()" [disabled]="!canExecute() || busy()"><mat-icon>play_arrow</mat-icon>Recover now</button> }
        @if (step() === 7) { <button mat-flat-button (click)="reset()">New recovery</button> }
      </div>
    </section>

    <section class="panel">
      <div class="panel-head"><h2>Recovery operations</h2><span class="spacer"></span><button mat-icon-button (click)="loadOps()" aria-label="Refresh"><mat-icon>refresh</mat-icon></button></div>
      @if (ops().length) {
        <div class="table-wrap">
          <table class="data">
            <thead><tr><th>Created</th><th>Type</th><th>Strategy</th><th>Status</th><th>Recovery commit</th><th>Result</th><th>Files</th></tr></thead>
            <tbody>
              @for (o of ops(); track o.operationId) {
                <tr class="clickable" (click)="showOp(o)">
                  <td class="nowrap" [title]="o.createdAt | dt">{{ o.createdAt | ago }}</td><td>{{ o.type | human }}</td><td>{{ o.strategy | human }}</td>
                  <td><app-chip [value]="o.status" />@if (o.error) {<div class="small error ellipsis" style="max-width:260px" [title]="o.error">{{ o.error }}</div>}</td>
                  <td><code>{{ o.recoveryCommit | sha }}</code></td>
                  <td>@if (o.resultCommitSha) { <code>{{ o.resultCommitSha | sha }}</code> } @if (o.resultBranch) { <div class="small"><code>{{ o.resultBranch }}</code></div> } @if (o.resultDurability) { <app-durability [state]="o.resultDurability" /> }</td>
                  <td>{{ o.affectedFiles.length }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      } @else { <app-empty icon="restore" text="No recovery operations yet" /> }
    </section>`,
  styles: [`
    .steps { display: flex; flex-wrap: wrap; gap: 4px; list-style: none; padding: 0; margin: 4px 0 12px; }
    .steps li { display: flex; align-items: center; gap: 6px; padding: 4px 10px 4px 4px; border-radius: 14px; background: var(--surface-2); color: var(--muted); cursor: pointer; font-size: 12px; }
    .steps li .n { width: 20px; height: 20px; border-radius: 50%; display: grid; place-items: center; background: var(--border); color: var(--text); font-size: 11px; font-weight: 600; }
    .steps li.cur { background: var(--selected); color: var(--text); font-weight: 600; } .steps li.cur .n { background: var(--accent); color: #fff; }
    .steps li.done .n { background: var(--good); color: #fff; }
    .kinds { display: grid; grid-template-columns: repeat(auto-fill, minmax(230px, 1fr)); gap: 8px; }
    .kind { display: flex; flex-direction: column; align-items: flex-start; gap: 4px; text-align: left; padding: 12px; border: 1px solid var(--border); border-radius: 8px;
      background: var(--surface); color: var(--text); cursor: pointer; font: inherit; }
    .kind:hover { background: var(--hover); } .kind.sel { border-color: var(--accent); background: var(--selected); box-shadow: 0 0 0 1px var(--accent); }
    .kind mat-icon { color: var(--accent); }
    .strategies { display: flex; flex-direction: column; gap: 8px; }
    .nav { display: flex; align-items: center; gap: 8px; margin-top: 14px; padding-top: 10px; border-top: 1px solid var(--border); }
    .confirm { margin-top: 8px; }`],
})
export class RecoveryTab implements OnInit {
  ctx = inject(RepoContext);
  private api = inject(ApiService);
  private router = inject(Router);
  private destroyRef = inject(DestroyRef);
  kinds = KINDS;
  st = STRATEGY_TEXT;
  stepNames = STEPS;
  safe = isRemoteSafe;

  step = signal(1);
  type = signal<RecoveryType | null>(null);
  point = signal<string>('');
  pointSha = signal<string>('');
  pointUnsafe = signal(false);
  sessionId = signal<string>('');
  commitShas = signal<string[]>([]);
  paths = signal<string[]>([]);
  strategy = signal<RecoveryStrategy | null>(null);
  plan = signal<RecoveryPlan | null>(null);
  result = signal<RecoveryPlan | null>(null);
  /** Durability of the result commit. The API leaves RecoveryPlanDto.resultDurability null, so it is looked up from the commit. */
  stored = signal<DurabilityState | null>(null);
  busy = signal(false);
  error = signal<unknown>(null);
  prefillLabel = signal<string | null>(null);
  pointMode = signal<'describe' | 'checkpoints' | 'commits'>('checkpoints');
  chosenCandidate = signal<string | null>(null);

  repos = signal<RepositorySummary[]>([]);
  sessions = signal<Session[]>([]);
  checkpoints = signal<Checkpoint[]>([]);
  commits = signal<CommitSummary[]>([]);
  candidates = signal<RecoveryCandidatesResponse | null>(null);
  deleted = signal<DeletedFile[]>([]);
  fileHits = signal<string[]>([]);
  ops = signal<RecoveryPlan[]>([]);

  query = ''; branch = ''; deletedFilter = ''; fileQuery = ''; manualPath = ''; targetDirectory = ''; ack = false;
  private poll?: Subscription;

  strategies = computed(() => STRATEGIES[this.type() ?? 'RestoreProject']);

  canNext = (): boolean => {
    const t = this.type();
    switch (this.step()) {
      case 1: return !!t;
      case 2:
        if (t === 'RevertSession') return !!this.sessionId();
        if (t === 'RevertCommits') return this.commitShas().length > 0;
        if (t === 'RestoreDeleted' || t === 'CloneProject') return true;
        return !!this.point();
      case 3: return (t !== 'RestoreFile' && t !== 'RestoreDeleted') || this.paths().length > 0;
      case 4: return !!this.plan();
      case 5: return !!this.plan() && (this.strategy() !== 'CloneToDirectory' || this.type() === 'CloneProject' || !!this.targetDirectory.trim());
      default: return false;
    }
  };

  hint = (): string => {
    if (this.canNext() || this.step() >= 6) return '';
    switch (this.step()) {
      case 1: return 'Choose what to recover';
      case 2: return this.type() === 'RevertSession' ? 'Pick a session' : this.type() === 'RevertCommits' ? 'Select at least one commit' : 'Pick a recovery point';
      case 3: return 'Select at least one file';
      default: return '';
    }
  };

  constructor() {
    inject(DestroyRef).onDestroy(() => this.poll?.unsubscribe());
    effect(() => {
      const p = this.ctx.recoveryPrefill();
      if (p) untracked(() => this.applyPrefill());
    });
  }

  ngOnInit() {
    this.api.repositories({ pageSize: 500 }).subscribe({ next: r => this.repos.set(r.items), error: () => undefined });
    this.api.checkpoints(this.ctx.id(), { pageSize: 100 }).subscribe({ next: r => this.checkpoints.set(r.items), error: e => this.error.set(e) });
    this.api.commits(this.ctx.id(), { pageSize: 100 }).subscribe({ next: r => this.commits.set(r.items), error: e => this.error.set(e) });
    this.api.sessions({ repositoryId: this.ctx.id(), pageSize: 100 }).subscribe({ next: r => this.sessions.set(r.items), error: e => this.error.set(e) });
    this.loadOps();
    this.ctx.refresh$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => this.loadOps());
  }

  private applyPrefill() {
    const p = this.ctx.recoveryPrefill();
    if (!p) return;
    this.ctx.recoveryPrefill.set(null);
    this.resetState();
    this.prefillLabel.set(p.label ?? null);
    if (p.type) this.setType(p.type, false);
    if (p.recoveryPoint) { this.point.set(p.recoveryPoint); this.pointSha.set(p.recoveryPoint); }
    if (p.sessionId) this.sessionId.set(p.sessionId);
    if (p.commitShas) this.commitShas.set(p.commitShas);
    if (p.paths) this.paths.set(p.paths);
    // Jump straight to the preview when everything needed is known.
    this.step.set(2);
    if (this.canNext()) { this.step.set(3); if (this.canNext()) { this.step.set(4); this.preview(); } }
  }

  switchRepo(id: string) {
    if (id === this.ctx.id()) return;
    this.router.navigate(['/repositories', id], { queryParams: { tab: 'recovery' } });
  }

  setType(t: RecoveryType, advance = true) {
    if (this.type() !== t) { this.plan.set(null); this.paths.set([]); this.commitShas.set([]); }
    this.type.set(t);
    this.strategy.set(STRATEGIES[t][0]);
    this.pointMode.set(t === 'RevertCommits' ? 'commits' : this.pointMode());
    if (t === 'RestoreDeleted') this.loadDeleted();
    if (advance) this.step.set(2);
  }

  goto(n: number) {
    if (n < this.step() && this.step() < 7) this.step.set(n);
  }

  back() { if (this.step() > 1) this.step.set(this.step() - 1); }

  next() {
    const s = this.step();
    if (s === 3) { this.step.set(4); this.preview(); return; }
    if (s === 5) { this.preview(() => this.step.set(6)); return; }
    this.step.set(s + 1);
  }

  pickPoint(point: string, sha: string, durability?: string | null) {
    this.point.set(point); this.pointSha.set(sha); this.plan.set(null);
    const cp = this.checkpoints().find(c => 'checkpoint:' + c.id === point);
    const cm = this.commits().find(c => c.sha === sha);
    const d = durability ?? cp?.syncState ?? cm?.durability ?? null;
    this.pointUnsafe.set(!!d && !isRemoteSafe(d as never));
  }

  pickCommit(c: CommitSummary) {
    if (this.type() === 'RevertCommits') {
      const s = this.commitShas();
      this.commitShas.set(s.includes(c.sha) ? s.filter(x => x !== c.sha) : [...s, c.sha]);
      this.plan.set(null);
    } else this.pickPoint(c.sha, c.sha, c.durability);
  }

  isCommitSelected(sha: string) { return this.type() === 'RevertCommits' ? this.commitShas().includes(sha) : this.point() === sha; }

  findCandidates() {
    this.busy.set(true);
    this.api.recoveryCandidates(this.ctx.id(), { query: this.query.trim(), limit: 10 }).subscribe({
      next: c => { this.candidates.set(c); this.busy.set(false); },
      error: e => { this.error.set(e); this.busy.set(false); },
    });
  }

  pickCandidate(k: RecoveryCandidatesResponse['candidates'][number]) {
    this.chosenCandidate.set(k.recoveryPoint + k.commitSha);
    if (k.suggestedType && k.suggestedType !== this.type()) { this.setType(k.suggestedType, false); }
    this.pickPoint(k.recoveryPoint, k.commitSha, k.durability);
    if (k.sessionId) this.sessionId.set(k.sessionId);
    if (k.suggestedCommits?.length) this.commitShas.set(k.suggestedCommits);
    if (k.suggestedPaths?.length) this.paths.set(k.suggestedPaths);
  }

  loadDeleted() {
    this.api.deletedFiles(this.ctx.id(), this.deletedFilter.trim() || undefined).subscribe({ next: d => this.deleted.set(d), error: e => this.error.set(e) });
  }

  searchFiles() {
    const q = this.fileQuery.trim(); if (!q) return;
    const ref = this.type() === 'RestoreFile' ? this.pointSha() || undefined : undefined;
    this.api.searchFiles(this.ctx.id(), q, ref?.startsWith('checkpoint:') ? undefined : ref).subscribe({ next: h => this.fileHits.set(h), error: e => this.error.set(e) });
  }

  addManual() { const p = this.manualPath.trim(); if (p && !this.paths().includes(p)) this.paths.set([...this.paths(), p]); this.manualPath = ''; this.plan.set(null); }

  togglePath(p: string) {
    const s = this.paths();
    this.paths.set(s.includes(p) ? s.filter(x => x !== p) : [...s, p]);
    this.plan.set(null);
  }

  private request(): RecoveryRequest {
    const t = this.type()!;
    const strat = this.strategy();
    return {
      type: t,
      recoveryPoint: t === 'RevertSession' || t === 'RevertCommits' ? null : this.point() || null,
      paths: this.paths().length ? this.paths() : null,
      sessionId: t === 'RevertSession' ? this.sessionId() : null,
      commitShas: t === 'RevertCommits' ? this.commitShas() : null,
      branch: t === 'CloneProject' && this.branch.trim() ? this.branch.trim() : null,
      strategy: STRATEGIES[t].length > 1 ? strat : null,
      targetDirectory: strat === 'CloneToDirectory' && this.targetDirectory.trim() ? this.targetDirectory.trim() : null,
    };
  }

  preview(then?: () => void) {
    this.busy.set(true); this.error.set(null); this.ack = false;
    this.api.recoveryPreview(this.ctx.id(), this.request()).subscribe({
      next: p => { this.plan.set(p); this.strategy.set(p.strategy); this.busy.set(false); this.loadOps(); then?.(); },
      error: e => { this.plan.set(null); this.error.set(e); this.busy.set(false); },
    });
  }

  canExecute() {
    const p = this.plan();
    return !!p && (!(p.destructive || p.approvalRequired) || this.ack);
  }

  execute() {
    const p = this.plan(); if (!p) return;
    this.busy.set(true); this.error.set(null);
    this.api.recoveryExecute(this.ctx.id(), p.type, { type: p.type, previewOperationId: p.operationId, approved: true }).subscribe({
      next: r => {
        this.result.set(r); this.resolveStored(r); this.step.set(7); this.busy.set(false); this.loadOps();
        if (r.status === 'Executing') this.track(r.operationId);
        else this.ctx.reload();
      },
      error: e => { this.error.set(e); this.busy.set(false); },
    });
  }

  private resolveStored(r: RecoveryPlan) {
    this.stored.set(r.resultDurability ?? null);
    if (r.resultDurability || !r.resultCommitSha || r.status !== 'Succeeded') return;
    const sha = r.resultCommitSha;
    // The push of the recovery branch can take a moment; re-check once after a short delay.
    const look = (retry: boolean) => this.api.commit(this.ctx.id(), sha).subscribe({
      next: c => {
        if (this.result()?.operationId !== r.operationId) return;
        this.stored.set(c.commit.durability ?? null);
        if (retry && !isRemoteSafe(c.commit.durability)) setTimeout(() => look(false), 4000);
      },
      error: () => undefined,
    });
    look(true);
  }

  private track(opId: string) {
    this.poll?.unsubscribe();
    this.busy.set(true);
    this.poll = this.api.pollRecovery(this.ctx.id(), opId).subscribe({
      next: r => { this.result.set(r); if (r.status !== 'Executing') { this.resolveStored(r); this.busy.set(false); this.loadOps(); this.ctx.reload(); } },
      error: e => { this.error.set(e); this.busy.set(false); },
    });
  }

  showOp(o: RecoveryPlan) {
    this.result.set(o); this.resolveStored(o); this.plan.set(o); this.type.set(o.type); this.step.set(7);
    if (o.status === 'Executing') this.track(o.operationId);
  }

  browse(branch: string) { this.router.navigate([], { queryParams: { tab: 'files', ref: branch, path: null } }); }

  loadOps() { this.api.recoveryOperations(this.ctx.id()).subscribe({ next: o => this.ops.set(o), error: () => undefined }); }

  private resetState() {
    this.poll?.unsubscribe();
    this.step.set(1); this.type.set(null); this.point.set(''); this.pointSha.set(''); this.pointUnsafe.set(false); this.sessionId.set('');
    this.commitShas.set([]); this.paths.set([]); this.strategy.set(null); this.plan.set(null); this.result.set(null); this.error.set(null);
    this.prefillLabel.set(null); this.candidates.set(null); this.chosenCandidate.set(null); this.fileHits.set([]);
    this.query = this.branch = this.deletedFilter = this.fileQuery = this.manualPath = this.targetDirectory = ''; this.ack = false; this.busy.set(false);
  }

  reset() { this.resetState(); }
}
