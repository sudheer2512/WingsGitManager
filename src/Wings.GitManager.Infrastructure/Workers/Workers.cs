using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wings.GitManager.Contracts;
using Wings.GitManager.Core;
using Wings.GitManager.Core.Commits;
using Wings.GitManager.Core.Entities;
using Wings.GitManager.Infrastructure.Backup;
using Wings.GitManager.Infrastructure.Data;
using Wings.GitManager.Infrastructure.Services;

namespace Wings.GitManager.Infrastructure.Workers;

/// <summary>Base loop: runs <see cref="TickAsync"/> in a fresh scope with the system identity; never dies on errors.</summary>
public abstract class LoopWorker(IServiceScopeFactory scopes, ILogger logger) : BackgroundService
{
    protected abstract TimeSpan Interval { get; }
    protected virtual TimeSpan InitialDelay => TimeSpan.FromSeconds(2);
    protected abstract Task TickAsync(IServiceProvider services, CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(InitialDelay, stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await TickAsync(scope.ServiceProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "{Worker} iteration failed", GetType().Name);
            }
            try { await Task.Delay(Interval, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
}

public sealed class SyncQueueWorker(IServiceScopeFactory scopes, IOptions<SyncOptions> o, ILogger<SyncQueueWorker> logger) : LoopWorker(scopes, logger)
{
    protected override TimeSpan Interval => TimeSpan.FromSeconds(o.Value.PollSeconds);
    protected override Task TickAsync(IServiceProvider sp, CancellationToken ct) => sp.GetRequiredService<SyncService>().ProcessDueAsync(20, ct);
}

public sealed class JobWorker(IServiceScopeFactory scopes, IOptions<WorkerOptions> w, ILogger<JobWorker> logger) : LoopWorker(scopes, logger)
{
    protected override TimeSpan Interval => TimeSpan.FromSeconds(2);

    protected override async Task TickAsync(IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<GitManagerDbContext>();
        var owner = $"{w.Value.InstanceName}:{Environment.ProcessId}";
        for (var i = 0; i < 5; i++)
        {
            var now = DateTime.UtcNow;
            var job = await db.BackgroundJobs.Where(j => j.State == JobState.Queued && j.NotBefore <= now || j.State == JobState.Running && j.LockedUntil < now)
                .OrderBy(j => j.CreatedAt).FirstOrDefaultAsync(ct);
            if (job is null) return;
            var claimed = await db.BackgroundJobs.Where(j => j.Id == job.Id && (j.State == JobState.Queued || j.State == JobState.Running && j.LockedUntil < now))
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.State, JobState.Running).SetProperty(j => j.LockedBy, owner)
                    .SetProperty(j => j.LockedUntil, now.AddHours(2)).SetProperty(j => j.StartedAt, now).SetProperty(j => j.Attempts, j => j.Attempts + 1), ct);
            if (claimed == 0) continue;
            await RunAsync(job.Id, ct);
        }
    }

    private async Task RunAsync(Guid jobId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GitManagerDbContext>();
        var job = await db.BackgroundJobs.FirstAsync(j => j.Id == jobId, ct);
        var handler = scope.ServiceProvider.GetServices<IJobHandler>().FirstOrDefault(h => h.Type == job.Type);
        try
        {
            if (handler is null) throw new InvalidOperationException($"No handler for job type {job.Type}");
            job.ResultJson = await handler.RunAsync(job, ct);
            job.State = JobState.Succeeded;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Job {JobId} ({Type}) failed", job.Id, job.Type);
            db.ChangeTracker.Clear();
            job = await db.BackgroundJobs.FirstAsync(j => j.Id == jobId, ct);
            job.Error = ex.Message;
            var retryable = ex is not GitManagerException { StatusCode: < 500 } && job.Attempts < job.MaxAttempts && job.Type is JobTypes.BackupCreate or JobTypes.BackupVerify;
            job.State = retryable ? JobState.Queued : JobState.Failed;
            job.NotBefore = DateTime.UtcNow.AddMinutes(Math.Pow(2, job.Attempts));
        }
        job.CompletedAt = job.State is JobState.Succeeded or JobState.Failed ? DateTime.UtcNow : null;
        job.LockedBy = null;
        job.LockedUntil = null;
        db.NotificationEvents.Add(new NotificationEvent { Type = "job." + job.State.ToString().ToLowerInvariant(), RepositoryId = job.RepositoryId, PayloadJson = Json.Serialize(new { job.Id, job.Type, job.Error }) });
        await db.SaveChangesAsync(CancellationToken.None);
    }
}

/// <summary>Schedules repository backups, periodic re-verification with test restores and metadata backups.</summary>
public sealed class BackupScheduler(IServiceScopeFactory scopes, IOptions<BackupOptions> o, ILogger<BackupScheduler> logger) : LoopWorker(scopes, logger)
{
    protected override TimeSpan Interval => TimeSpan.FromMinutes(Math.Max(1, o.Value.ScheduleCheckMinutes));
    protected override TimeSpan InitialDelay => TimeSpan.FromSeconds(30);

