using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wings.GitManager.Contracts;
using Wings.GitManager.Core;
using Wings.GitManager.Core.Entities;
using Wings.GitManager.Infrastructure.Data;
using Wings.GitManager.Infrastructure.Git;

namespace Wings.GitManager.Infrastructure.Services;

public sealed record VerificationResult(bool Verified, string? RemoteHead, string Method, bool? ProviderConfirmed);

/// <summary>
/// Persistent remote synchronization: durable push queue, non-force pushes, exponential retries, divergence
/// detection and verification that the commit is really on the remote branch before anything is reported as
/// remotely protected.
/// </summary>
public sealed class SyncService(
    GitManagerDbContext db,
    RepositoryContext ctx,
    NotificationPublisher notifications,
    AuditService audit,
    IOptions<SyncOptions> syncOptions,
    IOptions<WorkerOptions> workerOptions,
    ILogger<SyncService> logger)
{
    private readonly SyncOptions _o = syncOptions.Value;

    /// <summary>Queues a push (saved immediately so it survives restarts). Existing pending items for the same commit are reused.</summary>
    public async Task<PushQueueItem> EnqueueAsync(Repository repo, string workDir, string branch, string sha, Guid? operationId, CancellationToken ct)
    {
        var existing = await db.PushQueue.FirstOrDefaultAsync(p => p.RepositoryId == repo.Id && p.Branch == branch && p.CommitSha == sha
            && (p.State == PushJobState.Pending || p.State == PushJobState.RetryScheduled || p.State == PushJobState.Processing), ct);
        if (existing is not null) return existing;
        var known = await db.Branches.Where(b => b.RepositoryId == repo.Id && b.Name == branch).Select(b => b.RemoteHeadSha).FirstOrDefaultAsync(ct);
        var item = new PushQueueItem
        {
            RepositoryId = repo.Id, Branch = branch, CommitSha = sha, ChangeOperationId = operationId,
            WorkingDirectory = workDir, ExpectedRemoteHead = known, State = PushJobState.Pending
        };
        db.PushQueue.Add(item);
        await MarkCheckpointsAsync(repo.Id, sha, DurabilityState.RemotePushPending, onlyFrom: [DurabilityState.LocalCommit, DurabilityState.Failed], ct);
        await db.SaveChangesAsync(ct);
        return item;
    }

    /// <summary>Attempts the queued push now (bounded); leaves it queued for retry on failure.</summary>
    public async Task<DurabilityState> PushNowAsync(PushQueueItem item, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(ctx.Options.SynchronousPushTimeoutSeconds));
        if (!await TryClaimAsync(item, cts.Token)) return DurabilityState.RemotePushPending;
        try
        {
            var state = await ProcessClaimedAsync(item, cts.Token);
            return state switch
            {
                PushJobState.Succeeded => DurabilityState.RemoteSynchronized,
                PushJobState.Conflict => DurabilityState.RemoteDiverged,
                _ => DurabilityState.RemotePushPending
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await ScheduleRetryAsync(item, "Synchronous push timed out; will retry in the background.", CancellationToken.None);
            return DurabilityState.RemotePushPending;
        }
    }

    private async Task<bool> TryClaimAsync(PushQueueItem item, CancellationToken ct)
    {
        var owner = $"{workerOptions.Value.InstanceName}:{Environment.ProcessId}";
        var now = DateTime.UtcNow;
        var claimed = await db.PushQueue
            .Where(p => p.Id == item.Id && (p.State == PushJobState.Pending || p.State == PushJobState.RetryScheduled ||
                        p.State == PushJobState.AuthenticationFailed || p.State == PushJobState.PermanentFailure ||
                        (p.State == PushJobState.Processing && p.LockedUntil < now)))
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.State, PushJobState.Processing)
                .SetProperty(p => p.LockedBy, owner)
                .SetProperty(p => p.LockedUntil, now.AddSeconds(_o.LeaseSeconds)), ct);
        if (claimed == 0) return false;
        await db.Entry(item).ReloadAsync(ct);
        return true;
    }

    /// <summary>Worker entry point: claims and processes due queue items.</summary>
    public async Task<int> ProcessDueAsync(int max, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var due = await db.PushQueue
            .Where(p => (p.State == PushJobState.Pending || p.State == PushJobState.RetryScheduled) && p.NextAttemptAt <= now
                        || p.State == PushJobState.Processing && p.LockedUntil < now)
            .OrderBy(p => p.CreatedAt).Take(max).ToListAsync(ct);
        var processed = 0;
        foreach (var item in due)
        {
            if (!await TryClaimAsync(item, ct)) continue;
            try
            {
                await ProcessClaimedAsync(item, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Push job {JobId} failed unexpectedly", item.Id);
                await ScheduleRetryAsync(item, ex.Message, ct);
            }
            processed++;
        }
        return processed;
    }

    private async Task<PushJobState> ProcessClaimedAsync(PushQueueItem item, CancellationToken ct)
    {
        var repo = await db.Repositories.Include(r => r.Remotes).Include(r => r.Company).FirstAsync(r => r.Id == item.RepositoryId, ct);
        var attempt = new SyncAttempt { PushQueueItemId = item.Id, RepositoryId = repo.Id, Worker = workerOptions.Value.InstanceName };
        db.SyncAttempts.Add(attempt);

        var dir = await ResolveDirectoryAsync(item, ct);
        if (dir is null)
            return await FinishAsync(item, attempt, PushJobState.PermanentFailure, "No local working directory containing the commit exists.", null, null, ct);
        if (!await ctx.Git.ObjectExistsAsync(dir, item.CommitSha, ct))
            return await FinishAsync(item, attempt, PushJobState.PermanentFailure, $"Commit {item.CommitSha} no longer exists locally.", null, null, ct);

        var cred = ctx.Credential(repo);
        var remote = ctx.PrimaryRemote(repo);
        var allowFile = ctx.AllowFile(repo);
        string? before;
        try
        {
            before = await ctx.Git.LsRemoteAsync(dir, remote.Url, item.Branch, cred, ct, allowFile);
        }
        catch (GitCommandException ex)
        {
            var auth = IsAuthError(ex.StandardError);
            return auth
                ? await FinishAsync(item, attempt, PushJobState.AuthenticationFailed, ex.Message, null, null, ct)
                : await ScheduleRetryAsync(item, ex.Message, ct, attempt);
        }
        attempt.RemoteHeadBefore = before;

        if (before is not null && before != item.CommitSha)
        {
            // Make the remote head available locally so ancestry can be evaluated.
            try { await ctx.Git.FetchAsync(dir, remote.Url, cred, ct, allowFile); } catch (GitCommandException) { /* evaluated below */ }
            if (await ctx.Git.ObjectExistsAsync(dir, before, ct) && await ctx.Git.IsAncestorAsync(dir, item.CommitSha, before, ct))
            {
                // Already contained in the remote branch (a newer push covered it).
                return await VerifyAndCompleteAsync(item, attempt, repo, dir, before, ct);
            }
        }

        if (before != item.CommitSha)
        {
            var outcome = await ctx.Git.PushAsync(dir, remote.Url, item.CommitSha, item.Branch, cred, ct, allowFile);
            if (!outcome.Success)
            {
                if (outcome.Rejected)
                {
                    await MarkCheckpointsAsync(repo.Id, item.CommitSha, DurabilityState.RemoteDiverged, null, ct);
                    repo.RemoteStatus = RemoteStatus.Diverged;
                    notifications.Publish("sync.conflict", repo.CompanyId, repo.Id, new { item.Id, item.Branch, item.CommitSha });
                    audit.Record("sync.conflict", repo.Id, repo.CompanyId, item.Branch, "failure", new { item.CommitSha, remoteHead = before });
                    return await FinishAsync(item, attempt, PushJobState.Conflict,
                        $"Remote branch '{item.Branch}' has diverged (remote head {before}). Local commits are preserved; merge or rebase is required. No force push was attempted.", before, before, ct);
                }
                if (outcome.AuthFailed)
                    return await FinishAsync(item, attempt, PushJobState.AuthenticationFailed, outcome.Message, before, null, ct);
                return await ScheduleRetryAsync(item, outcome.Message, ct, attempt);
            }
        }
        return await VerifyAndCompleteAsync(item, attempt, repo, dir, item.CommitSha, ct);
    }

    private async Task<PushJobState> VerifyAndCompleteAsync(PushQueueItem item, SyncAttempt attempt, Repository repo, string dir, string expectedHead, CancellationToken ct)
    {
        var v = await VerifyAsync(repo, dir, item.Branch, item.CommitSha, ct);
        attempt.RemoteHeadAfter = v.RemoteHead;
        if (!v.Verified)
            return await ScheduleRetryAsync(item, $"Push reported success but verification failed ({v.Method}).", ct, attempt);

        await OnVerifiedAsync(repo, dir, item.Branch, v.RemoteHead ?? item.CommitSha, ct);
        notifications.Publish("sync.succeeded", repo.CompanyId, repo.Id, new { item.Id, item.Branch, item.CommitSha });
        return await FinishAsync(item, attempt, PushJobState.Succeeded, null, attempt.RemoteHeadBefore, v.RemoteHead, ct);
    }

    /// <summary>
    /// Independent verification: the remote branch head (ls-remote) must equal or descend from the commit, and,
    /// where the provider offers an API (Gitea), the provider must also report the commit.
    /// </summary>
    public async Task<VerificationResult> VerifyAsync(Repository repo, string dir, string branch, string sha, CancellationToken ct)
    {
        var remote = ctx.PrimaryRemote(repo);
        var cred = ctx.Credential(repo);
        var allowFile = ctx.AllowFile(repo);
        string? head;
        try
        {
            head = await ctx.Git.LsRemoteAsync(dir, remote.Url, branch, cred, ct, allowFile);
        }
        catch (GitCommandException)
        {
            return new VerificationResult(false, null, "ls-remote failed", null);
        }
        if (head is null) return new VerificationResult(false, null, "branch missing on remote", null);

        var contained = head == sha;
        if (!contained)
        {
            if (!await ctx.Git.ObjectExistsAsync(dir, head, ct))
                try { await ctx.Git.FetchAsync(dir, remote.Url, cred, ct, allowFile); } catch (GitCommandException) { }
            contained = await ctx.Git.ObjectExistsAsync(dir, head, ct) && await ctx.Git.IsAncestorAsync(dir, sha, head, ct);
        }
        if (!contained) return new VerificationResult(false, head, "commit not reachable from remote branch", null);

        bool? providerConfirmed = null;
        if (remote.Owner is not null && remote.RemoteRepositoryName is not null)
        {
            try { providerConfirmed = await ctx.Provider(repo).CommitExistsAsync(remote.Owner, remote.RemoteRepositoryName, sha, ct); }
            catch (Exception ex) { logger.LogWarning(ex, "Provider commit verification failed for {Repo}", repo.Id); }
        }
        if (providerConfirmed == false) return new VerificationResult(false, head, "provider API does not report the commit", false);
        return new VerificationResult(true, head, providerConfirmed == true ? "ls-remote + provider API" : "ls-remote", providerConfirmed);
    }

    /// <summary>Marks every checkpoint contained in the verified remote head as remotely synchronized.</summary>
    public async Task OnVerifiedAsync(Repository repo, string dir, string branch, string remoteHead, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var candidates = await db.Checkpoints.Where(c => c.RepositoryId == repo.Id &&
            (c.SyncState == DurabilityState.LocalCommit || c.SyncState == DurabilityState.RemotePushPending || c.SyncState == DurabilityState.Failed || c.SyncState == DurabilityState.RemoteDiverged))
            .ToListAsync(ct);
        foreach (var cp in candidates)
        {
            if (cp.CommitSha == remoteHead || await ctx.Git.ObjectExistsAsync(dir, cp.CommitSha, ct) && await ctx.Git.IsAncestorAsync(dir, cp.CommitSha, remoteHead, ct))
            {
                cp.SyncState = DurabilityState.RemoteSynchronized;
                cp.RemoteVerifiedAt = now;
            }
        }
        var b = await db.Branches.FirstOrDefaultAsync(x => x.RepositoryId == repo.Id && x.Name == branch, ct);
        if (b is null) db.Branches.Add(b = new Branch { RepositoryId = repo.Id, Name = branch });
        b.RemoteHeadSha = remoteHead;
        b.UpdatedAt = now;
        repo.LastPushAt = now;
        repo.LastVerifiedSha = remoteHead;
        if (branch == repo.DefaultBranch || repo.RemoteStatus is RemoteStatus.Unknown or RemoteStatus.Unreachable or RemoteStatus.Ahead)
            repo.RemoteStatus = RemoteStatus.InSync;
        await db.SaveChangesAsync(ct);
    }

    private async Task<string?> ResolveDirectoryAsync(PushQueueItem item, CancellationToken ct)
    {
        if (Directory.Exists(item.WorkingDirectory) && await ctx.Git.IsRepositoryAsync(item.WorkingDirectory, ct))
            return item.WorkingDirectory;
        return await ctx.AnyExistingDirectoryAsync(item.RepositoryId, ct);
    }

    private async Task<PushJobState> ScheduleRetryAsync(PushQueueItem item, string error, CancellationToken ct, SyncAttempt? attempt = null)
    {
        item.RetryCount++;
        if (item.RetryCount > _o.MaxRetries)
            return await FinishAsync(item, attempt, PushJobState.PermanentFailure, $"Gave up after {_o.MaxRetries} retries: {error}", attempt?.RemoteHeadBefore, null, ct);
        var delay = Math.Min(_o.MaxRetrySeconds, _o.BaseRetrySeconds * Math.Pow(2, item.RetryCount - 1));
        delay += Random.Shared.NextDouble() * delay * 0.2;
        item.NextAttemptAt = DateTime.UtcNow.AddSeconds(delay);
        return await FinishAsync(item, attempt, PushJobState.RetryScheduled, error, attempt?.RemoteHeadBefore, null, ct);
    }

    private async Task<PushJobState> FinishAsync(PushQueueItem item, SyncAttempt? attempt, PushJobState state, string? error, string? before, string? after, CancellationToken ct)
    {
        item.State = state;
        item.LastError = error is null ? null : GitExecutor.Redact(error.Length > 3900 ? error[..3900] : error);
        item.LockedBy = null;
        item.LockedUntil = null;
        if (state is PushJobState.Succeeded or PushJobState.Conflict or PushJobState.PermanentFailure or PushJobState.AuthenticationFailed)
            item.CompletedAt = DateTime.UtcNow;
        if (attempt is not null)
        {
            attempt.FinishedAt = DateTime.UtcNow;
            attempt.Outcome = state;
            attempt.Error = item.LastError;
            attempt.RemoteHeadBefore ??= before;
            attempt.RemoteHeadAfter ??= after;
        }
        if (state is PushJobState.PermanentFailure or PushJobState.AuthenticationFailed)
        {
            await MarkCheckpointsAsync(item.RepositoryId, item.CommitSha, DurabilityState.Failed, [DurabilityState.RemotePushPending, DurabilityState.LocalCommit], ct);
            db.MonitoringAlerts.Add(new MonitoringAlert
            {
                RepositoryId = item.RepositoryId, Key = $"sync-failed:{item.Id}", Category = "sync-failure",
                Severity = AlertSeverity.Critical, Message = $"Push of {item.CommitSha[..Math.Min(12, item.CommitSha.Length)]} to '{item.Branch}' failed: {item.LastError}"
            });
            notifications.Publish("sync.failed", null, item.RepositoryId, new { item.Id, item.Branch, item.CommitSha, state = state.ToString(), item.LastError });
        }
        if (state == PushJobState.RetryScheduled)
        {
            var repo = await db.Repositories.FindAsync([item.RepositoryId], ct);
            if (repo is not null && repo.RemoteStatus != RemoteStatus.Diverged) repo.RemoteStatus = RemoteStatus.Unreachable;
        }
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Push job {JobId} {Repo}/{Branch}@{Sha} -> {State}", item.Id, item.RepositoryId, item.Branch, item.CommitSha, state);
        return state;
    }

    private async Task MarkCheckpointsAsync(Guid repoId, string sha, DurabilityState state, DurabilityState[]? onlyFrom, CancellationToken ct)
    {
        var cps = await db.Checkpoints.Where(c => c.RepositoryId == repoId && c.CommitSha == sha).ToListAsync(ct);
        foreach (var c in cps.Where(c => onlyFrom is null || onlyFrom.Contains(c.SyncState)))
            if (c.SyncState != DurabilityState.RemoteSynchronized && c.SyncState != DurabilityState.IndependentlyBackedUp)
                c.SyncState = state;
    }

    /// <summary>Administrator retry: resets failed/conflicted items to pending.</summary>
    public async Task<int> RetryAsync(Guid repositoryId, Guid? jobId, CancellationToken ct)
    {
        var items = await db.PushQueue.Where(p => p.RepositoryId == repositoryId && (jobId == null || p.Id == jobId) &&
            (p.State == PushJobState.PermanentFailure || p.State == PushJobState.AuthenticationFailed || p.State == PushJobState.RetryScheduled || p.State == PushJobState.Conflict))
            .ToListAsync(ct);
        foreach (var i in items)
        {
            i.State = PushJobState.Pending;
            i.NextAttemptAt = DateTime.UtcNow;
            i.RetryCount = 0;
            i.CompletedAt = null;
        }
        await db.SaveChangesAsync(ct);
        return items.Count;
    }

    private static bool IsAuthError(string s)
    {
        var l = s.ToLowerInvariant();
        return l.Contains("authentication failed") || l.Contains("401") || l.Contains("403") || l.Contains("could not read username");
    }

    public static PushJobDto ToDto(PushQueueItem p, string? repoName = null) => new()
    {
        Id = p.Id, RepositoryId = p.RepositoryId, RepositoryName = repoName, Branch = p.Branch, CommitSha = p.CommitSha,
        ChangeOperationId = p.ChangeOperationId, CreatedAt = p.CreatedAt, RetryCount = p.RetryCount, LastError = p.LastError,
        State = p.State, ExpectedRemoteHead = p.ExpectedRemoteHead, NextAttemptAt = p.NextAttemptAt, CompletedAt = p.CompletedAt
    };
}
