using Microsoft.EntityFrameworkCore;
using Wings.GitManager.Contracts;
using Wings.GitManager.Core;
using Wings.GitManager.Core.Entities;
using Wings.GitManager.Core.Safety;
using Wings.GitManager.Infrastructure.Data;

namespace Wings.GitManager.Infrastructure.Services;

/// <summary>Authorized synchronization, manual commit and working-tree diff operations exposed by the API.</summary>
public sealed class SyncOperations(
    GitManagerDbContext db,
    AccessControl access,
    RepositoryContext ctx,
    SyncService sync,
    CommitRecorder recorder,
    CheckpointService checkpoints,
    AuditService audit,
    LockService locks)
{
    public async Task<SyncStatusDto> PushAsync(Guid repositoryId, PushRequest req, CancellationToken ct)
    {
        var repo = await access.RequireRepositoryAsync(repositoryId, RepoAction.Write, ct);
        var dir = await ctx.RequireMainDirectoryAsync(repo, ct);
        var branches = req.Branch is null ? (await ctx.Git.LocalBranchesAsync(dir, ct)).Select(b => b.Name).ToList() : [RepoPath.Branch(req.Branch)];
        foreach (var b in branches)
        {
            var sha = await ctx.Git.ResolveAsync(dir, "refs/heads/" + b, ct);
            if (sha is null) continue;
            var item = await sync.EnqueueAsync(repo, dir, b, sha, null, ct);
            if (req.Wait) await sync.PushNowAsync(item, ct);
        }
        audit.Record("sync.push", repo.Id, repo.CompanyId, req.Branch ?? "all");
        await db.SaveChangesAsync(ct);
        return await StatusAsync(repositoryId, ct);
    }

    /// <summary>Fetches from the central server and records verified remote heads (no local branch is changed).</summary>
    public async Task<FetchResultDto> FetchAsync(Guid repositoryId, CancellationToken ct)
    {
        var repo = await access.RequireRepositoryAsync(repositoryId, RepoAction.Write, ct);
        var dir = await ctx.RequireMainDirectoryAsync(repo, ct);
        try
        {
            await ctx.Git.FetchAsync(dir, ctx.PrimaryRemote(repo).Url, ctx.Credential(repo), ct, ctx.AllowFile(repo));
        }
        catch (GitCommandException ex)
        {
            repo.RemoteStatus = RemoteStatus.Unreachable;
            await db.SaveChangesAsync(ct);
            throw new GitManagerException(ErrorCodes.RemoteUnavailable, "Fetch failed: " + ex.Message, 502);
        }
        var remote = await ctx.Git.RemoteBranchesAsync(dir, "origin", ct);
        foreach (var (name, sha, _) in remote)
        {
            var b = await db.Branches.FirstOrDefaultAsync(x => x.RepositoryId == repo.Id && x.Name == name, ct);
            if (b is null) db.Branches.Add(b = new Branch { RepositoryId = repo.Id, Name = name });
            b.RemoteHeadSha = sha;
            b.UpdatedAt = DateTime.UtcNow;
            await sync.OnVerifiedAsync(repo, dir, name, sha, ct);
        }
        var status = await StatusAsync(repositoryId, ct);
        repo.RemoteStatus = status.Branches.Any(x => x.Status == RemoteStatus.Diverged) ? RemoteStatus.Diverged
            : status.Branches.FirstOrDefault(x => x.Branch == repo.DefaultBranch)?.Status ?? RemoteStatus.Unknown;
        await db.SaveChangesAsync(ct);
        return new FetchResultDto(repo.Id, status.Branches);
    }

    public async Task<SyncStatusDto> StatusAsync(Guid repositoryId, CancellationToken ct)
    {
        var repo = await access.RequireRepositoryAsync(repositoryId, RepoAction.Read, ct);
        var wd = await ctx.MainWorkingDirectoryAsync(repo.Id, ct);
        var branches = new List<BranchSyncDto>();
        var tracked = await db.Branches.AsNoTracking().Where(b => b.RepositoryId == repo.Id).ToListAsync(ct);
        if (wd is not null && await ctx.Git.IsRepositoryAsync(wd.Path, ct))
        {
            foreach (var (name, sha, _) in await ctx.Git.LocalBranchesAsync(wd.Path, ct))
            {
                var remote = tracked.FirstOrDefault(t => t.Name == name)?.RemoteHeadSha;
                int ahead = 0, behind = 0;
                if (remote is not null && remote != sha && await ctx.Git.ObjectExistsAsync(wd.Path, remote, ct))
                {
                    ahead = await ctx.Git.CountAsync(wd.Path, $"{remote}..{sha}", ct);
                    behind = await ctx.Git.CountAsync(wd.Path, $"{sha}..{remote}", ct);
                }
                var st = remote is null ? RemoteStatus.Unknown : ahead > 0 && behind > 0 ? RemoteStatus.Diverged : ahead > 0 ? RemoteStatus.Ahead : behind > 0 ? RemoteStatus.Behind : RemoteStatus.InSync;
                branches.Add(new BranchSyncDto(name, sha, remote, ahead, behind, st));
            }
        }
        var q = db.PushQueue.AsNoTracking().Where(p => p.RepositoryId == repo.Id);
        return new SyncStatusDto
        {
            RepositoryId = repo.Id, RemoteStatus = repo.RemoteStatus, LastPushAt = repo.LastPushAt, LastVerifiedSha = repo.LastVerifiedSha,
            Pending = await q.CountAsync(p => p.State == PushJobState.Pending || p.State == PushJobState.RetryScheduled || p.State == PushJobState.Processing, ct),
            Failed = await q.CountAsync(p => p.State == PushJobState.PermanentFailure || p.State == PushJobState.AuthenticationFailed, ct),
            Conflicts = await q.CountAsync(p => p.State == PushJobState.Conflict, ct),
            Branches = branches
        };
    }

    public async Task<IReadOnlyList<PushJobDto>> QueueAsync(Guid? repositoryId, PushJobState? state, int take, CancellationToken ct)
    {
        IQueryable<Guid> visible = (await access.VisibleRepositoriesAsync(ct)).Select(r => r.Id);
        if (repositoryId is { } rid) await access.RequireRepositoryAsync(rid, RepoAction.Read, ct, track: false);
        var q = db.PushQueue.AsNoTracking().Where(p => visible.Contains(p.RepositoryId));
        if (repositoryId is not null) q = q.Where(p => p.RepositoryId == repositoryId);
        if (state is not null) q = q.Where(p => p.State == state);
        var items = await q.OrderByDescending(p => p.CreatedAt).Take(Math.Clamp(take, 1, 1000)).ToListAsync(ct);
        var names = await db.Repositories.AsNoTracking().Where(r => items.Select(i => i.RepositoryId).Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Name, ct);
        return items.Select(i => SyncService.ToDto(i, names.GetValueOrDefault(i.RepositoryId))).ToList();
    }

    public async Task<int> RetryAsync(Guid repositoryId, RetryRequest req, CancellationToken ct)
    {
        var repo = await access.RequireRepositoryAsync(repositoryId, RepoAction.Administer, ct);
        var n = await sync.RetryAsync(repositoryId, req.JobId, ct);
        audit.Record("sync.retry", repo.Id, repo.CompanyId, req.JobId?.ToString() ?? "all", details: new { count = n });
        await db.SaveChangesAsync(ct);
        return n;
    }

    /// <summary>Manual commit from the UI or an integration (attributed to the calling user, not an agent).</summary>
    public async Task<CommitResultDto> CommitAsync(Guid repositoryId, CreateCommitRequest req, CancellationToken ct)
    {
        var repo = await access.RequireRepositoryAsync(repositoryId, RepoAction.Write, ct);
        if (string.IsNullOrWhiteSpace(req.Message)) throw new GitManagerException(ErrorCodes.Validation, "Message is required.");
        var dir = await ctx.RequireMainDirectoryAsync(repo, ct);
        await using var _ = await locks.AcquireAsync(LockService.KeyForPath(dir), TimeSpan.FromSeconds(30), ct);
        if (await db.ChangeOperations.AnyAsync(o => o.WorkingDirectory == dir && (o.State == ChangeOperationState.Active || o.State == ChangeOperationState.Committing), ct))
            throw new ConflictException(ErrorCodes.WorkspaceBusy, "A change operation is active in this working directory.");
        var head = await ctx.Git.HeadAsync(dir, ct);
        if (req.ExpectedHead is not null && head?.StartsWith(RepoPath.Sha(req.ExpectedHead)) != true)
            throw new ConflictException(ErrorCodes.HeadMismatch, $"HEAD is {head}, expected {req.ExpectedHead}.");
        var settings = repo.Settings ?? new RepositorySettings();
        var status = await ctx.Git.StatusAsync(dir, ct);
        var wanted = req.Paths?.Select(RepoPath.Normalize).ToList();
        var paths = status.Select(s => s.Path).Where(p => (wanted is null || wanted.Any(w => p == w || p.StartsWith(w.TrimEnd('/') + "/"))) && RepositoryContext.IsPermittedPath(p, settings)).ToList();
        foreach (var chunk in paths.Chunk(200)) await ctx.Git.RunAsync(dir, ["add", "-A", "--", .. chunk.Select(RepoPath.Literal)], null, ct);
        var diff = await ctx.Git.RunAsync(dir, ["diff", "--cached", "-U0", "--no-ext-diff", "--no-textconv"], null, ct);
        var findings = SecretScanner.ScanUnifiedDiff(diff.StdOut);
        if (findings.Count > 0 && settings.SecretScanMode == SecretScanMode.Block)
        {
            await ctx.Git.RunAsync(dir, ["reset", "-q"], null, ct);
            throw new PolicyViolationException(ErrorCodes.SecretsDetected, "Possible secrets detected; nothing was committed.", new { findings });
        }
        var files = await ctx.Git.DiffFilesAsync(dir, null, null, true, null, ct);
        if (files.Count == 0) return new CommitResultDto(null, true, null, DurabilityState.WorkingTreeOnly, []);
        var user = new Git.GitIdentity(access.Caller.Name, access.Caller.Name.Contains('@') ? access.Caller.Name : ctx.Options.ServiceCommitterEmail);
        var sha = await ctx.Git.CommitAsync(dir, req.Message.Trim() + "\n", user, ctx.ServiceIdentity, false, ct);
        var branch = await ctx.Git.CurrentBranchAsync(dir, ct) ?? repo.DefaultBranch;
        await recorder.RecordAsync(repo, dir, branch, sha, new CommitAttribution(InitiatingUser: access.Caller.Name), ct);
        var cp = await checkpoints.CreateAsync(repo, dir, branch, sha, CheckpointType.Manual, req.Message.Split('\n')[0], new CommitAttribution(InitiatingUser: access.Caller.Name), ct);
        audit.Record("commit.manual", repo.Id, repo.CompanyId, sha, details: new { files = files.Count });
        await db.SaveChangesAsync(ct);
        var durability = DurabilityState.LocalCommit;
        if (req.Push)
        {
            var item = await sync.EnqueueAsync(repo, dir, branch, sha, null, ct);
            durability = await sync.PushNowAsync(item, ct);
        }
        return new CommitResultDto(sha, false, cp.Id, durability, files);
    }

    /// <summary>Uncommitted changes of the main working directory as a diff against HEAD (tracked + untracked).</summary>
    public async Task<DiffDto> WorkingTreeDiffAsync(Guid repositoryId, bool includePatch, CancellationToken ct)
    {
        var repo = await access.RequireRepositoryAsync(repositoryId, RepoAction.Read, ct, track: false);
        var dir = await ctx.RequireMainDirectoryAsync(repo, ct);
        var head = await ctx.Git.HeadAsync(dir, ct) ?? throw new ConflictException(ErrorCodes.InvalidState, "No commits.");
        var status = await ctx.Git.StatusAsync(dir, ct);
        var tracked = await ctx.Git.DiffFilesAsync(dir, "HEAD", null, false, null, ct);
        var files = tracked.Select(f => new FileDiffDto { Path = f.Path, OldPath = f.OldPath, Kind = f.Kind, Additions = f.Additions, Deletions = f.Deletions }).ToList();
        if (includePatch)
        {
            var raw = (await ctx.Git.RunAsync(dir, ["diff", "--no-ext-diff", "--no-textconv", "--no-color", "HEAD"], null, ct)).StdOut;
            foreach (var chunk in raw.Split("diff --git ", StringSplitOptions.RemoveEmptyEntries))
            {
                var header = chunk.Split('\n')[0];
                var p = header[(header.LastIndexOf(" b/", StringComparison.Ordinal) + 3)..].Trim();
                var i = files.FindIndex(f => f.Path == p);
                if (i >= 0) files[i] = files[i] with { Patch = "diff --git " + (chunk.Length > 200_000 ? chunk[..200_000] : chunk) };
            }
        }
        foreach (var u in status.Where(s => s.Untracked))
            files.Add(new FileDiffDto { Path = u.Path, Kind = FileChangeKind.Added });
        return new DiffDto { FromRef = head, ToRef = "WORKING_TREE", Files = files, TotalAdditions = files.Sum(f => f.Additions), TotalDeletions = files.Sum(f => f.Deletions) };
    }

    public async Task<PagedResult<CheckpointDto>> CheckpointsAsync(Guid repositoryId, CheckpointType? type, Guid? sessionId, int page, int pageSize, CancellationToken ct)
    {
        await access.RequireRepositoryAsync(repositoryId, RepoAction.Read, ct, track: false);
        var q = db.Checkpoints.AsNoTracking().Where(c => c.RepositoryId == repositoryId);
        if (type is not null) q = q.Where(c => c.Type == type);
        if (sessionId is not null) q = q.Where(c => c.SessionId == sessionId);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(c => c.CreatedAt).Skip((Math.Max(1, page) - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<CheckpointDto>(items.Select(CheckpointService.ToDto).ToList(), total, page, pageSize);
    }

    /// <summary>Gitea push webhook: records that the remote moved (verification still happens through our own checks).</summary>
    public async Task HandleWebhookAsync(string fullName, string? refName, string? after, CancellationToken ct)
    {
        var parts = fullName.Split('/', 2);
        if (parts.Length != 2) return;
        var remote = await db.RepositoryRemotes.FirstOrDefaultAsync(r => r.Owner == parts[0] && r.RemoteRepositoryName == parts[1], ct);
        if (remote is null) return;
        var repo = await db.Repositories.FirstAsync(r => r.Id == remote.RepositoryId, ct);
        var branch = refName?.StartsWith("refs/heads/") == true ? refName["refs/heads/".Length..] : null;
        var known = branch is null ? null : await db.Branches.FirstOrDefaultAsync(b => b.RepositoryId == repo.Id && b.Name == branch, ct);
        var external = known?.RemoteHeadSha != after && known?.HeadSha != after;
        if (external && branch == repo.DefaultBranch && repo.RemoteStatus == RemoteStatus.InSync) repo.RemoteStatus = RemoteStatus.Behind;
        db.AuditLogs.Add(new AuditLog { Action = "webhook.push", RepositoryId = repo.Id, CompanyId = repo.CompanyId, Target = refName, Actor = "gitea", ActorType = "webhook", Details = Json.Serialize(new { after, external }) });
        db.NotificationEvents.Add(new NotificationEvent { Type = external ? "remote.updated-externally" : "remote.updated", CompanyId = repo.CompanyId, RepositoryId = repo.Id, PayloadJson = Json.Serialize(new { branch, after }) });
        await db.SaveChangesAsync(ct);
    }
}
