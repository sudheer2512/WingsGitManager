import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, switchMap, takeWhile, timer } from 'rxjs';
import * as M from './models';

type Q = Record<string, string | number | boolean | null | undefined>;

function params(q?: Q): HttpParams {
  let p = new HttpParams();
  for (const [k, v] of Object.entries(q ?? {})) if (v !== null && v !== undefined && v !== '') p = p.set(k, String(v));
  return p;
}

const BASE = '/api/v1';

@Injectable({ providedIn: 'root' })
export class ApiService {
  private http = inject(HttpClient);

  private get<T>(url: string, q?: Q) { return this.http.get<T>(BASE + url, { params: params(q) }); }
  private post<T>(url: string, body: unknown = {}, q?: Q) { return this.http.post<T>(BASE + url, body, { params: params(q) }); }
  private put<T>(url: string, body: unknown) { return this.http.put<T>(BASE + url, body); }
  private patch<T>(url: string, body: unknown) { return this.http.patch<T>(BASE + url, body); }
  private del<T>(url: string, q?: Q) { return this.http.delete<T>(BASE + url, { params: params(q) }); }

  // ---- identity ----
  me() { return this.get<M.Me>('/me'); }

  // ---- repositories ----
  repositories(q: { companyId?: string; projectId?: string; search?: string; state?: string; page?: number; pageSize?: number } = {}) {
    return this.get<M.Paged<M.RepositorySummary>>('/repositories', q);
  }
  repository(id: string) { return this.get<M.RepositoryDetail>(`/repositories/${id}`); }
  updateRepository(id: string, body: { description?: string | null; defaultBranch?: string | null; settings?: M.RepositorySettings }) {
    return this.patch<M.RepositoryDetail>(`/repositories/${id}`, body);
  }
  archiveRepository(id: string) { return this.post<void>(`/repositories/${id}/archive`); }
  status(id: string) { return this.get<M.RepositoryStatus>(`/repositories/${id}/status`); }
  workingTreeDiff(id: string) { return this.get<M.Diff>(`/repositories/${id}/diff`, { patch: true }); }
  diff(id: string, from: string, to: string, path?: string) { return this.get<M.Diff>(`/repositories/${id}/diff`, { from, to, path, patch: true }); }

  // ---- changes ----
  changes(id: string, q: { state?: string; page?: number; pageSize?: number } = {}) { return this.get<M.Paged<M.ChangeOperation>>(`/repositories/${id}/changes`, q); }

  // ---- history & files ----
  commits(id: string, q: { ref?: string; path?: string; since?: string; until?: string; author?: string; sessionId?: string; page?: number; pageSize?: number } = {}) {
    return this.get<M.Paged<M.CommitSummary>>(`/repositories/${id}/commits`, q);
  }
  commit(id: string, sha: string) { return this.get<M.CommitDetail>(`/repositories/${id}/commits/${sha}`); }
  commitDiff(id: string, sha: string, path?: string) { return this.get<M.Diff>(`/repositories/${id}/commits/${sha}/diff`, { path, patch: true }); }
  compare(id: string, base: string, head: string, path?: string) { return this.get<M.Diff>(`/repositories/${id}/compare`, { base, head, path, patch: true }); }
  tree(id: string, ref?: string, path?: string) { return this.get<M.TreeEntry[]>(`/repositories/${id}/tree`, { ref, path }); }
  fileContent(id: string, path: string, ref?: string) { return this.get<M.FileContent>(`/repositories/${id}/files/content`, { ref, path }); }
  fileHistory(id: string, path: string, ref?: string, page = 1) { return this.get<M.Paged<M.CommitSummary>>(`/repositories/${id}/files/history`, { path, ref, page, pageSize: 50 }); }
  searchFiles(id: string, q: string, ref?: string) { return this.get<string[]>(`/repositories/${id}/files/search`, { q, ref }); }
  grep(id: string, q: string, ref?: string) { return this.get<M.GrepHit[]>(`/repositories/${id}/grep`, { q, ref }); }
  rawFile(id: string, path: string, ref?: string) {
    return this.http.get(`${BASE}/repositories/${id}/files/raw`, { params: params({ path, ref }), responseType: 'blob' });
  }
  deletedFiles(id: string, pathContains?: string) { return this.get<M.DeletedFile[]>(`/repositories/${id}/deleted-files`, { pathContains }); }
  checkpoints(id: string, q: { type?: string; sessionId?: string; page?: number; pageSize?: number } = {}) {
    return this.get<M.Paged<M.Checkpoint>>(`/repositories/${id}/checkpoints`, q);
  }
  changesSummary(id: string, hours = 24) { return this.get<M.ChangeSummary>(`/repositories/${id}/changes-summary`, { hours }); }

