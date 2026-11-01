using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wings.GitManager.Contracts;
using Wings.GitManager.Core;
using Wings.GitManager.Core.Commits;
using Wings.GitManager.Core.Entities;
using Wings.GitManager.Core.Safety;
using Wings.GitManager.Infrastructure.Data;
using Wings.GitManager.Infrastructure.Git;

namespace Wings.GitManager.Infrastructure.Services;

/// <summary>Isolated Git worktrees: one working directory per concurrent AI session, owned by that session.</summary>
public sealed class WorktreeService(
    GitManagerDbContext db,
    AccessControl access,
    RepositoryContext ctx,
    SyncService sync,
    CheckpointService checkpoints,
    CommitRecorder recorder,
    AuditService audit,
    LockService locks)
{
    public async Task<Worktree> CreateInternalAsync(Repository repo, string mainDir, string branch, string startSha, Guid? sessionId, string? agentId, bool newBranch, CancellationToken ct)
    {
        RepoPath.Branch(branch);
        var root = WorkspacePaths.WorktreesRoot(mainDir);
        var leaf = sessionId is { } sid ? $"session-{sid.ToString("N")[..8]}" : RepoPath.Slugify(branch.Replace('/', '-'));
        var path = ctx.Paths.Authorize(Path.Combine(root, leaf), repo.Company!);
        if (!WorkspacePaths.IsEmptyOrMissing(path))
            throw new ConflictException("worktree_path_in_use", $"Worktree path {path} already exists.");
        Directory.CreateDirectory(root);

        await using (await locks.AcquireAsync(LockService.KeyForRepository(repo.Id), TimeSpan.FromSeconds(30), ct))
        {
            await ctx.Git.RunAsync(mainDir, ["worktree", "prune"], null, ct);
            if (newBranch)
            {
                if (await ctx.Git.ResolveAsync(mainDir, "refs/heads/" + branch, ct) is not null)
                    throw new ConflictException("branch_exists", $"Branch '{branch}' already exists.");
                await ctx.Git.RunAsync(mainDir, ["worktree", "add", "-b", branch, "--", path, startSha], null, ct);
            }
            else
            {
                var checkedOut = await ctx.Git.WorktreeListAsync(mainDir, ct);
                if (checkedOut.Any(w => w.Branch == branch))
                    throw new ConflictException("branch_checked_out", $"Branch '{branch}' is already checked out in another working directory.");
                await ctx.Git.RunAsync(mainDir, ["worktree", "add", "--", path, branch], null, ct);
            }
        }
        var wd = new WorkingDirectory { RepositoryId = repo.Id, Path = path, Kind = WorkingDirectoryKind.Worktree, Branch = branch };
        var wt = new Worktree
        {
            RepositoryId = repo.Id, WorkingDirectoryId = wd.Id, Path = path, Branch = branch, SessionId = sessionId,
            OwnerAgentId = agentId, BaseCommitSha = startSha
        };
        db.WorkingDirectories.Add(wd);
        db.Worktrees.Add(wt);
        var b = await db.Branches.FirstOrDefaultAsync(x => x.RepositoryId == repo.Id && x.Name == branch, ct);
        if (b is null) db.Branches.Add(new Branch { RepositoryId = repo.Id, Name = branch, HeadSha = startSha, SessionId = sessionId, IsSessionBranch = sessionId is not null });
        audit.Record("worktree.created", repo.Id, repo.CompanyId, path, details: new { branch, startSha, sessionId, agentId });
        await db.SaveChangesAsync(ct);
        return wt;
    }

    public async Task<WorktreeDto> CreateAsync(Guid repositoryId, CreateWorktreeRequest req, CancellationToken ct)
    {
        var repo = await access.RequireRepositoryAsync(repositoryId, RepoAction.Write, ct);
        var main = await ctx.RequireMainDirectoryAsync(repo, ct);
        var exists = await ctx.Git.ResolveAsync(main, "refs/heads/" + RepoPath.Branch(req.Branch), ct);
        var start = exists ?? await ctx.Git.ResolveRequiredAsync(main, RepoPath.Revision(req.StartPoint ?? repo.DefaultBranch), ct);
        var wt = await CreateInternalAsync(repo, main, req.Branch, start, req.SessionId, req.OwnerAgentId, exists is null, ct);
        return await ToDtoAsync(wt, ct);
    }

    public async Task<IReadOnlyList<WorktreeDto>> ListAsync(Guid repositoryId, bool includeReleased, CancellationToken ct)
    {
        await access.RequireRepositoryAsync(repositoryId, RepoAction.Read, ct, track: false);
        var list = await db.Worktrees.AsNoTracking().Where(w => w.RepositoryId == repositoryId && (includeReleased || w.State == WorktreeState.Active))
            .OrderByDescending(w => w.CreatedAt).ToListAsync(ct);
        var result = new List<WorktreeDto>();
        foreach (var w in list) result.Add(await ToDtoAsync(w, ct));
        return result;
    }

    /// <summary>Removes a worktree directory. Uncommitted work is first committed to the worktree's branch and pushed.</summary>
    public async Task<WorktreeDto> ReleaseAsync(Guid repositoryId, Guid worktreeId, ReleaseWorktreeRequest req, CancellationToken ct, bool systemCleanup = false)
    {
        var repo = systemCleanup
            ? await db.Repositories.Include(r => r.Remotes).Include(r => r.Company).Include(r => r.Settings).FirstAsync(r => r.Id == repositoryId, ct)
            : await access.RequireRepositoryAsync(repositoryId, RepoAction.Write, ct);
        var wt = await db.Worktrees.FirstOrDefaultAsync(w => w.Id == worktreeId && w.RepositoryId == repositoryId, ct) ?? throw new NotFoundException("Worktree");
        if (wt.State != WorktreeState.Active) return await ToDtoAsync(wt, ct);
        if (await db.ChangeOperations.AnyAsync(o => o.WorkingDirectory == wt.Path && (o.State == ChangeOperationState.Active || o.State == ChangeOperationState.Committing), ct))
            throw new ConflictException(ErrorCodes.WorkspaceBusy, "A change operation is active in this worktree.");

        var main = await ctx.RequireMainDirectoryAsync(repo, ct);
        if (await ctx.Git.IsRepositoryAsync(wt.Path, ct))
        {
            await using var _ = await locks.AcquireAsync(LockService.KeyForPath(wt.Path), TimeSpan.FromSeconds(30), ct);
            if (!await ctx.Git.IsCleanAsync(wt.Path, ct))
            {
                if (!req.PreserveUncommittedChanges)
                    throw new ConflictException("worktree_dirty", "The worktree has uncommitted changes; release with PreserveUncommittedChanges=true to save them first.");
                await ctx.Git.RunAsync(wt.Path, ["add", "-A", "--", "."], null, ct);
                var sha = await ctx.Git.CommitAsync(wt.Path, CommitMessageBuilder.Checkpoint("uncommitted work saved when the worktree was released", null, wt.SessionId, wt.OwnerAgentId, null),
                    ctx.ServiceIdentity, ctx.ServiceIdentity, false, ct);
                await recorder.RecordAsync(repo, wt.Path, wt.Branch, sha, new CommitAttribution(SessionId: wt.SessionId, AgentId: wt.OwnerAgentId, CommitType: "checkpoint"), ct);
                await checkpoints.CreateAsync(repo, wt.Path, wt.Branch, sha, CheckpointType.Recovery, "Saved when worktree was released", new CommitAttribution(SessionId: wt.SessionId, AgentId: wt.OwnerAgentId), ct);
                await db.SaveChangesAsync(ct);
                var item = await sync.EnqueueAsync(repo, main, wt.Branch, sha, null, ct);
                await sync.PushNowAsync(item, ct);
            }
            await using (await locks.AcquireAsync(LockService.KeyForRepository(repo.Id), TimeSpan.FromSeconds(30), ct))
                await ctx.Git.RunAsync(main, ["worktree", "remove", "--", wt.Path], null, ct);
        }
        else
        {
            await ctx.Git.RunAsync(main, ["worktree", "prune"], null, ct);
        }
        wt.State = WorktreeState.Released;
        wt.ReleasedAt = DateTime.UtcNow;
        await db.WorkingDirectories.Where(w => w.Id == wt.WorkingDirectoryId).ExecuteUpdateAsync(s => s.SetProperty(w => w.IsActive, false), ct);
        audit.Record("worktree.released", repo.Id, repo.CompanyId, wt.Path, details: new { wt.Branch, systemCleanup });
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(wt, ct);
    }

    public async Task<WorktreeDto> ToDtoAsync(Worktree w, CancellationToken ct)
    {
        string? head = null;
        var dirty = false;
        if (w.State == WorktreeState.Active && await ctx.Git.IsRepositoryAsync(w.Path, ct))
        {
            head = await ctx.Git.HeadAsync(w.Path, ct);
            dirty = !await ctx.Git.IsCleanAsync(w.Path, ct);
        }
        return new WorktreeDto
        {
            Id = w.Id, Path = w.Path, Branch = w.Branch, SessionId = w.SessionId, OwnerAgentId = w.OwnerAgentId,
            State = w.State == WorktreeState.Active && head is null ? WorktreeState.Missing : w.State,
            BaseCommitSha = w.BaseCommitSha, HeadSha = head, HasUncommittedChanges = dirty, CreatedAt = w.CreatedAt, ReleasedAt = w.ReleasedAt
        };
    }
}

