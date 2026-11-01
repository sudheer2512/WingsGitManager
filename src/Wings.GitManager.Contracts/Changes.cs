namespace Wings.GitManager.Contracts;

/// <summary>What Wings Code intends to do; evaluated by the policy engine before any file is touched.</summary>
public sealed record ChangeIntent
{
    public string[] FilesToModify { get; init; } = [];
    public string[] FilesToAdd { get; init; } = [];
    public string[] FilesToDelete { get; init; } = [];
    public string[] DirectoriesToDelete { get; init; } = [];
    public RenameIntent[] Renames { get; init; } = [];
    /// <summary>Free-form categories, e.g. "refactor", "restructure", "dependency-update", "replace-implementation", "multi-project".</summary>
    public string[] Categories { get; init; } = [];
    public int? EstimatedFileCount { get; init; }
}

public sealed record RenameIntent(string From, string To);

public sealed record BeginChangeRequest
{
    public Guid? SessionId { get; init; }
    public required string AgentId { get; init; }
    public required string Description { get; init; }
    public string? UserRequestRef { get; init; }
    /// <summary>Real user on whose behalf the agent works (recorded separately from the service identity).</summary>
    public string? InitiatingUser { get; init; }
    public ChangeIntent Intent { get; init; } = new();
    /// <summary>When set, only files under these repository-relative paths are staged on completion.</summary>
    public string[]? ScopePaths { get; init; }
    public string? ExpectedHead { get; init; }
    /// <summary>Policy override (administrators only; audited).</summary>
    public PolicyOverrideInput? Override { get; init; }
    public int? LeaseMinutes { get; init; }
}

public sealed record PolicyOverrideInput(string Reason);

public sealed record BeginChangeResponse
{
    public Guid OperationId { get; init; }
    public required string WorkingDirectory { get; init; }
    public required string Branch { get; init; }
    public required string ExpectedHead { get; init; }
    public RiskAssessmentDto Risk { get; init; } = new();
    public CheckpointDto? PreChangeCheckpoint { get; init; }
    public bool Authorized { get; init; }
    public DateTime LeaseExpiresAt { get; init; }
    public bool OverrideApplied { get; init; }
}

public sealed record RiskAssessmentDto
{
    public RiskLevel Level { get; init; }
    public IReadOnlyList<string> Reasons { get; init; } = [];
    public IReadOnlyList<string> AffectedPaths { get; init; } = [];
    public IReadOnlyList<string> AffectedModules { get; init; } = [];
    public bool RequiresVerifiedRemoteCheckpoint { get; init; }
}

public sealed record CompleteChangeRequest
{
    public string? Summary { get; init; }
    /// <summary>Conventional commit type override (feat, fix, refactor, chore, docs, test...).</summary>
    public string? CommitType { get; init; }
    public string? CommitMessage { get; init; }
    public string? ExpectedHead { get; init; }
    public string? BuildStatus { get; init; }
    public string? TestStatus { get; init; }
    public string[]? TestsExecuted { get; init; }
    /// <summary>Wait (bounded) for the push and remote verification before returning.</summary>
    public bool WaitForRemoteVerification { get; init; } = true;
}

public sealed record CompleteChangeResponse
{
    public Guid OperationId { get; init; }
    public ChangeOperationState State { get; init; }
    public string? CommitSha { get; init; }
    public bool NoChanges { get; init; }
    public IReadOnlyList<FileChangeDto> Files { get; init; } = [];
    public IReadOnlyList<string> SkippedFiles { get; init; } = [];
    public Guid? CheckpointId { get; init; }
    public DurabilityState Durability { get; init; }
    public RiskAssessmentDto? ActualRisk { get; init; }
    public IReadOnlyList<SecretFindingDto> SecretFindings { get; init; } = [];
    public string? CommitMessage { get; init; }
    public IReadOnlyList<string> RecoveryOptions { get; init; } = [];
}

public sealed record AbortChangeRequest
{
    public AbortMode Mode { get; init; } = AbortMode.PreserveAndReset;
    public string? Reason { get; init; }
}

public sealed record AbortChangeResponse(Guid OperationId, ChangeOperationState State, string? PreservedBranch, string? PreservedCommitSha);

public sealed record ChangeOperationDto
{
    public Guid Id { get; init; }
    public Guid RepositoryId { get; init; }
    public Guid? SessionId { get; init; }
    public required string AgentId { get; init; }
    public string? InitiatingUser { get; init; }
    public ChangeOperationState State { get; init; }
    public RiskLevel RiskLevel { get; init; }
    public IReadOnlyList<string> RiskReasons { get; init; } = [];
    public required string Description { get; init; }
    public string? UserRequestRef { get; init; }
    public required string WorkingDirectory { get; init; }
    public required string Branch { get; init; }
    public required string StartCommitSha { get; init; }
    public string? ResultCommitSha { get; init; }
    public Guid? PreChangeCheckpointId { get; init; }
    public Guid? PostChangeCheckpointId { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public DateTime LeaseExpiresAt { get; init; }
    public string? FailureReason { get; init; }
    public bool OverrideApplied { get; init; }
}

public sealed record FileChangeDto(string Path, FileChangeKind Kind, string? OldPath, int Additions, int Deletions);

public sealed record SecretFindingDto(string Path, int Line, string Rule, string Preview);

public sealed record CheckpointDto
{
    public Guid Id { get; init; }
    public Guid RepositoryId { get; init; }
    public Guid ProjectId { get; init; }
    public string? UserId { get; init; }
    public Guid? SessionId { get; init; }
    public string? AgentId { get; init; }
    public Guid? ChangeOperationId { get; init; }
    public required string Branch { get; init; }
    public required string CommitSha { get; init; }
    public DateTime CreatedAt { get; init; }
    public string? Description { get; init; }
    public CheckpointType Type { get; init; }
    public DurabilityState SyncState { get; init; }
    public DurabilityState? BackupState { get; init; }
    public DateTime? RemoteVerifiedAt { get; init; }
    public string? BuildStatus { get; init; }
    public string? TestStatus { get; init; }
}

public sealed record CreateCommitRequest
{
    public required string Message { get; init; }
    public string[]? Paths { get; init; }
    public string? ExpectedHead { get; init; }
    public bool Push { get; init; } = true;
}

public sealed record CommitResultDto(string? CommitSha, bool NoChanges, Guid? CheckpointId, DurabilityState Durability, IReadOnlyList<FileChangeDto> Files);
