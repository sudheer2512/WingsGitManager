// TypeScript mirror of src/Wings.GitManager.Contracts (camelCase JSON, enums as strings).

export type DurabilityState = 'WorkingTreeOnly' | 'LocalCommit' | 'RemotePushPending' | 'RemoteSynchronized' | 'IndependentlyBackedUp' | 'Failed' | 'RemoteDiverged';
export type CheckpointType = 'Initial' | 'PreChange' | 'PostChange' | 'Recovery' | 'Merge' | 'Manual' | 'External';
export type PushJobState = 'Pending' | 'Processing' | 'Succeeded' | 'RetryScheduled' | 'Conflict' | 'AuthenticationFailed' | 'PermanentFailure';
export type ChangeOperationState = 'Active' | 'Committing' | 'Completed' | 'Aborted' | 'Interrupted' | 'PolicyBlocked' | 'Failed';
export type RiskLevel = 'Low' | 'Medium' | 'High';
export type SessionState = 'Active' | 'Completed' | 'Merged' | 'Reverted' | 'Abandoned';
export type SessionIsolation = 'Worktree' | 'SharedWorkingDirectory';
export type RecoveryType = 'RestoreFile' | 'RestoreDeleted' | 'RestoreProject' | 'RevertSession' | 'RevertCommits' | 'CloneProject';
export type RecoveryStrategy = 'RestoreFilesAsNewCommit' | 'RecoveryBranchWorktree' | 'RollbackCommit' | 'RevertCommits' | 'CloneToDirectory';
export type RecoveryStatus = 'Previewed' | 'Executing' | 'Succeeded' | 'Conflict' | 'Failed' | 'Expired';
export type BackupState = 'Pending' | 'Running' | 'Completed' | 'Verified' | 'VerificationFailed' | 'Failed' | 'Expired';
export type JobState = 'Queued' | 'Running' | 'Succeeded' | 'Failed' | 'Cancelled';
export type RepositoryState = 'Provisioning' | 'Active' | 'Archived' | 'Deleted' | 'Failed';
export type RemoteStatus = 'Unknown' | 'InSync' | 'Ahead' | 'Behind' | 'Diverged' | 'Unreachable';
export type HostingProviderKind = 'Gitea' | 'FileSystem' | 'GenericRemote';
export type WorkspaceRole = 'ReadOnly' | 'Developer' | 'RepositoryAdministrator' | 'CompanyAdministrator';
export type WorktreeState = 'Active' | 'Released' | 'Abandoned' | 'Missing';
export type MergeRequestState = 'AwaitingApproval' | 'Conflict' | 'Merged' | 'Rejected' | 'Failed' | 'Mergeable';
export type SecretScanMode = 'Block' | 'Warn' | 'Off';
export type AlertSeverity = 'Info' | 'Warning' | 'Critical';
export type FileChangeKind = 'Added' | 'Modified' | 'Deleted' | 'Renamed' | 'Copied' | 'TypeChanged';

export const WORKSPACE_ROLES: WorkspaceRole[] = ['ReadOnly', 'Developer', 'RepositoryAdministrator', 'CompanyAdministrator'];
export const PUSH_JOB_STATES: PushJobState[] = ['Pending', 'Processing', 'Succeeded', 'RetryScheduled', 'Conflict', 'AuthenticationFailed', 'PermanentFailure'];
export const RECOVERY_STRATEGIES: RecoveryStrategy[] = ['RestoreFilesAsNewCommit', 'RecoveryBranchWorktree', 'RollbackCommit', 'RevertCommits', 'CloneToDirectory'];

export interface Paged<T> { items: T[]; total: number; page: number; pageSize: number; }

export interface RepositorySummary {
  id: string; name: string; companyId: string; companyName: string; projectId: string; projectName: string; projectExternalId?: string | null;
  defaultBranch: string; state: RepositoryState; lastCommitSha?: string | null; lastCommitAt?: string | null; lastPushAt?: string | null;
  remoteStatus: RemoteStatus; latestCheckpointId?: string | null; latestCheckpointDurability?: DurabilityState | null; storageBytes: number;
  hasUncommittedChanges: boolean; pendingPushes: number; createdAt: string;
}

