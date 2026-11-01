using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wings.GitManager.Core;
using Wings.GitManager.Core.Entities;
using Wings.GitManager.Core.Policies;
using Wings.GitManager.Infrastructure.Backup;
using Wings.GitManager.Infrastructure.Data;
using Wings.GitManager.Infrastructure.Git;
using Wings.GitManager.Infrastructure.Hosting;
using Wings.GitManager.Infrastructure.Services;
using Wings.GitManager.Infrastructure.Workers;

namespace Wings.GitManager.Infrastructure;

public static class ClaimNames
{
    public const string UserId = "wgm:uid";
    public const string SystemAdmin = "wgm:sysadmin";
    public const string ServiceAccount = "wgm:service";
    public const string Scope = "wgm:scope";
}

/// <summary>Caller derived from the authenticated principal (API requests).</summary>
public sealed class HttpCallerContext(ClaimsPrincipal user, HttpContext http) : ICallerContext
{
    public Guid? UserId => Guid.TryParse(user.FindFirstValue(ClaimNames.UserId), out var id) ? id : null;
    public string Name => user.FindFirstValue(ClaimTypes.Email) ?? user.Identity?.Name ?? "unknown";
    public string ActorType => IsServiceAccount ? "service" : "user";
    public bool IsSystemAdministrator => user.HasClaim(ClaimNames.SystemAdmin, "true");
    public bool IsServiceAccount => user.HasClaim(ClaimNames.ServiceAccount, "true");
    public IReadOnlyCollection<string> Scopes => user.FindAll(ClaimNames.Scope).Select(c => c.Value).ToList();
    public string? CorrelationId => http.Items["CorrelationId"] as string ?? http.TraceIdentifier;
    public string? IpAddress => http.Connection.RemoteIpAddress?.ToString();
}

/// <summary>Unauthenticated request: no identity, no scopes (every protected call is denied).</summary>
public sealed class AnonymousCallerContext : ICallerContext
{
    public static readonly AnonymousCallerContext Instance = new();
    public Guid? UserId => null;
    public string Name => "anonymous";
    public string ActorType => "anonymous";
    public bool IsSystemAdministrator => false;
    public bool IsServiceAccount => false;
    public IReadOnlyCollection<string> Scopes => [];
    public string? CorrelationId => null;
    public string? IpAddress => null;
}

public static class DependencyInjection
{
    public static IServiceCollection AddGitManagerInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<GitManagerOptions>(config.GetSection(GitManagerOptions.Section));
        services.Configure<GiteaOptions>(config.GetSection(GiteaOptions.Section));
        services.Configure<FileSystemRemoteOptions>(config.GetSection(FileSystemRemoteOptions.Section));
        services.Configure<SyncOptions>(config.GetSection(SyncOptions.Section));
        services.Configure<BackupOptions>(config.GetSection(BackupOptions.Section));
        services.Configure<MonitoringOptions>(config.GetSection(MonitoringOptions.Section));
        services.Configure<WorkerOptions>(config.GetSection(WorkerOptions.Section));