public sealed class SessionService(
    GitManagerDbContext db,
    AccessControl access,
    RepositoryContext ctx,
    WorktreeService worktrees,
    BranchService branches,
    SyncService sync,
    AuditService audit,
    NotificationPublisher notifications,
    ILogger<SessionService> logger)
{
    public async Task<SessionDto> StartAsync(StartSessionRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.AgentId)) throw new GitManagerException(ErrorCodes.Validation, "AgentId is required.");
        var repo = await access.RequireRepositoryAsync(req.RepositoryId, RepoAction.Write, ct);
        if (repo.State != RepositoryState.Active) throw new ConflictException(ErrorCodes.InvalidState, $"Repository is {repo.State}.");
        var main = await ctx.RequireMainDirectoryAsync(repo, ct);
        var baseBranch = RepoPath.Branch(req.BaseBranch ?? repo.DefaultBranch);
        var start = await ctx.Git.ResolveAsync(main, "refs/heads/" + baseBranch, ct) ?? throw new NotFoundException($"Branch '{baseBranch}'");

        var session = new AISession
        {
            RepositoryId = repo.Id, AgentId = req.AgentId, InitiatingUser = req.InitiatingUser, Title = req.Title, UserRequestRef = req.UserRequestRef,
            Isolation = req.Isolation, BaseBranch = baseBranch, StartingCommitSha = start
        };
        if (req.Isolation == SessionIsolation.Worktree)
        {
            session.Branch = $"ai/session-{session.Id.ToString("N")[..8]}";
            var wt = await worktrees.CreateInternalAsync(repo, main, session.Branch, start, session.Id, req.AgentId, newBranch: true, ct);
            session.WorktreeId = wt.Id;
            session.WorkingDirectory = wt.Path;
        }
        else
        {
            session.Branch = await ctx.Git.CurrentBranchAsync(main, ct) ?? baseBranch;
            session.WorkingDirectory = main;
        }
        db.AISessions.Add(session);
        audit.Record("session.started", repo.Id, repo.CompanyId, session.Id.ToString(), details: new { req.AgentId, session.Branch, session.WorkingDirectory, req.Isolation });
        notifications.Publish("session.started", repo.CompanyId, repo.Id, new { session.Id, session.AgentId, session.Branch });
        await db.SaveChangesAsync(ct);

        if (req.Isolation == SessionIsolation.Worktree && (repo.Settings?.AutoPush ?? true))
        {
            // Publish the session branch so its work is synchronised independently from the start.
            var item = await sync.EnqueueAsync(repo, session.WorkingDirectory, session.Branch, start, null, ct);
            await sync.PushNowAsync(item, ct);
        }
        return await ToDtoAsync(session, ct);
    }

    public async Task<SessionDto> GetAsync(Guid sessionId, CancellationToken ct)
    {
        var s = await LoadAsync(sessionId, RepoAction.Read, ct);
        return await ToDtoAsync(s, ct);
    }

    public async Task<PagedResult<SessionDto>> ListAsync(Guid? repositoryId, SessionState? state, int page, int pageSize, CancellationToken ct)
    {
        var visible = (await access.VisibleRepositoriesAsync(ct)).Select(r => r.Id);
        var q = db.AISessions.AsNoTracking().Where(s => visible.Contains(s.RepositoryId));
        if (repositoryId is not null) q = q.Where(s => s.RepositoryId == repositoryId);
        if (state is not null) q = q.Where(s => s.State == state);
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(s => s.CreatedAt).Skip((Math.Max(1, page) - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var list = new List<SessionDto>();
        foreach (var s in items) list.Add(await ToDtoAsync(s, ct));
        return new PagedResult<SessionDto>(list, total, page, pageSize);
    }

    public async Task<SessionDto> EndAsync(Guid sessionId, EndSessionRequest req, CancellationToken ct)
    {
        var s = await LoadAsync(sessionId, RepoAction.Write, ct);
        if (s.State == SessionState.Active)
        {
            s.State = SessionState.Completed;
            s.EndedAt = DateTime.UtcNow;
        }
        audit.Record("session.ended", s.RepositoryId, null, s.Id.ToString(), details: req);
        await db.SaveChangesAsync(ct);
        if (req.MergeIntoBase && s.Isolation == SessionIsolation.Worktree)
            await branches.MergeAsync(s.RepositoryId, new MergeBranchRequest { SourceBranch = s.Branch, TargetBranch = s.BaseBranch, SessionId = s.Id }, ct);
        if (req.ReleaseWorktree && s.WorktreeId is { } wt)
            await worktrees.ReleaseAsync(s.RepositoryId, wt, new ReleaseWorktreeRequest(), ct);
        return await GetAsync(sessionId, ct);
    }

    public async Task<IReadOnlyList<CommitSummaryDto>> CommitsAsync(Guid sessionId, bool includeCheckpoints, CancellationToken ct)
    {
        var s = await LoadAsync(sessionId, RepoAction.Read, ct);
        var rows = await db.AISessionCommits.AsNoTracking().Where(c => c.SessionId == sessionId && (includeCheckpoints || !c.IsCheckpointOnly))
            .OrderBy(c => c.Sequence).ToListAsync(ct);
        var shas = rows.Select(r => r.Sha).ToList();
        var meta = await db.CommitMetadata.AsNoTracking().Where(m => m.RepositoryId == s.RepositoryId && shas.Contains(m.Sha)).ToDictionaryAsync(m => m.Sha, ct);
        var cps = await db.Checkpoints.AsNoTracking().Where(c => c.RepositoryId == s.RepositoryId && shas.Contains(c.CommitSha))
            .GroupBy(c => c.CommitSha).Select(g => new { g.Key, State = g.Max(x => x.SyncState) }).ToDictionaryAsync(x => x.Key, x => x.State, ct);
        return rows.Select(r => meta.TryGetValue(r.Sha, out var m)
            ? BrowseService.ToSummary(m, cps.TryGetValue(r.Sha, out var st) ? st : null)
            : new CommitSummaryDto { Sha = r.Sha, Subject = "(not indexed)", AuthorName = "", AuthorEmail = "", SessionId = sessionId }).ToList();
    }

    public async Task<DiffDto> ChangesAsync(Guid sessionId, BrowseService browse, CancellationToken ct)
    {
        var s = await LoadAsync(sessionId, RepoAction.Read, ct);
        var end = s.EndingCommitSha ?? s.StartingCommitSha;
        return await browse.DiffAsync(s.RepositoryId, s.StartingCommitSha, end, null, includePatch: false, ct);
    }

    private async Task<AISession> LoadAsync(Guid sessionId, RepoAction action, CancellationToken ct)
    {
        var s = await db.AISessions.FirstOrDefaultAsync(x => x.Id == sessionId, ct) ?? throw new NotFoundException("AI session");
        await access.RequireRepositoryAsync(s.RepositoryId, action, ct);
        return s;
    }

    public async Task<SessionDto> ToDtoAsync(AISession s, CancellationToken ct)
    {
        var commits = await db.AISessionCommits.AsNoTracking().Where(c => c.SessionId == s.Id && !c.IsCheckpointOnly).Select(c => c.Sha).ToListAsync(ct);
        var files = await db.CommitMetadata.AsNoTracking().Where(m => m.RepositoryId == s.RepositoryId && commits.Contains(m.Sha))
            .SelectMany(m => m.Files.Select(f => f.Path)).Distinct().CountAsync(ct);
        DurabilityState? push = null;
        if (s.EndingCommitSha is not null)
            push = await db.Checkpoints.AsNoTracking().Where(c => c.RepositoryId == s.RepositoryId && c.CommitSha == s.EndingCommitSha)
                .OrderByDescending(c => c.SyncState).Select(c => (DurabilityState?)c.SyncState).FirstOrDefaultAsync(ct);
        return new SessionDto
        {
            Id = s.Id, RepositoryId = s.RepositoryId, AgentId = s.AgentId, InitiatingUser = s.InitiatingUser, Title = s.Title, UserRequestRef = s.UserRequestRef,
            State = s.State, Isolation = s.Isolation, Branch = s.Branch, BaseBranch = s.BaseBranch, WorkingDirectory = s.WorkingDirectory, WorktreeId = s.WorktreeId,
            StartingCommitSha = s.StartingCommitSha, EndingCommitSha = s.EndingCommitSha, CommitCount = commits.Count, FilesModified = files, PushStatus = push,
            TestsExecuted = s.TestsExecuted, LastTestStatus = s.LastTestStatus, RecoveryAvailable = commits.Count > 0 && s.State != SessionState.Reverted,
            CreatedAt = s.CreatedAt, EndedAt = s.EndedAt, LastActivityAt = s.LastActivityAt
        };
    }

    /// <summary>Marks idle sessions abandoned and releases worktrees past their retention (work is preserved first).</summary>
    public async Task<int> CleanupAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var count = 0;
        var candidates = await db.Worktrees.Where(w => w.State == WorktreeState.Active && w.SessionId != null).ToListAsync(ct);
        foreach (var wt in candidates)
        {
            var session = await db.AISessions.FirstOrDefaultAsync(s => s.Id == wt.SessionId, ct);
            var settings = await db.RepositorySettings.AsNoTracking().FirstOrDefaultAsync(s => s.RepositoryId == wt.RepositoryId, ct);
            var retention = TimeSpan.FromDays(settings?.WorktreeRetentionDays ?? 14);
            if (session is null) continue;
            var idle = now - session.LastActivityAt;
            if (session.State == SessionState.Active && idle > retention)
            {
                session.State = SessionState.Abandoned;
                session.EndedAt = now;
            }
            if (session.State != SessionState.Active && (session.EndedAt ?? session.LastActivityAt) < now - retention)
            {
                try
                {
                    await worktrees.ReleaseAsync(wt.RepositoryId, wt.Id, new ReleaseWorktreeRequest(), ct, systemCleanup: true);
                    count++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Could not release abandoned worktree {Path}", wt.Path);
                }
            }
        }
        await db.SaveChangesAsync(ct);
        return count;
    }
}