  // ---- branches & worktrees ----
  branches(id: string) { return this.get<M.Branch[]>(`/repositories/${id}/branches`); }
  merge(id: string, body: { sourceBranch: string; targetBranch: string; message?: string; dryRun: boolean; expectedTargetHead?: string }) {
    return this.post<M.MergeResult>(`/repositories/${id}/branches/merge`, body);
  }
  mergeRequests(id: string) { return this.get<M.MergeRequest[]>(`/repositories/${id}/merge-requests`); }
  approveMerge(id: string, mrId: string, approve: boolean) { return this.post<M.MergeResult>(`/repositories/${id}/merge-requests/${mrId}/approve`, {}, { approve }); }
  worktrees(id: string, includeReleased = false) { return this.get<M.Worktree[]>(`/repositories/${id}/worktrees`, { includeReleased }); }
  releaseWorktree(id: string, worktreeId: string, preserveUncommittedChanges = true) {
    return this.post<M.Worktree>(`/repositories/${id}/worktrees/${worktreeId}/release`, { preserveUncommittedChanges });
  }

  // ---- sync ----
  syncStatus(id: string) { return this.get<M.SyncStatus>(`/repositories/${id}/sync/status`); }
  repoQueue(id: string, state?: string) { return this.get<M.PushJob[]>(`/repositories/${id}/sync/queue`, { state }); }
  push(id: string, branch?: string) { return this.post<M.SyncStatus>(`/repositories/${id}/sync/push`, { branch, wait: true }); }
  fetch(id: string) { return this.post<{ repositoryId: string; branches: M.BranchSync[] }>(`/repositories/${id}/sync/fetch`); }
  retry(id: string, jobId?: string) { return this.post<{ retried: number }>(`/repositories/${id}/sync/retry`, { jobId }); }
  queue(state?: string, take = 200) { return this.get<M.PushJob[]>('/sync/queue', { state, take }); }

  // ---- recovery ----
  recoveryPreview(id: string, req: M.RecoveryRequest) { return this.post<M.RecoveryPlan>(`/repositories/${id}/recovery/preview`, req); }
  recoveryExecute(id: string, type: M.RecoveryType, req: M.RecoveryRequest) {
    const route: Record<M.RecoveryType, string> = {
      RestoreFile: 'restore-file', RestoreDeleted: 'restore-file', RestoreProject: 'restore-project',
      RevertSession: 'revert-session', RevertCommits: 'revert-commits', CloneProject: 'clone',
    };
    return this.post<M.RecoveryPlan>(`/repositories/${id}/recovery/${route[type]}`, req);
  }
  recoveryOperation(id: string, opId: string) { return this.get<M.RecoveryPlan>(`/repositories/${id}/recovery/operations/${opId}`); }
  recoveryOperations(id: string) { return this.get<M.RecoveryPlan[]>(`/repositories/${id}/recovery/operations`); }
  recoveryCandidates(id: string, req: M.RecoveryCandidatesRequest) { return this.post<M.RecoveryCandidatesResponse>(`/repositories/${id}/recovery/candidates`, req); }

  // ---- backups ----
  backups(id: string) { return this.get<M.Backup[]>(`/repositories/${id}/backups`); }
  createBackup(id: string) { return this.post<M.AcceptedJob>(`/repositories/${id}/backups`); }
  verifyBackup(id: string, backupId: string) { return this.post<M.AcceptedJob>(`/repositories/${id}/backups/${backupId}/verify`); }
  restoreBackup(id: string, backupId: string, req: M.RestoreBackupRequest) { return this.post<M.AcceptedJob>(`/repositories/${id}/backups/${backupId}/restore`, req); }