export interface RepositorySettings {
  autoPush: boolean; requireRemoteCheckpointBeforeChanges: boolean; commitUncommittedChangesOnBegin: boolean; secretScanMode: SecretScanMode;
  excludedPathPatterns: string[]; protectedBranches: string[]; requireMergeApproval: boolean; worktreeRetentionDays: number;
  backupIntervalHours: number; backupRetentionCount: number; watchFileSystem: boolean; autoCommitExternalChanges: boolean;
}

export interface RepositoryDetail {
  summary: RepositorySummary; description?: string | null; remoteUrl: string; provider: HostingProviderKind;
  mainWorkingDirectory?: string | null; settings: RepositorySettings;
}

export interface WorkingTreeChange { path: string; kind: FileChangeKind; oldPath?: string | null; staged: boolean; untracked: boolean; }
export interface RepositoryStatus {
  repositoryId: string; workingDirectory: string; workingDirectoryExists: boolean; branch?: string | null; headSha?: string | null;
  remoteHeadSha?: string | null; ahead: number; behind: number; remoteStatus: RemoteStatus; isClean: boolean; changes: WorkingTreeChange[];
  activeOperationId?: string | null;
}

export interface FileChange { path: string; kind: FileChangeKind; oldPath?: string | null; additions: number; deletions: number; }

export interface ChangeOperation {
  id: string; repositoryId: string; sessionId?: string | null; agentId: string; initiatingUser?: string | null; state: ChangeOperationState;
  riskLevel: RiskLevel; riskReasons: string[]; description: string; userRequestRef?: string | null; workingDirectory: string; branch: string;
  startCommitSha: string; resultCommitSha?: string | null; preChangeCheckpointId?: string | null; postChangeCheckpointId?: string | null;
  createdAt: string; completedAt?: string | null; leaseExpiresAt: string; failureReason?: string | null; overrideApplied: boolean;
}

export interface Checkpoint {
  id: string; repositoryId: string; projectId: string; userId?: string | null; sessionId?: string | null; agentId?: string | null;
  changeOperationId?: string | null; branch: string; commitSha: string; createdAt: string; description?: string | null; type: CheckpointType;
  syncState: DurabilityState; backupState?: DurabilityState | null; remoteVerifiedAt?: string | null; buildStatus?: string | null; testStatus?: string | null;
}

export interface CommitSummary {
  sha: string; subject: string; authorName: string; authorEmail: string; authoredAt: string; committedAt: string; parents: string[];
  sessionId?: string | null; agentId?: string | null; changeOperationId?: string | null; initiatingUser?: string | null; isAutomated: boolean;
  durability?: DurabilityState | null;
}
export interface CommitDetail { commit: CommitSummary; message: string; files: FileChange[]; userRequestRef?: string | null; buildStatus?: string | null; testStatus?: string | null; }

export interface FileDiff { path: string; oldPath?: string | null; kind: FileChangeKind; additions: number; deletions: number; isBinary: boolean; patch?: string | null; }
export interface Diff { fromRef: string; toRef: string; files: FileDiff[]; totalAdditions: number; totalDeletions: number; truncated: boolean; }

export interface TreeEntry { path: string; name: string; type: 'dir' | 'file' | 'submodule'; size?: number | null; objectSha: string; }
export interface FileContent {
  path: string; ref: string; commitSha: string; size: number; isBinary: boolean; content?: string | null; language?: string | null;
  truncated: boolean; lastCommit?: CommitSummary | null;
}
export interface GrepHit { path: string; line: number; text: string; }