        var provider = config["Database:Provider"] ?? "Sqlite";
        var connection = config.GetConnectionString("GitManager") ?? "Data Source=App_Data/git-manager.db";
        void ConfigureDb(DbContextOptionsBuilder o)
        {
            if (provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
                o.UseSqlServer(connection, s => s.EnableRetryOnFailure(3).CommandTimeout(60));
            else
                o.UseSqlite(connection, s => s.CommandTimeout(60));
        }
        services.AddDbContextFactory<GitManagerDbContext>(ConfigureDb, ServiceLifetime.Scoped);
        services.AddDbContext<GitManagerDbContext>(ConfigureDb, ServiceLifetime.Scoped, ServiceLifetime.Scoped);

        var dataDir = Path.GetFullPath(config[$"{GitManagerOptions.Section}:DataDirectory"] ?? "App_Data/git-manager");
        services.AddDataProtection().SetApplicationName("Wings.GitManager").PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")));

        services.AddHttpContextAccessor();
        services.AddScoped<ICallerContext>(sp =>
        {
            var http = sp.GetService<IHttpContextAccessor>()?.HttpContext;
            if (http is null) return SystemCallerContext.Instance;
            return http.User.Identity?.IsAuthenticated == true ? new HttpCallerContext(http.User, http) : AnonymousCallerContext.Instance;
        });

        services.AddSingleton<IGitExecutor, GitExecutor>();
        services.AddSingleton<GitClient>();
        services.AddSingleton<WorkspacePaths>();
        services.AddHttpClient(GiteaHostingProvider.HttpClientName, (sp, c) =>
        {
            var o = sp.GetRequiredService<IOptions<GiteaOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(o.BaseUrl)) c.BaseAddress = new Uri(o.BaseUrl.TrimEnd('/') + "/api/v1/");
            if (!string.IsNullOrWhiteSpace(o.Token)) c.DefaultRequestHeaders.Authorization = new("token", o.Token);
            c.Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds);
        });
        services.AddScoped<IGitHostingProvider>(sp => new GiteaHostingProvider(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(GiteaHostingProvider.HttpClientName),
            sp.GetRequiredService<IOptions<GiteaOptions>>(), sp.GetRequiredService<ILogger<GiteaHostingProvider>>()));
        services.AddScoped<IGitHostingProvider, FileSystemHostingProvider>();
        services.AddScoped<IGitHostingProvider, GenericRemoteProvider>();
        services.AddScoped<IHostingProviderResolver, HostingProviderResolver>();

        var backupProvider = config[$"{BackupOptions.Section}:Provider"] ?? "filesystem";
        if (backupProvider.Equals("s3", StringComparison.OrdinalIgnoreCase)) services.AddSingleton<IBackupStore, S3BackupStore>();
        else services.AddSingleton<IBackupStore, FileSystemBackupStore>();

        services.AddScoped<AuditService>();
        services.AddScoped<NotificationPublisher>();
        services.AddScoped<SecretProtector>();
        services.AddScoped<LockService>();
        services.AddScoped<AccessControl>();
        services.AddScoped<RepositoryContext>();
        services.AddScoped<SyncService>();
        services.AddScoped<CommitRecorder>();
        services.AddScoped<CheckpointService>();
        services.AddScoped<RepositoryService>();
        services.AddScoped<ChangeOperationService>();
        services.AddScoped<WorktreeService>();
        services.AddScoped<SessionService>();
        services.AddScoped<BranchService>();
        services.AddScoped<MirrorService>();
        services.AddScoped<BrowseService>();
        services.AddScoped<JobService>();
        services.AddScoped<RecoveryService>();
        services.AddScoped<ConversationalRecoveryService>();
        services.AddScoped<BackupService>();
        services.AddScoped<MonitoringService>();
        services.AddScoped<AdminService>();
        services.AddScoped<SyncOperations>();
        services.AddScoped<DatabaseInitializer>();

        services.AddScoped<IJobHandler, RecoveryCloneJobHandler>();
        services.AddScoped<IJobHandler, BackupJobHandlers.Create>();
        services.AddScoped<IJobHandler, BackupJobHandlers.Verify>();
        services.AddScoped<IJobHandler, BackupJobHandlers.Restore>();
        services.AddScoped<IJobHandler, BackupJobHandlers.Metadata>();
        return services;
    }

    /// <summary>Background services; the API hosts them when Worker:RunInApi is true, otherwise the Worker service does.</summary>
    public static IServiceCollection AddGitManagerWorkers(this IServiceCollection services, IConfiguration config)
    {
        var o = config.GetSection(WorkerOptions.Section).Get<WorkerOptions>() ?? new WorkerOptions();
        if (o.EnableSyncQueue) services.AddHostedService<SyncQueueWorker>();
        if (o.EnableJobs) services.AddHostedService<JobWorker>();
        if (o.EnableBackups) services.AddHostedService<BackupScheduler>();
        if (o.EnableMonitoring) services.AddHostedService<MonitoringWorker>();
        if (o.EnableFileWatcher) services.AddHostedService<FileWatcherService>();
        return services;
    }
}

/// <summary>Creates the schema (development) and seeds the default policy and the bootstrap administrator.</summary>
public sealed class DatabaseInitializer(GitManagerDbContext db, IConfiguration config, IOptions<GitManagerOptions> options, ILogger<DatabaseInitializer> logger)
{
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (config.GetValue("Database:AutoCreate", true))
        {
            var dbFile = db.Database.IsSqlite() ? new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(db.Database.GetConnectionString()).DataSource : null;
            if (dbFile is not null && Path.GetDirectoryName(Path.GetFullPath(dbFile)) is { } d) Directory.CreateDirectory(d);
            await db.Database.EnsureCreatedAsync(ct);
            if (db.Database.IsSqlite()) await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
        }

        if (!await db.PolicyDefinitions.AnyAsync(p => p.Key == HighRiskPolicySettings.Key && p.Scope == "global", ct))
        {
            db.PolicyDefinitions.Add(new PolicyDefinition { Key = HighRiskPolicySettings.Key, Scope = "global", SettingsJson = Json.Serialize(new HighRiskPolicySettings()), UpdatedBy = "system" });
            await db.SaveChangesAsync(ct);
        }

        if (!await db.Users.AnyAsync(ct))
        {
            var email = config["Bootstrap:AdminEmail"] ?? "admin@wings.local";
            var admin = new User { Email = email, DisplayName = "Wings Git Manager Administrator", IsSystemAdministrator = true };
            db.Users.Add(admin);
            var (secret, _) = await AdminService.IssueTokenAsync(db, admin.Id, "bootstrap", Scopes.All, null, ct);
            await db.SaveChangesAsync(ct);
            var file = config["Bootstrap:TokenFile"] ?? Path.Combine(Path.GetFullPath(options.Value.DataDirectory), "bootstrap-admin-token.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, secret + Environment.NewLine, ct);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            logger.LogWarning("Created bootstrap administrator {Email}; its API token was written to {File}. Store it securely and delete the file.", email, file);
        }
    }
}
