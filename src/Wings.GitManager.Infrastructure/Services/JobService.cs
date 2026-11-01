using Microsoft.EntityFrameworkCore;
using Wings.GitManager.Contracts;
using Wings.GitManager.Core;
using Wings.GitManager.Core.Entities;
using Wings.GitManager.Infrastructure.Data;

namespace Wings.GitManager.Infrastructure.Services;

public static class JobTypes
{
    public const string RecoveryClone = "recovery.clone";
    public const string BackupCreate = "backup.create";
    public const string BackupVerify = "backup.verify";
    public const string BackupRestore = "backup.restore";
    public const string MetadataBackup = "backup.metadata";
}

/// <summary>Persistent background jobs for long-running operations (state survives restarts).</summary>
public sealed class JobService(GitManagerDbContext db, AccessControl access)
{
    public async Task<BackgroundJob> EnqueueAsync(string type, Guid? repositoryId, object payload, string? createdBy, CancellationToken ct)
    {
        var job = new BackgroundJob { Type = type, RepositoryId = repositoryId, PayloadJson = Json.Serialize(payload), CreatedBy = createdBy };
        db.BackgroundJobs.Add(job);
        db.NotificationEvents.Add(new NotificationEvent { Type = "job.queued", RepositoryId = repositoryId, PayloadJson = Json.Serialize(new { job.Id, type }) });
        await db.SaveChangesAsync(ct);
        return job;
    }

    public async Task<JobDto> GetAsync(Guid id, CancellationToken ct)
    {
        var job = await db.BackgroundJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct) ?? throw new NotFoundException("Job");
        if (job.RepositoryId is { } rid) await access.RequireRepositoryAsync(rid, RepoAction.Read, ct, track: false);
        else access.RequireSystemAdministrator();
        return ToDto(job);
    }

    public async Task<IReadOnlyList<JobDto>> ListAsync(Guid? repositoryId, JobState? state, int take, CancellationToken ct)
    {
        if (repositoryId is { } rid) await access.RequireRepositoryAsync(rid, RepoAction.Read, ct, track: false);
        else access.RequireSystemAdministrator();
        var q = db.BackgroundJobs.AsNoTracking().AsQueryable();
        if (repositoryId is not null) q = q.Where(j => j.RepositoryId == repositoryId);
        if (state is not null) q = q.Where(j => j.State == state);
        return (await q.OrderByDescending(j => j.CreatedAt).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct)).Select(ToDto).ToList();
    }

    public static JobDto ToDto(BackgroundJob j) => new()
    {
        Id = j.Id, Type = j.Type, State = j.State, RepositoryId = j.RepositoryId, Result = j.ResultJson, Error = j.Error, Attempts = j.Attempts,
        Progress = j.Progress, CreatedAt = j.CreatedAt, StartedAt = j.StartedAt, CompletedAt = j.CompletedAt
    };
}

/// <summary>Executes one job type. Handlers are resolved per job inside a fresh DI scope.</summary>
public interface IJobHandler
{
    string Type { get; }
    Task<string?> RunAsync(BackgroundJob job, CancellationToken ct);
}

public sealed class RecoveryCloneJobHandler(RecoveryService recovery) : IJobHandler
{
    public string Type => JobTypes.RecoveryClone;
    public Task<string?> RunAsync(BackgroundJob job, CancellationToken ct) =>
        recovery.RunCloneJobAsync(Json.Deserialize<RecoveryJobPayload>(job.PayloadJson)!, ct)!;
}