export interface Branch {
  name: string; headSha: string; remoteHeadSha?: string | null; ahead: number; behind: number; isDefault: boolean; isProtected: boolean;
  isCheckedOut: boolean; sessionId?: string | null; lastCommitAt?: string | null;
}
export interface MergeResult {
  mergeRequestId: string; state: MergeRequestState; conflicts: string[]; resultCommitSha?: string | null; approvalRequired: boolean;
  dryRun: boolean; files: FileChange[]; durability?: DurabilityState | null;
}
export interface MergeRequest {
  id: string; repositoryId: string; sourceBranch: string; targetBranch: string; sourceSha?: string | null; targetSha?: string | null;
  sessionId?: string | null; state: MergeRequestState; message?: string | null; conflicts: string[]; requestedBy?: string | null;
  approvedBy?: string | null; resultSha?: string | null; createdAt: string; completedAt?: string | null;
}
export interface Worktree {
  id: string; path: string; branch: string; sessionId?: string | null; ownerAgentId?: string | null; state: WorktreeState; baseCommitSha: string;
  headSha?: string | null; hasUncommittedChanges: boolean; createdAt: string; releasedAt?: string | null;
}

export interface ChangeSummary {
  since: string; until: string; commitCount: number; filesChanged: number; additions: number; deletions: number; commits: CommitSummary[];
  files: FileChange[]; agents: string[]; sessions: string[]; narrative: string;
}

export interface Session {
  id: string; repositoryId: string; agentId: string; initiatingUser?: string | null; title?: string | null; userRequestRef?: string | null;
  state: SessionState; isolation: SessionIsolation; branch: string; baseBranch: string; workingDirectory: string; worktreeId?: string | null;
  startingCommitSha: string; endingCommitSha?: string | null; commitCount: number; filesModified: number; pushStatus?: DurabilityState | null;
  testsExecuted: string[]; lastTestStatus?: string | null; recoveryAvailable: boolean; createdAt: string; endedAt?: string | null; lastActivityAt: string;
}

export interface BranchSync { branch: string; localSha?: string | null; remoteSha?: string | null; ahead: number; behind: number; status: RemoteStatus; }
export interface SyncStatus {
  repositoryId: string; remoteStatus: RemoteStatus; lastPushAt?: string | null; lastVerifiedSha?: string | null; pending: number; failed: number;
  conflicts: number; branches: BranchSync[];
}
export interface PushJob {
  id: string; repositoryId: string; repositoryName?: string | null; branch: string; commitSha: string; changeOperationId?: string | null;
  createdAt: string; retryCount: number; lastError?: string | null; state: PushJobState; expectedRemoteHead?: string | null;
  nextAttemptAt?: string | null; completedAt?: string | null;
}

export interface RecoveryRequest {
  type: RecoveryType; recoveryPoint?: string | null; paths?: string[] | null; sessionId?: string | null; commitShas?: string[] | null;
  branch?: string | null; strategy?: RecoveryStrategy | null; targetDirectory?: string | null; previewOperationId?: string | null;
  approved?: boolean; dryRun?: boolean; requestedBy?: string | null; reason?: string | null;
}
export interface RecoveryPlan {
  operationId: string; repositoryId: string; type: RecoveryType; status: RecoveryStatus; strategy: RecoveryStrategy; currentBranch?: string | null;
  currentCommit?: string | null; recoveryCommit?: string | null; affectedFiles: FileChange[]; additions: number; deletions: number;
  expectedConflicts: string[]; uncommittedChanges: string[]; approvalRequired: boolean; destructive: boolean; expectedResult: string;
  explanation: string; targetDirectory?: string | null; resultCommitSha?: string | null; resultBranch?: string | null;
  resultDurability?: DurabilityState | null; safetyCheckpointId?: string | null; error?: string | null; createdAt: string;
  completedAt?: string | null; expiresAt: string; jobId?: string | null;
}
export interface RecoveryCandidatesRequest { query?: string | null; since?: string | null; until?: string | null; pathContains?: string | null; sessionId?: string | null; agentId?: string | null; limit?: number; }
export interface RecoveryInterpretation {
  suggestedType?: RecoveryType | null; since?: string | null; until?: string | null; keywords: string[]; lastSession: boolean;
  beforeLastRefactoring: boolean; deletedContent: boolean; changeSummaryOnly: boolean; preserveRecentWork: boolean; explanation: string;
}
export interface RecoveryCandidate {
  recoveryPoint: string; commitSha: string; checkpointId?: string | null; sessionId?: string | null; description: string; timestamp: string;
  durability?: DurabilityState | null; matchingPaths: string[]; score: number; suggestedType: RecoveryType; suggestedPaths?: string[] | null;
  suggestedCommits?: string[] | null;
}
export interface RecoveryCandidatesResponse { interpretation: RecoveryInterpretation; candidates: RecoveryCandidate[]; ambiguous: boolean; changeSummary?: ChangeSummary | null; }
export interface DeletedFile { path: string; deletedInCommit: string; deletedAt: string; lastVersionCommit: string; deletedBy?: string | null; }

