namespace Wings.GitManager.Contracts;

public sealed record BackupDto
{
    public Guid Id { get; init; }
    public Guid? RepositoryId { get; init; }
    public required string Kind { get; init; }
    public BackupState State { get; init; }
    public required string StorageProvider { get; init; }
    public string? StorageKey { get; init; }
    public long SizeBytes { get; init; }
    public string? Sha256 { get; init; }
    public bool Encrypted { get; init; }
    public string? HeadSha { get; init; }
    public int RefCount { get; init; }
    public required string Trigger { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public DateTime? VerifiedAt { get; init; }
    public string? Error { get; init; }
    public BackupVerificationDto? LastVerification { get; init; }
}

public sealed record BackupVerificationDto
{
    public Guid Id { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public bool Succeeded { get; init; }
    public bool ChecksumOk { get; init; }
    public bool BundleOk { get; init; }
    public bool FsckOk { get; init; }
    public bool RefsMatch { get; init; }
    public bool TestRestoreOk { get; init; }
    public string? Details { get; init; }
}

public sealed record RestoreBackupRequest
{
    /// <summary>"directory" restores into a new directory; "remote" re-creates the central repository (server loss).</summary>
    public string Target { get; init; } = "directory";
    public string? TargetDirectory { get; init; }
    public bool Approved { get; init; }
}

public sealed record JobDto
{
    public Guid Id { get; init; }
    public required string Type { get; init; }
    public JobState State { get; init; }
    public Guid? RepositoryId { get; init; }
    public string? Result { get; init; }
    public string? Error { get; init; }
    public int Attempts { get; init; }
    public string? Progress { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
}

public sealed record AcceptedJobDto(Guid JobId, string StatusUrl, Guid? ResourceId);

public sealed record DashboardSummaryDto
{
    public int TotalRepositories { get; init; }
    public int ActiveRepositories { get; init; }
    public int CommitsToday { get; init; }
    public int PendingPushes { get; init; }
    public int FailedPushes { get; init; }
    public int RepositoriesWithLocalOnlyChanges { get; init; }
    public int RepositoriesWithRemoteDivergence { get; init; }
    public DateTime? LastVerifiedBackupAt { get; init; }
    public int RepositoriesWithoutVerifiedBackup { get; init; }
    public long StorageBytes { get; init; }
    public int HighRiskOperations { get; init; }
    public int BlockedHighRiskOperations { get; init; }
    public int ActiveAISessions { get; init; }
    public int OpenAlerts { get; init; }
    public IReadOnlyList<DailyCountDto> CommitsPerDay { get; init; } = [];
    public IReadOnlyList<AlertDto> RecentAlerts { get; init; } = [];
}

public sealed record DailyCountDto(DateOnly Day, int Count);

public sealed record AlertDto
{
    public Guid Id { get; init; }
    public Guid? RepositoryId { get; init; }
    public string? RepositoryName { get; init; }
    public required string Category { get; init; }
    public AlertSeverity Severity { get; init; }
    public required string Message { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime LastSeenAt { get; init; }
    public DateTime? ResolvedAt { get; init; }
}

public sealed record SystemHealthDto
{
    public required string Status { get; init; }
    public IReadOnlyList<HealthComponentDto> Components { get; init; } = [];
    public DateTime CheckedAt { get; init; }
}

public sealed record HealthComponentDto(string Name, string Status, string? Detail, double? DurationMs);

public sealed record StorageReportDto
{
    public IReadOnlyList<VolumeDto> Volumes { get; init; } = [];
    public IReadOnlyList<RepositoryStorageDto> Repositories { get; init; } = [];
}

public sealed record VolumeDto(string Purpose, string Path, long TotalBytes, long FreeBytes, double UsedPercent, string Status);

public sealed record RepositoryStorageDto(Guid RepositoryId, string Name, long WorkingDirectoryBytes, long GitObjectBytes);

public sealed record AuditLogDto
{
    public Guid Id { get; init; }
    public DateTime Timestamp { get; init; }
    public string? UserId { get; init; }
    public required string Actor { get; init; }
    public required string ActorType { get; init; }
    public Guid? CompanyId { get; init; }
    public Guid? RepositoryId { get; init; }
    public required string Action { get; init; }
    public string? Target { get; init; }
    public required string Outcome { get; init; }
    public string? Details { get; init; }
    public string? CorrelationId { get; init; }
}

public sealed record CompanyDto(Guid Id, string Name, string Slug, string? ExternalId, string? GitNamespace, bool IsActive, int RepositoryCount);

public sealed record CreateCompanyRequest(string Name, string? Slug, string? ExternalId);

public sealed record ProjectDto(Guid Id, Guid CompanyId, string Name, string Slug, string? ExternalId, int RepositoryCount);

public sealed record UserDto(Guid Id, string Email, string DisplayName, bool IsSystemAdministrator, bool IsServiceAccount, bool IsActive, IReadOnlyList<MembershipDto> Memberships);

public sealed record MembershipDto(Guid CompanyId, string CompanyName, WorkspaceRole Role);

public sealed record CreateUserRequest(string Email, string DisplayName, bool IsSystemAdministrator, bool IsServiceAccount, Guid? CompanyId, WorkspaceRole? Role);

public sealed record SetMembershipRequest(Guid CompanyId, WorkspaceRole Role);

public sealed record RepositoryPermissionDto(Guid Id, Guid RepositoryId, Guid UserId, string UserEmail, WorkspaceRole Role);

public sealed record SetRepositoryPermissionRequest(Guid UserId, WorkspaceRole Role);

public sealed record CreateApiTokenRequest
{
    public required string Name { get; init; }
    public Guid? UserId { get; init; }
    public string[] Scopes { get; init; } = [];
    public int? ExpiresInDays { get; init; }
}

public sealed record ApiTokenDto(Guid Id, string Name, string Prefix, Guid UserId, string[] Scopes, DateTime CreatedAt, DateTime? ExpiresAt, DateTime? RevokedAt, DateTime? LastUsedAt);

public sealed record CreatedApiTokenDto(ApiTokenDto Token, string Secret);

public sealed record PolicyDto
{
    public Guid Id { get; init; }
    public required string Scope { get; init; }
    public Guid? ScopeId { get; init; }
    public required string Key { get; init; }
    public bool Enabled { get; init; }
    public required string SettingsJson { get; init; }
    public DateTime UpdatedAt { get; init; }
    public string? UpdatedBy { get; init; }
}

public sealed record UpsertPolicyRequest(string Scope, Guid? ScopeId, string Key, bool Enabled, string SettingsJson);

public sealed record MeDto(Guid UserId, string Email, string DisplayName, bool IsSystemAdministrator, IReadOnlyList<MembershipDto> Memberships, IReadOnlyList<string> Scopes);

public sealed record ApiError(string Code, string Message, string? CorrelationId, object? Details = null);
