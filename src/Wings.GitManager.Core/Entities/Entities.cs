using Wings.GitManager.Contracts;

namespace Wings.GitManager.Core.Entities;

// Git is authoritative for code and history; these tables hold metadata, policy and operational state.
// All timestamps are UTC DateTime (SQLite cannot order DateTimeOffset).

public class Company
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string? ExternalId { get; set; }
    /// <summary>Gitea organization (or equivalent namespace) that holds this company's repositories.</summary>
    public string? GitNamespace { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    /// <summary>OIDC subject ("iss|sub") once the user has signed in.</summary>
    public string? ExternalSubject { get; set; }
    public bool IsSystemAdministrator { get; set; }
    /// <summary>Service identity (e.g. the Wings Code AI agent service).</summary>
    public bool IsServiceAccount { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<CompanyMembership> Memberships { get; set; } = [];
}

public class CompanyMembership
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Company? Company { get; set; }
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public WorkspaceRole Role { get; set; }
}

public class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Company? Company { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string? ExternalId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Repository
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Company? Company { get; set; }
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string? Description { get; set; }
    public string DefaultBranch { get; set; } = "main";
    public RepositoryState State { get; set; } = RepositoryState.Provisioning;
    public string? LastCommitSha { get; set; }
    public DateTime? LastCommitAt { get; set; }
    public DateTime? LastPushAt { get; set; }
    public string? LastVerifiedSha { get; set; }
    public RemoteStatus RemoteStatus { get; set; } = RemoteStatus.Unknown;
    public Guid? LatestCheckpointId { get; set; }
    public long StorageBytes { get; set; }
    public bool HasUncommittedChanges { get; set; }
    public DateTime? ExternalChangeDetectedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ArchivedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
    public string? LastIndexedSha { get; set; }
    public List<RepositoryRemote> Remotes { get; set; } = [];
    public RepositorySettings? Settings { get; set; }
}

