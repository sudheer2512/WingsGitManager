using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wings.GitManager.Contracts;
using Wings.GitManager.Core;
using Wings.GitManager.Core.Entities;
using Wings.GitManager.Core.Policies;
using Wings.GitManager.Infrastructure.Data;
using Wings.GitManager.Infrastructure.Git;

namespace Wings.GitManager.Infrastructure.Services;

/// <summary>Resolves the pieces needed to run Git for a repository: directories, remote, provider and credential.</summary>
public sealed class RepositoryContext(
    GitManagerDbContext db,
    GitClient git,
    WorkspacePaths paths,
    IHostingProviderResolver providers,
    IOptions<GitManagerOptions> options)
{
    public GitClient Git => git;
    public WorkspacePaths Paths => paths;
    public GitManagerOptions Options => options.Value;

    public RepositoryRemote PrimaryRemote(Repository repo) =>
        repo.Remotes.FirstOrDefault(r => r.IsPrimary) ?? repo.Remotes.FirstOrDefault()
        ?? throw new GitManagerException("remote_missing", "Repository has no remote configured.", 409);

    public IGitHostingProvider Provider(Repository repo) => providers.Get(PrimaryRemote(repo).Provider);

    public GitCredential? Credential(Repository repo)
    {
        var remote = PrimaryRemote(repo);
        return providers.Get(remote.Provider).GetGitCredential(remote);
    }

    public bool AllowFile(Repository repo) => PrimaryRemote(repo).Provider == HostingProviderKind.FileSystem;

    public async Task<WorkingDirectory?> MainWorkingDirectoryAsync(Guid repositoryId, CancellationToken ct) =>
        await db.WorkingDirectories.Where(w => w.RepositoryId == repositoryId && w.Kind == WorkingDirectoryKind.Main && w.IsActive)
            .OrderByDescending(w => w.CreatedAt).FirstOrDefaultAsync(ct);

    /// <summary>Main working directory that exists on disk and is a Git repository; otherwise a recoverable error.</summary>
    public async Task<string> RequireMainDirectoryAsync(Repository repo, CancellationToken ct)
    {
        var wd = await MainWorkingDirectoryAsync(repo.Id, ct)
            ?? throw new GitManagerException("working_directory_missing", "Repository has no registered working directory. Use recovery/clone to restore one.", 409);
        var dir = paths.Revalidate(wd.Path);
        if (!await git.IsRepositoryAsync(dir, ct))
            throw new GitManagerException("working_directory_missing",
                $"The local working directory '{dir}' is missing or is no longer a Git repository. The code is safe on the remote; use POST /repositories/{repo.Id}/recovery/clone to restore it.", 409);
        return dir;
    }

    /// <summary>Any registered directory of the repository that still exists (worktrees share the object database).</summary>
    public async Task<string?> AnyExistingDirectoryAsync(Guid repositoryId, CancellationToken ct)
    {
        var dirs = await db.WorkingDirectories.Where(w => w.RepositoryId == repositoryId && w.IsActive)
            .OrderBy(w => w.Kind).Select(w => w.Path).ToListAsync(ct);
        foreach (var d in dirs)
            if (await git.IsRepositoryAsync(d, ct)) return d;
        return null;
    }

    public async Task<HighRiskPolicySettings> HighRiskPolicyAsync(Repository repo, CancellationToken ct)
    {
        var defs = await db.PolicyDefinitions.AsNoTracking()
            .Where(p => p.Key == HighRiskPolicySettings.Key && p.Enabled &&
                        (p.Scope == "global" || p.Scope == "company" && p.ScopeId == repo.CompanyId ||
                         p.Scope == "project" && p.ScopeId == repo.ProjectId || p.Scope == "repository" && p.ScopeId == repo.Id))
            .ToListAsync(ct);
        var best = defs.OrderByDescending(p => p.Scope switch { "repository" => 3, "project" => 2, "company" => 1, _ => 0 }).FirstOrDefault();
        return best is null ? new HighRiskPolicySettings() : Json.Deserialize<HighRiskPolicySettings>(best.SettingsJson) ?? new HighRiskPolicySettings();
    }

    public static GitIdentity AgentIdentity(string agentId, GitManagerOptions o)
    {
        var slug = Core.Safety.RepoPath.Slugify(agentId);
        return new GitIdentity($"Wings AI Agent ({agentId})", $"{slug}@{o.AgentAuthorEmailDomain}");
    }

    public GitIdentity ServiceIdentity => new(Options.ServiceCommitterName, Options.ServiceCommitterEmail);

    /// <summary>Paths that may be staged: not excluded by repository settings or the always-excluded secret patterns.</summary>
    public static bool IsPermittedPath(string path, RepositorySettings? settings) =>
        !Core.Safety.SecretScanner.IsSensitiveFile(path)
        && !Core.Safety.SecretScanner.AlwaysExcludedPatterns.Any(p => Core.Safety.RepoPath.GlobMatch(p, path))
        && !(settings?.ExcludedPathPatterns ?? []).Any(p => Core.Safety.RepoPath.GlobMatch(p, path));
}
