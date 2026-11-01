namespace Wings.GitManager.Contracts;

public sealed record CommitSummaryDto
{
    public required string Sha { get; init; }
    public required string Subject { get; init; }
    public required string AuthorName { get; init; }
    public required string AuthorEmail { get; init; }
    public DateTime AuthoredAt { get; init; }
    public DateTime CommittedAt { get; init; }
    public IReadOnlyList<string> Parents { get; init; } = [];
    public Guid? SessionId { get; init; }
    public string? AgentId { get; init; }
    public Guid? ChangeOperationId { get; init; }
    public string? InitiatingUser { get; init; }
    public bool IsAutomated { get; init; }
    public DurabilityState? Durability { get; init; }
}

public sealed record CommitDetailDto
{
    public required CommitSummaryDto Commit { get; init; }
    public required string Message { get; init; }
    public IReadOnlyList<FileChangeDto> Files { get; init; } = [];
    public string? UserRequestRef { get; init; }
    public string? BuildStatus { get; init; }
    public string? TestStatus { get; init; }
}

public sealed record DiffDto
{
    public required string FromRef { get; init; }
    public required string ToRef { get; init; }
    public IReadOnlyList<FileDiffDto> Files { get; init; } = [];
    public int TotalAdditions { get; init; }
    public int TotalDeletions { get; init; }
    public bool Truncated { get; init; }
}

public sealed record FileDiffDto
{
    public required string Path { get; init; }
    public string? OldPath { get; init; }
    public FileChangeKind Kind { get; init; }
    public int Additions { get; init; }
    public int Deletions { get; init; }
    public bool IsBinary { get; init; }
    /// <summary>Unified diff text (only when requested and small enough).</summary>
    public string? Patch { get; init; }
}

public sealed record TreeEntryDto(string Path, string Name, string Type, long? Size, string ObjectSha);

public sealed record FileContentDto
{
    public required string Path { get; init; }
    public required string Ref { get; init; }
    public required string CommitSha { get; init; }
    public long Size { get; init; }
    public bool IsBinary { get; init; }
    public string? Content { get; init; }
    public string? Language { get; init; }
    public bool Truncated { get; init; }
    public CommitSummaryDto? LastCommit { get; init; }
}

public sealed record BranchDto
{
    public required string Name { get; init; }
    public required string HeadSha { get; init; }
    public string? RemoteHeadSha { get; init; }
    public int Ahead { get; init; }
    public int Behind { get; init; }
    public bool IsDefault { get; init; }
    public bool IsProtected { get; init; }
    public bool IsCheckedOut { get; init; }
    public Guid? SessionId { get; init; }
    public DateTime? LastCommitAt { get; init; }
}

public sealed record CreateBranchRequest
{
    public required string Name { get; init; }
    public string? StartPoint { get; init; }
    public bool Push { get; init; } = true;
}

public sealed record MergeBranchRequest
{
    public required string SourceBranch { get; init; }
    public required string TargetBranch { get; init; }
    public string? ExpectedTargetHead { get; init; }
    public string? Message { get; init; }
    public bool DryRun { get; init; }
    public Guid? SessionId { get; init; }
}

public sealed record MergeResultDto
{
    public Guid MergeRequestId { get; init; }
    public MergeRequestState State { get; init; }
    public IReadOnlyList<string> Conflicts { get; init; } = [];
    public string? ResultCommitSha { get; init; }
    public bool ApprovalRequired { get; init; }
    public bool DryRun { get; init; }
    public IReadOnlyList<FileChangeDto> Files { get; init; } = [];
    public DurabilityState? Durability { get; init; }
}

public sealed record WorktreeDto
{
    public Guid Id { get; init; }
    public required string Path { get; init; }
    public required string Branch { get; init; }
    public Guid? SessionId { get; init; }
    public string? OwnerAgentId { get; init; }
    public WorktreeState State { get; init; }
    public required string BaseCommitSha { get; init; }
    public string? HeadSha { get; init; }
    public bool HasUncommittedChanges { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? ReleasedAt { get; init; }
}

public sealed record CreateWorktreeRequest
{
    public required string Branch { get; init; }
    public string? StartPoint { get; init; }
    public Guid? SessionId { get; init; }
    public string? OwnerAgentId { get; init; }
}

public sealed record ReleaseWorktreeRequest
{
    /// <summary>When the worktree has uncommitted changes they are saved to a recovery branch first.</summary>
    public bool PreserveUncommittedChanges { get; init; } = true;
}

public sealed record ChangeSummaryDto
{
    public DateTime Since { get; init; }
    public DateTime Until { get; init; }
    public int CommitCount { get; init; }
    public int FilesChanged { get; init; }
    public int Additions { get; init; }
    public int Deletions { get; init; }
    public IReadOnlyList<CommitSummaryDto> Commits { get; init; } = [];
    public IReadOnlyList<FileChangeDto> Files { get; init; } = [];
    public IReadOnlyList<string> Agents { get; init; } = [];
    public IReadOnlyList<Guid> Sessions { get; init; } = [];
    public required string Narrative { get; init; }
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);
