namespace Wings.GitManager.Contracts;

public sealed record CreateRepositoryRequest
{
    public required Guid CompanyId { get; init; }
    /// <summary>Wings Code project identifier; the (company, project) pair makes creation idempotent.</summary>
    public required string ProjectExternalId { get; init; }
    public required string ProjectName { get; init; }
    public string? RepositoryName { get; init; }
    public string? Description { get; init; }
    /// <summary>Working directory to initialise. Defaults to {workspaceRoot}/{company}/{project}/main.</summary>
    public string? WorkingDirectory { get; init; }
    public string DefaultBranch { get; init; } = "main";
    /// <summary>Optional .gitignore template name (dotnet, node, angular, python) merged with the platform defaults.</summary>
    public string[]? GitIgnoreTemplates { get; init; }
    public HostingProviderKind? Provider { get; init; }
    /// <summary>Only for GenericRemote: an already-provisioned remote (GitHub, GitLab, Azure DevOps...).</summary>
    public RemoteCredentialInput? ExistingRemote { get; init; }
    public string? InitialCommitMessage { get; init; }
}

public sealed record RemoteCredentialInput
{
    public required string Url { get; init; }
    public string? Username { get; init; }
    public string? Secret { get; init; }
}

public sealed record ImportRepositoryRequest
{
    public required Guid CompanyId { get; init; }
    public required string ProjectExternalId { get; init; }
    public required string ProjectName { get; init; }
    public string? RepositoryName { get; init; }
    /// <summary>Existing local repository to import (history is preserved). Mutually exclusive with SourceRemote.</summary>
    public string? LocalPath { get; init; }
    /// <summary>Existing remote repository to import by cloning (history is preserved).</summary>
    public RemoteCredentialInput? SourceRemote { get; init; }
    public string? WorkingDirectory { get; init; }
    public HostingProviderKind? Provider { get; init; }
}

public sealed record UpdateRepositoryRequest
{
    public string? Description { get; init; }
    public string? DefaultBranch { get; init; }
    public RepositorySettingsDto? Settings { get; init; }
}

public sealed record CloneRepositoryRequest
{
    public required string TargetDirectory { get; init; }
    public string? Branch { get; init; }
    public string? CommitSha { get; init; }
    /// <summary>Register the clone as the repository's main working directory (e.g. after the original was deleted).</summary>
    public bool RegisterAsMainWorkingDirectory { get; init; }
}

public sealed record RepositorySummaryDto
{
    public Guid Id { get; init; }
    public required string Name { get; init; }
    public Guid CompanyId { get; init; }
    public required string CompanyName { get; init; }
    public Guid ProjectId { get; init; }
    public required string ProjectName { get; init; }
    public string? ProjectExternalId { get; init; }
    public required string DefaultBranch { get; init; }
    public RepositoryState State { get; init; }
    public string? LastCommitSha { get; init; }
    public DateTime? LastCommitAt { get; init; }
    public DateTime? LastPushAt { get; init; }
    public RemoteStatus RemoteStatus { get; init; }
    public Guid? LatestCheckpointId { get; init; }
    public DurabilityState? LatestCheckpointDurability { get; init; }
    public long StorageBytes { get; init; }
    public bool HasUncommittedChanges { get; init; }
    public int PendingPushes { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed record RepositoryDetailDto
{
    public required RepositorySummaryDto Summary { get; init; }
    public string? Description { get; init; }
    public required string RemoteUrl { get; init; }
    public HostingProviderKind Provider { get; init; }
    public string? MainWorkingDirectory { get; init; }
    public required RepositorySettingsDto Settings { get; init; }
}

public sealed record RepositorySettingsDto
{
    public bool AutoPush { get; init; } = true;
    /// <summary>Block every new change operation until the pre-change checkpoint is verified on the remote.</summary>
    public bool RequireRemoteCheckpointBeforeChanges { get; init; }
    public bool CommitUncommittedChangesOnBegin { get; init; } = true;
    public SecretScanMode SecretScanMode { get; init; } = SecretScanMode.Block;
    public string[] ExcludedPathPatterns { get; init; } = [];
    public string[] ProtectedBranches { get; init; } = ["main", "release/*"];
    public bool RequireMergeApproval { get; init; }
    public int WorktreeRetentionDays { get; init; } = 14;
    public int BackupIntervalHours { get; init; } = 24;
    public int BackupRetentionCount { get; init; } = 30;
    public bool WatchFileSystem { get; init; } = true;
    public bool AutoCommitExternalChanges { get; init; }
}

/// <summary>Result returned to Wings Code after automatic repository creation or import.</summary>
public sealed record RepositoryProvisionResult
{
    public Guid RepositoryId { get; init; }
    public required string RemoteUrl { get; init; }
    public required string Branch { get; init; }
    public string? CommitSha { get; init; }
    public Guid? CheckpointId { get; init; }
    public DurabilityState Durability { get; init; }
    public required string WorkingDirectory { get; init; }
    /// <summary>True when the request matched an existing repository (idempotent replay).</summary>
    public bool AlreadyExisted { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record RepositoryStatusDto
{
    public Guid RepositoryId { get; init; }
    public required string WorkingDirectory { get; init; }
    public bool WorkingDirectoryExists { get; init; }
    public string? Branch { get; init; }
    public string? HeadSha { get; init; }
    public string? RemoteHeadSha { get; init; }
    public int Ahead { get; init; }
    public int Behind { get; init; }
    public RemoteStatus RemoteStatus { get; init; }
    public bool IsClean { get; init; }
    public IReadOnlyList<WorkingTreeChangeDto> Changes { get; init; } = [];
    public Guid? ActiveOperationId { get; init; }
}

public sealed record WorkingTreeChangeDto(string Path, FileChangeKind Kind, string? OldPath, bool Staged, bool Untracked);