export interface BackupVerification {
  id: string; startedAt: string; completedAt?: string | null; succeeded: boolean; checksumOk: boolean; bundleOk: boolean; fsckOk: boolean;
  refsMatch: boolean; testRestoreOk: boolean; details?: string | null;
}
export interface Backup {
  id: string; repositoryId?: string | null; kind: string; state: BackupState; storageProvider: string; storageKey?: string | null; sizeBytes: number;
  sha256?: string | null; encrypted: boolean; headSha?: string | null; refCount: number; trigger: string; createdAt: string; completedAt?: string | null;
  verifiedAt?: string | null; error?: string | null; lastVerification?: BackupVerification | null;
}
export interface RestoreBackupRequest { target: 'directory' | 'remote'; targetDirectory?: string | null; approved: boolean; }

export interface Job {
  id: string; type: string; state: JobState; repositoryId?: string | null; result?: string | null; error?: string | null; attempts: number;
  progress?: string | null; createdAt: string; startedAt?: string | null; completedAt?: string | null;
}
export interface AcceptedJob { jobId: string; statusUrl: string; resourceId?: string | null; }

export interface DailyCount { day: string; count: number; }
export interface Alert {
  id: string; repositoryId?: string | null; repositoryName?: string | null; category: string; severity: AlertSeverity; message: string;
  createdAt: string; lastSeenAt: string; resolvedAt?: string | null;
}
export interface DashboardSummary {
  totalRepositories: number; activeRepositories: number; commitsToday: number; pendingPushes: number; failedPushes: number;
  repositoriesWithLocalOnlyChanges: number; repositoriesWithRemoteDivergence: number; lastVerifiedBackupAt?: string | null;
  repositoriesWithoutVerifiedBackup: number; storageBytes: number; highRiskOperations: number; blockedHighRiskOperations: number;
  activeAISessions: number; openAlerts: number; commitsPerDay: DailyCount[]; recentAlerts: Alert[];
}

export interface HealthComponent { name: string; status: string; detail?: string | null; durationMs?: number | null; }
export interface SystemHealth { status: string; components: HealthComponent[]; checkedAt: string; }
export interface Volume { purpose: string; path: string; totalBytes: number; freeBytes: number; usedPercent: number; status: string; }
export interface RepositoryStorage { repositoryId: string; name: string; workingDirectoryBytes: number; gitObjectBytes: number; }
export interface StorageReport { volumes: Volume[]; repositories: RepositoryStorage[]; }

export interface AuditLog {
  id: string; timestamp: string; userId?: string | null; actor: string; actorType: string; companyId?: string | null; repositoryId?: string | null;
  action: string; target?: string | null; outcome: string; details?: string | null; correlationId?: string | null;
}

