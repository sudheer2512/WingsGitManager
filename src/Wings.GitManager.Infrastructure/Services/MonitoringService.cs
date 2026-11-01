using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wings.GitManager.Contracts;
using Wings.GitManager.Core;
using Wings.GitManager.Core.Entities;
using Wings.GitManager.Infrastructure.Backup;
using Wings.GitManager.Infrastructure.Data;

namespace Wings.GitManager.Infrastructure.Services;

/// <summary>Repository health checks, alerting, storage monitoring, the dashboard summary and health endpoints.</summary>
public sealed class MonitoringService(
    GitManagerDbContext db,
    AccessControl access,
    RepositoryContext ctx,
    IHostingProviderResolver providers,
    IBackupStore backupStore,
    NotificationPublisher notifications,
    IOptions<MonitoringOptions> options,
    IOptions<BackupOptions> backupOptions,
    ILogger<MonitoringService> logger)
{
    private readonly MonitoringOptions _o = options.Value;
    private static DateTime _lastFsck = DateTime.MinValue;
    private static readonly string[] ManagedCategories =
        ["working-directory-missing", "uncommitted-changes", "unpushed-commits", "remote-divergence", "corrupted-repository", "storage", "unverified-backup", "abandoned-worktree", "gitea-health", "unauthorized-access", "recovery-failures"];

    /// <summary>One monitoring pass (run by the worker). Raises, refreshes and resolves alerts.</summary>
    public async Task RunChecksAsync(CancellationToken ct)
    {
        var active = new HashSet<string>();
        var now = DateTime.UtcNow;
        var runFsck = now - _lastFsck > TimeSpan.FromHours(_o.FsckIntervalHours);
        var repos = await db.Repositories.Include(r => r.Remotes).Where(r => r.State == RepositoryState.Active).ToListAsync(ct);

        foreach (var repo in repos)
        {
            try
            {
                var wd = await ctx.MainWorkingDirectoryAsync(repo.Id, ct);
                if (wd is null || !await ctx.Git.IsRepositoryAsync(wd.Path, ct))
                {
                    Raise(active, $"wd-missing:{repo.Id}", "working-directory-missing", AlertSeverity.Warning, repo,
                        $"Local working directory of '{repo.Name}' is missing. The code is safe on the central server; recover it with the clone recovery.");
                    continue;
                }
                var status = await ctx.Git.StatusAsync(wd.Path, ct);
                repo.HasUncommittedChanges = status.Count > 0;
                var hasActiveOp = await db.ChangeOperations.AnyAsync(o => o.RepositoryId == repo.Id && (o.State == ChangeOperationState.Active || o.State == ChangeOperationState.Committing), ct);
                if (status.Count > 0 && !hasActiveOp && repo.ExternalChangeDetectedAt is { } detected && now - detected > TimeSpan.FromMinutes(_o.UnpushedWarningMinutes))
                    Raise(active, $"dirty:{repo.Id}", "uncommitted-changes", AlertSeverity.Info, repo, $"'{repo.Name}' has {status.Count} uncommitted change(s) made outside Wings Code.");

                // Local commits not yet on the remote.
                foreach (var b in await db.Branches.Where(b => b.RepositoryId == repo.Id).ToListAsync(ct))
                {
                    var local = await ctx.Git.ResolveAsync(wd.Path, "refs/heads/" + b.Name, ct);
                    if (local is null) continue;
                    b.HeadSha = local;
                    if (b.RemoteHeadSha == local) continue;
                    var pending = await db.PushQueue.AnyAsync(p => p.RepositoryId == repo.Id && p.Branch == b.Name && p.State != PushJobState.Succeeded && p.CreatedAt > now.AddMinutes(-_o.UnpushedWarningMinutes), ct);
                    var ahead = b.RemoteHeadSha is null ? 1 : await ctx.Git.ObjectExistsAsync(wd.Path, b.RemoteHeadSha, ct) ? await ctx.Git.CountAsync(wd.Path, $"{b.RemoteHeadSha}..{local}", ct) : 1;
                    if (ahead > 0 && !pending && !b.Name.StartsWith("wings/"))
                        Raise(active, $"unpushed:{repo.Id}:{b.Name}", "unpushed-commits", AlertSeverity.Warning, repo, $"Branch '{b.Name}' of '{repo.Name}' has {ahead} local commit(s) not verified on the remote.");
                }
                if (repo.RemoteStatus == RemoteStatus.Diverged)
                    Raise(active, $"diverged:{repo.Id}", "remote-divergence", AlertSeverity.Critical, repo, $"'{repo.Name}' has diverged from the central repository; local commits are preserved and need a merge.");

                repo.StorageBytes = await RepoSizeAsync(wd.Path, ct);
                if (runFsck)
                {
                    var fsck = await ctx.Git.RunAsync(wd.Path, ["fsck", "--connectivity-only", "--no-dangling", "--no-progress"], new() { ThrowOnError = false, Timeout = TimeSpan.FromMinutes(20) }, ct);
                    if (!fsck.Success) Raise(active, $"corrupt:{repo.Id}", "corrupted-repository", AlertSeverity.Critical, repo, $"git fsck failed for '{repo.Name}': {fsck.StdErr.Trim()}");
                }
                else if (await db.MonitoringAlerts.AnyAsync(a => a.Key == $"corrupt:{repo.Id}" && a.ResolvedAt == null, ct)) active.Add($"corrupt:{repo.Id}");

                var lastVerified = await db.RepositoryBackups.Where(b => b.RepositoryId == repo.Id && b.State == BackupState.Verified).MaxAsync(b => (DateTime?)b.VerifiedAt, ct);
                if (lastVerified is null || now - lastVerified > TimeSpan.FromHours(_o.BackupStaleHours))
                    Raise(active, $"backup-stale:{repo.Id}", "unverified-backup", AlertSeverity.Warning, repo,
                        lastVerified is null ? $"'{repo.Name}' has no verified independent backup." : $"Last verified backup of '{repo.Name}' is from {lastVerified:yyyy-MM-dd HH:mm} UTC.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Monitoring failed for repository {Repo}", repo.Id);
            }
        }
        if (runFsck) _lastFsck = now;

        // Abandoned worktrees.
        var worktrees = await db.Worktrees.Where(w => w.State == WorktreeState.Active).ToListAsync(ct);
        foreach (var w in worktrees)
        {
            var session = w.SessionId is { } sid ? await db.AISessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sid, ct) : null;
            var missing = !Directory.Exists(w.Path);
            if (missing || session is { State: SessionState.Abandoned })
            {
                var repo = repos.FirstOrDefault(r => r.Id == w.RepositoryId);
                Raise(active, $"worktree:{w.Id}", "abandoned-worktree", AlertSeverity.Info, repo,
                    missing ? $"Worktree {w.Path} is registered but missing on disk." : $"Worktree {w.Path} belongs to an abandoned AI session and will be released by retention.");
            }
        }

        // Storage volumes.
        foreach (var v in Volumes())
            if (v.Status != "Healthy")
                Raise(active, $"storage:{v.Path}", "storage", v.Status == "Critical" ? AlertSeverity.Critical : AlertSeverity.Warning, null,
                    $"{v.Purpose} volume {v.Path} is {v.UsedPercent:0.#}% full ({v.FreeBytes / 1024 / 1024} MB free).");

        // Hosting providers.
        foreach (var p in providers.All)
        {
            var h = await p.CheckHealthAsync(ct);
            if (h.Status is not ("Healthy" or "NotConfigured"))
                Raise(active, $"provider:{p.Kind}", "gitea-health", AlertSeverity.Critical, null, $"{p.Kind} hosting provider is {h.Status}: {h.Detail}");
        }

        // Security & reliability signals.
        var denied = await db.AuditLogs.CountAsync(a => a.Outcome == "denied" && a.Timestamp > now.AddHours(-1), ct);
        if (denied >= 10) Raise(active, "unauthorized", "unauthorized-access", AlertSeverity.Warning, null, $"{denied} denied access attempts in the last hour.");
        var failures = await db.RecoveryOperations.Where(r => (r.Status == RecoveryStatus.Failed || r.Status == RecoveryStatus.Conflict) && r.CreatedAt > now.AddDays(-1))
            .GroupBy(r => r.RepositoryId).Select(g => new { g.Key, Count = g.Count() }).Where(g => g.Count >= 3).ToListAsync(ct);
        foreach (var f in failures)
            Raise(active, $"recovery-failures:{f.Key}", "recovery-failures", AlertSeverity.Warning, repos.FirstOrDefault(r => r.Id == f.Key), $"{f.Count} recovery attempts failed in the last 24 hours.");

        // Resolve alerts whose condition cleared.
        var open = await db.MonitoringAlerts.Where(a => a.ResolvedAt == null && ManagedCategories.Contains(a.Category)).ToListAsync(ct);
        foreach (var a in open.Where(a => !active.Contains(a.Key))) a.ResolvedAt = now;
        await db.SaveChangesAsync(ct);
    }

    private void Raise(HashSet<string> active, string key, string category, AlertSeverity severity, Repository? repo, string message)
    {
        active.Add(key);
        var existing = db.MonitoringAlerts.Local.FirstOrDefault(a => a.Key == key && a.ResolvedAt == null)
                       ?? db.MonitoringAlerts.FirstOrDefault(a => a.Key == key && a.ResolvedAt == null);
        if (existing is not null)
        {
            existing.LastSeenAt = DateTime.UtcNow;
            existing.Message = message;
            existing.Severity = severity;
            return;
        }
        db.MonitoringAlerts.Add(new MonitoringAlert { Key = key, Category = category, Severity = severity, Message = message, RepositoryId = repo?.Id, CompanyId = repo?.CompanyId });
        notifications.Publish("alert.raised", repo?.CompanyId, repo?.Id, new { key, category, severity = severity.ToString(), message });
    }

    private async Task<long> RepoSizeAsync(string dir, CancellationToken ct)
    {
        var r = await ctx.Git.RunAsync(dir, ["count-objects", "-v"], new() { ThrowOnError = false }, ct);
        long kib = 0;
        foreach (var line in r.StdOut.Split('\n'))
        {
            var parts = line.Split(':', 2);
            if (parts.Length == 2 && parts[0] is "size" or "size-pack" && long.TryParse(parts[1].Trim(), out var n)) kib += n;
        }
        return kib * 1024;
    }

    public IReadOnlyList<VolumeDto> Volumes()
    {
        var list = new List<(string Purpose, string Path)>();
        foreach (var r in ctx.Paths.Roots) list.Add(("workspace", r));
        list.Add(("data", Path.GetFullPath(ctx.Options.DataDirectory)));
        if (backupStore.LocalRoot is { Length: > 0 } b) list.Add(("backup", b));
        return list.Select(x =>
        {
            try
            {
                Directory.CreateDirectory(x.Path);
                var d = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(x.Path))!);
                var used = d.TotalSize == 0 ? 0 : 100.0 * (d.TotalSize - d.AvailableFreeSpace) / d.TotalSize;
                var status = used >= _o.StorageCriticalPercent ? "Critical" : used >= _o.StorageWarningPercent ? "Warning" : "Healthy";
                return new VolumeDto(x.Purpose, x.Path, d.TotalSize, d.AvailableFreeSpace, Math.Round(used, 1), status);
            }
            catch (Exception ex)
            {
                return new VolumeDto(x.Purpose, x.Path, 0, 0, 0, "Unavailable: " + ex.Message);
            }
        }).ToList();
    }

    // ---------------------------------------------------------------- read APIs

    public async Task<DashboardSummaryDto> DashboardAsync(Guid? companyId, Guid? projectId, string? user, DateTime? from, DateTime? to, CancellationToken ct)
    {
        var repos = await access.VisibleRepositoriesAsync(ct);
        if (companyId is not null) repos = repos.Where(r => r.CompanyId == companyId);
        if (projectId is not null) repos = repos.Where(r => r.ProjectId == projectId);
        var ids = await repos.Select(r => r.Id).ToListAsync(ct);
        var today = DateTime.UtcNow.Date;
        var start = from ?? today.AddDays(-13);
        var end = to ?? DateTime.UtcNow.AddMinutes(1);

        var commits = db.CommitMetadata.AsNoTracking().Where(c => ids.Contains(c.RepositoryId));
        if (!string.IsNullOrWhiteSpace(user)) commits = commits.Where(c => c.InitiatingUser == user || c.AuthorEmail == user || c.AgentId == user);
        var perDay = await commits.Where(c => c.CommittedAt >= start && c.CommittedAt < end).Select(c => c.CommittedAt).ToListAsync(ct);
        var ops = db.ChangeOperations.AsNoTracking().Where(o => ids.Contains(o.RepositoryId) && o.CreatedAt >= start && o.CreatedAt < end);
        var lastVerified = await db.RepositoryBackups.AsNoTracking().Where(b => b.RepositoryId != null && ids.Contains(b.RepositoryId.Value) && b.State == BackupState.Verified).MaxAsync(b => (DateTime?)b.VerifiedAt, ct);
        var verifiedRepos = await db.RepositoryBackups.AsNoTracking().Where(b => b.RepositoryId != null && ids.Contains(b.RepositoryId.Value) && b.State == BackupState.Verified).Select(b => b.RepositoryId).Distinct().CountAsync(ct);
        var alerts = await db.MonitoringAlerts.AsNoTracking().Where(a => a.ResolvedAt == null && (a.RepositoryId == null || ids.Contains(a.RepositoryId.Value)))
            .OrderByDescending(a => a.Severity).ThenByDescending(a => a.LastSeenAt).Take(10).ToListAsync(ct);
        var names = await db.Repositories.AsNoTracking().Where(r => ids.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Name, ct);
        var activeRepos = await repos.CountAsync(r => r.State == RepositoryState.Active, ct);

        return new DashboardSummaryDto
        {
            TotalRepositories = ids.Count,
            ActiveRepositories = activeRepos,
            CommitsToday = await commits.CountAsync(c => c.CommittedAt >= today, ct),
            PendingPushes = await db.PushQueue.CountAsync(p => ids.Contains(p.RepositoryId) && (p.State == PushJobState.Pending || p.State == PushJobState.RetryScheduled || p.State == PushJobState.Processing), ct),
            FailedPushes = await db.PushQueue.CountAsync(p => ids.Contains(p.RepositoryId) && (p.State == PushJobState.PermanentFailure || p.State == PushJobState.AuthenticationFailed || p.State == PushJobState.Conflict), ct),
            RepositoriesWithLocalOnlyChanges = await repos.CountAsync(r => r.HasUncommittedChanges || db.Checkpoints.Any(c => c.RepositoryId == r.Id && (c.SyncState == DurabilityState.LocalCommit || c.SyncState == DurabilityState.RemotePushPending || c.SyncState == DurabilityState.Failed)), ct),
            RepositoriesWithRemoteDivergence = await repos.CountAsync(r => r.RemoteStatus == RemoteStatus.Diverged, ct),
            LastVerifiedBackupAt = lastVerified,
            RepositoriesWithoutVerifiedBackup = Math.Max(0, activeRepos - verifiedRepos),
            StorageBytes = await repos.SumAsync(r => r.StorageBytes, ct),
            HighRiskOperations = await ops.CountAsync(o => o.RiskLevel == RiskLevel.High, ct),
            BlockedHighRiskOperations = await ops.CountAsync(o => o.State == ChangeOperationState.PolicyBlocked, ct),
            ActiveAISessions = await db.AISessions.CountAsync(s => ids.Contains(s.RepositoryId) && s.State == SessionState.Active, ct),
            OpenAlerts = await db.MonitoringAlerts.CountAsync(a => a.ResolvedAt == null && (a.RepositoryId == null || ids.Contains(a.RepositoryId.Value)), ct),
            CommitsPerDay = Enumerable.Range(0, Math.Max(1, (int)Math.Ceiling((end - start).TotalDays)))
                .Select(i => DateOnly.FromDateTime(start.Date.AddDays(i)))
                .Select(d => new DailyCountDto(d, perDay.Count(c => DateOnly.FromDateTime(c) == d))).ToList(),
            RecentAlerts = alerts.Select(a => ToDto(a, a.RepositoryId is { } rid ? names.GetValueOrDefault(rid) : null)).ToList()
        };
    }

    public async Task<IReadOnlyList<AlertDto>> AlertsAsync(bool includeResolved, Guid? repositoryId, CancellationToken ct)
    {
        var ids = await (await access.VisibleRepositoriesAsync(ct)).Select(r => r.Id).ToListAsync(ct);
        var q = db.MonitoringAlerts.AsNoTracking().Where(a => (a.RepositoryId == null && access.Caller.IsSystemAdministrator) || (a.RepositoryId != null && ids.Contains(a.RepositoryId.Value)));
        if (!includeResolved) q = q.Where(a => a.ResolvedAt == null);
        if (repositoryId is not null) q = q.Where(a => a.RepositoryId == repositoryId);
        var list = await q.OrderByDescending(a => a.LastSeenAt).Take(500).ToListAsync(ct);
        var names = await db.Repositories.AsNoTracking().Where(r => ids.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Name, ct);
        return list.Select(a => ToDto(a, a.RepositoryId is { } rid ? names.GetValueOrDefault(rid) : null)).ToList();
    }

    public async Task ResolveAlertAsync(Guid id, CancellationToken ct)
    {
        var alert = await db.MonitoringAlerts.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw new NotFoundException("Alert");
        if (alert.RepositoryId is { } rid) await access.RequireRepositoryAsync(rid, RepoAction.Administer, ct);
        else access.RequireSystemAdministrator();
        alert.ResolvedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<SystemHealthDto> HealthAsync(CancellationToken ct)
    {
        var components = new List<HealthComponentDto>();
        var sw = Stopwatch.StartNew();
        try { await db.Database.ExecuteSqlRawAsync("SELECT 1", ct); components.Add(new("database", "Healthy", db.Database.ProviderName, sw.Elapsed.TotalMilliseconds)); }
        catch (Exception ex) { components.Add(new("database", "Unhealthy", ex.Message, sw.Elapsed.TotalMilliseconds)); }
        sw.Restart();
        try
        {
            var v = await ctx.Git.RunAsync(Path.GetFullPath(ctx.Options.DataDirectory), ["version"], null, ct);
            components.Add(new("git", "Healthy", v.StdOut.Trim(), sw.Elapsed.TotalMilliseconds));
        }
        catch (Exception ex) { components.Add(new("git", "Unhealthy", ex.Message, sw.Elapsed.TotalMilliseconds)); }
        foreach (var p in providers.All) components.Add(await p.CheckHealthAsync(ct));
        components.Add(await backupStore.CheckHealthAsync(ct));
        var queue = await db.PushQueue.CountAsync(p => p.State == PushJobState.PermanentFailure || p.State == PushJobState.AuthenticationFailed, ct);
        components.Add(new("sync-queue", queue == 0 ? "Healthy" : "Degraded", $"{queue} failed push job(s)", null));
        var stuckJobs = await db.BackgroundJobs.CountAsync(j => j.State == JobState.Running && j.LockedUntil < DateTime.UtcNow, ct);
        components.Add(new("jobs", stuckJobs == 0 ? "Healthy" : "Degraded", $"{stuckJobs} stalled job(s)", null));
        foreach (var v in Volumes()) components.Add(new($"volume:{v.Purpose}", v.Status == "Healthy" ? "Healthy" : v.Status == "Warning" ? "Degraded" : "Unhealthy", $"{v.Path} {v.UsedPercent}% used", null));
        if (backupOptions.Value.RequireEncryption && string.IsNullOrWhiteSpace(backupOptions.Value.EncryptionKey))
            components.Add(new("backup-encryption", "Unhealthy", "Backup:EncryptionKey is not configured", null));

        var overall = components.Any(c => c.Status is "Unhealthy" or "Unreachable" or "Unauthorized") ? "Unhealthy"
            : components.Any(c => c.Status is "Degraded") ? "Degraded" : "Healthy";
        return new SystemHealthDto { Status = overall, Components = components, CheckedAt = DateTime.UtcNow };
    }

    public async Task<StorageReportDto> StorageAsync(CancellationToken ct)
    {
        var visible = await access.VisibleRepositoriesAsync(ct);
        var repos = await visible.AsNoTracking().OrderByDescending(r => r.StorageBytes).Take(200).ToListAsync(ct);
        var list = new List<RepositoryStorageDto>();
        foreach (var r in repos)
        {
            var wd = await ctx.MainWorkingDirectoryAsync(r.Id, ct);
            long wdBytes = 0;
            if (wd is not null && Directory.Exists(wd.Path))
                try { wdBytes = new DirectoryInfo(wd.Path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Sum(f => f.Length); } catch { }
            list.Add(new RepositoryStorageDto(r.Id, r.Name, wdBytes, r.StorageBytes));
        }
        return new StorageReportDto { Volumes = access.Caller.IsSystemAdministrator ? Volumes() : [], Repositories = list };
    }

    public static AlertDto ToDto(MonitoringAlert a, string? repoName) => new()
    {
        Id = a.Id, RepositoryId = a.RepositoryId, RepositoryName = repoName, Category = a.Category, Severity = a.Severity, Message = a.Message,
        CreatedAt = a.CreatedAt, LastSeenAt = a.LastSeenAt, ResolvedAt = a.ResolvedAt
    };
}
