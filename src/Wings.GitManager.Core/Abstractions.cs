using Wings.GitManager.Contracts;
using Wings.GitManager.Core.Entities;

namespace Wings.GitManager.Core;

public sealed record RemoteRepositoryInfo(
    string Owner,
    string Name,
    string CloneUrl,
    string? ProviderRepositoryId,
    bool IsEmpty,
    string? DefaultBranch,
    bool Archived = false);

public sealed record RemoteBranchInfo(string Name, string HeadSha, bool Protected);

/// <summary>Credentials passed to git over HTTP via an injected header (never stored in remote URLs or logged).</summary>
public sealed record GitCredential(string Username, string Secret);

/// <summary>
/// Provider-specific provisioning and administration (organizations, repositories, permissions, webhooks).
/// Ordinary Git transport (push/fetch/clone) always goes through the native Git CLI.
/// </summary>
public interface IGitHostingProvider
{
    HostingProviderKind Kind { get; }
    bool CanProvision { get; }

    /// <summary>Ensures the company's isolated namespace (Gitea organization) exists; returns its name.</summary>
    Task<string> EnsureNamespaceAsync(Company company, CancellationToken ct);
    Task<RemoteRepositoryInfo> CreateRepositoryAsync(string ns, string name, string? description, string defaultBranch, CancellationToken ct);
    Task<RemoteRepositoryInfo?> GetRepositoryAsync(string ns, string name, CancellationToken ct);
    Task ArchiveRepositoryAsync(string ns, string name, bool archived, CancellationToken ct);
    Task DeleteRepositoryAsync(string ns, string name, CancellationToken ct);
    Task<IReadOnlyList<RemoteBranchInfo>> ListBranchesAsync(string ns, string name, CancellationToken ct);
    Task<string?> GetBranchHeadAsync(string ns, string name, string branch, CancellationToken ct);
    /// <summary>Independent check (provider API, not the local git client) that the commit exists remotely.</summary>
    Task<bool?> CommitExistsAsync(string ns, string name, string sha, CancellationToken ct);
    /// <summary>Disable force pushes and deletion of the branch (pattern supported where the provider allows).</summary>
    Task ProtectBranchAsync(string ns, string name, string branchPattern, CancellationToken ct);
    Task EnsureWebhookAsync(string ns, string name, CancellationToken ct);
    Task SetCollaboratorAsync(string ns, string name, string username, WorkspaceRole role, CancellationToken ct);
    GitCredential? GetGitCredential(RepositoryRemote remote);
    Task<HealthComponentDto> CheckHealthAsync(CancellationToken ct);
}

public interface IHostingProviderResolver
{
    IGitHostingProvider Get(HostingProviderKind kind);
    IEnumerable<IGitHostingProvider> All { get; }
}

/// <summary>The authenticated caller of the current request (or the background service identity).</summary>
public interface ICallerContext
{
    Guid? UserId { get; }
    string Name { get; }
    string ActorType { get; }
    bool IsSystemAdministrator { get; }
    bool IsServiceAccount { get; }
    IReadOnlyCollection<string> Scopes { get; }
    string? CorrelationId { get; }
    string? IpAddress { get; }
}

public static class Scopes
{
    public const string ReposRead = "repos:read";
    public const string ReposWrite = "repos:write";
    public const string ChangesWrite = "changes:write";
    public const string RecoveryExecute = "recovery:execute";
    public const string BackupsManage = "backups:manage";
    public const string Admin = "admin";
    public static readonly string[] All = [ReposRead, ReposWrite, ChangesWrite, RecoveryExecute, BackupsManage, Admin];
    /// <summary>Scopes granted to the Wings Code AI agent service identity.</summary>
    public static readonly string[] AgentService = [ReposRead, ReposWrite, ChangesWrite, RecoveryExecute];
}

/// <summary>Repository-level actions checked by the access-control service.</summary>
public enum RepoAction { Read, Write, Recover, Administer, Delete }
