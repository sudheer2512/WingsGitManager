using System.Diagnostics;
using Microsoft.Extensions.Options;
using Wings.GitManager.Contracts;
using Wings.GitManager.Core;
using Wings.GitManager.Core.Entities;
using Wings.GitManager.Infrastructure.Git;
using Wings.GitManager.Infrastructure.Services;

namespace Wings.GitManager.Infrastructure.Hosting;

/// <summary>
/// Bare repositories on a dedicated volume (or network share). Used for air-gapped installs and for tests;
/// non-fast-forward pushes and ref deletions are refused by the bare repository configuration.
/// </summary>
public sealed class FileSystemHostingProvider(GitClient git, IOptions<FileSystemRemoteOptions> options) : IGitHostingProvider
{
    private readonly FileSystemRemoteOptions _o = options.Value;
    public HostingProviderKind Kind => HostingProviderKind.FileSystem;
    public bool CanProvision => true;

    private string Root
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_o.Root)) throw new GitManagerException("filesystem_remote_not_configured", "FileSystemRemote:Root is not configured.", 503);
            var root = Path.GetFullPath(_o.Root);
            Directory.CreateDirectory(root);
            return root;
        }
    }

    private string RepoDir(string ns, string name) => Path.Combine(Root, Core.Safety.RepoPath.Slugify(ns), Core.Safety.RepoPath.Slugify(name) + ".git");

    public Task<string> EnsureNamespaceAsync(Company company, CancellationToken ct)
    {
        var ns = company.GitNamespace ?? company.Slug;
        Directory.CreateDirectory(Path.Combine(Root, Core.Safety.RepoPath.Slugify(ns)));
        return Task.FromResult(ns);
    }

    public async Task<RemoteRepositoryInfo> CreateRepositoryAsync(string ns, string name, string? description, string defaultBranch, CancellationToken ct)
    {
        var dir = RepoDir(ns, name);
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            await git.RunAsync(dir, ["init", "--bare", "--initial-branch", defaultBranch], null, ct);
            await git.RunAsync(dir, ["config", "receive.denyNonFastForwards", "true"], null, ct);
            await git.RunAsync(dir, ["config", "receive.denyDeletes", "true"], null, ct);
            await git.RunAsync(dir, ["config", "core.logAllRefUpdates", "true"], null, ct);
        }
        return (await GetRepositoryAsync(ns, name, ct))!;
    }

    public async Task<RemoteRepositoryInfo?> GetRepositoryAsync(string ns, string name, CancellationToken ct)
    {
        var dir = RepoDir(ns, name);
        if (!Directory.Exists(dir)) return null;
        var refs = await git.RunAsync(dir, ["for-each-ref", "--count=1", "refs/heads/"], null, ct);
        return new RemoteRepositoryInfo(ns, name, dir, null, string.IsNullOrWhiteSpace(refs.StdOut), null);
    }

    public Task ArchiveRepositoryAsync(string ns, string name, bool archived, CancellationToken ct)
    {
        var marker = Path.Combine(RepoDir(ns, name), "wings-archived");
        if (archived) File.WriteAllText(marker, DateTime.UtcNow.ToString("O")); else File.Delete(marker);
        return Task.CompletedTask;
    }

    public Task DeleteRepositoryAsync(string ns, string name, CancellationToken ct)
    {
        var dir = RepoDir(ns, name);
        if (Directory.Exists(dir))
        {
            // Keep a tombstone copy rather than destroying history outright.
            var tomb = Path.Combine(Root, ".deleted", $"{Core.Safety.RepoPath.Slugify(ns)}-{Core.Safety.RepoPath.Slugify(name)}-{DateTime.UtcNow:yyyyMMddHHmmss}.git");
            Directory.CreateDirectory(Path.GetDirectoryName(tomb)!);
            Directory.Move(dir, tomb);
        }
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<RemoteBranchInfo>> ListBranchesAsync(string ns, string name, CancellationToken ct) =>
        (await git.LocalBranchesAsync(RepoDir(ns, name), ct)).Select(b => new RemoteBranchInfo(b.Name, b.Sha, true)).ToList();

    public async Task<string?> GetBranchHeadAsync(string ns, string name, string branch, CancellationToken ct)
    {
        var dir = RepoDir(ns, name);
        if (!Directory.Exists(dir)) return null;
        var r = await git.RunAsync(dir, ["rev-parse", "--verify", "--quiet", "refs/heads/" + branch], new() { ThrowOnError = false }, ct);
        return r.Success ? r.StdOut.Trim() : null;
    }

    public async Task<bool?> CommitExistsAsync(string ns, string name, string sha, CancellationToken ct)
    {
        var dir = RepoDir(ns, name);
        if (!Directory.Exists(dir)) return false;
        return await git.ObjectExistsAsync(dir, sha, ct);
    }

    public Task ProtectBranchAsync(string ns, string name, string branchPattern, CancellationToken ct) => Task.CompletedTask;
    public Task EnsureWebhookAsync(string ns, string name, CancellationToken ct) => Task.CompletedTask;
    public Task SetCollaboratorAsync(string ns, string name, string username, WorkspaceRole role, CancellationToken ct) => Task.CompletedTask;
    public GitCredential? GetGitCredential(RepositoryRemote remote) => null;

    public Task<HealthComponentDto> CheckHealthAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_o.Root)) return Task.FromResult(new HealthComponentDto("filesystem-remote", "NotConfigured", null, null));
        var ok = Directory.Exists(_o.Root);
        return Task.FromResult(new HealthComponentDto("filesystem-remote", ok ? "Healthy" : "Unhealthy", _o.Root, 0));
    }
}