  // ---- permissions ----
  permissions(id: string) { return this.get<M.RepositoryPermission[]>(`/repositories/${id}/permissions`); }
  setPermission(id: string, userId: string, role: M.WorkspaceRole) { return this.put<void>(`/repositories/${id}/permissions`, { userId, role }); }
  removePermission(id: string, userId: string) { return this.del<void>(`/repositories/${id}/permissions/${userId}`); }

  // ---- AI sessions ----
  sessions(q: { repositoryId?: string; state?: string; page?: number; pageSize?: number } = {}) { return this.get<M.Paged<M.Session>>('/ai/sessions', q); }
  session(sessionId: string) { return this.get<M.Session>(`/ai/sessions/${sessionId}`); }
  sessionCommits(sessionId: string, includeCheckpoints = false) { return this.get<M.CommitSummary[]>(`/ai/sessions/${sessionId}/commits`, { includeCheckpoints }); }
  sessionChanges(sessionId: string) { return this.get<M.Diff>(`/ai/sessions/${sessionId}/changes`); }
  endSession(sessionId: string, body: { mergeIntoBase: boolean; releaseWorktree: boolean }) { return this.post<M.Session>(`/ai/sessions/${sessionId}/end`, body); }

  // ---- monitoring ----
  dashboard(q: { companyId?: string; projectId?: string; user?: string; from?: string; to?: string } = {}) { return this.get<M.DashboardSummary>('/dashboard/summary', q); }
  health() { return this.get<M.SystemHealth>('/system/health'); }
  storage() { return this.get<M.StorageReport>('/system/storage'); }
  alerts(includeResolved = false, repositoryId?: string) { return this.get<M.Alert[]>('/alerts', { includeResolved, repositoryId }); }
  resolveAlert(alertId: string) { return this.post<void>(`/alerts/${alertId}/resolve`); }
  jobs(q: { repositoryId?: string; state?: string; take?: number } = {}) { return this.get<M.Job[]>('/jobs', q); }
  job(jobId: string) { return this.get<M.Job>(`/jobs/${jobId}`); }
  audit(q: { companyId?: string; repositoryId?: string; action?: string; actor?: string; outcome?: string; from?: string; to?: string; page?: number; pageSize?: number }) {
    return this.get<M.Paged<M.AuditLog>>('/audit-logs', q);
  }

  // ---- administration ----
  companies() { return this.get<M.Company[]>('/companies'); }
  createCompany(body: { name: string; slug?: string | null; externalId?: string | null }) { return this.post<M.Company>('/companies', body); }
  projects(companyId?: string) { return this.get<M.Project[]>('/projects', { companyId }); }
  users(companyId?: string) { return this.get<M.User[]>('/users', { companyId }); }
  createUser(body: M.CreateUserRequest) { return this.post<M.User>('/users', body); }
  setMembership(userId: string, companyId: string, role: M.WorkspaceRole) { return this.put<void>(`/users/${userId}/memberships`, { companyId, role }); }
  removeMembership(userId: string, companyId: string) { return this.del<void>(`/users/${userId}/memberships/${companyId}`); }
  tokens() { return this.get<M.ApiToken[]>('/tokens'); }
  createToken(body: M.CreateApiTokenRequest) { return this.post<M.CreatedApiToken>('/tokens', body); }
  revokeToken(tokenId: string) { return this.del<void>(`/tokens/${tokenId}`); }
  policies() { return this.get<M.Policy[]>('/policies'); }
  upsertPolicy(body: M.UpsertPolicyRequest) { return this.put<M.Policy>('/policies', body); }

  /** Polls a background job every 1.5 s until it reaches a terminal state (emits every snapshot, the last one included). */
  pollJob(jobId: string): Observable<M.Job> {
    return timer(0, 1500).pipe(
      switchMap(() => this.job(jobId)),
      takeWhile(j => j.state === 'Queued' || j.state === 'Running', true),
    );
  }

  /** Polls a recovery operation until it leaves the Executing state. */
  pollRecovery(repoId: string, opId: string): Observable<M.RecoveryPlan> {
    return timer(0, 1500).pipe(
      switchMap(() => this.recoveryOperation(repoId, opId)),
      takeWhile(p => p.status === 'Executing', true),
    );
  }
}