export interface Company { id: string; name: string; slug: string; externalId?: string | null; gitNamespace?: string | null; isActive: boolean; repositoryCount: number; }
export interface Project { id: string; companyId: string; name: string; slug: string; externalId?: string | null; repositoryCount: number; }
export interface Membership { companyId: string; companyName: string; role: WorkspaceRole; }
export interface User { id: string; email: string; displayName: string; isSystemAdministrator: boolean; isServiceAccount: boolean; isActive: boolean; memberships: Membership[]; }
export interface CreateUserRequest { email: string; displayName: string; isSystemAdministrator: boolean; isServiceAccount: boolean; companyId?: string | null; role?: WorkspaceRole | null; }
export interface RepositoryPermission { id: string; repositoryId: string; userId: string; userEmail: string; role: WorkspaceRole; }
export interface ApiToken { id: string; name: string; prefix: string; userId: string; scopes: string[]; createdAt: string; expiresAt?: string | null; revokedAt?: string | null; lastUsedAt?: string | null; }
export interface CreatedApiToken { token: ApiToken; secret: string; }
export interface CreateApiTokenRequest { name: string; userId?: string | null; scopes: string[]; expiresInDays?: number | null; }
export interface Policy { id: string; scope: string; scopeId?: string | null; key: string; enabled: boolean; settingsJson: string; updatedAt: string; updatedBy?: string | null; }
export interface UpsertPolicyRequest { scope: string; scopeId?: string | null; key: string; enabled: boolean; settingsJson: string; }
export interface Me { userId: string; email: string; displayName: string; isSystemAdministrator: boolean; memberships: Membership[]; scopes: string[]; }

export interface ApiProblem { status: number; code?: string; detail: string; correlationId?: string | null; details?: unknown; }

export interface Notification { id: number; type: string; repositoryId?: string | null; companyId?: string | null; payload: any; createdAt: string; }

/** Default high-risk policy (mirrors HighRiskPolicySettings in Wings.GitManager.Core/Policies/HighRiskPolicy.cs). */
export const DEFAULT_HIGH_RISK_SETTINGS = {
  requireVerifiedRemoteCheckpoint: true,
  allowAdministratorOverride: true,
  multipleFileDeleteThreshold: 3,
  largeChangeFileThreshold: 25,
  mediumChangeFileThreshold: 10,
  renameThreshold: 10,
  sourceDirectoryDeletionIsHighRisk: true,
  migrationPatterns: ['**/Migrations/**', '**/migrations/**', '**/migration/**', '**/db/migrate/**', '**/alembic/**', '**/flyway/**', '**/*.sql'],
  authPatterns: ['**/auth/**', '**/authentication/**', '**/authorization/**', '**/identity/**', '**/security/**',
    '**/*Auth*.*', '**/*auth*.*', '**/*Login*.*', '**/*Permission*.*', '**/*.guard.ts', '**/*Jwt*.*', '**/*Oidc*.*'],
  deploymentPatterns: ['Dockerfile', '**/Dockerfile', '**/docker-compose*.yml', '**/docker-compose*.yaml', '**/*.bicep', '**/*.tf',
    '**/k8s/**', '**/kubernetes/**', '**/helm/**', '**/.github/workflows/**', '**/azure-pipelines*.yml', '.gitlab-ci.yml',
    '**/web.config', '**/appsettings.Production.json', '**/nginx*.conf', '**/deploy/**', '**/deployment/**'],
  sharedLibraryPatterns: ['**/shared/**', '**/common/**', '**/libs/**', '**/*.Shared/**', '**/*.Common/**', '**/packages/**'],
  dependencyManifests: ['package.json', 'package-lock.json', 'yarn.lock', 'pnpm-lock.yaml', '*.csproj', 'Directory.Packages.props', 'Directory.Build.props',
    'packages.config', 'nuget.config', 'requirements.txt', 'pyproject.toml', 'Pipfile.lock', 'poetry.lock', 'pom.xml',
    'build.gradle', 'build.gradle.kts', 'go.mod', 'go.sum', 'Cargo.toml', 'Cargo.lock', 'Gemfile', 'Gemfile.lock', 'composer.json'],
  highRiskCategories: ['large-refactor', 'refactor', 'restructure', 'replace-implementation', 'multi-project', 'bulk-rename', 'bulk-delete', 'dependency-update', 'migration'],
};