public sealed class BranchService(
    GitManagerDbContext db,
    AccessControl access,
    RepositoryContext ctx,
    SyncService sync,
    CommitRecorder recorder,
    CheckpointService checkpoints,
    AuditService audit,
    NotificationPublisher notifications,
    LockService locks)
{
    public async Task<IReadOnlyList<BranchDto>> ListAsync(Guid repositoryId, CancellationToken ct)
    {
        var repo = await access.RequireRepositoryAsync(repositoryId, RepoAction.Read, ct, track: false);
        var dir = await ctx.RequireMainDirectoryAsync(repo, ct);
        var local = await ctx.Git.LocalBranchesAsync(dir, ct);
        var tracked = await db.Branches.AsNoTracking().Where(b => b.RepositoryId == repositoryId).ToDictionaryAsync(b => b.Name, ct);
        var checkedOut = (await ctx.Git.WorktreeListAsync(dir, ct)).Select(w => w.Branch).Where(b => b is not null).ToHashSet();
        var protectedPatterns = repo.Settings?.ProtectedBranches ?? [repo.DefaultBranch];
        var result = new List<BranchDto>();
        foreach (var (name, sha, date) in local)
        {
            tracked.TryGetValue(name, out var t);
            var remote = t?.RemoteHeadSha;
            int ahead = 0, behind = 0;
            if (remote is not null && remote != sha && await ctx.Git.ObjectExistsAsync(dir, remote, ct))
            {
                ahead = await ctx.Git.CountAsync(dir, $"{remote}..{sha}", ct);
                behind = await ctx.Git.CountAsync(dir, $"{sha}..{remote}", ct);
            }
            result.Add(new BranchDto
            {
                Name = name, HeadSha = sha, RemoteHeadSha = remote, Ahead = remote is null ? await ctx.Git.CountAsync(dir, sha, ct) : ahead, Behind = behind,
                IsDefault = name == repo.DefaultBranch, IsProtected = protectedPatterns.Any(p => RepoPath.GlobMatch(p, name)),
                IsCheckedOut = checkedOut.Contains(name), SessionId = t?.SessionId, LastCommitAt = date
            });
        }
        return result.OrderByDescending(b => b.IsDefault).ThenByDescending(b => b.LastCommitAt).ToList();
    }

    public async Task<BranchDto> CreateAsync(Guid repositoryId, CreateBranchRequest req, CancellationToken ct)
    {
        var repo = await access.RequireRepositoryAsync(repositoryId, RepoAction.Write, ct);
        var dir = await ctx.RequireMainDirectoryAsync(repo, ct);
        var name = RepoPath.Branch(req.Name);
        if (await ctx.Git.ResolveAsync(dir, "refs/heads/" + name, ct) is not null) throw new ConflictException("branch_exists", $"Branch '{name}' already exists.");
        var start = await ctx.Git.ResolveRequiredAsync(dir, RepoPath.Revision(req.StartPoint ?? repo.DefaultBranch), ct);
        await ctx.Git.RunAsync(dir, ["branch", "--no-track", "--", name, start], null, ct);
        db.Branches.Add(new Branch { RepositoryId = repo.Id, Name = name, HeadSha = start });
        audit.Record("branch.created", repo.Id, repo.CompanyId, name, details: new { start });
        await db.SaveChangesAsync(ct);
        if (req.Push)
        {
            var item = await sync.EnqueueAsync(repo, dir, name, start, null, ct);
            await sync.PushNowAsync(item, ct);
        }
        return (await ListAsync(repositoryId, ct)).First(b => b.Name == name);
    }

    public async Task<MergeResultDto> MergeAsync(Guid repositoryId, MergeBranchRequest req, CancellationToken ct)
    {
        var repo = await access.RequireRepositoryAsync(repositoryId, RepoAction.Write, ct);
        var dir = await ctx.RequireMainDirectoryAsync(repo, ct);
        var source = RepoPath.Branch(req.SourceBranch);
        var target = RepoPath.Branch(req.TargetBranch);
        var sourceSha = await ctx.Git.ResolveAsync(dir, "refs/heads/" + source, ct) ?? throw new NotFoundException($"Branch '{source}'");
        var targetSha = await ctx.Git.ResolveAsync(dir, "refs/heads/" + target, ct) ?? throw new NotFoundException($"Branch '{target}'");
        if (req.ExpectedTargetHead is not null && !targetSha.StartsWith(RepoPath.Sha(req.ExpectedTargetHead)))
            throw new ConflictException(ErrorCodes.HeadMismatch, $"Target branch is at {targetSha}, expected {req.ExpectedTargetHead}.", new { targetSha });

        var preview = await PreviewAsync(dir, sourceSha, targetSha, ct);
        var mr = new MergeRequest
        {
            RepositoryId = repo.Id, SourceBranch = source, TargetBranch = target, SourceSha = sourceSha, TargetSha = targetSha, SessionId = req.SessionId,
            Message = req.Message, RequestedBy = access.Caller.Name, Conflicts = preview.Conflicts.ToList()
        };
        var settings = repo.Settings ?? new RepositorySettings();
        var isProtected = settings.ProtectedBranches.Any(p => RepoPath.GlobMatch(p, target));
        var approvalRequired = (settings.RequireMergeApproval || isProtected && req.SessionId is not null || isProtected && access.Caller.IsServiceAccount)
                               && !(await access.CanOverridePoliciesAsync(repo, ct));

        if (req.DryRun)
            return new MergeResultDto { State = preview.Conflicts.Count > 0 ? MergeRequestState.Conflict : approvalRequired ? MergeRequestState.AwaitingApproval : MergeRequestState.Mergeable, Conflicts = preview.Conflicts, ApprovalRequired = approvalRequired, DryRun = true, Files = preview.Files };

        if (preview.Conflicts.Count > 0)
        {
            mr.State = MergeRequestState.Conflict;
            db.MergeRequests.Add(mr);
            audit.Record("merge.conflict", repo.Id, repo.CompanyId, $"{source} -> {target}", "conflict", preview.Conflicts);
            await db.SaveChangesAsync(ct);
            return new MergeResultDto { MergeRequestId = mr.Id, State = mr.State, Conflicts = preview.Conflicts, ApprovalRequired = approvalRequired, Files = preview.Files };
        }
        if (approvalRequired)
        {
            mr.State = MergeRequestState.AwaitingApproval;
            db.MergeRequests.Add(mr);
            audit.Record("merge.requested", repo.Id, repo.CompanyId, $"{source} -> {target}", "pending");
            notifications.Publish("merge.approval-required", repo.CompanyId, repo.Id, new { mr.Id, source, target });
            await db.SaveChangesAsync(ct);
            return new MergeResultDto { MergeRequestId = mr.Id, State = mr.State, ApprovalRequired = true, Files = preview.Files };
        }
        db.MergeRequests.Add(mr);
        await db.SaveChangesAsync(ct);
        return await ExecuteAsync(repo, dir, mr, preview.Files, ct);
    }

    public async Task<MergeResultDto> ApproveAsync(Guid repositoryId, Guid mergeRequestId, bool approve, CancellationToken ct)
    {
        var repo = await access.RequireRepositoryAsync(repositoryId, RepoAction.Administer, ct);
        var mr = await db.MergeRequests.FirstOrDefaultAsync(m => m.Id == mergeRequestId && m.RepositoryId == repositoryId, ct) ?? throw new NotFoundException("Merge request");
        if (mr.State != MergeRequestState.AwaitingApproval) throw new ConflictException(ErrorCodes.InvalidState, $"Merge request is {mr.State}.");
        mr.ApprovedBy = access.Caller.Name;
        if (!approve)
        {
            mr.State = MergeRequestState.Rejected;
            mr.CompletedAt = DateTime.UtcNow;
            audit.Record("merge.rejected", repo.Id, repo.CompanyId, mr.Id.ToString());
            await db.SaveChangesAsync(ct);
            return new MergeResultDto { MergeRequestId = mr.Id, State = mr.State };
        }
        var dir = await ctx.RequireMainDirectoryAsync(repo, ct);
        var sourceSha = await ctx.Git.ResolveRequiredAsync(dir, "refs/heads/" + mr.SourceBranch, ct);
        var targetSha = await ctx.Git.ResolveRequiredAsync(dir, "refs/heads/" + mr.TargetBranch, ct);
        if (sourceSha != mr.SourceSha || targetSha != mr.TargetSha)
        {
            // Branches moved since the request: re-check conflicts against the current heads.
            mr.SourceSha = sourceSha;
            mr.TargetSha = targetSha;
        }
        var preview = await PreviewAsync(dir, sourceSha, targetSha, ct);
        if (preview.Conflicts.Count > 0)
        {
            mr.State = MergeRequestState.Conflict;
            mr.Conflicts = preview.Conflicts.ToList();
            await db.SaveChangesAsync(ct);
            return new MergeResultDto { MergeRequestId = mr.Id, State = mr.State, Conflicts = preview.Conflicts };
        }
        audit.Record("merge.approved", repo.Id, repo.CompanyId, mr.Id.ToString());
        return await ExecuteAsync(repo, dir, mr, preview.Files, ct);
    }

    public async Task<IReadOnlyList<MergeRequest>> ListMergeRequestsAsync(Guid repositoryId, CancellationToken ct)
    {
        await access.RequireRepositoryAsync(repositoryId, RepoAction.Read, ct, track: false);
        return await db.MergeRequests.AsNoTracking().Where(m => m.RepositoryId == repositoryId).OrderByDescending(m => m.CreatedAt).Take(100).ToListAsync(ct);
    }

    internal sealed record MergePreview(IReadOnlyList<string> Conflicts, IReadOnlyList<FileChangeDto> Files, string? Tree, bool UpToDate, bool FastForward);

    private async Task<MergePreview> PreviewAsync(string dir, string sourceSha, string targetSha, CancellationToken ct)
    {
        if (await ctx.Git.IsAncestorAsync(dir, sourceSha, targetSha, ct)) return new MergePreview([], [], null, true, false);
        // What the merge brings in: changes on the source since the merge base (not a two-dot tree comparison).
        var mergeBase = (await ctx.Git.RunAsync(dir, ["merge-base", targetSha, sourceSha], new() { ThrowOnError = false }, ct)).StdOut.Trim();
        var files = await ctx.Git.DiffFilesAsync(dir, mergeBase.Length > 0 ? mergeBase : targetSha, sourceSha, false, null, ct);
        if (await ctx.Git.IsAncestorAsync(dir, targetSha, sourceSha, ct)) return new MergePreview([], files, null, false, true);
        var r = await ctx.Git.RunAsync(dir, ["merge-tree", "--write-tree", "--name-only", "--no-messages", targetSha, sourceSha], new() { ThrowOnError = false }, ct);
        var lines = r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (r.ExitCode == 0) return new MergePreview([], files, lines[0].Trim(), false, false);
        if (r.ExitCode == 1) return new MergePreview(lines.Skip(1).Select(l => l.Trim()).Distinct().ToList(), files, null, false, false);
        throw new GitCommandException("merge-tree failed: " + r.StdErr.Trim(), r.ExitCode, r.StdErr);
    }

    private async Task<MergeResultDto> ExecuteAsync(Repository repo, string dir, MergeRequest mr, IReadOnlyList<FileChangeDto> files, CancellationToken ct)
    {
        await using var _ = await locks.AcquireAsync(LockService.KeyForRepository(repo.Id), TimeSpan.FromSeconds(30), ct);
        var preview = await PreviewAsync(dir, mr.SourceSha!, mr.TargetSha!, ct);
        string result;
        if (preview.UpToDate) result = mr.TargetSha!;
        else if (preview.FastForward) result = mr.SourceSha!;
        else
        {
            var message = mr.Message ?? $"merge: {mr.SourceBranch} into {mr.TargetBranch}" +
                (mr.SessionId is { } sid ? $"\n\nMerges the work of AI session {sid}.\n\n{CommitMessageBuilder.SessionTrailer}: {sid}\n" : "\n");
            var env = GitClient.IdentityEnv(ctx.ServiceIdentity, ctx.ServiceIdentity);
            result = (await ctx.Git.RunAsync(dir, ["commit-tree", preview.Tree!, "-p", mr.TargetSha!, "-p", mr.SourceSha!], new() { StandardInput = message, Environment = env }, ct)).StdOut.Trim();
        }

        if (result != mr.TargetSha)
        {
            // Update the target branch: via the worktree that has it checked out (keeps that tree consistent), else CAS ref update.
            var holder = (await ctx.Git.WorktreeListAsync(dir, ct)).FirstOrDefault(w => w.Branch == mr.TargetBranch);
            if (holder is not null)
            {
                if (await db.ChangeOperations.AnyAsync(o => o.WorkingDirectory == holder.Path && (o.State == ChangeOperationState.Active || o.State == ChangeOperationState.Committing), ct))
                    throw new ConflictException(ErrorCodes.WorkspaceBusy, "A change operation is active in the target branch's working directory; retry after it completes.");
                await using var __ = await locks.AcquireAsync(LockService.KeyForPath(holder.Path), TimeSpan.FromSeconds(30), ct);
                var ff = await ctx.Git.RunAsync(holder.Path, ["merge", "--ff-only", "--no-edit", "-q", result], new() { ThrowOnError = false }, ct);
                if (!ff.Success)
                {
                    mr.State = MergeRequestState.Failed;
                    await db.SaveChangesAsync(ct);
                    throw new ConflictException(ErrorCodes.MergeConflict, "The target working directory has local changes that would be overwritten; nothing was changed. " + GitExecutor.Redact(ff.StdErr.Trim()));
                }
            }
            else
            {
                await ctx.Git.UpdateRefAsync(dir, "refs/heads/" + mr.TargetBranch, result, mr.TargetSha, ct);
            }
        }

        var attribution = new CommitAttribution(SessionId: mr.SessionId, InitiatingUser: access.Caller.Name);
        if (result != mr.TargetSha) await recorder.RecordRangeAsync(repo, dir, mr.TargetBranch, mr.TargetSha, result, null, ct);
        var cp = await checkpoints.CreateAsync(repo, dir, mr.TargetBranch, result, CheckpointType.Merge, $"Merged {mr.SourceBranch} into {mr.TargetBranch}", attribution, ct);
        mr.State = MergeRequestState.Merged;
        mr.ResultSha = result;
        mr.CompletedAt = DateTime.UtcNow;
        if (mr.SessionId is { } sessionId && await db.AISessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct) is { } session)
        {
            session.State = SessionState.Merged;
            session.EndedAt ??= DateTime.UtcNow;
        }
        var b = await db.Branches.FirstOrDefaultAsync(x => x.RepositoryId == repo.Id && x.Name == mr.TargetBranch, ct);
        if (b is not null) b.HeadSha = result;
        audit.Record("merge.completed", repo.Id, repo.CompanyId, $"{mr.SourceBranch} -> {mr.TargetBranch}", details: new { result, fastForward = preview.FastForward });
        notifications.Publish("merge.completed", repo.CompanyId, repo.Id, new { mr.Id, result });
        await db.SaveChangesAsync(ct);

        var durability = DurabilityState.LocalCommit;
        if (repo.Settings?.AutoPush ?? true)
        {
            var item = await sync.EnqueueAsync(repo, dir, mr.TargetBranch, result, null, ct);
            durability = await sync.PushNowAsync(item, ct);
        }
        return new MergeResultDto { MergeRequestId = mr.Id, State = mr.State, ResultCommitSha = result, Files = files, Durability = durability };
    }
}