    protected override async Task TickAsync(IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<GitManagerDbContext>();
        var backups = sp.GetRequiredService<BackupService>();
        var jobs = sp.GetRequiredService<JobService>();
        var now = DateTime.UtcNow;
        var repos = await db.Repositories.Include(r => r.Settings).Include(r => r.Remotes).Include(r => r.Company)
            .Where(r => r.State == RepositoryState.Active || r.State == RepositoryState.Archived).ToListAsync(ct);
        foreach (var repo in repos)
        {
            var last = await db.RepositoryBackups.Where(b => b.RepositoryId == repo.Id && b.State != BackupState.Failed && b.State != BackupState.Expired)
                .OrderByDescending(b => b.CreatedAt).FirstOrDefaultAsync(ct);
            var interval = TimeSpan.FromHours(repo.Settings?.BackupIntervalHours ?? 24);
            if (last is null || last.State is not (BackupState.Pending or BackupState.Running) && now - last.CreatedAt > interval)
            {
                await backups.QueueAsync(repo, "scheduled", ct);
                continue;
            }
            // Re-verify (with a test restore) the latest backup periodically.
            if (last.State is BackupState.Verified or BackupState.Completed && (last.VerifiedAt is null || now - last.VerifiedAt > TimeSpan.FromHours(o.Value.VerifyEveryHours)))
            {
                var queued = await db.BackgroundJobs.AnyAsync(j => j.Type == JobTypes.BackupVerify && j.State != JobState.Succeeded && j.State != JobState.Failed && j.RepositoryId == repo.Id, ct);
                if (!queued) await jobs.EnqueueAsync(JobTypes.BackupVerify, repo.Id, new BackupJobPayload(last.Id, TestRestore: true), "scheduler", ct);
            }
        }
        var lastMeta = await db.RepositoryBackups.Where(b => b.RepositoryId == null && b.Kind == "metadata").OrderByDescending(b => b.CreatedAt).FirstOrDefaultAsync(ct);
        if (lastMeta is null || now - lastMeta.CreatedAt > TimeSpan.FromHours(o.Value.MetadataBackupIntervalHours))
        {
            var meta = new RepositoryBackup { Kind = "metadata", StorageProvider = sp.GetRequiredService<IBackupStore>().Name, Trigger = "scheduled" };
            db.RepositoryBackups.Add(meta);
            await db.SaveChangesAsync(ct);
            await jobs.EnqueueAsync(JobTypes.MetadataBackup, null, new BackupJobPayload(meta.Id), "scheduler", ct);
        }
    }
}

public sealed class MonitoringWorker(IServiceScopeFactory scopes, IOptions<MonitoringOptions> o, ILogger<MonitoringWorker> logger) : LoopWorker(scopes, logger)
{
    private bool _started;
    private DateTime _lastCleanup = DateTime.MinValue;
    protected override TimeSpan Interval => TimeSpan.FromSeconds(Math.Max(10, o.Value.IntervalSeconds));
    protected override TimeSpan InitialDelay => TimeSpan.FromSeconds(5);

