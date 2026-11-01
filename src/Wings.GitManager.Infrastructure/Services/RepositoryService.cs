using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wings.GitManager.Contracts;
using Wings.GitManager.Core;
using Wings.GitManager.Core.Entities;
using Wings.GitManager.Core.Safety;
using Wings.GitManager.Infrastructure.Data;
using Wings.GitManager.Infrastructure.Git;

namespace Wings.GitManager.Infrastructure.Services;

/// <summary>Automatic repository creation, import, metadata, archive/delete and working-directory status.</summary>
public sealed class RepositoryService(
    GitManagerDbContext db,
    AccessControl access,
    RepositoryContext ctx,
    IHostingProviderResolver providers,
    SyncService sync,
    CommitRecorder recorder,
    CheckpointService checkpoints,
    AuditService audit,
    NotificationPublisher notifications,
    LockService locks,
    SecretProtector protector,
    ILogger<RepositoryService> logger)
{
    private static readonly string[] DangerousConfigKeys =
    [
        "core.fsmonitor", "core.sshcommand", "core.hookspath", "core.pager", "core.editor", "core.askpass", "core.gitproxy",
        "diff.external", "sequence.editor", "uploadpack.packobjectshook", "credential.helper", "include.path", "gpg.program"
    ];

    public async Task<RepositoryProvisionResult> CreateAsync(CreateRepositoryRequest req, CancellationToken ct)
    {
        Validate(req.ProjectExternalId, req.ProjectName);
        var company = await access.RequireCompanyAsync(req.CompanyId, WorkspaceRole.Developer, Scopes.ReposWrite, ct);
        var branch = RepoPath.Branch(req.DefaultBranch);
        var project = await EnsureProjectAsync(company, req.ProjectExternalId, req.ProjectName, ct);

        // Idempotency on (company, project): one repository per Wings Code project.
        var repo = await db.Repositories.Include(r => r.Remotes).Include(r => r.Settings).Include(r => r.Company).Include(r => r.Project)
            .FirstOrDefaultAsync(r => r.ProjectId == project.Id && r.State != RepositoryState.Deleted, ct);
        if (repo is { State: RepositoryState.Active or RepositoryState.Archived })
            return await ExistingResultAsync(repo, ct);

        var kind = req.Provider ?? ctx.Options.DefaultProvider;
        if (req.ExistingRemote is not null) kind = HostingProviderKind.GenericRemote;
        var provider = providers.Get(kind);
        var workDir = ctx.Paths.Authorize(req.WorkingDirectory ?? ctx.Paths.DefaultMainDirectory(company, project), company);

        await using var _ = await locks.AcquireAsync(LockService.KeyForPath(workDir), TimeSpan.FromSeconds(30), ct);
        var warnings = new List<string>();

        if (repo is null)
        {
            if (await db.WorkingDirectories.AnyAsync(w => w.Path == workDir && w.IsActive, ct))
                throw new ConflictException("working_directory_in_use", "The working directory is already registered to another repository.");
            repo = new Repository
            {
                CompanyId = company.Id, Company = company, ProjectId = project.Id, Project = project,
                Name = req.RepositoryName ?? req.ProjectName, Slug = await UniqueSlugAsync(company.Id, req.RepositoryName ?? req.ProjectName, ct),
                Description = req.Description, DefaultBranch = branch, State = RepositoryState.Provisioning,
                Settings = new RepositorySettings()
            };
            db.Repositories.Add(repo);
            db.WorkingDirectories.Add(new WorkingDirectory { RepositoryId = repo.Id, Path = workDir, Kind = WorkingDirectoryKind.Main, Branch = branch });
            audit.Record("repository.create.started", repo.Id, company.Id, repo.Slug, details: new { req.ProjectExternalId, workDir, provider = kind });
            await db.SaveChangesAsync(ct);
        }

        try
        {
            // 1. Remote repository (idempotent at the provider).
            var remote = repo.Remotes.FirstOrDefault();
            if (remote is null)
            {
                remote = await ProvisionRemoteAsync(repo, company, provider, req.ExistingRemote, ct);
                repo.Remotes.Add(remote);
                await db.SaveChangesAsync(ct);
            }

            // 2. Local repository.
            Directory.CreateDirectory(workDir);
            if (await ctx.Git.IsRepositoryAsync(workDir, ct))
            {
                var origin = await ctx.Git.GetConfigAsync(workDir, "remote.origin.url", ct);
                if (origin is not null && origin != remote.Url)
                    throw new ConflictException("already_git_repository",
                        "The working directory already contains a Git repository with a different origin. Use the import API to adopt it without losing history.");
            }
            else
            {
                if (Directory.Exists(Path.Combine(workDir, ".git")))
                    throw new ConflictException("invalid_git_directory", "The working directory contains a damaged .git folder; it was not modified.");
                await ctx.Git.InitAsync(workDir, branch, ct);
            }
            if (await ctx.Git.GetConfigAsync(workDir, "remote.origin.url", ct) is null)
                await ctx.Git.RunAsync(workDir, ["remote", "add", "origin", remote.Url], null, ct);

            // 3. .gitignore (never overwrite an existing one).
            var gitignore = Path.Combine(workDir, ".gitignore");
            if (!File.Exists(gitignore))
                await File.WriteAllTextAsync(gitignore, GitIgnoreTemplates.Build(req.GitIgnoreTemplates), ct);
            else
                warnings.Add("An existing .gitignore was kept unchanged.");

            // 4. Initial commit with secret scanning and exclusions.
            var head = await ctx.Git.HeadAsync(workDir, ct);
            if (head is null)
            {
                var status = await ctx.Git.StatusAsync(workDir, ct);
                var permitted = status.Where(s => RepositoryContext.IsPermittedPath(s.Path, repo.Settings)).Select(s => s.Path).ToList();
                foreach (var skipped in status.Select(s => s.Path).Except(permitted)) warnings.Add($"Excluded from version control: {skipped}");
                await StageAsync(workDir, permitted, ct);
                var findings = SecretScanner.ScanUnifiedDiff((await ctx.Git.RunAsync(workDir, ["diff", "--cached", "-U0", "--no-ext-diff", "--no-textconv"], null, ct)).StdOut);
                if (findings.Count > 0 && repo.Settings!.SecretScanMode == SecretScanMode.Block)
                {
                    var bad = findings.Select(f => f.Path).Distinct().ToList();
                    await ctx.Git.RunAsync(workDir, ["reset", "-q", "--", .. bad.Select(RepoPath.Literal)], new() { ThrowOnError = false }, ct);
                    warnings.AddRange(findings.Select(f => $"Not committed (possible secret, {f.Rule}): {f.Path}:{f.Line}"));
                    audit.Record("secrets.blocked", repo.Id, company.Id, string.Join(", ", bad), "blocked", findings);
                }
                var message = req.InitialCommitMessage is { Length: > 0 } m ? m : $"chore: initialize {repo.Name} repository\n\nCreated automatically by Wings Git Manager for Wings Code project {req.ProjectExternalId}.\n";
                head = await ctx.Git.CommitAsync(workDir, message, ctx.ServiceIdentity, ctx.ServiceIdentity, allowEmpty: true, ct);
            }

            await recorder.RecordRangeAsync(repo, workDir, branch, null, head, new CommitAttribution(InitiatingUser: access.Caller.Name), ct);
            var cp = await checkpoints.CreateAsync(repo, workDir, branch, head, CheckpointType.Initial, "Initial repository state", new CommitAttribution(InitiatingUser: access.Caller.Name), ct);
            repo.State = RepositoryState.Active;
            repo.UpdatedAt = DateTime.UtcNow;
            db.Branches.Add(new Branch { RepositoryId = repo.Id, Name = branch, HeadSha = head });
            await db.SaveChangesAsync(ct);

            // 5. Push + verify (queued durably first, so a Gitea outage never loses the initial commit).
            var item = await sync.EnqueueAsync(repo, workDir, branch, head, null, ct);
            var durability = await sync.PushNowAsync(item, ct);
            if (durability != DurabilityState.RemoteSynchronized)
                warnings.Add("The initial commit is stored locally and queued for synchronization; the remote is not yet verified.");
            else
                await ProtectAsync(repo, remote, provider, warnings, ct);

            audit.Record("repository.created", repo.Id, company.Id, repo.Slug, details: new { head, durability, remote = remote.Url });
            notifications.Publish("repository.created", company.Id, repo.Id, new { repo.Id, repo.Name });
            await db.SaveChangesAsync(ct);
            await db.Entry(cp).ReloadAsync(ct);

            return new RepositoryProvisionResult
            {
                RepositoryId = repo.Id, RemoteUrl = remote.Url, Branch = branch, CommitSha = head, CheckpointId = cp.Id,
                Durability = cp.SyncState, WorkingDirectory = workDir, Warnings = warnings
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Provisioning repository {Repo} failed", repo.Id);
            db.ChangeTracker.Clear();
            await audit.RecordNowAsync("repository.create", repo.Id, company.Id, repo.Slug, "failure", new { error = ex.Message }, CancellationToken.None);
            throw;
        }
    }

    public async Task<RepositoryProvisionResult> ImportAsync(ImportRepositoryRequest req, CancellationToken ct)
    {
        Validate(req.ProjectExternalId, req.ProjectName);
        if ((req.LocalPath is null) == (req.SourceRemote is null))
            throw new GitManagerException(ErrorCodes.Validation, "Provide exactly one of LocalPath or SourceRemote.");
        var company = await access.RequireCompanyAsync(req.CompanyId, WorkspaceRole.Developer, Scopes.ReposWrite, ct);
        var project = await EnsureProjectAsync(company, req.ProjectExternalId, req.ProjectName, ct);
        var existing = await db.Repositories.Include(r => r.Remotes).Include(r => r.Settings).Include(r => r.Company).Include(r => r.Project)
            .FirstOrDefaultAsync(r => r.ProjectId == project.Id && r.State != RepositoryState.Deleted, ct);
        if (existing is { State: RepositoryState.Active or RepositoryState.Archived }) return await ExistingResultAsync(existing, ct);
        if (existing is not null)
            throw new ConflictException(ErrorCodes.InvalidState, "A previous import or creation for this project did not finish; delete it or call create to resume.");

        var warnings = new List<string>();
        string workDir;
        if (req.LocalPath is not null)
        {
            workDir = ctx.Paths.Authorize(req.LocalPath, company);
            if (!await ctx.Git.IsRepositoryAsync(workDir, ct))
                throw new GitManagerException(ErrorCodes.Validation, "LocalPath is not the root of a Git working tree.");
            await EnsureSafeConfigAsync(workDir, ct);
        }
        else
        {
            workDir = ctx.Paths.Authorize(req.WorkingDirectory ?? ctx.Paths.DefaultMainDirectory(company, project), company);
            if (!WorkspacePaths.IsEmptyOrMissing(workDir))
                throw new ConflictException("target_not_empty", "The target working directory exists and is not empty; it was not modified.");
        }
        if (await db.WorkingDirectories.AnyAsync(w => w.Path == workDir && w.IsActive, ct))
            throw new ConflictException("working_directory_in_use", "The working directory is already registered to another repository.");

        await using var _ = await locks.AcquireAsync(LockService.KeyForPath(workDir), TimeSpan.FromSeconds(30), ct);
        if (req.SourceRemote is not null)
        {
            ValidateRemoteUrl(req.SourceRemote.Url);
            Directory.CreateDirectory(Path.GetDirectoryName(workDir)!);
            var cred = req.SourceRemote.Secret is null ? null : new GitCredential(req.SourceRemote.Username ?? "git", req.SourceRemote.Secret);
            await ctx.Git.RunAsync(Path.GetDirectoryName(workDir)!, ["clone", "--no-checkout", "--", req.SourceRemote.Url, workDir],
                new() { Credential = cred, Network = true, AllowFileProtocol = ctx.Options.AllowFileRemotes }, ct);
            // Bring every source branch into a local branch so the full history is pushed to the central server.
            foreach (var (name, sha, _) in await ctx.Git.RemoteBranchesAsync(workDir, "origin", ct))
                if (await ctx.Git.ResolveAsync(workDir, "refs/heads/" + name, ct) is null)
                    await ctx.Git.RunAsync(workDir, ["branch", "--no-track", "--", name, sha], null, ct);
            await ctx.Git.RunAsync(workDir, ["checkout", "--quiet", "--force"], null, ct);
        }

        var head = await ctx.Git.HeadAsync(workDir, ct) ?? throw new GitManagerException(ErrorCodes.Validation, "The repository has no commits to import.");
        var branch = await ctx.Git.CurrentBranchAsync(workDir, ct) ?? throw new GitManagerException(ErrorCodes.Validation, "The repository is in detached HEAD state; check out a branch before importing.");
        if (!await ctx.Git.IsCleanAsync(workDir, ct))
            warnings.Add("The working tree has uncommitted changes; they were not committed and will be checkpointed before the next change operation.");

        var kind = req.Provider ?? ctx.Options.DefaultProvider;
        var provider = providers.Get(kind);
        var repo = new Repository
        {
            CompanyId = company.Id, Company = company, ProjectId = project.Id, Project = project, Name = req.RepositoryName ?? req.ProjectName,
            Slug = await UniqueSlugAsync(company.Id, req.RepositoryName ?? req.ProjectName, ct), DefaultBranch = branch,
            State = RepositoryState.Provisioning, Settings = new RepositorySettings()
        };
        db.Repositories.Add(repo);
        db.WorkingDirectories.Add(new WorkingDirectory { RepositoryId = repo.Id, Path = workDir, Kind = WorkingDirectoryKind.Main, Branch = branch });
        await db.SaveChangesAsync(ct);

        var remote = await ProvisionRemoteAsync(repo, company, provider, null, ct);
        repo.Remotes.Add(remote);
        var origin = await ctx.Git.GetConfigAsync(workDir, "remote.origin.url", ct);
        if (req.SourceRemote is not null) await ctx.Git.RunAsync(workDir, ["remote", "set-url", "origin", remote.Url], null, ct);
        else if (origin is null) await ctx.Git.RunAsync(workDir, ["remote", "add", "origin", remote.Url], null, ct);
        else if (origin != remote.Url) warnings.Add($"Existing 'origin' remote kept unchanged; Wings Git Manager pushes directly to {remote.Url}.");

        // Push every branch and tag without force (the central repository is new and empty).
        var push = await ctx.Git.RunAsync(workDir, ["push", "--porcelain", remote.Url, "refs/heads/*:refs/heads/*", "refs/tags/*:refs/tags/*"],
            new() { Credential = ctx.Credential(repo), Network = true, ThrowOnError = false, AllowFileProtocol = ctx.AllowFile(repo) }, ct);
        if (!push.Success) warnings.Add("Initial push failed; the default branch was queued for retry: " + GitExecutor.Redact(push.StdErr.Trim()));

        await recorder.RecordRangeAsync(repo, workDir, branch, null, head, null, ct);
        foreach (var (name, sha, _) in await ctx.Git.LocalBranchesAsync(workDir, ct))
            db.Branches.Add(new Branch { RepositoryId = repo.Id, Name = name, HeadSha = sha });
        var cp = await checkpoints.CreateAsync(repo, workDir, branch, head, CheckpointType.Initial, "Imported repository state", new CommitAttribution(InitiatingUser: access.Caller.Name), ct);
        repo.State = RepositoryState.Active;
        audit.Record("repository.imported", repo.Id, company.Id, repo.Slug, details: new { source = req.LocalPath ?? GitExecutor.Redact(req.SourceRemote!.Url), head, branch });
        await db.SaveChangesAsync(ct);

        var item = await sync.EnqueueAsync(repo, workDir, branch, head, null, ct);
        var durability = await sync.PushNowAsync(item, ct);
        if (durability == DurabilityState.RemoteSynchronized) await ProtectAsync(repo, remote, provider, warnings, ct);
        await db.SaveChangesAsync(ct);
        await db.Entry(cp).ReloadAsync(ct);
        return new RepositoryProvisionResult
        {
            RepositoryId = repo.Id, RemoteUrl = remote.Url, Branch = branch, CommitSha = head, CheckpointId = cp.Id,
            Durability = cp.SyncState, WorkingDirectory = workDir, Warnings = warnings
        };
    }

    private async Task<RepositoryRemote> ProvisionRemoteAsync(Repository repo, Company company, IGitHostingProvider provider, RemoteCredentialInput? existing, CancellationToken ct)
    {
        if (provider.Kind == HostingProviderKind.GenericRemote)
        {
            if (existing is null) throw new GitManagerException(ErrorCodes.Validation, "ExistingRemote is required for generic remotes.");
            ValidateRemoteUrl(existing.Url);
            return new RepositoryRemote
            {
                RepositoryId = repo.Id, Provider = provider.Kind, Url = existing.Url, CredentialUsername = existing.Username,
                EncryptedCredential = existing.Secret is null ? null : protector.Protect(existing.Secret)
            };
        }
        var ns = await provider.EnsureNamespaceAsync(company, ct);
        if (company.GitNamespace != ns)
        {
            var tracked = await db.Companies.FirstAsync(c => c.Id == company.Id, ct);
            tracked.GitNamespace = ns;
        }
        var info = await provider.GetRepositoryAsync(ns, repo.Slug, ct);
        if (info is not null && !info.IsEmpty)
        {
            // Never adopt a non-empty remote silently: another project could own it.
            var owned = await db.RepositoryRemotes.AnyAsync(r => r.Owner == ns && r.RemoteRepositoryName == repo.Slug && r.RepositoryId != repo.Id, ct);
            if (owned) throw new ConflictException("remote_exists", $"Remote repository {ns}/{repo.Slug} already belongs to another project.");
            throw new ConflictException("remote_not_empty", $"Remote repository {ns}/{repo.Slug} already contains commits; import it instead.");
        }
        info ??= await provider.CreateRepositoryAsync(ns, repo.Slug, repo.Description, repo.DefaultBranch, ct);
        ValidateRemoteUrl(info.CloneUrl, allowFile: provider.Kind == HostingProviderKind.FileSystem);
        return new RepositoryRemote
        {
            RepositoryId = repo.Id, Provider = provider.Kind, Url = info.CloneUrl, Owner = info.Owner.Length > 0 ? info.Owner : ns,
            RemoteRepositoryName = info.Name, ProviderRepositoryId = info.ProviderRepositoryId
        };
    }

    private async Task ProtectAsync(Repository repo, RepositoryRemote remote, IGitHostingProvider provider, List<string> warnings, CancellationToken ct)
    {
        if (remote.Owner is null || remote.RemoteRepositoryName is null) return;
        try
        {
            foreach (var pattern in repo.Settings?.ProtectedBranches ?? [repo.DefaultBranch])
                await provider.ProtectBranchAsync(remote.Owner, remote.RemoteRepositoryName, pattern, ct);
            await provider.EnsureWebhookAsync(remote.Owner, remote.RemoteRepositoryName, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Branch protection/webhook setup failed for {Repo}", repo.Id);
            warnings.Add("Branch protection or webhook could not be configured on the remote: " + ex.Message);
        }
    }

    private async Task EnsureSafeConfigAsync(string dir, CancellationToken ct)
    {
        var list = await ctx.Git.RunAsync(dir, ["config", "--local", "--list"], null, ct);
        var bad = list.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('=')[0].ToLowerInvariant())
            .Where(k => DangerousConfigKeys.Contains(k) || k.StartsWith("filter.") && (k.EndsWith(".clean") || k.EndsWith(".smudge") || k.EndsWith(".process"))
                        || k.StartsWith("diff.") && (k.EndsWith(".textconv") || k.EndsWith(".command")) || k.StartsWith("includeif.") || k.StartsWith("merge.") && k.EndsWith(".driver"))
            .Distinct().ToList();
        if (bad.Count > 0)
            throw new PolicyViolationException(ErrorCodes.UnsafeRepository,
                "The repository configuration defines commands that Git could execute. Remove them before importing.", new { keys = bad });
        var hooks = Path.Combine(dir, ".git", "hooks");
        if (Directory.Exists(hooks) && Directory.EnumerateFiles(hooks).Any(f => !f.EndsWith(".sample")))
            logger.LogInformation("Repository {Dir} has hooks; they are never executed by Wings Git Manager", dir);
    }

    public void ValidateRemoteUrl(string url, bool allowFile = false)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http" && ctx.Options.AllowHttpRemotes))
        {
            if (!string.IsNullOrEmpty(u.UserInfo))
                throw new GitManagerException(ErrorCodes.Validation, "Remote URLs must not embed credentials; pass them separately.");
            return;
        }
        if ((allowFile || ctx.Options.AllowFileRemotes) && Path.IsPathFullyQualified(url)) return;
        throw new GitManagerException(ErrorCodes.Validation, "Remote URL must be https:// (ssh and other transports are disabled).");
    }

    private async Task StageAsync(string dir, IReadOnlyList<string> paths, CancellationToken ct)
    {
        foreach (var chunk in paths.Chunk(200))
            await ctx.Git.RunAsync(dir, ["add", "-A", "--", .. chunk.Select(RepoPath.Literal)], null, ct);
    }

    private async Task<Project> EnsureProjectAsync(Company company, string externalId, string name, CancellationToken ct)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.CompanyId == company.Id && p.ExternalId == externalId, ct);
        if (project is not null) return project;
        project = new Project { CompanyId = company.Id, ExternalId = externalId, Name = name, Slug = RepoPath.Slugify(name) };
        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);
        return project;
    }

    private async Task<string> UniqueSlugAsync(Guid companyId, string name, CancellationToken ct)
    {
        var baseSlug = RepoPath.Slugify(name);
        var slug = baseSlug;
        for (var i = 2; await db.Repositories.AnyAsync(r => r.CompanyId == companyId && r.Slug == slug, ct); i++) slug = $"{baseSlug}-{i}";
        return slug;
    }

    private async Task<RepositoryProvisionResult> ExistingResultAsync(Repository repo, CancellationToken ct)
    {
        var wd = await ctx.MainWorkingDirectoryAsync(repo.Id, ct);
        var cp = repo.LatestCheckpointId is { } id ? await db.Checkpoints.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct) : null;
        return new RepositoryProvisionResult
        {
            RepositoryId = repo.Id, RemoteUrl = ctx.PrimaryRemote(repo).Url, Branch = repo.DefaultBranch, CommitSha = cp?.CommitSha ?? repo.LastCommitSha,
            CheckpointId = cp?.Id, Durability = cp?.SyncState ?? DurabilityState.LocalCommit, WorkingDirectory = wd?.Path ?? "", AlreadyExisted = true
        };
    }

    private static void Validate(string externalId, string name)
    {
        if (string.IsNullOrWhiteSpace(externalId) || externalId.Length > 200)
            throw new GitManagerException(ErrorCodes.Validation, "ProjectExternalId is required (max 200 characters).");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200)
            throw new GitManagerException(ErrorCodes.Validation, "ProjectName is required (max 200 characters).");
    }

    // ---------------- Queries & management ----------------

    public async Task<PagedResult<RepositorySummaryDto>> ListAsync(Guid? companyId, Guid? projectId, string? search, RepositoryState? state, int page, int pageSize, CancellationToken ct)
    {
        var q = (await access.VisibleRepositoriesAsync(ct)).AsNoTracking();
        if (companyId is not null) q = q.Where(r => r.CompanyId == companyId);
        if (projectId is not null) q = q.Where(r => r.ProjectId == projectId);
        if (state is not null) q = q.Where(r => r.State == state);
        if (!string.IsNullOrWhiteSpace(search)) q = q.Where(r => r.Name.Contains(search) || r.Slug.Contains(search) || r.Project!.Name.Contains(search));
        var total = await q.CountAsync(ct);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var repos = await q.Include(r => r.Company).Include(r => r.Project).OrderBy(r => r.Company!.Name).ThenBy(r => r.Name)
            .Skip((Math.Max(page, 1) - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<RepositorySummaryDto>(await SummariesAsync(repos, ct), total, page, pageSize);
    }

    public async Task<IReadOnlyList<RepositorySummaryDto>> SummariesAsync(IReadOnlyList<Repository> repos, CancellationToken ct)
    {
        var ids = repos.Select(r => r.Id).ToList();
        var pending = await db.PushQueue.Where(p => ids.Contains(p.RepositoryId) && p.State != PushJobState.Succeeded)
            .GroupBy(p => p.RepositoryId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var cpIds = repos.Where(r => r.LatestCheckpointId != null).Select(r => r.LatestCheckpointId!.Value).ToList();
        var cps = await db.Checkpoints.Where(c => cpIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.SyncState, ct);
        return repos.Select(r => new RepositorySummaryDto
        {
            Id = r.Id, Name = r.Name, CompanyId = r.CompanyId, CompanyName = r.Company?.Name ?? "", ProjectId = r.ProjectId,
            ProjectName = r.Project?.Name ?? "", ProjectExternalId = r.Project?.ExternalId, DefaultBranch = r.DefaultBranch, State = r.State,
            LastCommitSha = r.LastCommitSha, LastCommitAt = r.LastCommitAt, LastPushAt = r.LastPushAt, RemoteStatus = r.RemoteStatus,
            LatestCheckpointId = r.LatestCheckpointId,
            LatestCheckpointDurability = r.LatestCheckpointId is { } c && cps.TryGetValue(c, out var s) ? s : null,
            StorageBytes = r.StorageBytes, HasUncommittedChanges = r.HasUncommittedChanges,
            PendingPushes = pending.GetValueOrDefault(r.Id), CreatedAt = r.CreatedAt
        }).ToList();
    }

    public async Task<RepositoryDetailDto> GetAsync(Guid id, CancellationToken ct)
    {
        var repo = await access.RequireRepositoryAsync(id, RepoAction.Read, ct, track: false);
        var wd = await ctx.MainWorkingDirectoryAsync(id, ct);
        var remote = ctx.PrimaryRemote(repo);
        return new RepositoryDetailDto
        {
            Summary = (await SummariesAsync([repo], ct))[0], Description = repo.Description, RemoteUrl = remote.Url,
            Provider = remote.Provider, MainWorkingDirectory = wd?.Path, Settings = ToDto(repo.Settings ?? new RepositorySettings())
        };
    }

    public async Task<RepositoryDetailDto> UpdateAsync(Guid id, UpdateRepositoryRequest req, CancellationToken ct)
    {
        var repo = await access.RequireRepositoryAsync(id, RepoAction.Administer, ct);
        if (req.Description is not null) repo.Description = req.Description;
        if (req.DefaultBranch is not null && req.DefaultBranch != repo.DefaultBranch)
        {
            var dir = await ctx.RequireMainDirectoryAsync(repo, ct);
            if (await ctx.Git.ResolveAsync(dir, "refs/heads/" + RepoPath.Branch(req.DefaultBranch), ct) is null)
                throw new GitManagerException(ErrorCodes.Validation, $"Branch '{req.DefaultBranch}' does not exist.");
            repo.DefaultBranch = req.DefaultBranch;
        }
        if (req.Settings is { } s)
        {
            var settings = repo.Settings ??= new RepositorySettings { RepositoryId = repo.Id };
            settings.AutoPush = s.AutoPush;
            settings.RequireRemoteCheckpointBeforeChanges = s.RequireRemoteCheckpointBeforeChanges;
            settings.CommitUncommittedChangesOnBegin = s.CommitUncommittedChangesOnBegin;
            settings.SecretScanMode = s.SecretScanMode;
            settings.ExcludedPathPatterns = s.ExcludedPathPatterns.Where(p => !string.IsNullOrWhiteSpace(p) && p.Length < 300 && !p.Contains('\0')).ToList();
            settings.ProtectedBranches = s.ProtectedBranches.Select(RepoPath.BranchPattern).ToList();
            settings.RequireMergeApproval = s.RequireMergeApproval;
            settings.WorktreeRetentionDays = Math.Clamp(s.WorktreeRetentionDays, 1, 3650);
            settings.BackupIntervalHours = Math.Clamp(s.BackupIntervalHours, 1, 24 * 30);
            settings.BackupRetentionCount = Math.Clamp(s.BackupRetentionCount, 3, 10000);
            settings.WatchFileSystem = s.WatchFileSystem;
            settings.AutoCommitExternalChanges = s.AutoCommitExternalChanges;
            settings.UpdatedAt = DateTime.UtcNow;
        }
        repo.UpdatedAt = DateTime.UtcNow;
        audit.Record("repository.updated", repo.Id, repo.CompanyId, repo.Slug, details: req);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task ArchiveAsync(Guid id, CancellationToken ct)
    {
        var repo = await access.RequireRepositoryAsync(id, RepoAction.Administer, ct);
        var remote = ctx.PrimaryRemote(repo);
        if (remote.Owner is not null && remote.RemoteRepositoryName is not null)
            await ctx.Provider(repo).ArchiveRepositoryAsync(remote.Owner, remote.RemoteRepositoryName, true, ct);
        repo.State = RepositoryState.Archived;
        repo.ArchivedAt = DateTime.UtcNow;
        audit.Record("repository.archived", repo.Id, repo.CompanyId, repo.Slug);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Removes the repository from Wings. The central remote is archived, not destroyed, unless a system
    /// administrator purges it — and purging requires a verified independent backup.
    /// </summary>
    public async Task DeleteAsync(Guid id, bool purgeRemote, CancellationToken ct)
    {
        var repo = await access.RequireRepositoryAsync(id, RepoAction.Delete, ct);
        var remote = ctx.PrimaryRemote(repo);
        var provider = ctx.Provider(repo);
        if (purgeRemote)
        {
            access.RequireSystemAdministrator();
            var verified = await db.RepositoryBackups.AnyAsync(b => b.RepositoryId == id && b.State == BackupState.Verified, ct);
            if (!verified) throw new PolicyViolationException("backup_required", "Purging the central repository requires a verified backup first.");
            if (remote.Owner is not null && remote.RemoteRepositoryName is not null)
                await provider.DeleteRepositoryAsync(remote.Owner, remote.RemoteRepositoryName, ct);
        }
        else if (remote.Owner is not null && remote.RemoteRepositoryName is not null)
        {
            await provider.ArchiveRepositoryAsync(remote.Owner, remote.RemoteRepositoryName, true, ct);
        }
        repo.State = RepositoryState.Deleted;
        repo.DeletedAt = DateTime.UtcNow;
        await db.WorkingDirectories.Where(w => w.RepositoryId == id).ExecuteUpdateAsync(s => s.SetProperty(w => w.IsActive, false), ct);
        audit.Record("repository.deleted", repo.Id, repo.CompanyId, repo.Slug, details: new { purgeRemote });
        await db.SaveChangesAsync(ct);
    }

    public async Task<RepositoryStatusDto> StatusAsync(Guid id, CancellationToken ct)
    {
        var repo = await access.RequireRepositoryAsync(id, RepoAction.Read, ct);
        var wd = await ctx.MainWorkingDirectoryAsync(id, ct);
        var activeOp = await db.ChangeOperations.Where(o => o.RepositoryId == id && (o.State == ChangeOperationState.Active || o.State == ChangeOperationState.Committing))
            .Select(o => (Guid?)o.Id).FirstOrDefaultAsync(ct);
        if (wd is null || !await ctx.Git.IsRepositoryAsync(wd.Path, ct))
            return new RepositoryStatusDto { RepositoryId = id, WorkingDirectory = wd?.Path ?? "", WorkingDirectoryExists = false, RemoteStatus = repo.RemoteStatus, ActiveOperationId = activeOp };

        var head = await ctx.Git.HeadAsync(wd.Path, ct);
        var branch = await ctx.Git.CurrentBranchAsync(wd.Path, ct);
        var changes = await ctx.Git.StatusAsync(wd.Path, ct);
        var known = branch is null ? null : await db.Branches.Where(b => b.RepositoryId == id && b.Name == branch).Select(b => b.RemoteHeadSha).FirstOrDefaultAsync(ct);
        int ahead = 0, behind = 0;
        if (head is not null && known is not null && await ctx.Git.ObjectExistsAsync(wd.Path, known, ct))
        {
            ahead = await ctx.Git.CountAsync(wd.Path, $"{known}..{head}", ct);
            behind = await ctx.Git.CountAsync(wd.Path, $"{head}..{known}", ct);
        }
        var status = known is null ? RemoteStatus.Unknown : ahead > 0 && behind > 0 ? RemoteStatus.Diverged : ahead > 0 ? RemoteStatus.Ahead : behind > 0 ? RemoteStatus.Behind : RemoteStatus.InSync;
        repo.HasUncommittedChanges = changes.Count > 0;
        await db.SaveChangesAsync(ct);
        return new RepositoryStatusDto
        {
            RepositoryId = id, WorkingDirectory = wd.Path, WorkingDirectoryExists = true, Branch = branch, HeadSha = head, RemoteHeadSha = known,
            Ahead = ahead, Behind = behind, RemoteStatus = status, IsClean = changes.Count == 0, Changes = changes, ActiveOperationId = activeOp
        };
    }

    public static RepositorySettingsDto ToDto(RepositorySettings s) => new()
    {
        AutoPush = s.AutoPush, RequireRemoteCheckpointBeforeChanges = s.RequireRemoteCheckpointBeforeChanges,
        CommitUncommittedChangesOnBegin = s.CommitUncommittedChangesOnBegin, SecretScanMode = s.SecretScanMode,
        ExcludedPathPatterns = s.ExcludedPathPatterns.ToArray(), ProtectedBranches = s.ProtectedBranches.ToArray(),
        RequireMergeApproval = s.RequireMergeApproval, WorktreeRetentionDays = s.WorktreeRetentionDays, BackupIntervalHours = s.BackupIntervalHours,
        BackupRetentionCount = s.BackupRetentionCount, WatchFileSystem = s.WatchFileSystem, AutoCommitExternalChanges = s.AutoCommitExternalChanges
    };
}

public static class GitIgnoreTemplates
{
    private const string Platform = """
        # --- Wings Git Manager defaults: secrets are never committed ---
        .env
        .env.*
        !.env.example
        !.env.sample
        *.pem
        *.key
        *.pfx
        *.p12
        secrets.json
        .git-credentials
        .netrc
        # OS / editor
        .DS_Store
        Thumbs.db
        *.swp
        .idea/
        .vs/
        *.log
        """;

    private static readonly Dictionary<string, string> Templates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dotnet"] = "# .NET\nbin/\nobj/\n*.user\n*.suo\nTestResults/\nartifacts/\n",
        ["node"] = "# Node\nnode_modules/\nnpm-debug.log*\n.npm/\ncoverage/\n",
        ["angular"] = "# Angular\ndist/\n.angular/\n.sass-cache/\n",
        ["python"] = "# Python\n__pycache__/\n*.py[cod]\n.venv/\nvenv/\n.pytest_cache/\n",
        ["java"] = "# Java\ntarget/\n*.class\n.gradle/\nbuild/\n",
    };

    public static string Build(IEnumerable<string>? templates)
    {
        var names = templates?.ToList() is { Count: > 0 } t ? t : ["dotnet", "node", "angular"];
        return Platform.Replace("        ", "") + "\n\n" + string.Join("\n", names.Where(Templates.ContainsKey).Select(n => Templates[n]));
    }
}