/// <summary>
/// Any standard Git remote that was provisioned elsewhere (GitHub, GitLab, Azure DevOps, Bitbucket...).
/// Provisioning is not automated; push/fetch/clone and ls-remote verification work with stored credentials.
/// </summary>
public sealed class GenericRemoteProvider(SecretProtector protector) : IGitHostingProvider
{
    public HostingProviderKind Kind => HostingProviderKind.GenericRemote;
    public bool CanProvision => false;

    public Task<string> EnsureNamespaceAsync(Company company, CancellationToken ct) => Task.FromResult(company.Slug);
    public Task<RemoteRepositoryInfo> CreateRepositoryAsync(string ns, string name, string? description, string defaultBranch, CancellationToken ct) =>
        throw new GitManagerException("provider_cannot_provision", "Generic remotes must be provisioned beforehand; pass ExistingRemote.", 400);
    public Task<RemoteRepositoryInfo?> GetRepositoryAsync(string ns, string name, CancellationToken ct) => Task.FromResult<RemoteRepositoryInfo?>(null);
    public Task ArchiveRepositoryAsync(string ns, string name, bool archived, CancellationToken ct) => Task.CompletedTask;
    public Task DeleteRepositoryAsync(string ns, string name, CancellationToken ct) => Task.CompletedTask;
    public Task<IReadOnlyList<RemoteBranchInfo>> ListBranchesAsync(string ns, string name, CancellationToken ct) => Task.FromResult<IReadOnlyList<RemoteBranchInfo>>([]);
    public Task<string?> GetBranchHeadAsync(string ns, string name, string branch, CancellationToken ct) => Task.FromResult<string?>(null);
    public Task<bool?> CommitExistsAsync(string ns, string name, string sha, CancellationToken ct) => Task.FromResult<bool?>(null);
    public Task ProtectBranchAsync(string ns, string name, string branchPattern, CancellationToken ct) => Task.CompletedTask;
    public Task EnsureWebhookAsync(string ns, string name, CancellationToken ct) => Task.CompletedTask;
    public Task SetCollaboratorAsync(string ns, string name, string username, WorkspaceRole role, CancellationToken ct) => Task.CompletedTask;

    public GitCredential? GetGitCredential(RepositoryRemote remote) =>
        remote.EncryptedCredential is null ? null : new GitCredential(remote.CredentialUsername ?? "git", protector.Unprotect(remote.EncryptedCredential));

    public Task<HealthComponentDto> CheckHealthAsync(CancellationToken ct) =>
        Task.FromResult(new HealthComponentDto("generic-remote", "Healthy", "No central health endpoint", null));
}

public sealed class HostingProviderResolver(IEnumerable<IGitHostingProvider> providers) : IHostingProviderResolver
{
    private readonly Dictionary<HostingProviderKind, IGitHostingProvider> _map = providers.ToDictionary(p => p.Kind);
    public IEnumerable<IGitHostingProvider> All => _map.Values;
    public IGitHostingProvider Get(HostingProviderKind kind) =>
        _map.TryGetValue(kind, out var p) ? p : throw new GitManagerException("provider_unavailable", $"Hosting provider {kind} is not registered.", 400);
}