    protected override async Task TickAsync(IServiceProvider sp, CancellationToken ct)
    {
        await sp.GetRequiredService<ChangeOperationService>().ReconcileAsync(startup: !_started, ct);
        _started = true;
        await sp.GetRequiredService<MonitoringService>().RunChecksAsync(ct);
        if (DateTime.UtcNow - _lastCleanup > TimeSpan.FromHours(1))
        {
            await sp.GetRequiredService<SessionService>().CleanupAsync(ct);
            var db = sp.GetRequiredService<GitManagerDbContext>();
            await db.IdempotencyRecords.Where(r => r.CreatedAt < DateTime.UtcNow.AddDays(-2)).ExecuteDeleteAsync(ct);
            await db.NotificationEvents.Where(n => n.CreatedAt < DateTime.UtcNow.AddDays(-2)).ExecuteDeleteAsync(ct);
            await db.RecoveryOperations.Where(r => r.Status == RecoveryStatus.Previewed && r.ExpiresAt < DateTime.UtcNow)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RecoveryStatus.Expired), ct);
            _lastCleanup = DateTime.UtcNow;
        }
    }
}

/// <summary>
/// Watches main working directories for edits made outside Wings Code (debounced). It only reconciles with
/// git status and flags/optionally checkpoints the changes; the explicit change-operation API stays primary.
/// </summary>
public sealed class FileWatcherService(IServiceScopeFactory scopes, IOptions<MonitoringOptions> o, ILogger<FileWatcherService> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, (Guid RepoId, FileSystemWatcher Watcher)> _watchers = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _dirty = new();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RefreshWatchersAsync(ct);
                await ReconcileAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "File watcher iteration failed"); }
            try { await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(500, o.Value.FileWatchDebounceMilliseconds / 2)), ct); } catch (OperationCanceledException) { break; }
        }
        foreach (var (_, w) in _watchers) w.Watcher.Dispose();
    }

    private DateTime _lastRefresh = DateTime.MinValue;

    private async Task RefreshWatchersAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _lastRefresh < TimeSpan.FromSeconds(30)) return;
        _lastRefresh = DateTime.UtcNow;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GitManagerDbContext>();
        var dirs = await db.WorkingDirectories.AsNoTracking()
            .Where(w => w.IsActive && w.Kind == WorkingDirectoryKind.Main)
            .Join(db.RepositorySettings.Where(s => s.WatchFileSystem), w => w.RepositoryId, s => s.RepositoryId, (w, s) => new { w.Path, w.RepositoryId })
            .ToListAsync(ct);
        var wanted = dirs.Where(d => Directory.Exists(d.Path)).ToDictionary(d => d.Path, d => d.RepositoryId);
        foreach (var path in _watchers.Keys.Except(wanted.Keys).ToList())
            if (_watchers.TryRemove(path, out var old)) old.Watcher.Dispose();
        foreach (var (path, repoId) in wanted)
        {
            if (_watchers.ContainsKey(path)) continue;
            try
            {
                var w = new FileSystemWatcher(path) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
                FileSystemEventHandler h = (_, e) => OnEvent(repoId, path, e.FullPath);
                w.Changed += h; w.Created += h; w.Deleted += h;
                w.Renamed += (_, e) => OnEvent(repoId, path, e.FullPath);
                w.Error += (_, e) => logger.LogWarning(e.GetException(), "Watcher error for {Path}", path);
                w.EnableRaisingEvents = true;
                _watchers[path] = (repoId, w);
            }
            catch (Exception ex) { logger.LogWarning(ex, "Cannot watch {Path}", path); }
        }
    }

    private void OnEvent(Guid repoId, string root, string fullPath)
    {
        var rel = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        if (rel == ".git" || rel.StartsWith(".git/")) return;
        _dirty[repoId] = DateTime.UtcNow;
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        var debounce = TimeSpan.FromMilliseconds(o.Value.FileWatchDebounceMilliseconds);
        foreach (var (repoId, at) in _dirty.ToArray())
        {
            if (DateTime.UtcNow - at < debounce) continue;
            _dirty.TryRemove(repoId, out _);
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GitManagerDbContext>();
            var ctx = scope.ServiceProvider.GetRequiredService<RepositoryContext>();
            // Edits made inside an active change operation belong to that operation.
            if (await db.ChangeOperations.AnyAsync(op => op.RepositoryId == repoId && (op.State == ChangeOperationState.Active || op.State == ChangeOperationState.Committing), ct)) continue;
            var repo = await db.Repositories.Include(r => r.Settings).Include(r => r.Remotes).Include(r => r.Company).FirstOrDefaultAsync(r => r.Id == repoId && r.State == RepositoryState.Active, ct);
            if (repo is null) continue;
            var wd = await ctx.MainWorkingDirectoryAsync(repoId, ct);
            if (wd is null || !await ctx.Git.IsRepositoryAsync(wd.Path, ct)) continue;
            var status = await ctx.Git.StatusAsync(wd.Path, ct);
            var dirty = status.Count > 0;
            if (dirty == repo.HasUncommittedChanges && !dirty) continue;
            repo.HasUncommittedChanges = dirty;
            if (dirty) repo.ExternalChangeDetectedAt ??= DateTime.UtcNow; else repo.ExternalChangeDetectedAt = null;
            scope.ServiceProvider.GetRequiredService<NotificationPublisher>().Publish("repository.external-change", repo.CompanyId, repo.Id, new { files = status.Count });
            await db.SaveChangesAsync(ct);
            if (dirty && repo.Settings?.AutoCommitExternalChanges == true)
                await AutoCommitAsync(scope.ServiceProvider, repo, wd.Path, ct);
        }
    }

    private async Task AutoCommitAsync(IServiceProvider sp, Repository repo, string dir, CancellationToken ct)
    {
        var ctx = sp.GetRequiredService<RepositoryContext>();
        var locks = sp.GetRequiredService<LockService>();
        await using var _ = await locks.AcquireAsync(LockService.KeyForPath(dir), TimeSpan.FromSeconds(10), ct);
        var status = await ctx.Git.StatusAsync(dir, ct);
        var paths = status.Where(s => RepositoryContext.IsPermittedPath(s.Path, repo.Settings)).Select(s => s.Path).ToList();
        if (paths.Count == 0) return;
        foreach (var chunk in paths.Chunk(200))
            await ctx.Git.RunAsync(dir, ["add", "-A", "--", .. chunk.Select(Core.Safety.RepoPath.Literal)], null, ct);
        var diff = await ctx.Git.RunAsync(dir, ["diff", "--cached", "-U0", "--no-ext-diff", "--no-textconv"], null, ct);
        var findings = Core.Safety.SecretScanner.ScanUnifiedDiff(diff.StdOut);
        if (findings.Count > 0 && repo.Settings?.SecretScanMode == SecretScanMode.Block)
        {
            await ctx.Git.RunAsync(dir, ["reset", "-q"], null, ct);
            logger.LogWarning("External changes in {Repo} contain possible secrets; not auto-committed", repo.Id);
            return;
        }
        var files = await ctx.Git.DiffFilesAsync(dir, null, null, true, null, ct);
        var message = CommitMessageBuilder.Build(new CommitContext { Summary = "record changes made outside Wings Code", CommitType = "chore", Files = files });
        var sha = await ctx.Git.CommitAsync(dir, message, ctx.ServiceIdentity, ctx.ServiceIdentity, false, ct);
        var branch = await ctx.Git.CurrentBranchAsync(dir, ct) ?? repo.DefaultBranch;
        await sp.GetRequiredService<CommitRecorder>().RecordAsync(repo, dir, branch, sha, null, ct);
        await sp.GetRequiredService<CheckpointService>().CreateAsync(repo, dir, branch, sha, CheckpointType.External, "External changes detected by file watcher", new CommitAttribution(), ct);
        repo.HasUncommittedChanges = false;
        repo.ExternalChangeDetectedAt = null;
        var db = sp.GetRequiredService<GitManagerDbContext>();
        await db.SaveChangesAsync(ct);
        await sp.GetRequiredService<SyncService>().EnqueueAsync(repo, dir, branch, sha, null, ct);
    }
}
