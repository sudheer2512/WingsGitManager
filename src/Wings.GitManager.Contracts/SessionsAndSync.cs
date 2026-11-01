namespace Wings.GitManager.Contracts;

public sealed record StartSessionRequest
{
    public required Guid RepositoryId { get; init; }
    public required string AgentId { get; init; }
    public string? InitiatingUser { get; init; }
    public string? Title { get; init; }
    public string? UserRequestRef { get; init; }
    public SessionIsolation Isolation { get; init; } = SessionIsolation.Worktree;
    /// <summary>Branch the session starts from (defaults to the repository default branch).</summary>
    public string? BaseBranch { get; init; }
}

public sealed record SessionDto
{
    public Guid Id { get; init; }
    public Guid RepositoryId { get; init; }
    public required string AgentId { get; init; }
    public string? InitiatingUser { get; init; }
    public string? Title { get; init; }
    public string? UserRequestRef { get; init; }
    public SessionState State { get; init; }
    public SessionIsolation Isolation { get; init; }
    public required string Branch { get; init; }
    public required string BaseBranch { get; init; }
    public required string WorkingDirectory { get; init; }
    public Guid? WorktreeId { get; init; }
    public required string StartingCommitSha { get; init; }
    public string? EndingCommitSha { get; init; }
    public int CommitCount { get; init; }
    public int FilesModified { get; init; }
    public DurabilityState? PushStatus { get; init; }
    public IReadOnlyList<string> TestsExecuted { get; init; } = [];
    public string? LastTestStatus { get; init; }
    public bool RecoveryAvailable { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? EndedAt { get; init; }
    public DateTime LastActivityAt { get; init; }
}

public sealed record EndSessionRequest
{
    public bool MergeIntoBase { get; init; }
    public bool ReleaseWorktree { get; init; }
}

public sealed record SyncStatusDto
{
    public Guid RepositoryId { get; init; }
    public RemoteStatus RemoteStatus { get; init; }
    public DateTime? LastPushAt { get; init; }
    public string? LastVerifiedSha { get; init; }
    public int Pending { get; init; }
    public int Failed { get; init; }
    public int Conflicts { get; init; }
    public IReadOnlyList<BranchSyncDto> Branches { get; init; } = [];
}

public sealed record BranchSyncDto(string Branch, string? LocalSha, string? RemoteSha, int Ahead, int Behind, RemoteStatus Status);

public sealed record PushJobDto
{
    public Guid Id { get; init; }
    public Guid RepositoryId { get; init; }
    public string? RepositoryName { get; init; }
    public required string Branch { get; init; }
    public required string CommitSha { get; init; }
    public Guid? ChangeOperationId { get; init; }
    public DateTime CreatedAt { get; init; }
    public int RetryCount { get; init; }
    public string? LastError { get; init; }
    public PushJobState State { get; init; }
    public string? ExpectedRemoteHead { get; init; }
    public DateTime? NextAttemptAt { get; init; }
    public DateTime? CompletedAt { get; init; }
}

public sealed record PushRequest
{
    public string? Branch { get; init; }
    /// <summary>Wait for the push and verification instead of only queueing.</summary>
    public bool Wait { get; init; } = true;
}

public sealed record RetryRequest
{
    public Guid? JobId { get; init; }
}

public sealed record FetchResultDto(Guid RepositoryId, IReadOnlyList<BranchSyncDto> Branches);
