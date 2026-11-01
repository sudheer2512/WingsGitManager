namespace Wings.GitManager.Contracts;

/// <summary>
/// One request shape for every recovery kind. Preview (dry run) first; execute with the preview's
/// operation id and <see cref="Approved"/> = true.
/// </summary>
public sealed record RecoveryRequest
{
    public RecoveryType Type { get; init; }
    /// <summary>Recovery point: commit SHA, branch or checkpoint id (as "checkpoint:{guid}").</summary>
    public string? RecoveryPoint { get; init; }
    public string[]? Paths { get; init; }
    public Guid? SessionId { get; init; }
    public string[]? CommitShas { get; init; }
    /// <summary>Branch to restore onto (defaults to the main working directory's branch).</summary>
    public string? Branch { get; init; }
    public RecoveryStrategy? Strategy { get; init; }
    /// <summary>For project restores / clones into a new directory.</summary>
    public string? TargetDirectory { get; init; }
    /// <summary>Execute a previously previewed operation (required for execution).</summary>
    public Guid? PreviewOperationId { get; init; }
    public bool Approved { get; init; }
    public bool DryRun { get; init; }
    public string? RequestedBy { get; init; }
    public string? Reason { get; init; }
}

public sealed record RecoveryPlanDto
{
    public Guid OperationId { get; init; }
    public Guid RepositoryId { get; init; }
    public RecoveryType Type { get; init; }
    public RecoveryStatus Status { get; init; }
    public RecoveryStrategy Strategy { get; init; }
    public string? CurrentBranch { get; init; }
    public string? CurrentCommit { get; init; }
    public string? RecoveryCommit { get; init; }
    public IReadOnlyList<FileChangeDto> AffectedFiles { get; init; } = [];
    public int Additions { get; init; }
    public int Deletions { get; init; }
    public IReadOnlyList<string> ExpectedConflicts { get; init; } = [];
    public IReadOnlyList<string> UncommittedChanges { get; init; } = [];
    public bool ApprovalRequired { get; init; }
    public bool Destructive { get; init; }
    public required string ExpectedResult { get; init; }
    public required string Explanation { get; init; }
    public string? TargetDirectory { get; init; }
    public string? ResultCommitSha { get; init; }
    public string? ResultBranch { get; init; }
    public DurabilityState? ResultDurability { get; init; }
    public Guid? SafetyCheckpointId { get; init; }
    public string? Error { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public DateTime ExpiresAt { get; init; }
    public Guid? JobId { get; init; }
}

/// <summary>Search for recovery points from a natural-language request (conversational recovery).</summary>
public sealed record RecoveryCandidatesRequest
{
    public string? Query { get; init; }
    public DateTime? Since { get; init; }
    public DateTime? Until { get; init; }
    public string? PathContains { get; init; }
    public Guid? SessionId { get; init; }
    public string? AgentId { get; init; }
    public int Limit { get; init; } = 10;
}

public sealed record RecoveryInterpretationDto
{
    /// <summary>The recovery the service believes the user asked for.</summary>
    public RecoveryType? SuggestedType { get; init; }
    public DateTime? Since { get; init; }
    public DateTime? Until { get; init; }
    public IReadOnlyList<string> Keywords { get; init; } = [];
    public bool LastSession { get; init; }
    public bool BeforeLastRefactoring { get; init; }
    public bool DeletedContent { get; init; }
    public bool ChangeSummaryOnly { get; init; }
    public bool PreserveRecentWork { get; init; }
    public required string Explanation { get; init; }
}

public sealed record RecoveryCandidateDto
{
    public required string RecoveryPoint { get; init; }
    public required string CommitSha { get; init; }
    public Guid? CheckpointId { get; init; }
    public Guid? SessionId { get; init; }
    public required string Description { get; init; }
    public DateTime Timestamp { get; init; }
    public DurabilityState? Durability { get; init; }
    public IReadOnlyList<string> MatchingPaths { get; init; } = [];
    public double Score { get; init; }
    public RecoveryType SuggestedType { get; init; }
    public string[]? SuggestedPaths { get; init; }
    public string[]? SuggestedCommits { get; init; }
}

public sealed record RecoveryCandidatesResponse
{
    public required RecoveryInterpretationDto Interpretation { get; init; }
    public IReadOnlyList<RecoveryCandidateDto> Candidates { get; init; } = [];
    /// <summary>True when more than one plausible candidate exists and the user must choose.</summary>
    public bool Ambiguous { get; init; }
    public ChangeSummaryDto? ChangeSummary { get; init; }
}

public sealed record DeletedFileDto(string Path, string DeletedInCommit, DateTime DeletedAt, string LastVersionCommit, string? DeletedBy);
