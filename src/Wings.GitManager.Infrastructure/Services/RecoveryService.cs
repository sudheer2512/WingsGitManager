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

/// <summary>
/// Recovery engine. Every recovery is previewed first (current vs recovery commit, affected files, conflicts,
/// strategy, expected result) and executed only with approval. Methods never rewrite published history and never
/// discard uncommitted work: dirty working trees are saved as a safety checkpoint before anything is touched.
/// </summary>
public sealed class RecoveryService(
    GitManagerDbContext db,
    AccessControl access,
    RepositoryContext ctx,
    SyncService sync,
    CommitRecorder recorder,
    CheckpointService checkpoints,
    WorktreeService worktrees,
    BrowseService browse,
    JobService jobs,
    AuditService audit,
    NotificationPublisher notifications,
    LockService locks,
    ILogger<RecoveryService> logger)
{
    private sealed record Target(string Dir, string Branch, string Head);

    // ---------------------------------------------------------------- preview

    public async Task<RecoveryPlanDto> PreviewAsync(Guid repositoryId, RecoveryRequest req, CancellationToken ct)
    {
        var repo = await access.RequireRepositoryAsync(repositoryId, RepoAction.Read, ct);
        var plan = await BuildPlanAsync(repo, req, ct);
        var op = new RecoveryOperation
        {
            RepositoryId = repo.Id, Type = req.Type, Strategy = plan.Strategy, Status = RecoveryStatus.Previewed, RequestedBy = req.RequestedBy ?? access.Caller.Name,
            CallerUserId = access.Caller.UserId, SessionId = req.SessionId, Branch = plan.CurrentBranch, CurrentCommitSha = plan.CurrentCommit,
            RecoveryCommitSha = plan.RecoveryCommit, SelectedCommits = req.CommitShas?.ToList() ?? [], TargetDirectory = plan.TargetDirectory, ApprovalRequired = plan.ApprovalRequired,
            Reason = req.Reason, ExpiresAt = DateTime.UtcNow.AddMinutes(ctx.Options.RecoveryPreviewTtlMinutes)
        };
        op.SelectedPaths = req.Paths?.Select(RepoPath.Normalize).ToList() ?? [];
        if (req.Type is RecoveryType.RevertSession or RecoveryType.RevertCommits) op.SelectedCommits = plan.AffectedCommitShas.ToList();
        var dto = plan.ToDto(op);
        op.PlanJson = Json.Serialize(dto);
        db.RecoveryOperations.Add(op);
        audit.Record("recovery.preview", repo.Id, repo.CompanyId, op.Id.ToString(), details: new { req.Type, plan.Strategy, plan.RecoveryCommit, files = plan.AffectedFiles.Count, conflicts = plan.ExpectedConflicts.Count });
        await db.SaveChangesAsync(ct);
        return dto;
    }

    private sealed record Plan(
        RecoveryType Type, RecoveryStrategy Strategy, string? CurrentBranch, string? CurrentCommit, string? RecoveryCommit,
        IReadOnlyList<FileChangeDto> AffectedFiles, IReadOnlyList<string> ExpectedConflicts, IReadOnlyList<string> Uncommitted,
        bool ApprovalRequired, bool Destructive, string ExpectedResult, string Explanation, string? TargetDirectory,
        IReadOnlyList<string> AffectedCommitShas, IReadOnlyDictionary<string, string>? PathSources = null)
    {
        public RecoveryPlanDto ToDto(RecoveryOperation op) => new()
        {
            OperationId = op.Id, RepositoryId = op.RepositoryId, Type = Type, Status = op.Status, Strategy = Strategy, CurrentBranch = CurrentBranch,
            CurrentCommit = CurrentCommit, RecoveryCommit = RecoveryCommit, AffectedFiles = AffectedFiles, Additions = AffectedFiles.Sum(f => f.Additions),
            Deletions = AffectedFiles.Sum(f => f.Deletions), ExpectedConflicts = ExpectedConflicts, UncommittedChanges = Uncommitted,
            ApprovalRequired = ApprovalRequired, Destructive = Destructive, ExpectedResult = ExpectedResult, Explanation = Explanation,
            TargetDirectory = TargetDirectory, CreatedAt = op.CreatedAt, ExpiresAt = op.ExpiresAt
        };
    }

    private async Task<Plan> BuildPlanAsync(Repository repo, RecoveryRequest req, CancellationToken ct)
    {
        switch (req.Type)
        {
            case RecoveryType.RestoreFile:
            case RecoveryType.RestoreDeleted:
            {
                if (req.Paths is not { Length: > 0 }) throw new GitManagerException(ErrorCodes.Validation, "Paths are required for file restoration.");
                var t = await ResolveTargetAsync(repo, req.Branch, req.SessionId, ct);
                var paths = req.Paths.Select(RepoPath.Normalize).Distinct().ToList();
                var sources = new Dictionary<string, string>();
                string? recovery = null;
                if (req.Type == RecoveryType.RestoreFile)
                {
                    recovery = await ResolvePointAsync(repo, t.Dir, req.RecoveryPoint ?? throw new GitManagerException(ErrorCodes.Validation, "RecoveryPoint is required."), ct);
                    foreach (var p in paths)
                    {
                        if (!await ctx.Git.PathExistsAtAsync(t.Dir, recovery, p, ct))
                            throw new GitManagerException("path_not_in_recovery_point", $"'{p}' does not exist at {recovery[..12]}; use RestoreDeleted for deleted files.", 404);
                        sources[p] = recovery;
                    }
                }
                else
                {
                    foreach (var p in paths)
                    {
                        var del = await ctx.Git.RunAsync(t.Dir, ["log", "-1", "--diff-filter=D", "--format=%H", t.Head, "--", RepoPath.Literal(p)], null, ct);
                        var deleting = del.StdOut.Trim();
                        string? src = req.RecoveryPoint is not null ? await ResolvePointAsync(repo, t.Dir, req.RecoveryPoint, ct) : deleting.Length > 0 ? deleting + "^" : null;
                        if (src is not null) src = await ctx.Git.ResolveAsync(t.Dir, src, ct);
                        if (src is null || !await ctx.Git.PathExistsAtAsync(t.Dir, src, p, ct))
                            throw new GitManagerException("deleted_file_not_found", $"No deleted version of '{p}' was found in the history of {t.Branch}.", 404);
                        sources[p] = src;
                    }
                    recovery = sources.Values.Distinct().Count() == 1 ? sources.Values.First() : null;
                }
                var affected = new List<FileChangeDto>();
                foreach (var g in sources.GroupBy(kv => kv.Value))
                    affected.AddRange(await ctx.Git.DiffFilesAsync(t.Dir, t.Head, g.Key, false, g.Select(x => RepoPath.Literal(x.Key)).ToList(), ct));
                var uncommitted = (await ctx.Git.StatusAsync(t.Dir, ct)).Select(s => s.Path).ToList();
                var conflicts = uncommitted.Where(paths.Contains).Select(p => $"{p} has uncommitted local changes; they are saved in a safety checkpoint before restoring.").ToList();
                return new Plan(req.Type, RecoveryStrategy.RestoreFilesAsNewCommit, t.Branch, t.Head, recovery, affected, conflicts, uncommitted,
                    ApprovalRequired: true, Destructive: affected.Count > 0,
                    ExpectedResult: $"A new commit on '{t.Branch}' restores {paths.Count} file(s); all other files and later work are unchanged.",
                    Explanation: affected.Count == 0 ? "The selected files already match the recovery point; nothing would change." :
                        $"{affected.Count} file(s) will be restored to their content at {(recovery is null ? "their last versions before deletion" : recovery[..12])}. History is preserved, so this restoration can itself be undone.",
                    TargetDirectory: t.Dir, AffectedCommitShas: [], PathSources: sources);
            }
            case RecoveryType.RestoreProject:
            {
                var t = await ResolveTargetAsync(repo, req.Branch, req.SessionId, ct);
                var recovery = await ResolvePointAsync(repo, t.Dir, req.RecoveryPoint ?? throw new GitManagerException(ErrorCodes.Validation, "RecoveryPoint is required."), ct);
                var strategy = req.Strategy ?? (req.TargetDirectory is not null ? RecoveryStrategy.CloneToDirectory : RecoveryStrategy.RecoveryBranchWorktree);
                if (strategy is not (RecoveryStrategy.RecoveryBranchWorktree or RecoveryStrategy.RollbackCommit or RecoveryStrategy.CloneToDirectory))
                    throw new GitManagerException(ErrorCodes.Validation, $"Strategy {strategy} is not valid for project restoration.");
                var affected = await ctx.Git.DiffFilesAsync(t.Dir, t.Head, recovery, false, null, ct);
                var uncommitted = (await ctx.Git.StatusAsync(t.Dir, ct)).Select(s => s.Path).ToList();
                string? targetDir = strategy == RecoveryStrategy.CloneToDirectory ? ctx.Paths.Authorize(req.TargetDirectory!, repo.Company!) : null;
                if (targetDir is not null && !WorkspacePaths.IsEmptyOrMissing(targetDir))
                    throw new ConflictException("target_not_empty", "The target directory exists and is not empty.");
                var shortSha = recovery[..12];
                return strategy switch
                {
                    RecoveryStrategy.RollbackCommit => new Plan(req.Type, strategy, t.Branch, t.Head, recovery, affected, [], uncommitted, true, true,
                        $"A new commit on '{t.Branch}' makes every tracked file match {shortSha}. Earlier commits stay in history; untracked files are not touched.",
                        $"The whole project on '{t.Branch}' returns to {shortSha}: {affected.Count} file(s) change." + (uncommitted.Count > 0 ? $" {uncommitted.Count} uncommitted change(s) are first saved in a safety checkpoint." : ""),
                        t.Dir, []),
                    RecoveryStrategy.CloneToDirectory => new Plan(req.Type, strategy, t.Branch, t.Head, recovery, affected, [], [], false, false,
                        $"A fresh clone in {targetDir} checked out at {shortSha}; the current working directory is not modified.",
                        $"The project as of {shortSha} is restored into a new directory for inspection or reuse.", targetDir, []),
                    _ => new Plan(req.Type, RecoveryStrategy.RecoveryBranchWorktree, t.Branch, t.Head, recovery, affected, [], [], false, false,
                        $"A new branch 'wings/recovery/{shortSha[..8]}' in its own worktree at {shortSha}; '{t.Branch}' is not modified.",
                        $"The project as of {shortSha} is restored side by side with the current code ({affected.Count} file(s) differ). Merge it, or switch to it, once it is confirmed.",
                        WorkspacePaths.WorktreesRoot((await ctx.MainWorkingDirectoryAsync(repo.Id, ct))!.Path), [])
                };
            }
            case RecoveryType.RevertSession:
            case RecoveryType.RevertCommits:
            {
                List<string> commits;
                AISession? session = null;
                if (req.Type == RecoveryType.RevertSession)
                {
                    session = await db.AISessions.FirstOrDefaultAsync(s => s.Id == (req.SessionId ?? Guid.Empty) && s.RepositoryId == repo.Id, ct) ?? throw new NotFoundException("AI session");
                    commits = await db.AISessionCommits.Where(c => c.SessionId == session.Id && !c.IsCheckpointOnly).OrderByDescending(c => c.Sequence).Select(c => c.Sha).ToListAsync(ct);
                    if (req.CommitShas is { Length: > 0 } subset)
                    {
                        var wanted = subset.Select(RepoPath.Sha).ToList();
                        commits = commits.Where(c => wanted.Any(w => c.StartsWith(w))).ToList();
                    }
                }
                else
                {
                    if (req.CommitShas is not { Length: > 0 }) throw new GitManagerException(ErrorCodes.Validation, "CommitShas are required.");
                    commits = req.CommitShas.Select(RepoPath.Sha).ToList();
                }
                if (commits.Count == 0) throw new GitManagerException("nothing_to_revert", "No commits were found to revert.", 404);

                // Target: the session branch (if not merged) or the base/main branch.
                var branch = req.Branch ?? (session is { State: not SessionState.Merged } && session.Isolation == SessionIsolation.Worktree ? session.Branch : session?.BaseBranch);
                var t = await ResolveTargetAsync(repo, branch, session is { State: not SessionState.Merged } ? session.Id : null, ct);
                var resolved = new List<string>();
                var missing = new List<string>();
                foreach (var c in commits)
                {
                    var full = await ctx.Git.ResolveAsync(t.Dir, c, ct);
                    if (full is not null && await ctx.Git.IsAncestorAsync(t.Dir, full, t.Head, ct)) resolved.Add(full); else missing.Add(c);
                }
                if (resolved.Count == 0) throw new GitManagerException("nothing_to_revert", $"None of the commits are on '{t.Branch}'.", 404);
                resolved = await OrderNewestFirstAsync(t.Dir, resolved, ct);
                var paths = req.Paths?.Select(RepoPath.Normalize).ToList();
                var sim = await SimulateRevertAsync(t.Dir, t.Head, resolved, paths, ct);
                var uncommitted = (await ctx.Git.StatusAsync(t.Dir, ct)).Select(s => s.Path).ToList();
                var subjects = new List<string>();
                foreach (var c in resolved.Take(5)) subjects.Add((await ctx.Git.GetCommitAsync(t.Dir, c, ct))?.Subject ?? c);
                return new Plan(req.Type, RecoveryStrategy.RevertCommits, t.Branch, t.Head, resolved.Last() + "^", sim.Files, sim.Conflicts, uncommitted,
                    ApprovalRequired: true, Destructive: true,
                    ExpectedResult: sim.Conflicts.Count > 0
                        ? $"Reverting would conflict in {sim.Conflicts.Count} file(s); nothing will be changed unless the conflicts are resolved first."
                        : $"One new revert commit on '{t.Branch}' reverses {resolved.Count} commit(s){(paths is null ? "" : $" for {paths.Count} selected path(s)")}; later, unrelated changes are kept.",
                    Explanation: $"Reverts: {string.Join("; ", subjects)}{(resolved.Count > 5 ? $" and {resolved.Count - 5} more" : "")}." +
                                 (missing.Count > 0 ? $" Not on this branch (skipped): {string.Join(", ", missing.Select(m => m[..Math.Min(12, m.Length)]))}." : ""),
                    TargetDirectory: t.Dir, AffectedCommitShas: resolved);
            }
            case RecoveryType.CloneProject:
            {
                var main = await ctx.MainWorkingDirectoryAsync(repo.Id, ct);
                var mainExists = main is not null && await ctx.Git.IsRepositoryAsync(main.Path, ct);
                var targetDir = req.TargetDirectory is not null ? ctx.Paths.Authorize(req.TargetDirectory, repo.Company!)
                    : main is not null && !mainExists && WorkspacePaths.IsEmptyOrMissing(main.Path) ? main.Path
                    : ctx.Paths.Authorize(Path.Combine(ctx.Paths.RecoveryRoot(main?.Path ?? ctx.Paths.DefaultMainDirectory(repo.Company!, repo.Project!)), $"restored-{DateTime.UtcNow:yyyyMMddHHmmss}"), repo.Company!);
                if (!WorkspacePaths.IsEmptyOrMissing(targetDir)) throw new ConflictException("target_not_empty", "The target directory exists and is not empty.");
                var branch = RepoPath.Branch(req.Branch ?? repo.DefaultBranch);
                var mirror = await browse.ReadSourceAsync(repo.Id, ct);
                var point = req.RecoveryPoint is null ? await ctx.Git.ResolveAsync(mirror.Dir, "refs/heads/" + branch, ct) : await ResolvePointAsync(repo, mirror.Dir, req.RecoveryPoint, ct);
                if (point is null) throw new NotFoundException($"Branch '{branch}' on the central repository");
                return new Plan(req.Type, RecoveryStrategy.CloneToDirectory, branch, mainExists ? await ctx.Git.HeadAsync(main!.Path, ct) : null, point, [], [], [],
                    ApprovalRequired: false, Destructive: false,
                    ExpectedResult: $"The central repository is cloned into {targetDir} and checked out at {point[..12]} on '{branch}'.",
                    Explanation: mainExists
                        ? "The local project still exists; the clone goes to a separate directory and nothing existing is modified."
                        : "The local working directory is missing. It is recreated from the central Git server, which holds the complete history.",
                    TargetDirectory: targetDir, AffectedCommitShas: []);
            }
            default: throw new GitManagerException(ErrorCodes.Validation, $"Unsupported recovery type {req.Type}.");
        }
    }

    // ---------------------------------------------------------------- execute

    public async Task<RecoveryPlanDto> ExecuteAsync(Guid repositoryId, RecoveryRequest req, RecoveryType expectedType, CancellationToken ct)
    {
        if (req.DryRun) return await PreviewAsync(repositoryId, req with { Type = expectedType }, ct);
        var repo = await access.RequireRepositoryAsync(repositoryId, RepoAction.Recover, ct);
        RecoveryOperation op;
        if (req.PreviewOperationId is { } pid)
        {
            op = await db.RecoveryOperations.FirstOrDefaultAsync(o => o.Id == pid && o.RepositoryId == repositoryId, ct) ?? throw new NotFoundException("Recovery preview");
            if (op.Type != expectedType) throw new GitManagerException(ErrorCodes.Validation, $"Preview {pid} is a {op.Type}, not {expectedType}.");
            if (op.Status is RecoveryStatus.Succeeded or RecoveryStatus.Executing) return await WithDurabilityAsync(Dto(op), ct);
            if (op.Status != RecoveryStatus.Previewed) throw new ConflictException(ErrorCodes.InvalidState, $"Recovery operation is {op.Status}; create a new preview.");
            if (op.ExpiresAt < DateTime.UtcNow) { op.Status = RecoveryStatus.Expired; await db.SaveChangesAsync(ct); throw new ConflictException(ErrorCodes.PreviewExpired, "The preview expired; preview again."); }
        }
        else
        {
            var preview = await PreviewAsync(repositoryId, req with { Type = expectedType }, ct);
            op = await db.RecoveryOperations.FirstAsync(o => o.Id == preview.OperationId, ct);
        }
        var plan = Json.Deserialize<RecoveryPlanDto>(op.PlanJson)!;
        if (op.ApprovalRequired && !req.Approved)
            throw new PolicyViolationException(ErrorCodes.ApprovalRequired, "This recovery changes existing code. Show the preview to the user and resend with approved=true.", plan);
        if (plan.ExpectedConflicts.Count > 0 && op.Strategy == RecoveryStrategy.RevertCommits)
            throw new ConflictException(ErrorCodes.MergeConflict, "The revert would conflict; nothing was changed.", plan);

        op.ApprovedBy = req.Approved ? access.Caller.Name : null;
        op.ApprovedAt = req.Approved ? DateTime.UtcNow : null;
        op.Status = RecoveryStatus.Executing;
        await db.SaveChangesAsync(ct);
        audit.Record("recovery.execute", repo.Id, repo.CompanyId, op.Id.ToString(), details: new { op.Type, op.Strategy, approvedBy = op.ApprovedBy });

        if (op.Type == RecoveryType.CloneProject || op.Strategy == RecoveryStrategy.CloneToDirectory)
        {
            var job = await jobs.EnqueueAsync(JobTypes.RecoveryClone, repo.Id, new RecoveryJobPayload(op.Id, req.RecoveryPoint is null), access.Caller.Name, ct);
            op.JobId = job.Id;
            await db.SaveChangesAsync(ct);
            return await WithDurabilityAsync(Dto(op), ct);
        }

        try
        {
            await ExecuteInPlaceAsync(repo, op, plan, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Recovery {Op} failed", op.Id);
            op.Status = ex is ConflictException ? RecoveryStatus.Conflict : RecoveryStatus.Failed;
            op.Error = ex.Message;
            op.CompletedAt = DateTime.UtcNow;
            audit.Record("recovery.failed", repo.Id, repo.CompanyId, op.Id.ToString(), "failure", new { ex.Message });
            db.MonitoringAlerts.Add(new MonitoringAlert
            {
                RepositoryId = repo.Id, CompanyId = repo.CompanyId, Key = $"recovery-failed:{op.Id}", Category = "recovery-failure",
                Severity = AlertSeverity.Warning, Message = $"{op.Type} recovery failed: {ex.Message}"
            });
            await db.SaveChangesAsync(CancellationToken.None);
            if (ex is GitManagerException) throw;
            throw new GitManagerException("recovery_failed", ex.Message, 500);
        }
        return await WithDurabilityAsync(Dto(op), ct);
    }

    private async Task ExecuteInPlaceAsync(Repository repo, RecoveryOperation op, RecoveryPlanDto plan, CancellationToken ct)
    {
        if (op.Strategy == RecoveryStrategy.RecoveryBranchWorktree)
        {
            var main = await ctx.RequireMainDirectoryAsync(repo, ct);
            var branch = $"wings/recovery/{op.RecoveryCommitSha![..8]}-{op.Id.ToString("N")[..6]}";
            var wt = await worktrees.CreateInternalAsync(repo, main, branch, op.RecoveryCommitSha!, null, null, newBranch: true, ct);
            await checkpoints.CreateAsync(repo, wt.Path, branch, op.RecoveryCommitSha!, CheckpointType.Recovery, $"Recovery branch for {op.RecoveryCommitSha[..12]}", new CommitAttribution(InitiatingUser: op.RequestedBy), ct);
            op.ResultBranch = branch;
            op.ResultCommitSha = op.RecoveryCommitSha;
            op.TargetDirectory = wt.Path;
            await FinishAsync(repo, op, wt.Path, branch, op.RecoveryCommitSha!, ct);
            return;
        }

        var target = await ResolveTargetAsync(repo, op.Branch, op.Type == RecoveryType.RevertSession ? await SessionWorktreeIdAsync(op, ct) : null, ct, op.TargetDirectory);
        await using var gitLock = await locks.AcquireAsync(LockService.KeyForPath(target.Dir), TimeSpan.FromSeconds(30), ct);
        if (await db.ChangeOperations.AnyAsync(o => o.WorkingDirectory == target.Dir && (o.State == ChangeOperationState.Active || o.State == ChangeOperationState.Committing), ct))
            throw new ConflictException(ErrorCodes.WorkspaceBusy, "A change operation is active in this working directory; recovery waits until it completes.");
        var head = await ctx.Git.HeadAsync(target.Dir, ct);
        if (head != op.CurrentCommitSha)
            throw new ConflictException(ErrorCodes.PreviewExpired, $"The branch moved since the preview (now {head?[..12]}); preview again so the impact shown is accurate.");

        // Safety checkpoint for uncommitted work: never discard it.
        if (!await ctx.Git.IsCleanAsync(target.Dir, ct))
        {
            var settings = repo.Settings ?? new RepositorySettings();
            var status = await ctx.Git.StatusAsync(target.Dir, ct);
            foreach (var chunk in status.Where(s => RepositoryContext.IsPermittedPath(s.Path, settings)).Select(s => s.Path).Chunk(200))
                await ctx.Git.RunAsync(target.Dir, ["add", "-A", "--", .. chunk.Select(RepoPath.Literal)], null, ct);
            var safety = await ctx.Git.CommitAsync(target.Dir, CommitMessageBuilder.Checkpoint($"before recovery {op.Id}", null, op.SessionId, null, op.RequestedBy), ctx.ServiceIdentity, ctx.ServiceIdentity, true, ct);
            await recorder.RecordAsync(repo, target.Dir, target.Branch, safety, new CommitAttribution(InitiatingUser: op.RequestedBy, CommitType: "checkpoint"), ct);
            var cp = await checkpoints.CreateAsync(repo, target.Dir, target.Branch, safety, CheckpointType.PreChange, "Uncommitted work saved before recovery", new CommitAttribution(InitiatingUser: op.RequestedBy), ct);
            op.SafetyCheckpointId = cp.Id;
            await db.SaveChangesAsync(ct);
            if (!await ctx.Git.IsCleanAsync(target.Dir, ct))
                throw new ConflictException("uncommitted_excluded_files", "Some uncommitted files are excluded from version control (secrets/ignored patterns) and would be affected; move them aside first.");
        }

        var trailer = $"\n{CommitMessageBuilder.RecoveryTrailer}: {op.Id}\n{CommitMessageBuilder.UserTrailer}: {op.RequestedBy}\n";
        string message;
        switch (op.Strategy)
        {
            case RecoveryStrategy.RestoreFilesAsNewCommit:
            {
                var sources = await SourcesAsync(target.Dir, op, ct);
                foreach (var g in sources.GroupBy(kv => kv.Value))
                    await ctx.Git.RunAsync(target.Dir, ["checkout", g.Key, "--", .. g.Select(x => RepoPath.Literal(x.Key))], null, ct);
                var names = string.Join(", ", sources.Keys.Take(3)) + (sources.Count > 3 ? $" and {sources.Count - 3} more" : "");
                message = $"revert: restore {names}\n\nRestored from {string.Join(", ", sources.Values.Distinct().Select(v => v[..Math.Min(12, v.Length)]))} by Wings Git Manager recovery.\n" + trailer;
                break;
            }
            case RecoveryStrategy.RollbackCommit:
                await ctx.Git.RunAsync(target.Dir, ["read-tree", "-u", "--reset", op.RecoveryCommitSha!], null, ct);
                message = $"revert: restore project to {op.RecoveryCommitSha![..12]}\n\nAll tracked files now match {op.RecoveryCommitSha}. Previous history is preserved.\n" + trailer;
                break;
            case RecoveryStrategy.RevertCommits:
            {
                var commits = op.SelectedCommits;
                var paths = op.SelectedPaths.Count > 0 && op.Type is RecoveryType.RevertSession or RecoveryType.RevertCommits ? op.SelectedPaths : null;
                var conflicts = await ApplyRevertAsync(target.Dir, commits, paths, ct);
                if (conflicts.Count > 0)
                {
                    op.Status = RecoveryStatus.Conflict;
                    op.Error = "Revert conflicts in: " + string.Join(", ", conflicts);
                    op.CompletedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                    throw new ConflictException(ErrorCodes.MergeConflict, op.Error, new { conflicts });
                }
                var label = op.Type == RecoveryType.RevertSession ? $"reverse changes from AI session {op.SessionId}" : $"reverse {commits.Count} commit(s)";
                message = $"revert: {label}\n\nReverted commits:\n{string.Join("\n", commits.Select(c => "  " + c))}\n" +
                          (paths is null ? "" : $"\nOnly these paths were reverted: {string.Join(", ", paths)}\n") +
                          (op.SessionId is { } sid ? $"{CommitMessageBuilder.SessionTrailer}: {sid}\n" : "") + trailer;
                break;
            }
            default: throw new GitManagerException(ErrorCodes.Validation, $"Strategy {op.Strategy} cannot run in place.");
        }

        var staged = await ctx.Git.RunAsync(target.Dir, ["diff", "--cached", "--quiet"], new() { ThrowOnError = false }, ct);
        string result;
        if (staged.ExitCode == 0)
        {
            result = head!;
            op.Error = "Nothing changed: the code already matched the recovery point.";
        }
        else
        {
            result = await ctx.Git.CommitAsync(target.Dir, message, ctx.ServiceIdentity, ctx.ServiceIdentity, false, ct);
            await recorder.RecordAsync(repo, target.Dir, target.Branch, result, new CommitAttribution(SessionId: op.SessionId, InitiatingUser: op.RequestedBy, CommitType: "revert"), ct);
        }
        await checkpoints.CreateAsync(repo, target.Dir, target.Branch, result, CheckpointType.Recovery, $"After {op.Type} recovery", new CommitAttribution(InitiatingUser: op.RequestedBy), ct);
        if (op.Type == RecoveryType.RevertSession && op.SessionId is { } sessionId && await db.AISessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct) is { } session)
            session.State = SessionState.Reverted;
        op.ResultCommitSha = result;
        op.ResultBranch = target.Branch;
        await FinishAsync(repo, op, target.Dir, target.Branch, result, ct);
    }

    private async Task FinishAsync(Repository repo, RecoveryOperation op, string dir, string branch, string sha, CancellationToken ct)
    {
        op.Status = RecoveryStatus.Succeeded;
        op.CompletedAt = DateTime.UtcNow;
        audit.Record("recovery.succeeded", repo.Id, repo.CompanyId, op.Id.ToString(), details: new { op.Type, op.ResultCommitSha, op.ResultBranch });
        notifications.Publish("recovery.succeeded", repo.CompanyId, repo.Id, new { op.Id, op.Type, op.ResultCommitSha });
        await db.SaveChangesAsync(ct);
        if (repo.Settings?.AutoPush ?? true)
        {
            var item = await sync.EnqueueAsync(repo, dir, branch, sha, null, ct);
            await sync.PushNowAsync(item, ct);
        }
    }

    /// <summary>Background job: clone the central repository into a new directory (deleted-project recovery).</summary>
    public async Task<string> RunCloneJobAsync(RecoveryJobPayload payload, CancellationToken ct)
    {
        var op = await db.RecoveryOperations.FirstAsync(o => o.Id == payload.OperationId, ct);
        var repo = await db.Repositories.Include(r => r.Remotes).Include(r => r.Company).Include(r => r.Project).Include(r => r.Settings).FirstAsync(r => r.Id == op.RepositoryId, ct);
        var target = ctx.Paths.Authorize(op.TargetDirectory!, repo.Company!);
        try
        {
            if (!WorkspacePaths.IsEmptyOrMissing(target)) throw new ConflictException("target_not_empty", "The target directory is no longer empty.");
            var remote = ctx.PrimaryRemote(repo);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await ctx.Git.RunAsync(Path.GetDirectoryName(target)!, ["clone", "--no-checkout", "--", remote.Url, target],
                new() { Credential = ctx.Credential(repo), Network = true, AllowFileProtocol = ctx.AllowFile(repo) }, ct);
            var branch = op.Branch ?? repo.DefaultBranch;
            var sha = op.RecoveryCommitSha!;
            var remoteHead = await ctx.Git.ResolveAsync(target, "refs/remotes/origin/" + branch, ct);
            string checkedOutBranch;
            if (remoteHead == sha)
            {
                await ctx.Git.RunAsync(target, ["checkout", "-q", "-B", branch, "refs/remotes/origin/" + branch], null, ct);
                checkedOutBranch = branch;
            }
            else
            {
                if (!await ctx.Git.ObjectExistsAsync(target, sha, ct)) throw new GitManagerException("commit_not_on_remote", $"Commit {sha} is not on the central repository.", 404);
                checkedOutBranch = $"wings/recovery/{sha[..8]}-{op.Id.ToString("N")[..6]}";
                await ctx.Git.RunAsync(target, ["checkout", "-q", "-b", checkedOutBranch, sha], null, ct);
            }
            var head = await ctx.Git.HeadAsync(target, ct);
            if (head != sha) throw new GitManagerException("verification_failed", $"Recovered HEAD {head} does not match the requested commit {sha}.", 500);
            await ctx.Git.RunAsync(target, ["fsck", "--connectivity-only", "--no-progress"], null, ct);
            if (!await ctx.Git.IsCleanAsync(target, ct)) throw new GitManagerException("verification_failed", "The recovered working tree is not clean.", 500);

            var main = await ctx.MainWorkingDirectoryAsync(repo.Id, ct);
            var mainMissing = main is null || !await ctx.Git.IsRepositoryAsync(main.Path, ct);
            if (mainMissing)
            {
                if (main is not null) main.IsActive = false;
                db.WorkingDirectories.Add(new WorkingDirectory { RepositoryId = repo.Id, Path = target, Kind = WorkingDirectoryKind.Main, Branch = checkedOutBranch });
                await db.Worktrees.Where(w => w.RepositoryId == repo.Id && w.State == WorktreeState.Active).ExecuteUpdateAsync(s => s.SetProperty(w => w.State, WorktreeState.Missing), ct);
            }
            else
            {
                db.WorkingDirectories.Add(new WorkingDirectory { RepositoryId = repo.Id, Path = target, Kind = WorkingDirectoryKind.Clone, Branch = checkedOutBranch });
            }
            op.ResultCommitSha = head;
            op.ResultBranch = checkedOutBranch;
            op.Status = RecoveryStatus.Succeeded;
            op.CompletedAt = DateTime.UtcNow;
            audit.Record("recovery.clone.succeeded", repo.Id, repo.CompanyId, target, details: new { head, checkedOutBranch, registeredAsMain = mainMissing });
            notifications.Publish("recovery.succeeded", repo.CompanyId, repo.Id, new { op.Id, op.Type, head });
            await db.SaveChangesAsync(ct);
            return Json.Serialize(new { target, head, branch = checkedOutBranch, registeredAsMain = mainMissing });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            op.Status = RecoveryStatus.Failed;
            op.Error = ex.Message;
            op.CompletedAt = DateTime.UtcNow;
            audit.Record("recovery.clone.failed", repo.Id, repo.CompanyId, target, "failure", new { ex.Message });
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<RecoveryPlanDto> GetAsync(Guid repositoryId, Guid operationId, CancellationToken ct)
    {
        await access.RequireRepositoryAsync(repositoryId, RepoAction.Read, ct, track: false);
        var op = await db.RecoveryOperations.AsNoTracking().FirstOrDefaultAsync(o => o.Id == operationId && o.RepositoryId == repositoryId, ct) ?? throw new NotFoundException("Recovery operation");
        return await WithDurabilityAsync(Dto(op), ct);
    }

    public async Task<IReadOnlyList<RecoveryPlanDto>> ListAsync(Guid repositoryId, CancellationToken ct)
    {
        await access.RequireRepositoryAsync(repositoryId, RepoAction.Read, ct, track: false);
        var ops = await db.RecoveryOperations.AsNoTracking().Where(o => o.RepositoryId == repositoryId).OrderByDescending(o => o.CreatedAt).Take(100).ToListAsync(ct);
        var list = new List<RecoveryPlanDto>();
        foreach (var o in ops) list.Add(await WithDurabilityAsync(Dto(o), ct));
        return list;
    }

    /// <summary>Adds the verified durability of the resulting commit (best known checkpoint state).</summary>
    private async Task<RecoveryPlanDto> WithDurabilityAsync(RecoveryPlanDto dto, CancellationToken ct)
    {
        if (dto.ResultCommitSha is null) return dto;
        var states = await db.Checkpoints.AsNoTracking().Where(c => c.RepositoryId == dto.RepositoryId && c.CommitSha == dto.ResultCommitSha)
            .Select(c => new { c.SyncState, c.BackupState }).ToListAsync(ct);
        if (states.Count == 0) return dto;
        var best = states.Any(x => x.BackupState == DurabilityState.IndependentlyBackedUp && x.SyncState == DurabilityState.RemoteSynchronized)
            ? DurabilityState.IndependentlyBackedUp : states.Max(x => x.SyncState);
        return dto with { ResultDurability = best };
    }

    private static RecoveryPlanDto Dto(RecoveryOperation op)
    {
        var plan = Json.Deserialize<RecoveryPlanDto>(op.PlanJson) ?? new RecoveryPlanDto { ExpectedResult = "", Explanation = "" };
        return plan with
        {
            OperationId = op.Id, Status = op.Status, ResultCommitSha = op.ResultCommitSha, ResultBranch = op.ResultBranch, Error = op.Error,
            CompletedAt = op.CompletedAt, SafetyCheckpointId = op.SafetyCheckpointId, JobId = op.JobId, TargetDirectory = op.TargetDirectory ?? plan.TargetDirectory
        };
    }

    // ---------------------------------------------------------------- helpers

    private async Task<Guid?> SessionWorktreeIdAsync(RecoveryOperation op, CancellationToken ct) =>
        op.SessionId is { } sid && await db.AISessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sid, ct) is { State: not SessionState.Merged } s ? s.Id : null;

    private async Task<Target> ResolveTargetAsync(Repository repo, string? branch, Guid? sessionId, CancellationToken ct, string? knownDir = null)
    {
        if (knownDir is not null && await ctx.Git.IsRepositoryAsync(knownDir, ct))
        {
            var b = await ctx.Git.CurrentBranchAsync(knownDir, ct) ?? throw new ConflictException(ErrorCodes.InvalidState, "Detached HEAD.");
            return new Target(knownDir, b, (await ctx.Git.HeadAsync(knownDir, ct))!);
        }
        string dir;
        if (sessionId is { } sid && await db.AISessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sid && s.RepositoryId == repo.Id, ct) is { } session
            && session.Isolation == SessionIsolation.Worktree && await ctx.Git.IsRepositoryAsync(session.WorkingDirectory, ct) && (branch is null || branch == session.Branch))
        {
            dir = session.WorkingDirectory;
        }
        else
        {
            dir = await ctx.RequireMainDirectoryAsync(repo, ct);
            if (branch is not null)
            {
                var holder = (await ctx.Git.WorktreeListAsync(dir, ct)).FirstOrDefault(w => w.Branch == branch);
                if (holder is null) throw new ConflictException("branch_not_checked_out", $"Branch '{branch}' is not checked out in any working directory; restore into a new worktree instead.");
                dir = holder.Path;
            }
        }
        dir = ctx.Paths.Revalidate(dir);
        var current = await ctx.Git.CurrentBranchAsync(dir, ct) ?? throw new ConflictException(ErrorCodes.InvalidState, "Working directory is in detached HEAD state.");
        var head = await ctx.Git.HeadAsync(dir, ct) ?? throw new ConflictException(ErrorCodes.InvalidState, "Repository has no commits.");
        return new Target(dir, current, head);
    }

    private async Task<string> ResolvePointAsync(Repository repo, string dir, string point, CancellationToken ct)
    {
        if (point.StartsWith("checkpoint:", StringComparison.OrdinalIgnoreCase))
        {
            var id = Guid.Parse(point["checkpoint:".Length..]);
            var cp = await db.Checkpoints.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id && c.RepositoryId == repo.Id, ct) ?? throw new NotFoundException("Checkpoint");
            return await ctx.Git.ResolveAsync(dir, cp.CommitSha, ct) ?? throw new NotFoundException($"Checkpoint commit {cp.CommitSha} locally");
        }
        var parentSuffix = point.EndsWith('^') ? "^" : "";
        var rev = RepoPath.Revision(point.TrimEnd('^'));
        return await ctx.Git.ResolveAsync(dir, rev + parentSuffix, ct) ?? throw new NotFoundException($"Recovery point '{point}'");
    }

    private async Task<Dictionary<string, string>> SourcesAsync(string dir, RecoveryOperation op, CancellationToken ct)
    {
        var plan = Json.Deserialize<RecoveryPlanDto>(op.PlanJson)!;
        var result = new Dictionary<string, string>();
        foreach (var p in op.SelectedPaths)
        {
            if (op.Type == RecoveryType.RestoreFile) { result[p] = op.RecoveryCommitSha!; continue; }
            var del = await ctx.Git.RunAsync(dir, ["log", "-1", "--diff-filter=D", "--format=%H", plan.CurrentCommit!, "--", RepoPath.Literal(p)], null, ct);
            var src = op.RecoveryCommitSha ?? (await ctx.Git.ResolveAsync(dir, del.StdOut.Trim() + "^", ct))!;
            result[p] = src;
        }
        return result;
    }

    private async Task<List<string>> OrderNewestFirstAsync(string dir, List<string> commits, CancellationToken ct)
    {
        var order = new List<(string Sha, int Depth)>();
        foreach (var c in commits) order.Add((c, await ctx.Git.CountAsync(dir, c, ct)));
        return order.OrderByDescending(o => o.Depth).Select(o => o.Sha).Distinct().ToList();
    }

    private sealed record Simulation(IReadOnlyList<FileChangeDto> Files, IReadOnlyList<string> Conflicts);

    /// <summary>Runs the revert in a disposable detached worktree to report the exact files and conflicts.</summary>
    private async Task<Simulation> SimulateRevertAsync(string dir, string head, IReadOnlyList<string> commits, IReadOnlyList<string>? paths, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetFullPath(ctx.Options.DataDirectory), "tmp-worktrees", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path.GetDirectoryName(tmp)!);
        await ctx.Git.RunAsync(dir, ["worktree", "add", "--detach", "--", tmp, head], null, ct);
        try
        {
            var conflicts = await ApplyRevertAsync(tmp, commits, paths, ct, simulation: true);
            var files = conflicts.Count > 0 ? [] : await ctx.Git.DiffFilesAsync(tmp, head, null, true, null, ct);
            return new Simulation(files, conflicts);
        }
        finally
        {
            await ctx.Git.RunAsync(dir, ["worktree", "remove", "--force", "--", tmp], new() { ThrowOnError = false }, ct);
            await ctx.Git.RunAsync(dir, ["worktree", "prune"], new() { ThrowOnError = false }, ct);
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { /* best effort */ }
        }
    }

    /// <summary>Applies reverts into the index/working tree without committing; on conflict restores the clean state.</summary>
    private async Task<IReadOnlyList<string>> ApplyRevertAsync(string dir, IReadOnlyList<string> commitsNewestFirst, IReadOnlyList<string>? paths, CancellationToken ct, bool simulation = false)
    {
        var hasMerge = false;
        foreach (var c in commitsNewestFirst)
            if ((await ctx.Git.GetCommitAsync(dir, c, ct))?.Parents.Count > 1) hasMerge = true;
        if (paths is null && !hasMerge)
        {
            var r = await ctx.Git.RunAsync(dir, ["revert", "--no-edit", "--no-commit", .. commitsNewestFirst], new() { ThrowOnError = false }, ct);
            if (r.Success) return [];
            var conflicted = await ConflictedAsync(dir, ct);
            await ctx.Git.RunAsync(dir, ["revert", "--abort"], new() { ThrowOnError = false }, ct);
            if (conflicted.Count == 0) throw new GitCommandException("git revert failed: " + r.StdErr.Trim(), r.ExitCode, r.StdErr);
            return conflicted;
        }
        // Selective (path-limited) reverts and merge commits: apply the reverse patch against the first parent.
        foreach (var c in commitsNewestFirst)
        {
            var patch = await ctx.Git.RunAsync(dir, ["diff", "--binary", "--no-ext-diff", "--no-textconv", c, c + "^1", "--", .. (paths ?? []).Select(RepoPath.Literal)], null, ct);
            if (string.IsNullOrWhiteSpace(patch.StdOut)) continue;
            var apply = await ctx.Git.RunAsync(dir, ["apply", "--3way", "--index", "--whitespace=nowarn", "-"], new() { StandardInput = patch.StdOut, ThrowOnError = false }, ct);
            if (!apply.Success)
            {
                var conflicted = await ConflictedAsync(dir, ct);
                // The tree was clean before (safety checkpoint / fresh worktree), so resetting only drops this attempt.
                await ctx.Git.RunAsync(dir, ["reset", "-q", "--hard", "HEAD"], null, ct);
                return conflicted.Count > 0 ? conflicted : (paths ?? [c]).ToList();
            }
        }
        return [];
    }

    private async Task<IReadOnlyList<string>> ConflictedAsync(string dir, CancellationToken ct)
    {
        var r = await ctx.Git.RunAsync(dir, ["diff", "--name-only", "--diff-filter=U"], new() { ThrowOnError = false }, ct);
        return r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
    }
}

public sealed record RecoveryJobPayload(Guid OperationId, bool UseBranchHead);
