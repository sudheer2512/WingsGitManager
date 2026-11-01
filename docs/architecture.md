# Architecture

## Components

| # | Spec component | Implementation |
|---|---|---|
| 1 | Repository Management Service | `RepositoryService` (create/import/update/archive/delete/status), `AdminService` (companies, projects) |
| 2 | Git Execution Service | `GitExecutor` (the only process launcher) + `GitClient` (typed operations) |
| 3 | Change Tracking Service | `ChangeOperationService` (begin/complete/abort/reconcile) + `FileWatcherService` (external edits) |
| 4 | Commit & Checkpoint Service | `CommitRecorder`, `CheckpointService`, `CommitMessageBuilder` |
| 5 | AI Session Management | `SessionService` |
| 6 | Remote Synchronization | `SyncService` (durable queue, retries, verification) + `SyncQueueWorker`, `SyncOperations` (API facade) |
| 7 | Branch & Worktree Management | `BranchService` (branches, merges, merge requests), `WorktreeService` |
| 8 | Recovery & Restoration | `RecoveryService`, `ConversationalRecoveryService`, `RecoveryIntentParser` |
| 9 | Independent Backup | `BackupService`, `IBackupStore` (`FileSystemBackupStore`, `S3BackupStore`), `BackupCrypto`, `BackupScheduler` |
| 10 | Authentication & Authorization | `ApiTokenAuthenticationHandler`, OIDC JwtBearer, `AccessControl` |
| 11 | Repository Monitoring | `MonitoringService` + `MonitoringWorker` |
| 12 | Wings Code Integration API | REST `/api/v1` + `Wings.GitManager.Client` SDK + SignalR hub |
| 13 | Angular Management UI | `web/` (served from the API's `wwwroot`) |
| 14 | Background Job Processing | `JobService` + `JobWorker` (`BackgroundJobs` table) |

Hosting providers implement `IGitHostingProvider`: `GiteaHostingProvider` (default; REST for organizations, repositories, branch protection, webhooks, collaborators, commit verification), `FileSystemHostingProvider` (bare repositories on a separate volume — air-gapped installs and tests) and `GenericRemoteProvider` (pre-provisioned GitHub/GitLab/Azure DevOps remotes; push/fetch/verify only). A GitHub/GitLab/Azure DevOps provisioning adapter is a new `IGitHostingProvider` implementation; Wings Code's API does not change.

## Data model

Git is authoritative for code and history. SQL Server (SQLite in development) stores metadata and operational state — the tables required by the specification plus `ApiTokens`, `CompanyMemberships`, `MutationLocks`, `IdempotencyRecords`, `MonitoringAlerts`, `NotificationEvents` (SignalR outbox) and `MergeRequests`. All primary keys are application-generated GUIDs; commits are identified by full SHA. Hierarchy: `Company → Project → Repository → Branch → AI Session → Commit`.

## Directory layout on the Wings Code host

```
<workspace root>/<company>/<project>/main/                 main working directory (default branch)
<workspace root>/<company>/<project>/worktrees/session-*/  isolated AI session worktrees (ai/session-* branches)
<workspace root>/<company>/<project>/recovery/             clones / restores into new directories
<data dir>/mirrors/<repo>.git                              server-side mirrors (browsing + backups when local copies are gone)
<data dir>/git-home, git-empty-hooks, keys/, backup-work/  private git configuration, data-protection keys, scratch
```

The API and the worker must see the same workspace volume (same host, or a shared volume). Central repositories never depend on these directories.

## Key flows

**Begin change** — authorize → resolve workspace (session worktree or main) → evaluate declared intent with the high-risk policy → acquire the workspace mutation lock → reject if another operation's lease is active → commit pre-existing permitted uncommitted work as a `checkpoint:` commit (pre-change checkpoint) → if high risk (or the repository requires it) push and **verify** the checkpoint; on failure block (422) unless an audited administrator override → create the lease.

**Complete change** — mark `Committing` (crash-safe) → `git status` → select files (operation scope, not excluded, not pre-existing) → stage → scan staged diff for secrets (block) → commit with trailers (`Wings-Operation-Id`, `Wings-Session-Id`, `Wings-Agent-Id`, `Wings-Initiated-By`, `Wings-Request-Ref`) → evaluate actual risk (alert on undeclared high-risk changes) → record metadata + post-change checkpoint → enqueue push (persisted) → push now (bounded) → verify → return durability.

**Push/verify** — never `--force`/`--mirror`/`--delete` (rejected by `GitExecutor`). Non-fast-forward ⇒ `Conflict` + `RemoteDiverged`, local commits kept. Network errors ⇒ exponential retry (`RetryScheduled`), auth errors ⇒ `AuthenticationFailed`, exhaustion ⇒ `PermanentFailure` + critical alert. Verification: remote branch head (`ls-remote`) equals or descends from the commit **and** the provider API (Gitea `git/commits/{sha}`) reports it.

**Concurrency** — database-backed short-lived mutation locks (shared by API and worker) per working directory/repository, workspace leases per change operation, optimistic concurrency via `expectedHead` and a row version on change operations, CAS ref updates for merges.

**Recovery** — preview (persisted, expiring) → explicit approval when the current branch changes → safety checkpoint of uncommitted work → apply as new commits (`git checkout <rev> -- paths`, `read-tree -u --reset`, `revert --no-commit`, reverse-patch `apply --3way` for path-limited/merge reverts) → push + verify. Conflicts are simulated in a disposable detached worktree during preview.

**Backups** — refresh the mirror from the central server (no prune: deleted branches remain recoverable) → add local-only branches as `refs/wings-local/*` → `git bundle --all` + metadata JSON → AES-256-GCM chunked encryption → immutable objects in the store + manifest (refs + SHA-256) → immediate verification (checksum, `bundle verify`, test restore, `fsck --full`, ref comparison) → checkpoints contained in the backup become `IndependentlyBackedUp` → retention (never removes the newest verified backups).

## Real-time notifications

Services write `NotificationEvents` (outbox) in the same unit of work; the API's `NotificationRelay` streams them to SignalR groups (`company:{id}`, `sysadmin`). This works when the worker runs in a different process or host without a message broker.
