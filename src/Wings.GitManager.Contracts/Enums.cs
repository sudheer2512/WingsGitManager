using System.Text.Json.Serialization;

namespace Wings.GitManager.Contracts;

/// <summary>How safely a checkpoint (commit) is stored. Never report more than has been verified.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DurabilityState>))]
public enum DurabilityState
{
    WorkingTreeOnly,
    LocalCommit,
    RemotePushPending,
    RemoteSynchronized,
    IndependentlyBackedUp,
    Failed,
    RemoteDiverged
}

[JsonConverter(typeof(JsonStringEnumConverter<CheckpointType>))]
public enum CheckpointType { Initial, PreChange, PostChange, Recovery, Merge, Manual, External }

[JsonConverter(typeof(JsonStringEnumConverter<PushJobState>))]
public enum PushJobState { Pending, Processing, Succeeded, RetryScheduled, Conflict, AuthenticationFailed, PermanentFailure }

[JsonConverter(typeof(JsonStringEnumConverter<ChangeOperationState>))]
public enum ChangeOperationState { Active, Committing, Completed, Aborted, Interrupted, PolicyBlocked, Failed }

[JsonConverter(typeof(JsonStringEnumConverter<RiskLevel>))]
public enum RiskLevel { Low, Medium, High }

[JsonConverter(typeof(JsonStringEnumConverter<SessionState>))]
public enum SessionState { Active, Completed, Merged, Reverted, Abandoned }

[JsonConverter(typeof(JsonStringEnumConverter<SessionIsolation>))]
public enum SessionIsolation { Worktree, SharedWorkingDirectory }

[JsonConverter(typeof(JsonStringEnumConverter<RecoveryType>))]
public enum RecoveryType { RestoreFile, RestoreDeleted, RestoreProject, RevertSession, RevertCommits, CloneProject }

[JsonConverter(typeof(JsonStringEnumConverter<RecoveryStrategy>))]
public enum RecoveryStrategy
{
    /// <summary>Check out selected paths from the recovery commit and commit them on the current branch.</summary>
    RestoreFilesAsNewCommit,
    /// <summary>Create a recovery branch + worktree at the recovery commit; the current branch is untouched.</summary>
    RecoveryBranchWorktree,
    /// <summary>Create a new commit on the current branch whose tree equals the recovery commit (history preserved).</summary>
    RollbackCommit,
    /// <summary>git revert of the selected commits; later unrelated changes are preserved.</summary>
    RevertCommits,
    /// <summary>Clone the remote repository into a new directory and check out the recovery commit.</summary>
    CloneToDirectory
}

[JsonConverter(typeof(JsonStringEnumConverter<RecoveryStatus>))]
public enum RecoveryStatus { Previewed, Executing, Succeeded, Conflict, Failed, Expired }

[JsonConverter(typeof(JsonStringEnumConverter<BackupState>))]
public enum BackupState { Pending, Running, Completed, Verified, VerificationFailed, Failed, Expired }

[JsonConverter(typeof(JsonStringEnumConverter<JobState>))]
public enum JobState { Queued, Running, Succeeded, Failed, Cancelled }

[JsonConverter(typeof(JsonStringEnumConverter<RepositoryState>))]
public enum RepositoryState { Provisioning, Active, Archived, Deleted, Failed }

[JsonConverter(typeof(JsonStringEnumConverter<RemoteStatus>))]
public enum RemoteStatus { Unknown, InSync, Ahead, Behind, Diverged, Unreachable }

[JsonConverter(typeof(JsonStringEnumConverter<HostingProviderKind>))]
public enum HostingProviderKind { Gitea, FileSystem, GenericRemote }

[JsonConverter(typeof(JsonStringEnumConverter<WorkspaceRole>))]
public enum WorkspaceRole { ReadOnly = 0, Developer = 1, RepositoryAdministrator = 2, CompanyAdministrator = 3 }

[JsonConverter(typeof(JsonStringEnumConverter<WorktreeState>))]
public enum WorktreeState { Active, Released, Abandoned, Missing }

[JsonConverter(typeof(JsonStringEnumConverter<MergeRequestState>))]
public enum MergeRequestState { AwaitingApproval, Conflict, Merged, Rejected, Failed, Mergeable }

[JsonConverter(typeof(JsonStringEnumConverter<SecretScanMode>))]
public enum SecretScanMode { Block, Warn, Off }

[JsonConverter(typeof(JsonStringEnumConverter<AbortMode>))]
public enum AbortMode
{
    /// <summary>Leave the files as they are; they are checkpointed by the next operation.</summary>
    KeepChanges,
    /// <summary>Save the changes on a recovery branch (pushed) and restore the starting commit.</summary>
    PreserveAndReset
}

[JsonConverter(typeof(JsonStringEnumConverter<AlertSeverity>))]
public enum AlertSeverity { Info, Warning, Critical }

[JsonConverter(typeof(JsonStringEnumConverter<FileChangeKind>))]
public enum FileChangeKind { Added, Modified, Deleted, Renamed, Copied, TypeChanged }
