using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wings.GitManager.Core;
using Wings.GitManager.Core.Entities;
using Wings.GitManager.Infrastructure.Data;

namespace Wings.GitManager.Infrastructure.Services;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T? Deserialize<T>(string? json) => string.IsNullOrEmpty(json) ? default : JsonSerializer.Deserialize<T>(json, Options);
}

/// <summary>Identity used by background workers.</summary>
public sealed class SystemCallerContext : ICallerContext
{
    public static readonly SystemCallerContext Instance = new();
    public Guid? UserId => null;
    public string Name => "wings-git-manager";
    public string ActorType => "system";
    public bool IsSystemAdministrator => true;
    public bool IsServiceAccount => true;
    public IReadOnlyCollection<string> Scopes => Core.Scopes.All;
    public string? CorrelationId => null;
    public string? IpAddress => null;
}

public sealed class AuditService(GitManagerDbContext db, ICallerContext caller, ILogger<AuditService> logger)
{
    /// <summary>Adds an audit row to the current unit of work (saved with the caller's SaveChanges).</summary>
    public void Record(string action, Guid? repositoryId = null, Guid? companyId = null, string? target = null, string outcome = "success", object? details = null)
    {
        db.AuditLogs.Add(new AuditLog
        {
            Action = action,
            RepositoryId = repositoryId,
            CompanyId = companyId,
            Target = target,
            Outcome = outcome,
            Details = details is null ? null : details as string ?? Json.Serialize(details),
            UserId = caller.UserId?.ToString(),
            Actor = caller.Name,
            ActorType = caller.ActorType,
            CorrelationId = caller.CorrelationId,
            IpAddress = caller.IpAddress,
        });
        logger.LogInformation("Audit {Action} {Outcome} repo={RepositoryId} target={Target} by {Actor}", action, outcome, repositoryId, target, caller.Name);
    }

    /// <summary>Writes an audit row immediately (used for denials and failures that roll back other work).</summary>
    public async Task RecordNowAsync(string action, Guid? repositoryId, Guid? companyId, string? target, string outcome, object? details, CancellationToken ct)
    {
        Record(action, repositoryId, companyId, target, outcome, details);
        await db.SaveChangesAsync(ct);
    }
}

public sealed class NotificationPublisher(GitManagerDbContext db)
{
    public void Publish(string type, Guid? companyId, Guid? repositoryId, object payload) =>
        db.NotificationEvents.Add(new NotificationEvent
        {
            Type = type, CompanyId = companyId, RepositoryId = repositoryId, PayloadJson = Json.Serialize(payload)
        });
}

/// <summary>Encrypts remote credentials and secret settings with ASP.NET Core Data Protection.</summary>
public sealed class SecretProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("Wings.GitManager.Secrets.v1");
    public string Protect(string plaintext) => _protector.Protect(plaintext);
    public string Unprotect(string ciphertext) => _protector.Unprotect(ciphertext);
}

/// <summary>
/// Short-lived database-backed mutation locks shared by the API and worker processes, so two processes never
/// mutate the same working directory or repository at once.
/// </summary>
public sealed class LockService(IDbContextFactory<GitManagerDbContext> dbFactory, IOptions<WorkerOptions> worker, ILogger<LockService> logger)
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> Local = new();

    public static string KeyForPath(string path) =>
        "wd:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToLowerInvariant())))[..40];

    public static string KeyForRepository(Guid repositoryId) => "repo:" + repositoryId.ToString("N");

    public async Task<IAsyncDisposable> AcquireAsync(string key, TimeSpan wait, CancellationToken ct, TimeSpan? hold = null)
    {
        var holdFor = hold ?? TimeSpan.FromMinutes(10);
        var local = Local.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        if (!await local.WaitAsync(wait, ct))
            throw new ConflictException(ErrorCodes.LockTimeout, "Another Git operation is in progress for this workspace. Retry shortly.");

        var owner = $"{worker.Value.InstanceName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
        var deadline = DateTime.UtcNow + wait;
        try
        {
            while (true)
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                var now = DateTime.UtcNow;
                await db.MutationLocks.Where(l => l.LockKey == key && l.ExpiresAt < now).ExecuteDeleteAsync(ct);
                try
                {
                    db.MutationLocks.Add(new MutationLock { LockKey = key, Owner = owner, ExpiresAt = now + holdFor });
                    await db.SaveChangesAsync(ct);
                    return new Releaser(this, key, owner, local);
                }
                catch (DbUpdateException)
                {
                    if (DateTime.UtcNow > deadline)
                        throw new ConflictException(ErrorCodes.LockTimeout, "Another process holds the Git lock for this workspace. Retry shortly.");
                    await Task.Delay(200, ct);
                }
            }
        }
        catch
        {
            local.Release();
            throw;
        }
    }

    private async Task ReleaseAsync(string key, string owner)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            await db.MutationLocks.Where(l => l.LockKey == key && l.Owner == owner).ExecuteDeleteAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to release lock {Key}; it expires automatically", key);
        }
    }

    private sealed class Releaser(LockService svc, string key, string owner, SemaphoreSlim local) : IAsyncDisposable
    {
        private int _disposed;
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try { await svc.ReleaseAsync(key, owner); }
            finally { local.Release(); }
        }
    }
}
