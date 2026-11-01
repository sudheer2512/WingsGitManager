using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Wings.GitManager.Core.Entities;

namespace Wings.GitManager.Infrastructure.Data;

public sealed class GitManagerDbContext(DbContextOptions<GitManagerDbContext> options) : DbContext(options)
{
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<User> Users => Set<User>();
    public DbSet<CompanyMembership> CompanyMemberships => Set<CompanyMembership>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Repository> Repositories => Set<Repository>();
    public DbSet<RepositoryRemote> RepositoryRemotes => Set<RepositoryRemote>();
    public DbSet<RepositorySettings> RepositorySettings => Set<RepositorySettings>();
    public DbSet<RepositoryPermission> RepositoryPermissions => Set<RepositoryPermission>();
    public DbSet<WorkingDirectory> WorkingDirectories => Set<WorkingDirectory>();
    public DbSet<Worktree> Worktrees => Set<Worktree>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<CommitMetadata> CommitMetadata => Set<CommitMetadata>();
    public DbSet<CommitFileChange> CommitFileChanges => Set<CommitFileChange>();
    public DbSet<AISession> AISessions => Set<AISession>();
    public DbSet<AISessionCommit> AISessionCommits => Set<AISessionCommit>();
    public DbSet<ChangeOperation> ChangeOperations => Set<ChangeOperation>();
    public DbSet<Checkpoint> Checkpoints => Set<Checkpoint>();
    public DbSet<PushQueueItem> PushQueue => Set<PushQueueItem>();
    public DbSet<SyncAttempt> SyncAttempts => Set<SyncAttempt>();
    public DbSet<RecoveryOperation> RecoveryOperations => Set<RecoveryOperation>();
    public DbSet<RepositoryBackup> RepositoryBackups => Set<RepositoryBackup>();
    public DbSet<BackupVerificationResult> BackupVerificationResults => Set<BackupVerificationResult>();
    public DbSet<PolicyDefinition> PolicyDefinitions => Set<PolicyDefinition>();
    public DbSet<PolicyOverride> PolicyOverrides => Set<PolicyOverride>();
    public DbSet<BackgroundJob> BackgroundJobs => Set<BackgroundJob>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();
    public DbSet<MutationLock> MutationLocks => Set<MutationLock>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<MonitoringAlert> MonitoringAlerts => Set<MonitoringAlert>();
    public DbSet<NotificationEvent> NotificationEvents => Set<NotificationEvent>();
    public DbSet<MergeRequest> MergeRequests => Set<MergeRequest>();

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        b.Properties<DateTime>().HaveConversion<UtcConverter>();
        b.Properties<DateTime?>().HaveConversion<NullableUtcConverter>();
        b.Properties<string>().HaveMaxLength(450);
        b.Properties<List<string>>().HaveConversion<StringListConverter, StringListComparer>().HaveMaxLength(-1);
    }

    protected override void OnModelCreating(ModelBuilder m)
    {
        // GUID keys are assigned by the application, so entities reached through navigations are inserts, not updates.
        foreach (var entity in m.Model.GetEntityTypes())
            if (entity.FindPrimaryKey()?.Properties is [{ ClrType: var t } key] && t == typeof(Guid))
                key.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;

        // Long text columns.
        m.Entity<CommitMetadata>().Property(x => x.Message).HasMaxLength(-1);
        m.Entity<ChangeOperation>().Property(x => x.IntentJson).HasMaxLength(-1);
        m.Entity<ChangeOperation>().Property(x => x.Description).HasMaxLength(4000);
        m.Entity<ChangeOperation>().Property(x => x.FailureReason).HasMaxLength(4000);
        m.Entity<RecoveryOperation>().Property(x => x.PlanJson).HasMaxLength(-1);
        m.Entity<RecoveryOperation>().Property(x => x.Error).HasMaxLength(4000);
        m.Entity<RepositoryBackup>().Property(x => x.RefsJson).HasMaxLength(-1);
        m.Entity<RepositoryBackup>().Property(x => x.Error).HasMaxLength(4000);
        m.Entity<BackupVerificationResult>().Property(x => x.Details).HasMaxLength(-1);
        m.Entity<PolicyDefinition>().Property(x => x.SettingsJson).HasMaxLength(-1);
        m.Entity<PolicyOverride>().Property(x => x.Details).HasMaxLength(-1);
        m.Entity<BackgroundJob>().Property(x => x.PayloadJson).HasMaxLength(-1);
        m.Entity<BackgroundJob>().Property(x => x.ResultJson).HasMaxLength(-1);
        m.Entity<BackgroundJob>().Property(x => x.Error).HasMaxLength(4000);
        m.Entity<AuditLog>().Property(x => x.Details).HasMaxLength(-1);
        m.Entity<SystemSetting>().Property(x => x.Value).HasMaxLength(-1);
        m.Entity<IdempotencyRecord>().Property(x => x.ResponseBody).HasMaxLength(-1);
        m.Entity<MonitoringAlert>().Property(x => x.Message).HasMaxLength(4000);
        m.Entity<NotificationEvent>().Property(x => x.PayloadJson).HasMaxLength(-1);
        m.Entity<PushQueueItem>().Property(x => x.LastError).HasMaxLength(4000);
        m.Entity<SyncAttempt>().Property(x => x.Error).HasMaxLength(4000);
        m.Entity<RepositoryRemote>().Property(x => x.EncryptedCredential).HasMaxLength(-1);
        m.Entity<Repository>().Property(x => x.Description).HasMaxLength(4000);
        m.Entity<Checkpoint>().Property(x => x.Description).HasMaxLength(4000);
        m.Entity<MergeRequest>().Property(x => x.Message).HasMaxLength(4000);
        m.Entity<RecoveryOperation>().Property(x => x.Reason).HasMaxLength(4000);
        m.Entity<PolicyOverride>().Property(x => x.Reason).HasMaxLength(4000);

        m.Entity<Company>().HasIndex(x => x.Slug).IsUnique();
        m.Entity<Company>().HasIndex(x => x.ExternalId);
        m.Entity<User>().HasIndex(x => x.Email).IsUnique();
        m.Entity<User>().HasIndex(x => x.ExternalSubject);
        m.Entity<CompanyMembership>().HasIndex(x => new { x.CompanyId, x.UserId }).IsUnique();
        m.Entity<CompanyMembership>().HasOne(x => x.User).WithMany(u => u.Memberships).HasForeignKey(x => x.UserId);
        m.Entity<CompanyMembership>().HasOne(x => x.Company).WithMany().HasForeignKey(x => x.CompanyId);
        m.Entity<Project>().HasIndex(x => new { x.CompanyId, x.ExternalId }).IsUnique();
        m.Entity<Project>().HasOne(x => x.Company).WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Restrict);

        m.Entity<Repository>().HasIndex(x => new { x.CompanyId, x.Slug }).IsUnique();
        m.Entity<Repository>().HasIndex(x => x.ProjectId);
        m.Entity<Repository>().HasOne(x => x.Company).WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<Repository>().HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<Repository>().HasMany(x => x.Remotes).WithOne().HasForeignKey(x => x.RepositoryId);
        m.Entity<Repository>().HasOne(x => x.Settings).WithOne().HasForeignKey<RepositorySettings>(x => x.RepositoryId);
        m.Entity<RepositorySettings>().HasIndex(x => x.RepositoryId).IsUnique();

        m.Entity<RepositoryPermission>().HasIndex(x => new { x.RepositoryId, x.UserId }).IsUnique();
        m.Entity<RepositoryPermission>().HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
        m.Entity<WorkingDirectory>().HasIndex(x => new { x.RepositoryId, x.Kind });
        m.Entity<WorkingDirectory>().HasIndex(x => x.Path);
        m.Entity<WorkingDirectory>().Property(x => x.Path).HasMaxLength(800);
        m.Entity<Worktree>().Property(x => x.Path).HasMaxLength(1024);
        m.Entity<Worktree>().HasIndex(x => new { x.RepositoryId, x.State });
        m.Entity<Worktree>().HasIndex(x => x.SessionId);
        m.Entity<Branch>().HasIndex(x => new { x.RepositoryId, x.Name }).IsUnique();
        m.Entity<CommitMetadata>().HasIndex(x => new { x.RepositoryId, x.Sha }).IsUnique();
        m.Entity<CommitMetadata>().Property(x => x.Sha).HasMaxLength(64);
        m.Entity<CommitMetadata>().HasIndex(x => new { x.RepositoryId, x.CommittedAt });
        m.Entity<CommitMetadata>().HasIndex(x => x.SessionId);
        m.Entity<CommitMetadata>().HasMany(x => x.Files).WithOne().HasForeignKey(x => x.CommitMetadataId);
        m.Entity<CommitFileChange>().Property(x => x.Path).HasMaxLength(1024);
        m.Entity<CommitFileChange>().Property(x => x.OldPath).HasMaxLength(1024);
        m.Entity<AISession>().HasIndex(x => new { x.RepositoryId, x.State });
        m.Entity<AISession>().Property(x => x.WorkingDirectory).HasMaxLength(1024);
        m.Entity<AISessionCommit>().HasIndex(x => new { x.SessionId, x.Sequence });
        m.Entity<ChangeOperation>().HasIndex(x => new { x.RepositoryId, x.State });
        m.Entity<ChangeOperation>().HasIndex(x => x.WorkingDirectory);
        m.Entity<ChangeOperation>().Property(x => x.WorkingDirectory).HasMaxLength(800);
        m.Entity<ChangeOperation>().Property(x => x.Version).IsConcurrencyToken();
        m.Entity<Checkpoint>().HasIndex(x => new { x.RepositoryId, x.CreatedAt });
        m.Entity<Checkpoint>().HasIndex(x => new { x.RepositoryId, x.CommitSha });
        m.Entity<PushQueueItem>().HasIndex(x => new { x.State, x.NextAttemptAt });
        m.Entity<PushQueueItem>().HasIndex(x => new { x.RepositoryId, x.Branch, x.State });
        m.Entity<PushQueueItem>().Property(x => x.WorkingDirectory).HasMaxLength(1024);
        m.Entity<SyncAttempt>().HasIndex(x => x.PushQueueItemId);
        m.Entity<RecoveryOperation>().HasIndex(x => new { x.RepositoryId, x.CreatedAt });
        m.Entity<RecoveryOperation>().Property(x => x.TargetDirectory).HasMaxLength(1024);
        m.Entity<RepositoryBackup>().HasIndex(x => new { x.RepositoryId, x.CreatedAt });
        m.Entity<RepositoryBackup>().Property(x => x.StorageKey).HasMaxLength(1024);
        m.Entity<BackupVerificationResult>().HasIndex(x => x.BackupId);
        m.Entity<PolicyDefinition>().HasIndex(x => new { x.Key, x.Scope, x.ScopeId }).IsUnique();
        m.Entity<PolicyDefinition>().Property(x => x.Key).HasMaxLength(100);
        m.Entity<PolicyDefinition>().Property(x => x.Scope).HasMaxLength(20);
        m.Entity<PolicyOverride>().HasIndex(x => x.RepositoryId);
        m.Entity<BackgroundJob>().HasIndex(x => new { x.State, x.NotBefore });
        m.Entity<AuditLog>().HasIndex(x => x.Timestamp);
        m.Entity<AuditLog>().HasIndex(x => new { x.RepositoryId, x.Timestamp });
        m.Entity<AuditLog>().HasIndex(x => new { x.CompanyId, x.Timestamp });
        m.Entity<SystemSetting>().HasIndex(x => x.Key).IsUnique();
        m.Entity<ApiToken>().HasIndex(x => x.TokenHash).IsUnique();
        m.Entity<ApiToken>().HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
        m.Entity<MutationLock>().HasIndex(x => x.LockKey).IsUnique();
        m.Entity<MutationLock>().Property(x => x.LockKey).HasMaxLength(200);
        m.Entity<IdempotencyRecord>().HasIndex(x => new { x.Principal, x.Key }).IsUnique();
        m.Entity<IdempotencyRecord>().Property(x => x.Principal).HasMaxLength(100);
        m.Entity<IdempotencyRecord>().Property(x => x.Key).HasMaxLength(200);
        m.Entity<IdempotencyRecord>().HasIndex(x => x.CreatedAt);
        m.Entity<MonitoringAlert>().HasIndex(x => new { x.Key, x.ResolvedAt });
        m.Entity<NotificationEvent>().HasKey(x => x.Id);
        m.Entity<NotificationEvent>().HasIndex(x => x.CreatedAt);
        m.Entity<MergeRequest>().HasIndex(x => new { x.RepositoryId, x.State });
    }

    private sealed class UtcConverter() : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private sealed class NullableUtcConverter() : ValueConverter<DateTime?, DateTime?>(
        v => v == null ? v : v.Value.Kind == DateTimeKind.Utc ? v : v.Value.ToUniversalTime(),
        v => v == null ? v : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc));

    private sealed class StringListConverter() : ValueConverter<List<string>, string>(
        v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
        v => string.IsNullOrEmpty(v) ? new List<string>() : JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>());

    private sealed class StringListComparer() : ValueComparer<List<string>>(
        (a, b) => (a == null && b == null) || (a != null && b != null && a.SequenceEqual(b)),
        v => v.Aggregate(0, (h, s) => HashCode.Combine(h, s.GetHashCode())),
        v => v.ToList());
}
