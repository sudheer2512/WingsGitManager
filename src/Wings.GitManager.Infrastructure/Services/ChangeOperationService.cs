using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wings.GitManager.Contracts;
using Wings.GitManager.Core;
using Wings.GitManager.Core.Commits;
using Wings.GitManager.Core.Entities;
using Wings.GitManager.Core.Policies;
using Wings.GitManager.Core.Safety;
using Wings.GitManager.Infrastructure.Data;

namespace Wings.GitManager.Infrastructure.Services;

/// <summary>
/// The explicit change-operation lifecycle used by Wings Code: begin (policy + pre-change checkpoint +
/// workspace lease) → agent edits files → complete (stage only the operation's files, scan, commit, push, verify).
/// </summary>
public sealed class ChangeOperationService(
    GitManagerDbContext db,
    AccessControl access,
    RepositoryContext ctx,
    SyncService sync,
    CommitRecorder recorder,
    CheckpointService checkpoints,
    AuditService audit,
    NotificationPublisher notifications,
    LockService locks,
    ILogger<ChangeOperationService> logger)
{
    public async Task<BeginChangeResponse> BeginAsync(Guid repositoryId, BeginChangeRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.AgentId) || req.AgentId.Length > 200) throw new GitManagerException(ErrorCodes.Validation, "AgentId is required.");
        if (string.IsNullOrWhiteSpace(req.Description)) throw new GitManagerException(ErrorCodes.Validation, "Description is required.");
        var repo = await access.RequireRepositoryAsync(repositoryId, RepoAction.Write, ct);
        if (repo.State != RepositoryState.Active) throw new ConflictException(ErrorCodes.InvalidState, $"Repository is {repo.State}; changes are not allowed.");
        var settings = repo.Settings ?? new RepositorySettings();
        var scope = req.ScopePaths?.Select(RepoPath.Normalize).ToList() ?? [];

        // Resolve the workspace: the session's isolated worktree or the main working directory.
        AISession? session = null;
        string workDir;
        if (req.SessionId is { } sid)
        {
            session = await db.AISessions.FirstOrDefaultAsync(s => s.Id == sid && s.RepositoryId == repositoryId, ct) ?? throw new NotFoundException("AI session");
            if (session.State != SessionState.Active) throw new ConflictException(ErrorCodes.InvalidState, $"AI session is {session.State}.");
            if (!string.Equals(session.AgentId, req.AgentId, StringComparison.Ordinal))
                throw new ForbiddenException("The workspace of this AI session belongs to another agent.");
            workDir = ctx.Paths.Revalidate(session.WorkingDirectory);
            if (!await ctx.Git.IsRepositoryAsync(workDir, ct))
                throw new GitManagerException("working_directory_missing", "The session worktree no longer exists.", 409);
        }
        else
        {
            workDir = await ctx.RequireMainDirectoryAsync(repo, ct);
        }

        var policy = await ctx.HighRiskPolicyAsync(repo, ct);
        var risk = HighRiskPolicyEngine.EvaluateIntent(req.Intent, policy);

        await using var gitLock = await locks.AcquireAsync(LockService.KeyForPath(workDir), TimeSpan.FromSeconds(30), ct);

        // One active operation per working directory; expired leases become Interrupted (state preserved).
        var now = DateTime.UtcNow;
        var active = await db.ChangeOperations.Where(o => o.WorkingDirectory == workDir && (o.State == ChangeOperationState.Active || o.State == ChangeOperationState.Committing)).ToListAsync(ct);
        foreach (var stale in active.Where(o => o.LeaseExpiresAt < now))
        {
            stale.State = ChangeOperationState.Interrupted;
            stale.FailureReason = "Lease expired before completion; changes were left in place and are checkpointed by the next operation.";
        }
        var busy = active.FirstOrDefault(o => o.LeaseExpiresAt >= now);
        if (busy is not null)
            throw new ConflictException(ErrorCodes.WorkspaceBusy, $"Workspace is in use by change operation {busy.Id} (agent {busy.AgentId}) until {busy.LeaseExpiresAt:O}.", new { operationId = busy.Id, busy.AgentId });

        var branch = await ctx.Git.CurrentBranchAsync(workDir, ct) ?? throw new ConflictException(ErrorCodes.InvalidState, "Working directory is in detached HEAD state.");
        var head = await ctx.Git.HeadAsync(workDir, ct) ?? throw new ConflictException(ErrorCodes.InvalidState, "Repository has no commits.");
        if (req.ExpectedHead is not null && !head.StartsWith(RepoPath.Sha(req.ExpectedHead), StringComparison.OrdinalIgnoreCase))
            throw new ConflictException(ErrorCodes.HeadMismatch, $"HEAD is {head}, expected {req.ExpectedHead}.", new { head });

        var op = new ChangeOperation
        {
            RepositoryId = repo.Id, SessionId = session?.Id, AgentId = req.AgentId, InitiatingUser = req.InitiatingUser ?? session?.InitiatingUser,
            CallerUserId = access.Caller.UserId, Description = req.Description.Trim(), UserRequestRef = req.UserRequestRef,
            WorkingDirectory = workDir, Branch = branch, StartCommitSha = head, RiskLevel = risk.Level, RiskReasons = risk.Reasons.ToList(),
            ScopePaths = scope, IntentJson = Json.Serialize(req.Intent),
            LeaseExpiresAt = now.AddMinutes(Math.Clamp(req.LeaseMinutes ?? ctx.Options.DefaultLeaseMinutes, 1, 24 * 60))
        };
        var attribution = new CommitAttribution(op.Id, session?.Id, req.AgentId, op.InitiatingUser, req.UserRequestRef);

        // Pre-change checkpoint: commit pre-existing uncommitted work (never silently mixed into this operation).
        var dirty = await ctx.Git.StatusAsync(workDir, ct);
        var checkpointSha = head;
        if (dirty.Count > 0)
        {
            var permitted = dirty.Where(d => RepositoryContext.IsPermittedPath(d.Path, settings)).Select(d => d.Path).ToList();
            op.ExcludedPaths = dirty.Select(d => d.Path).Except(permitted).ToList();
            if (settings.CommitUncommittedChangesOnBegin && permitted.Count > 0)
            {
                await StageAsync(workDir, permitted, ct);
                var findings = await ScanStagedAsync(workDir, ct);
                if (findings.Count > 0 && settings.SecretScanMode == SecretScanMode.Block)
                {
                    var bad = findings.Select(f => f.Path).Distinct().ToList();
                    await ctx.Git.RunAsync(workDir, ["reset", "-q", "--", .. bad.Select(RepoPath.Literal)], new() { ThrowOnError = false }, ct);
                    op.ExcludedPaths.AddRange(bad);
                }
                if (await HasStagedChangesAsync(workDir, ct))
                {
                    checkpointSha = await ctx.Git.CommitAsync(workDir,
                        CommitMessageBuilder.Checkpoint($"before {Truncate(op.Description, 60)}", op.Id, session?.Id, req.AgentId, op.InitiatingUser),
                        ctx.ServiceIdentity, ctx.ServiceIdentity, false, ct);
                    await recorder.RecordAsync(repo, workDir, branch, checkpointSha, attribution with { CommitType = "checkpoint" }, ct);
                }
            }
            else
            {
                op.ExcludedPaths = dirty.Select(d => d.Path).ToList();
            }
        }
        op.StartCommitSha = checkpointSha;

        var cp = await db.Checkpoints.Where(c => c.RepositoryId == repo.Id && c.CommitSha == checkpointSha && c.Branch == branch)
            .OrderByDescending(c => c.CreatedAt).FirstOrDefaultAsync(ct);
        if (cp is null || checkpointSha != head)
        {
            cp = await checkpoints.CreateAsync(repo, workDir, branch, checkpointSha, CheckpointType.PreChange,
                checkpointSha == head ? $"Pre-change state for: {op.Description}" : $"Uncommitted work saved before: {op.Description}", attribution, ct);
        }
        op.PreChangeCheckpointId = cp.Id;
        db.ChangeOperations.Add(op);
        await db.SaveChangesAsync(ct);

        // High-risk protection: the starting point must be verified on the remote before work begins.
        var requireRemote = risk.RequiresVerifiedRemoteCheckpoint || settings.RequireRemoteCheckpointBeforeChanges;
        var overrideApplied = false;
        if (requireRemote)
        {
            var durability = cp.SyncState;
            if (durability != DurabilityState.RemoteSynchronized || !await IsStillVerifiedAsync(repo, workDir, branch, checkpointSha, ct))
            {
                var item = await sync.EnqueueAsync(repo, workDir, branch, checkpointSha, op.Id, ct);
                durability = await sync.PushNowAsync(item, ct);
            }
            await db.Entry(cp).ReloadAsync(ct);
            if (durability != DurabilityState.RemoteSynchronized)
            {
                if (req.Override is { Reason.Length: > 0 } ov && policy.AllowAdministratorOverride && await access.CanOverridePoliciesAsync(repo, ct))
                {
                    var po = new PolicyOverride
                    {
                        RepositoryId = repo.Id, ChangeOperationId = op.Id, PolicyKey = HighRiskPolicySettings.Key,
                        GrantedByUserId = access.Caller.UserId ?? Guid.Empty, GrantedBy = access.Caller.Name, Reason = ov.Reason,
                        Details = Json.Serialize(new { risk.Reasons, durability })
                    };
                    db.PolicyOverrides.Add(po);
                    op.PolicyOverrideId = po.Id;
                    overrideApplied = true;
                    audit.Record("policy.override", repo.Id, repo.CompanyId, op.Id.ToString(), "override", new { ov.Reason, risk.Reasons, durability });
                }
                else
                {
                    op.State = ChangeOperationState.PolicyBlocked;
                    op.Authorized = false;
                    op.FailureReason = $"Remote checkpoint verification failed ({durability}); high-risk modification blocked.";
                    audit.Record("change.blocked", repo.Id, repo.CompanyId, op.Id.ToString(), "blocked", new { risk.Reasons, durability });
                    notifications.Publish("change.blocked", repo.CompanyId, repo.Id, new { op.Id, risk.Reasons });
                    db.MonitoringAlerts.Add(new MonitoringAlert
                    {
                        RepositoryId = repo.Id, CompanyId = repo.CompanyId, Key = $"high-risk-blocked:{op.Id}", Category = "high-risk-failure",
                        Severity = AlertSeverity.Warning, Message = $"High-risk change '{Truncate(op.Description, 80)}' blocked: remote checkpoint not verified."
                    });
                    await db.SaveChangesAsync(ct);
                    throw new PolicyViolationException(
                        risk.Level == RiskLevel.High ? ErrorCodes.HighRiskBlocked : ErrorCodes.RemoteCheckpointRequired,
                        "The pre-change checkpoint could not be verified on the central Git server, so the modification was not authorized. Retry when the server is reachable, or ask a repository administrator to apply an audited override.",
                        new { operationId = op.Id, risk = risk.ToDto(), checkpoint = CheckpointService.ToDto(cp) });
                }
            }
        }
        else if (checkpointSha != head && settings.AutoPush)
        {
            await sync.EnqueueAsync(repo, workDir, branch, checkpointSha, op.Id, ct);
        }

        op.Authorized = true;
        op.State = ChangeOperationState.Active;
        if (session is not null)
        {
            session.LastActivityAt = now;
            if (checkpointSha != head)
                db.AISessionCommits.Add(new AISessionCommit { SessionId = session.Id, RepositoryId = repo.Id, Sha = checkpointSha, IsCheckpointOnly = true, Sequence = await NextSequenceAsync(session.Id, ct) });
        }
        audit.Record("change.begin", repo.Id, repo.CompanyId, op.Id.ToString(), details: new { op.AgentId, op.Description, risk.Level, overrideApplied });
        notifications.Publish("change.begin", repo.CompanyId, repo.Id, new { op.Id, op.AgentId, op.Description, risk = risk.Level.ToString() });
        await db.SaveChangesAsync(ct);
        await db.Entry(cp).ReloadAsync(ct);

        return new BeginChangeResponse
        {
            OperationId = op.Id, WorkingDirectory = workDir, Branch = branch, ExpectedHead = checkpointSha, Risk = risk.ToDto(),
            PreChangeCheckpoint = CheckpointService.ToDto(cp), Authorized = true, LeaseExpiresAt = op.LeaseExpiresAt, OverrideApplied = overrideApplied
        };
    }

    private async Task<bool> IsStillVerifiedAsync(Repository repo, string dir, string branch, string sha, CancellationToken ct)
    {
        var v = await sync.VerifyAsync(repo, dir, branch, sha, ct);
        return v.Verified;
    }

    public async Task<CompleteChangeResponse> CompleteAsync(Guid repositoryId, Guid operationId, CompleteChangeRequest req, CancellationToken ct)
    {
        var repo = await access.RequireRepositoryAsync(repositoryId, RepoAction.Write, ct);
        var settings = repo.Settings ?? new RepositorySettings();
        var op = await db.ChangeOperations.FirstOrDefaultAsync(o => o.Id == operationId && o.RepositoryId == repositoryId, ct) ?? throw new NotFoundException("Change operation");
        if (op.State == ChangeOperationState.Completed) return await CompletedResultAsync(op, ct);
        if (!op.Authorized) throw new ConflictException(ErrorCodes.InvalidState, "This operation was blocked by policy and was never authorized.");
        if (op.State is not (ChangeOperationState.Active or ChangeOperationState.Interrupted or ChangeOperationState.PolicyBlocked or ChangeOperationState.Committing))
            throw new ConflictException(ErrorCodes.InvalidState, $"Operation is {op.State}.");
        if (op.CallerUserId is not null && op.CallerUserId != access.Caller.UserId && !access.Caller.IsSystemAdministrator)
            throw new ForbiddenException("Only the caller that began the operation can complete it.");
        var session = op.SessionId is { } sid ? await db.AISessions.FirstOrDefaultAsync(s => s.Id == sid, ct) : null;
        var dir = ctx.Paths.Revalidate(op.WorkingDirectory);

        await using var gitLock = await locks.AcquireAsync(LockService.KeyForPath(dir), TimeSpan.FromSeconds(30), ct);
        var head = await ctx.Git.HeadAsync(dir, ct) ?? throw new ConflictException(ErrorCodes.InvalidState, "Repository has no commits.");
        if (req.ExpectedHead is not null && !head.StartsWith(RepoPath.Sha(req.ExpectedHead), StringComparison.OrdinalIgnoreCase))
            throw new ConflictException(ErrorCodes.HeadMismatch, $"HEAD is {head}, expected {req.ExpectedHead}.", new { head });
        if (head != op.StartCommitSha && !await ctx.Git.IsAncestorAsync(dir, op.StartCommitSha, head, ct))
            throw new ConflictException(ErrorCodes.HeadMismatch, "The branch was moved to a commit that does not contain the operation's starting point; reconcile manually (nothing was committed).", new { head, start = op.StartCommitSha });
        var branch = await ctx.Git.CurrentBranchAsync(dir, ct) ?? op.Branch;

        op.State = ChangeOperationState.Committing;
        op.Version = Guid.NewGuid();
        await db.SaveChangesAsync(ct);

        // 1. Detect and select the operation's files.
        var status = await ctx.Git.StatusAsync(dir, ct);
        var skipped = new List<string>();
        var selected = new List<string>();
        foreach (var change in status)
        {
            var path = change.Path;
            var inScope = op.ScopePaths.Count == 0 || op.ScopePaths.Any(s => path == s || path.StartsWith(s.TrimEnd('/') + "/", StringComparison.Ordinal));
            if (op.ExcludedPaths.Contains(path) || !inScope || !RepositoryContext.IsPermittedPath(path, settings)) skipped.Add(path);
            else selected.Add(path);
        }

        // 2. Stage and scan.
        await StageAsync(dir, selected, ct);
        var findings = await ScanStagedAsync(dir, ct);
        if (findings.Count > 0 && settings.SecretScanMode == SecretScanMode.Block)
        {
            await ctx.Git.RunAsync(dir, ["reset", "-q"], null, ct);
            op.State = ChangeOperationState.PolicyBlocked;
            op.FailureReason = $"Possible secrets detected in {findings.Select(f => f.Path).Distinct().Count()} file(s); nothing was committed. Remove them and call complete again, or abort.";
            audit.Record("secrets.blocked", repo.Id, repo.CompanyId, op.Id.ToString(), "blocked", findings);
            await db.SaveChangesAsync(ct);
            throw new PolicyViolationException(ErrorCodes.SecretsDetected, op.FailureReason, new { operationId = op.Id, findings });
        }

        // 3. Commit.
        var staged = await ctx.Git.DiffFilesAsync(dir, null, null, true, null, ct);
        string resultSha = head;
        string? message = null;
        if (staged.Count > 0)
        {
            message = CommitMessageBuilder.Build(new CommitContext
            {
                Summary = req.Summary ?? op.Description, Description = op.Description, CommitType = req.CommitType, ExplicitMessage = req.CommitMessage,
                Files = staged, OperationId = op.Id, SessionId = op.SessionId, AgentId = op.AgentId, InitiatingUser = op.InitiatingUser,
                UserRequestRef = op.UserRequestRef, BuildStatus = req.BuildStatus, TestStatus = req.TestStatus
            });
            resultSha = await ctx.Git.CommitAsync(dir, message, RepositoryContext.AgentIdentity(op.AgentId, ctx.Options), ctx.ServiceIdentity, false, ct);
        }

        var allFiles = resultSha == op.StartCommitSha ? [] : await ctx.Git.DiffFilesAsync(dir, op.StartCommitSha, resultSha, false, null, ct);
        var actualRisk = HighRiskPolicyEngine.EvaluateActual(allFiles, await ctx.HighRiskPolicyAsync(repo, ct));
        if (actualRisk.Level == RiskLevel.High && op.RiskLevel != RiskLevel.High)
        {
            audit.Record("change.undeclared-high-risk", repo.Id, repo.CompanyId, op.Id.ToString(), "warning", actualRisk.Reasons);
            db.MonitoringAlerts.Add(new MonitoringAlert
            {
                RepositoryId = repo.Id, CompanyId = repo.CompanyId, Key = $"undeclared-high-risk:{op.Id}", Category = "high-risk-operation",
                Severity = AlertSeverity.Warning, Message = $"Operation '{Truncate(op.Description, 60)}' made high-risk changes it did not declare: {string.Join("; ", actualRisk.Reasons.Take(2))}"
            });
        }

        if (resultSha == op.StartCommitSha)
        {
            op.State = ChangeOperationState.Completed;
            op.CompletedAt = DateTime.UtcNow;
            op.ResultCommitSha = null;
            audit.Record("change.complete", repo.Id, repo.CompanyId, op.Id.ToString(), details: new { noChanges = true, skipped });
            await db.SaveChangesAsync(ct);
            return new CompleteChangeResponse { OperationId = op.Id, State = op.State, NoChanges = true, SkippedFiles = skipped, Durability = DurabilityState.WorkingTreeOnly, ActualRisk = actualRisk.ToDto() };
        }

        // 4. Metadata, checkpoint, session.
        var attribution = new CommitAttribution(op.Id, op.SessionId, op.AgentId, op.InitiatingUser, op.UserRequestRef, req.BuildStatus, req.TestStatus);
        var recorded = await recorder.RecordRangeAsync(repo, dir, branch, op.StartCommitSha, resultSha, attribution, ct);
        var cp = await checkpoints.CreateAsync(repo, dir, branch, resultSha, CheckpointType.PostChange, req.Summary ?? op.Description, attribution, ct);
        op.ResultCommitSha = resultSha;
        op.PostChangeCheckpointId = cp.Id;
        op.State = ChangeOperationState.Completed;
        op.CompletedAt = DateTime.UtcNow;
        op.FailureReason = null;
        if (session is not null)
        {
            var seq = await NextSequenceAsync(session.Id, ct);
            foreach (var c in recorded)
                db.AISessionCommits.Add(new AISessionCommit { SessionId = session.Id, RepositoryId = repo.Id, Sha = c.Sha, CommitMetadataId = c.Id, Sequence = seq++ });
            session.LastActivityAt = DateTime.UtcNow;
            session.EndingCommitSha = resultSha;
            if (req.TestsExecuted is { Length: > 0 }) session.TestsExecuted = session.TestsExecuted.Concat(req.TestsExecuted).Distinct().TakeLast(200).ToList();
            if (req.TestStatus is not null) session.LastTestStatus = req.TestStatus;
        }
        var branchRow = await db.Branches.FirstOrDefaultAsync(b => b.RepositoryId == repo.Id && b.Name == branch, ct);
        if (branchRow is null) db.Branches.Add(new Branch { RepositoryId = repo.Id, Name = branch, HeadSha = resultSha, SessionId = op.SessionId, IsSessionBranch = op.SessionId is not null });
        else { branchRow.HeadSha = resultSha; branchRow.UpdatedAt = DateTime.UtcNow; }
        audit.Record("change.complete", repo.Id, repo.CompanyId, op.Id.ToString(), details: new { resultSha, files = allFiles.Count, skipped, findings = findings.Count });
        notifications.Publish("change.completed", repo.CompanyId, repo.Id, new { op.Id, resultSha, files = allFiles.Count });
        await db.SaveChangesAsync(ct);
        await gitLock.DisposeAsync();

        // 5. Push and verify (durably queued first).
        var durability = DurabilityState.LocalCommit;
        if (settings.AutoPush)
        {
            var item = await sync.EnqueueAsync(repo, dir, branch, resultSha, op.Id, ct);
            durability = req.WaitForRemoteVerification ? await sync.PushNowAsync(item, ct) : DurabilityState.RemotePushPending;
        }
        await db.Entry(cp).ReloadAsync(ct);
        logger.LogInformation("Change operation {Op} committed {Sha} ({Durability})", op.Id, resultSha, cp.SyncState);

        return new CompleteChangeResponse
        {
            OperationId = op.Id, State = op.State, CommitSha = resultSha, Files = allFiles, SkippedFiles = skipped, CheckpointId = cp.Id,
            Durability = cp.SyncState, ActualRisk = actualRisk.ToDto(), SecretFindings = findings, CommitMessage = message,
            RecoveryOptions = RecoveryOptionsFor(repo.Id, op)
        };
    }

    public async Task<AbortChangeResponse> AbortAsync(Guid repositoryId, Guid operationId, AbortChangeRequest req, CancellationToken ct)
    {
        var repo = await access.RequireRepositoryAsync(repositoryId, RepoAction.Write, ct);
        var op = await db.ChangeOperations.FirstOrDefaultAsync(o => o.Id == operationId && o.RepositoryId == repositoryId, ct) ?? throw new NotFoundException("Change operation");
        if (op.State is ChangeOperationState.Completed or ChangeOperationState.Aborted)
            return new AbortChangeResponse(op.Id, op.State, op.PreservedBranch, null);
        var dir = ctx.Paths.Revalidate(op.WorkingDirectory);

        string? preservedBranch = null, preservedSha = null;
        if (op.Authorized && req.Mode == AbortMode.PreserveAndReset && await ctx.Git.IsRepositoryAsync(dir, ct))
        {
            await using var gitLock = await locks.AcquireAsync(LockService.KeyForPath(dir), TimeSpan.FromSeconds(30), ct);
            var head = await ctx.Git.HeadAsync(dir, ct)!;
            preservedSha = await ctx.Git.SnapshotWorkingTreeAsync(dir,
                $"checkpoint: aborted operation {op.Id}\n\n{op.Description}\n\n{CommitMessageBuilder.OperationTrailer}: {op.Id}\n{CommitMessageBuilder.AgentTrailer}: {op.AgentId}\n",
                ctx.ServiceIdentity, ct);
            if (preservedSha is null && head != op.StartCommitSha) preservedSha = head;
            if (preservedSha is not null)
            {
                preservedBranch = $"wings/aborted/{op.Id.ToString("N")[..12]}";
                await ctx.Git.UpdateRefAsync(dir, "refs/heads/" + preservedBranch, preservedSha, null, ct);
                await checkpoints.CreateAsync(repo, dir, preservedBranch, preservedSha, CheckpointType.Recovery, $"Work preserved from aborted operation: {op.Description}",
                    new CommitAttribution(op.Id, op.SessionId, op.AgentId, op.InitiatingUser), ct);
                await db.SaveChangesAsync(ct);
                await sync.EnqueueAsync(repo, dir, preservedBranch, preservedSha, op.Id, ct);
                // Only now (work saved on a ref) restore the starting point; the branch only moves back over unpublished commits.
                var target = head != op.StartCommitSha && await RemoteContainsAsync(repo, op.Branch, head!, ct) ? head! : op.StartCommitSha;
                await ctx.Git.RunAsync(dir, ["reset", "--hard", "-q", target], null, ct);
                await ctx.Git.RunAsync(dir, ["clean", "-fdq"], null, ct);
            }
        }
        op.State = ChangeOperationState.Aborted;
        op.CompletedAt = DateTime.UtcNow;
        op.PreservedBranch = preservedBranch;
        op.FailureReason = req.Reason ?? op.FailureReason;
        audit.Record("change.abort", repo.Id, repo.CompanyId, op.Id.ToString(), details: new { req.Mode, preservedBranch, preservedSha, req.Reason });
        await db.SaveChangesAsync(ct);
        return new AbortChangeResponse(op.Id, op.State, preservedBranch, preservedSha);
    }

    private async Task<bool> RemoteContainsAsync(Repository repo, string branch, string sha, CancellationToken ct)
    {
        var known = await db.Branches.Where(b => b.RepositoryId == repo.Id && b.Name == branch).Select(b => b.RemoteHeadSha).FirstOrDefaultAsync(ct);
        return known is not null && (known == sha || await ctx.Git.IsAncestorAsync(ctx.Paths.Revalidate((await ctx.MainWorkingDirectoryAsync(repo.Id, ct))!.Path), sha, known, ct));
    }

    public async Task<ChangeOperationDto> GetAsync(Guid repositoryId, Guid operationId, CancellationToken ct)
    {
        await access.RequireRepositoryAsync(repositoryId, RepoAction.Read, ct, track: false);
        var op = await db.ChangeOperations.AsNoTracking().FirstOrDefaultAsync(o => o.Id == operationId && o.RepositoryId == repositoryId, ct) ?? throw new NotFoundException("Change operation");
        return ToDto(op);
    }

    public async Task<PagedResult<ChangeOperationDto>> ListAsync(Guid repositoryId, ChangeOperationState? state, int page, int pageSize, CancellationToken ct)
    {
        await access.RequireRepositoryAsync(repositoryId, RepoAction.Read, ct, track: false);
        var q = db.ChangeOperations.AsNoTracking().Where(o => o.RepositoryId == repositoryId);
        if (state is not null) q = q.Where(o => o.State == state);
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(o => o.CreatedAt).Skip((Math.Max(1, page) - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<ChangeOperationDto>(items.Select(ToDto).ToList(), total, page, pageSize);
    }

    /// <summary>
    /// Crash recovery for operations interrupted in the Committing state: if the commit carrying the operation id
    /// exists, finish the bookkeeping; otherwise mark the operation Interrupted so it can be completed or aborted.
    /// </summary>
    public async Task<int> ReconcileAsync(bool startup, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var stuck = await db.ChangeOperations.Where(o => o.State == ChangeOperationState.Committing && (startup || o.LeaseExpiresAt < now)).ToListAsync(ct);
        var expired = await db.ChangeOperations.Where(o => o.State == ChangeOperationState.Active && o.LeaseExpiresAt < now).ToListAsync(ct);
        foreach (var o in expired)
        {
            o.State = ChangeOperationState.Interrupted;
            o.FailureReason = "Lease expired before completion; complete or abort the operation to reconcile.";
        }
        foreach (var op in stuck)
        {
            var repo = await db.Repositories.Include(r => r.Remotes).FirstAsync(r => r.Id == op.RepositoryId, ct);
            if (!await ctx.Git.IsRepositoryAsync(op.WorkingDirectory, ct)) { op.State = ChangeOperationState.Interrupted; continue; }
            var entries = await ctx.Git.LogAsync(op.WorkingDirectory, [$"{op.StartCommitSha}..HEAD"], 50, 0, ct);
            var match = entries.FirstOrDefault(e => CommitMessageBuilder.ParseTrailers(e.Body).TryGetValue(CommitMessageBuilder.OperationTrailer, out var id) && id == op.Id.ToString());
            if (match is null)
            {
                op.State = ChangeOperationState.Interrupted;
                op.FailureReason = "Interrupted while committing; no commit was created. Complete or abort to reconcile.";
                continue;
            }
            var branch = await ctx.Git.CurrentBranchAsync(op.WorkingDirectory, ct) ?? op.Branch;
            var attribution = new CommitAttribution(op.Id, op.SessionId, op.AgentId, op.InitiatingUser, op.UserRequestRef);
            await recorder.RecordRangeAsync(repo, op.WorkingDirectory, branch, op.StartCommitSha, match.Sha, attribution, ct);
            var cp = await checkpoints.CreateAsync(repo, op.WorkingDirectory, branch, match.Sha, CheckpointType.PostChange, op.Description, attribution, ct);
            op.ResultCommitSha = match.Sha;
            op.PostChangeCheckpointId = cp.Id;
            op.State = ChangeOperationState.Completed;
            op.CompletedAt = now;
            audit.Record("change.reconciled", repo.Id, repo.CompanyId, op.Id.ToString(), details: new { match.Sha });
            await db.SaveChangesAsync(ct);
            await sync.EnqueueAsync(repo, op.WorkingDirectory, branch, match.Sha, op.Id, ct);
        }
        await db.SaveChangesAsync(ct);
        return stuck.Count + expired.Count;
    }

    private async Task<CompleteChangeResponse> CompletedResultAsync(ChangeOperation op, CancellationToken ct)
    {
        var cp = op.PostChangeCheckpointId is { } id ? await db.Checkpoints.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct) : null;
        var meta = op.ResultCommitSha is null ? null : await db.CommitMetadata.AsNoTracking().Include(c => c.Files).FirstOrDefaultAsync(c => c.RepositoryId == op.RepositoryId && c.Sha == op.ResultCommitSha, ct);
        return new CompleteChangeResponse
        {
            OperationId = op.Id, State = op.State, CommitSha = op.ResultCommitSha, NoChanges = op.ResultCommitSha is null, CheckpointId = cp?.Id,
            Durability = cp?.SyncState ?? DurabilityState.WorkingTreeOnly, CommitMessage = meta?.Message,
            Files = meta?.Files.Select(f => new FileChangeDto(f.Path, f.Kind, f.OldPath, f.Additions, f.Deletions)).ToList() ?? [],
            RecoveryOptions = RecoveryOptionsFor(op.RepositoryId, op)
        };
    }

    private static IReadOnlyList<string> RecoveryOptionsFor(Guid repoId, ChangeOperation op) =>
    [
        $"Undo this change: POST /api/v1/repositories/{repoId}/recovery/preview {{\"type\":\"RevertCommits\",\"commitShas\":[\"{op.ResultCommitSha}\"]}}",
        $"Return to the pre-change checkpoint: POST /api/v1/repositories/{repoId}/recovery/preview {{\"type\":\"RestoreProject\",\"recoveryPoint\":\"{op.StartCommitSha}\",\"strategy\":\"RollbackCommit\"}}",
    ];

    private async Task StageAsync(string dir, IReadOnlyList<string> paths, CancellationToken ct)
    {
        foreach (var chunk in paths.Chunk(200))
            await ctx.Git.RunAsync(dir, ["add", "-A", "--", .. chunk.Select(RepoPath.Literal)], null, ct);
    }

    private async Task<IReadOnlyList<SecretFindingDto>> ScanStagedAsync(string dir, CancellationToken ct)
    {
        var diff = await ctx.Git.RunAsync(dir, ["diff", "--cached", "-U0", "--no-ext-diff", "--no-textconv", "--no-color"], null, ct);
        return SecretScanner.ScanUnifiedDiff(diff.StdOut);
    }

    private async Task<bool> HasStagedChangesAsync(string dir, CancellationToken ct)
    {
        var r = await ctx.Git.RunAsync(dir, ["diff", "--cached", "--quiet"], new() { ThrowOnError = false }, ct);
        return r.ExitCode == 1;
    }

    private async Task<int> NextSequenceAsync(Guid sessionId, CancellationToken ct) =>
        (await db.AISessionCommits.Where(c => c.SessionId == sessionId).MaxAsync(c => (int?)c.Sequence, ct) ?? 0)
        + db.AISessionCommits.Local.Count(c => c.SessionId == sessionId) + 1;

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    public static ChangeOperationDto ToDto(ChangeOperation o) => new()
    {
        Id = o.Id, RepositoryId = o.RepositoryId, SessionId = o.SessionId, AgentId = o.AgentId, InitiatingUser = o.InitiatingUser, State = o.State,
        RiskLevel = o.RiskLevel, RiskReasons = o.RiskReasons, Description = o.Description, UserRequestRef = o.UserRequestRef,
        WorkingDirectory = o.WorkingDirectory, Branch = o.Branch, StartCommitSha = o.StartCommitSha, ResultCommitSha = o.ResultCommitSha,
        PreChangeCheckpointId = o.PreChangeCheckpointId, PostChangeCheckpointId = o.PostChangeCheckpointId, CreatedAt = o.CreatedAt,
        CompletedAt = o.CompletedAt, LeaseExpiresAt = o.LeaseExpiresAt, FailureReason = o.FailureReason, OverrideApplied = o.PolicyOverrideId is not null
    };
}