public class RepositoryRemote
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RepositoryId { get; set; }
    public string Name { get; set; } = "origin";
    public HostingProviderKind Provider { get; set; }
    /// <summary>Clone URL without credentials.</summary>
    public string Url { get; set; } = "";
    public string? Owner { get; set; }
    public string? RemoteRepositoryName { get; set; }
    public string? ProviderRepositoryId { get; set; }
    public string? CredentialUsername { get; set; }
    /// <summary>Data-protection encrypted secret (generic remotes only; Gitea uses the service credential).</summary>
    public string? EncryptedCredential { get; set; }
    public bool IsPrimary { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class RepositorySettings
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RepositoryId { get; set; }
    public bool AutoPush { get; set; } = true;
    public bool RequireRemoteCheckpointBeforeChanges { get; set; }
    public bool CommitUncommittedChangesOnBegin { get; set; } = true;
    public SecretScanMode SecretScanMode { get; set; } = SecretScanMode.Block;
    public List<string> ExcludedPathPatterns { get; set; } = [];
    public List<string> ProtectedBranches { get; set; } = ["main", "release/*"];
    public bool RequireMergeApproval { get; set; }
    public int WorktreeRetentionDays { get; set; } = 14;
    public int BackupIntervalHours { get; set; } = 24;
    public int BackupRetentionCount { get; set; } = 30;
    public bool WatchFileSystem { get; set; } = true;
    public bool AutoCommitExternalChanges { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class RepositoryPermission
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RepositoryId { get; set; }
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public WorkspaceRole Role { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum WorkingDirectoryKind { Main, Worktree, RecoveryWorktree, Clone }

public class WorkingDirectory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RepositoryId { get; set; }
    public string Path { get; set; } = "";
    public WorkingDirectoryKind Kind { get; set; }
    public string? Branch { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastSeenAt { get; set; }
}

public class Worktree
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RepositoryId { get; set; }
    public Guid? WorkingDirectoryId { get; set; }
    public string Path { get; set; } = "";
    public string Branch { get; set; } = "";
    public Guid? SessionId { get; set; }
    public string? OwnerAgentId { get; set; }
    public WorktreeState State { get; set; } = WorktreeState.Active;
    public string BaseCommitSha { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReleasedAt { get; set; }
}

public class Branch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RepositoryId { get; set; }
    public string Name { get; set; } = "";
    public string? HeadSha { get; set; }
    public string? RemoteHeadSha { get; set; }
    public bool IsSessionBranch { get; set; }
    public Guid? SessionId { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class CommitMetadata
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RepositoryId { get; set; }
    public string Sha { get; set; } = "";
    public string Branch { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Message { get; set; } = "";
    public string AuthorName { get; set; } = "";
    public string AuthorEmail { get; set; } = "";
    public DateTime CommittedAt { get; set; }
    public string? InitiatingUser { get; set; }
    public Guid? SessionId { get; set; }
    public string? AgentId { get; set; }
    public Guid? ChangeOperationId { get; set; }
    public string? UserRequestRef { get; set; }
    public string? CommitType { get; set; }
    public string? BuildStatus { get; set; }
    public string? TestStatus { get; set; }
    public bool IsAutomated { get; set; }
    public int FilesChanged { get; set; }
    public int Additions { get; set; }
    public int Deletions { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<CommitFileChange> Files { get; set; } = [];
}

public class CommitFileChange
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommitMetadataId { get; set; }
    public string Path { get; set; } = "";
    public string? OldPath { get; set; }
    public FileChangeKind Kind { get; set; }
    public int Additions { get; set; }
    public int Deletions { get; set; }
}

public class AISession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RepositoryId { get; set; }
    public string AgentId { get; set; } = "";
    public string? InitiatingUser { get; set; }
    public string? Title { get; set; }
    public string? UserRequestRef { get; set; }
    public SessionState State { get; set; } = SessionState.Active;
    public SessionIsolation Isolation { get; set; }
    public string Branch { get; set; } = "";
    public string BaseBranch { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public Guid? WorktreeId { get; set; }
    public string StartingCommitSha { get; set; } = "";
    public string? EndingCommitSha { get; set; }
    public List<string> TestsExecuted { get; set; } = [];
    public string? LastTestStatus { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }
}

public class AISessionCommit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public Guid RepositoryId { get; set; }
    public string Sha { get; set; } = "";
    public Guid? CommitMetadataId { get; set; }
    public int Sequence { get; set; }
    public bool IsCheckpointOnly { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class ChangeOperation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RepositoryId { get; set; }
    public Guid? SessionId { get; set; }
    public string AgentId { get; set; } = "";
    public string? InitiatingUser { get; set; }
    public Guid? CallerUserId { get; set; }
    public ChangeOperationState State { get; set; } = ChangeOperationState.Active;
    public RiskLevel RiskLevel { get; set; }
    public List<string> RiskReasons { get; set; } = [];
    public string Description { get; set; } = "";
    public string? UserRequestRef { get; set; }
    public string WorkingDirectory { get; set; } = "";
    public string Branch { get; set; } = "";
    public string StartCommitSha { get; set; } = "";
    public string? ResultCommitSha { get; set; }
    public Guid? PreChangeCheckpointId { get; set; }
    public Guid? PostChangeCheckpointId { get; set; }
    public List<string> ScopePaths { get; set; } = [];
    /// <summary>Uncommitted paths that existed before the operation and do not belong to it.</summary>
    public List<string> ExcludedPaths { get; set; } = [];
    /// <summary>False when the policy engine blocked the operation at begin.</summary>
    public bool Authorized { get; set; }
    public string? IntentJson { get; set; }
    public Guid? PolicyOverrideId { get; set; }
    public string? PreservedBranch { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public DateTime LeaseExpiresAt { get; set; }
    public string? FailureReason { get; set; }
    /// <summary>Optimistic concurrency token.</summary>
    public Guid Version { get; set; } = Guid.NewGuid();
}

public class Checkpoint
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RepositoryId { get; set; }
    public Guid ProjectId { get; set; }
    public string? UserId { get; set; }
    public Guid? SessionId { get; set; }
    public string? AgentId { get; set; }
    public Guid? ChangeOperationId { get; set; }
    public string Branch { get; set; } = "";
    public string CommitSha { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? Description { get; set; }
    public CheckpointType Type { get; set; }
    public DurabilityState SyncState { get; set; } = DurabilityState.LocalCommit;
    /// <summary>IndependentlyBackedUp once a verified backup contains the commit.</summary>
    public DurabilityState? BackupState { get; set; }
    public DateTime? RemoteVerifiedAt { get; set; }
    public DateTime? BackedUpAt { get; set; }
    public string? BuildStatus { get; set; }
    public string? TestStatus { get; set; }
}

public class PushQueueItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RepositoryId { get; set; }
    public string Branch { get; set; } = "";
    public string CommitSha { get; set; } = "";
    public Guid? ChangeOperationId { get; set; }
    public string WorkingDirectory { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
    public PushJobState State { get; set; } = PushJobState.Pending;
    public string? ExpectedRemoteHead { get; set; }
    public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;
    public string? LockedBy { get; set; }
    public DateTime? LockedUntil { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public class SyncAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PushQueueItemId { get; set; }
    public Guid RepositoryId { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
    public PushJobState Outcome { get; set; }
    public string? Error { get; set; }
    public string? RemoteHeadBefore { get; set; }
    public string? RemoteHeadAfter { get; set; }
    public string? Worker { get; set; }
}

public class RecoveryOperation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RepositoryId { get; set; }
    public RecoveryType Type { get; set; }
    public RecoveryStatus Status { get; set; } = RecoveryStatus.Previewed;
    public RecoveryStrategy Strategy { get; set; }
    public string? RequestedBy { get; set; }
    public Guid? CallerUserId { get; set; }
    public Guid? SessionId { get; set; }
    public string? Branch { get; set; }
    public string? CurrentCommitSha { get; set; }
    public string? RecoveryCommitSha { get; set; }
    public List<string> SelectedPaths { get; set; } = [];
    public List<string> SelectedCommits { get; set; } = [];
    public string? TargetDirectory { get; set; }
    public string PlanJson { get; set; } = "{}";
    public bool ApprovalRequired { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? ResultCommitSha { get; set; }
    public string? ResultBranch { get; set; }
    public Guid? SafetyCheckpointId { get; set; }
    public Guid? JobId { get; set; }
    public string? Reason { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddHours(1);
    public DateTime? CompletedAt { get; set; }
}

public class RepositoryBackup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Null for system-wide metadata backups.</summary>
    public Guid? RepositoryId { get; set; }
    public string Kind { get; set; } = "repository";
    public BackupState State { get; set; } = BackupState.Pending;
    public string StorageProvider { get; set; } = "";
    public string? StorageKey { get; set; }
    public long SizeBytes { get; set; }
    public string? Sha256 { get; set; }
    public bool Encrypted { get; set; }
    public string? HeadSha { get; set; }
    public string? RefsJson { get; set; }
    public string Trigger { get; set; } = "on-demand";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public DateTime? VerifiedAt { get; set; }
    public string? Error { get; set; }
    public bool RetentionLocked { get; set; }
}

public class BackupVerificationResult
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BackupId { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public bool Succeeded { get; set; }
    public bool ChecksumOk { get; set; }
    public bool BundleOk { get; set; }
    public bool FsckOk { get; set; }
    public bool RefsMatch { get; set; }
    public bool TestRestoreOk { get; set; }
    public string? Details { get; set; }
}

public class PolicyDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>global | company | project | repository</summary>
    public string Scope { get; set; } = "global";
    public Guid? ScopeId { get; set; }
    public string Key { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string SettingsJson { get; set; } = "{}";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? UpdatedBy { get; set; }
}

public class PolicyOverride
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RepositoryId { get; set; }
    public Guid? ChangeOperationId { get; set; }
    public string PolicyKey { get; set; } = "";
    public Guid GrantedByUserId { get; set; }
    public string GrantedBy { get; set; } = "";
    public string Reason { get; set; } = "";
    public string? Details { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class BackgroundJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Type { get; set; } = "";
    public JobState State { get; set; } = JobState.Queued;
    public Guid? RepositoryId { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public string? ResultJson { get; set; }
    public string? Error { get; set; }
    public string? Progress { get; set; }
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 3;
    public string? CreatedBy { get; set; }
    public string? LockedBy { get; set; }
    public DateTime? LockedUntil { get; set; }
    public DateTime NotBefore { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string? UserId { get; set; }
    public string Actor { get; set; } = "system";
    public string ActorType { get; set; } = "system";
    public Guid? CompanyId { get; set; }
    public Guid? RepositoryId { get; set; }
    public string Action { get; set; } = "";
    public string? Target { get; set; }
    public string Outcome { get; set; } = "success";
    public string? Details { get; set; }
    public string? CorrelationId { get; set; }
    public string? IpAddress { get; set; }
}

public class SystemSetting
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Key { get; set; } = "";
    public string? Value { get; set; }
    public bool IsSecret { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

// ---- Operational tables beyond the minimum list ----

public class ApiToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public string Prefix { get; set; } = "";
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public List<string> Scopes { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
}

public class MutationLock
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string LockKey { get; set; } = "";
    public string Owner { get; set; } = "";
    public DateTime AcquiredAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
}

public class IdempotencyRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Key { get; set; } = "";
    public string Principal { get; set; } = "";
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public int StatusCode { get; set; }
    public string? ResponseBody { get; set; }
    public bool Completed { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class MonitoringAlert
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? RepositoryId { get; set; }
    public Guid? CompanyId { get; set; }
    /// <summary>Dedupe key; an open alert with the same key is refreshed instead of duplicated.</summary>
    public string Key { get; set; } = "";
    public string Category { get; set; } = "";
    public AlertSeverity Severity { get; set; }
    public string Message { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }
}

/// <summary>Outbox relayed to SignalR clients by the API (works across the API and worker processes).</summary>
public class NotificationEvent
{
    public long Id { get; set; }
    public string Type { get; set; } = "";
    public Guid? CompanyId { get; set; }
    public Guid? RepositoryId { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class MergeRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RepositoryId { get; set; }
    public string SourceBranch { get; set; } = "";
    public string TargetBranch { get; set; } = "";
    public string? SourceSha { get; set; }
    public string? TargetSha { get; set; }
    public Guid? SessionId { get; set; }
    public MergeRequestState State { get; set; }
    public string? Message { get; set; }
    public List<string> Conflicts { get; set; } = [];
    public string? RequestedBy { get; set; }
    public string? ApprovedBy { get; set; }
    public string? ResultSha { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}
