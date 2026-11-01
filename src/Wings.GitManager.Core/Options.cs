using Wings.GitManager.Contracts;

namespace Wings.GitManager.Core;

public sealed class GitManagerOptions
{
    public const string Section = "GitManager";

    /// <summary>Roots under which working directories may live. Requests outside them are rejected.</summary>
    public List<string> WorkspaceRoots { get; set; } = [];
    /// <summary>Require {root}/{companySlug}/... so one tenant's paths can never name another's.</summary>
    public bool EnforceCompanyDirectories { get; set; } = true;
    /// <summary>Private scratch space (git home, temp worktrees, backup staging). Not inside a workspace root.</summary>
    public string DataDirectory { get; set; } = "App_Data/git-manager";
    public string GitExecutable { get; set; } = "git";
    public int GitTimeoutSeconds { get; set; } = 120;
    public int GitNetworkTimeoutSeconds { get; set; } = 900;
    public long MaxGitOutputBytes { get; set; } = 64L * 1024 * 1024;
    public HostingProviderKind DefaultProvider { get; set; } = HostingProviderKind.Gitea;
    /// <summary>Identity used as author/committer of automated commits (the real user is recorded in trailers and SQL).</summary>
    public string ServiceCommitterName { get; set; } = "Wings Git Manager";
    public string ServiceCommitterEmail { get; set; } = "git-manager@wings.invalid";
    public string AgentAuthorEmailDomain { get; set; } = "agents.wings.invalid";
    public int DefaultLeaseMinutes { get; set; } = 60;
    /// <summary>Time a change-complete call waits for the synchronous push + verification.</summary>
    public int SynchronousPushTimeoutSeconds { get; set; } = 60;
    public bool AllowHttpRemotes { get; set; }
    public bool AllowFileRemotes { get; set; }
    public int RecoveryPreviewTtlMinutes { get; set; } = 60;
}

public sealed class GiteaOptions
{
    public const string Section = "Gitea";
    public string BaseUrl { get; set; } = "";
    /// <summary>Service account token (store via user-secrets / environment / key vault; never in code).</summary>
    public string? Token { get; set; }
    /// <summary>Username that owns the token, used for Git HTTP basic auth.</summary>
    public string ServiceUser { get; set; } = "wings-git-manager";
    /// <summary>Prefix for company organizations, e.g. "wings-" → wings-acme.</summary>
    public string OrganizationPrefix { get; set; } = "";
    public bool AutoCreateOrganizations { get; set; } = true;
    public bool ProtectDefaultBranch { get; set; } = true;
    /// <summary>Public URL of this service for Gitea push webhooks (optional).</summary>
    public string? WebhookUrl { get; set; }
    public string? WebhookSecret { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
}

public sealed class FileSystemRemoteOptions
{
    public const string Section = "FileSystemRemote";
    /// <summary>Root of bare repositories (should be a separate volume from working directories).</summary>
    public string Root { get; set; } = "";
}

public sealed class SyncOptions
{
    public const string Section = "Sync";
    public int PollSeconds { get; set; } = 5;
    public int MaxRetries { get; set; } = 12;
    public int BaseRetrySeconds { get; set; } = 10;
    public int MaxRetrySeconds { get; set; } = 3600;
    public int LeaseSeconds { get; set; } = 300;
}

public sealed class BackupOptions
{
    public const string Section = "Backup";
    /// <summary>filesystem | s3</summary>
    public string Provider { get; set; } = "filesystem";
    public string FileSystemRoot { get; set; } = "";
    /// <summary>Base64 256-bit key; backups are AES-256-GCM encrypted when present.</summary>
    public string? EncryptionKey { get; set; }
    public bool RequireEncryption { get; set; } = true;
    public S3BackupOptions S3 { get; set; } = new();
    public int ScheduleCheckMinutes { get; set; } = 15;
    public int VerifyEveryHours { get; set; } = 24;
    public int TestRestoreEveryHours { get; set; } = 168;
    public int MinimumRetainedVerified { get; set; } = 3;
    public int MetadataBackupIntervalHours { get; set; } = 24;
}

public sealed class S3BackupOptions
{
    public string? ServiceUrl { get; set; }
    public string Region { get; set; } = "us-east-1";
    public string Bucket { get; set; } = "";
    public string Prefix { get; set; } = "wings-git-backups/";
    public string? AccessKey { get; set; }
    public string? SecretKey { get; set; }
    public bool ForcePathStyle { get; set; } = true;
    /// <summary>Apply S3 Object Lock (GOVERNANCE) retention when the bucket supports it.</summary>
    public int? ObjectLockDays { get; set; }
}

public sealed class MonitoringOptions
{
    public const string Section = "Monitoring";
    public int IntervalSeconds { get; set; } = 120;
    public int FsckIntervalHours { get; set; } = 24;
    public double StorageWarningPercent { get; set; } = 80;
    public double StorageCriticalPercent { get; set; } = 92;
    public int BackupStaleHours { get; set; } = 36;
    public int UnpushedWarningMinutes { get; set; } = 30;
    public int FileWatchDebounceMilliseconds { get; set; } = 2000;
}

public sealed class WorkerOptions
{
    public const string Section = "Worker";
    /// <summary>Run background workers inside the API process (single-node / development).</summary>
    public bool RunInApi { get; set; } = true;
    public string InstanceName { get; set; } = Environment.MachineName;
    public bool EnableSyncQueue { get; set; } = true;
    public bool EnableJobs { get; set; } = true;
    public bool EnableBackups { get; set; } = true;
    public bool EnableMonitoring { get; set; } = true;
    public bool EnableFileWatcher { get; set; } = true;
}
