using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wings.GitManager.Contracts;
using Wings.GitManager.Core;
using Wings.GitManager.Core.Entities;
using Wings.GitManager.Infrastructure.Data;
using Wings.GitManager.Infrastructure.Git;
using Wings.GitManager.Infrastructure.Services;

namespace Wings.GitManager.Infrastructure.Backup;

public sealed record BackupJobPayload(Guid BackupId, bool TestRestore = true);
public sealed record RestoreJobPayload(Guid BackupId, string Target, string? TargetDirectory);

public sealed record BackupManifest(
    Guid BackupId, Guid? RepositoryId, string? RepositoryName, string? CompanySlug, DateTime CreatedAt, string? DefaultBranch, string? HeadSha,
    Dictionary<string, string> Refs, string BundleSha256, string? MetadataSha256, bool Encrypted, string Format = "wings-git-backup/1");

/// <summary>
/// Independent backups: a bundle of every ref (central server refs from the mirror plus local-only branches),
/// repository metadata, an integrity manifest, encryption, retention, verification with test restore, and
/// restore into a new directory or into a re-created central repository after a Git server loss.
/// </summary>
public sealed class BackupService(
    GitManagerDbContext db,
    AccessControl access,
    RepositoryContext ctx,
    MirrorService mirrors,
    IBackupStore store,
    JobService jobs,
    IHostingProviderResolver providers,
    AuditService audit,
    NotificationPublisher notifications,
    IOptions<BackupOptions> options,
    ILogger<BackupService> logger)
{
    private readonly BackupOptions _o = options.Value;

    private byte[] Key
    {
        get
        {
            var key = BackupCrypto.ParseKey(_o.EncryptionKey);
            if (key.Length == 0 && _o.RequireEncryption)
                throw new GitManagerException("backup_key_missing", "Backup encryption is required but Backup:EncryptionKey is not configured.", 503);
            return key;
        }
    }

    // ---------------------------------------------------------------- API entry points

    public async Task<AcceptedJobDto> RequestBackupAsync(Guid repositoryId, string trigger, CancellationToken ct)
    {
        var repo = await access.RequireRepositoryAsync(repositoryId, RepoAction.Administer, ct);
        var pending = await db.RepositoryBackups.FirstOrDefaultAsync(b => b.RepositoryId == repositoryId && (b.State == BackupState.Pending || b.State == BackupState.Running), ct);
        if (pending is not null)
        {
            var existingJob = await db.BackgroundJobs.Where(j => j.Type == JobTypes.BackupCreate && j.PayloadJson.Contains(pending.Id.ToString())).Select(j => j.Id).FirstOrDefaultAsync(ct);
            return new AcceptedJobDto(existingJob, $"/api/v1/jobs/{existingJob}", pending.Id);
        }
        return await QueueAsync(repo, trigger, ct);
    }

    public async Task<AcceptedJobDto> QueueAsync(Repository repo, string trigger, CancellationToken ct)
    {
        var backup = new RepositoryBackup { RepositoryId = repo.Id, Kind = "repository", StorageProvider = store.Name, Trigger = trigger, Encrypted = Key.Length > 0 };
        db.RepositoryBackups.Add(backup);
        await db.SaveChangesAsync(ct);
        var job = await jobs.EnqueueAsync(JobTypes.BackupCreate, repo.Id, new BackupJobPayload(backup.Id), access.Caller.Name, ct);
        audit.Record("backup.requested", repo.Id, repo.CompanyId, backup.Id.ToString(), details: new { trigger });
        await db.SaveChangesAsync(ct);
        return new AcceptedJobDto(job.Id, $"/api/v1/jobs/{job.Id}", backup.Id);
    }

    public async Task<IReadOnlyList<BackupDto>> ListAsync(Guid repositoryId, CancellationToken ct)
    {
        await access.RequireRepositoryAsync(repositoryId, RepoAction.Read, ct, track: false);
        var list = await db.RepositoryBackups.AsNoTracking().Where(b => b.RepositoryId == repositoryId).OrderByDescending(b => b.CreatedAt).Take(200).ToListAsync(ct);
        var ids = list.Select(b => b.Id).ToList();
        var verifications = await db.BackupVerificationResults.AsNoTracking().Where(v => ids.Contains(v.BackupId)).ToListAsync(ct);
        return list.Select(b => ToDto(b, verifications.Where(v => v.BackupId == b.Id).MaxBy(v => v.StartedAt))).ToList();
    }

    public async Task<AcceptedJobDto> RequestVerifyAsync(Guid repositoryId, Guid backupId, CancellationToken ct)
    {
        var repo = await access.RequireRepositoryAsync(repositoryId, RepoAction.Administer, ct);
        var backup = await db.RepositoryBackups.FirstOrDefaultAsync(b => b.Id == backupId && b.RepositoryId == repositoryId, ct) ?? throw new NotFoundException("Backup");
        if (backup.State is not (BackupState.Completed or BackupState.Verified or BackupState.VerificationFailed))
            throw new ConflictException(ErrorCodes.InvalidState, $"Backup is {backup.State}.");
        var job = await jobs.EnqueueAsync(JobTypes.BackupVerify, repo.Id, new BackupJobPayload(backup.Id, TestRestore: true), access.Caller.Name, ct);
        return new AcceptedJobDto(job.Id, $"/api/v1/jobs/{job.Id}", backup.Id);
    }

    public async Task<AcceptedJobDto> RequestRestoreAsync(Guid repositoryId, Guid backupId, RestoreBackupRequest req, CancellationToken ct)
    {
        var repo = await access.RequireRepositoryAsync(repositoryId, req.Target == "remote" ? RepoAction.Delete : RepoAction.Administer, ct);
        var backup = await db.RepositoryBackups.FirstOrDefaultAsync(b => b.Id == backupId && b.RepositoryId == repositoryId, ct) ?? throw new NotFoundException("Backup");
        if (backup.State is not (BackupState.Completed or BackupState.Verified)) throw new ConflictException(ErrorCodes.InvalidState, $"Backup is {backup.State}.");
        if (!req.Approved) throw new PolicyViolationException(ErrorCodes.ApprovalRequired, "Restoring a backup requires approved=true.");
        string? dir = null;
        if (req.Target == "directory")
        {
            dir = ctx.Paths.Authorize(req.TargetDirectory ?? throw new GitManagerException(ErrorCodes.Validation, "TargetDirectory is required."), repo.Company!);
            if (!WorkspacePaths.IsEmptyOrMissing(dir)) throw new ConflictException("target_not_empty", "The target directory exists and is not empty.");
        }
        else if (req.Target != "remote") throw new GitManagerException(ErrorCodes.Validation, "Target must be 'directory' or 'remote'.");
        var job = await jobs.EnqueueAsync(JobTypes.BackupRestore, repo.Id, new RestoreJobPayload(backup.Id, req.Target, dir), access.Caller.Name, ct);
        audit.Record("backup.restore.requested", repo.Id, repo.CompanyId, backup.Id.ToString(), details: new { req.Target, dir });
        await db.SaveChangesAsync(ct);
        return new AcceptedJobDto(job.Id, $"/api/v1/jobs/{job.Id}", backup.Id);
    }

    // ---------------------------------------------------------------- job bodies

    public async Task<string> RunCreateAsync(BackupJobPayload payload, CancellationToken ct)
    {
        var backup = await db.RepositoryBackups.FirstAsync(b => b.Id == payload.BackupId, ct);
        var repo = await db.Repositories.Include(r => r.Remotes).Include(r => r.Company).Include(r => r.Settings).FirstAsync(r => r.Id == backup.RepositoryId, ct);
        backup.State = BackupState.Running;
        await db.SaveChangesAsync(ct);
        var work = Scratch();
        try
        {
            // 1. Independent copy: refresh the mirror from the central server (no prune) and add local-only work.
            string mirror;
            try { mirror = await mirrors.EnsureAsync(repo, forceFetch: true, ct); }
            catch (Exception ex) when (Directory.Exists(Path.Combine(mirrors.MirrorPath(repo.Id), "refs")))
            {
                logger.LogWarning(ex, "Central server unreachable during backup of {Repo}; backing up the last mirrored state and local refs", repo.Id);
                mirror = mirrors.MirrorPath(repo.Id);
            }
            var main = await ctx.MainWorkingDirectoryAsync(repo.Id, ct);
            if (main is not null && await ctx.Git.IsRepositoryAsync(main.Path, ct))
                await ctx.Git.RunAsync(mirror, ["fetch", "--no-tags", "--no-prune", main.Path, "+refs/heads/*:refs/wings-local/heads/*"], new() { AllowFileProtocol = true, ThrowOnError = false }, ct);

            var refs = await RefsAsync(mirror, ct);
            if (refs.Count == 0) throw new GitManagerException("backup_empty", "The repository has no refs to back up.");
            var head = refs.GetValueOrDefault("refs/heads/" + repo.DefaultBranch) ?? refs.GetValueOrDefault("refs/wings-local/heads/" + repo.DefaultBranch);

            // 2. Bundle + metadata.
            var bundle = Path.Combine(work, "repository.bundle");
            await ctx.Git.RunAsync(mirror, ["bundle", "create", bundle, "--all"], new() { Timeout = TimeSpan.FromMinutes(60) }, ct);
            var bundleSha = await BackupCrypto.Sha256Async(bundle, ct);
            var metadata = Path.Combine(work, "metadata.json");
            await File.WriteAllTextAsync(metadata, await MetadataJsonAsync(repo, ct), ct);
            var metaSha = await BackupCrypto.Sha256Async(metadata, ct);

            // 3. Encrypt and store (keys are independent of the Git server and its storage).
            var prefix = $"repositories/{repo.CompanyId:N}/{repo.Id:N}/{DateTime.UtcNow:yyyyMMddTHHmmssZ}-{backup.Id.ToString("N")[..8]}";
            var key = Key;
            var bundleObj = await SealAsync(bundle, key, ct);
            var metaObj = await SealAsync(metadata, key, ct);
            var manifest = new BackupManifest(backup.Id, repo.Id, repo.Name, repo.Company?.Slug, DateTime.UtcNow, repo.DefaultBranch, head, refs, bundleSha, metaSha, key.Length > 0);
            var manifestFile = Path.Combine(work, "manifest.json");
            await File.WriteAllTextAsync(manifestFile, Json.Serialize(manifest), ct);
            await store.PutAsync($"{prefix}/repository.bundle{(key.Length > 0 ? ".enc" : "")}", bundleObj, ct);
            await store.PutAsync($"{prefix}/metadata.json{(key.Length > 0 ? ".enc" : "")}", metaObj, ct);
            await store.PutAsync($"{prefix}/manifest.json", manifestFile, ct);

            backup.StorageKey = prefix;
            backup.SizeBytes = new FileInfo(bundleObj).Length + new FileInfo(metaObj).Length;
            backup.Sha256 = bundleSha;
            backup.HeadSha = head;
            backup.RefsJson = Json.Serialize(refs);
            backup.Encrypted = key.Length > 0;
            backup.State = BackupState.Completed;
            backup.CompletedAt = DateTime.UtcNow;
            audit.Record("backup.completed", repo.Id, repo.CompanyId, backup.Id.ToString(), details: new { prefix, refs = refs.Count, backup.SizeBytes });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            backup.State = BackupState.Failed;
            backup.Error = ex.Message;
            db.MonitoringAlerts.Add(new MonitoringAlert { RepositoryId = repo.Id, CompanyId = repo.CompanyId, Key = $"backup-failed:{backup.Id}", Category = "backup-failure", Severity = AlertSeverity.Critical, Message = $"Backup failed: {ex.Message}" });
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            TryDelete(work);
        }

        // 4. Verify immediately so the dashboard never shows an unverified backup as protection.
        var verification = await RunVerifyAsync(new BackupJobPayload(backup.Id, payload.TestRestore), ct);
        await ApplyRetentionAsync(repo, ct);
        return verification;
    }

    public async Task<string> RunVerifyAsync(BackupJobPayload payload, CancellationToken ct)
    {
        var backup = await db.RepositoryBackups.FirstAsync(b => b.Id == payload.BackupId, ct);
        var result = new BackupVerificationResult { BackupId = backup.Id };
        db.BackupVerificationResults.Add(result);
        var work = Scratch();
        var details = new List<string>();
        try
        {
            var manifestFile = Path.Combine(work, "manifest.json");
            await store.GetAsync($"{backup.StorageKey}/manifest.json", manifestFile, ct);
            var manifest = Json.Deserialize<BackupManifest>(await File.ReadAllTextAsync(manifestFile, ct))!;
            var bundle = await OpenAsync($"{backup.StorageKey}/repository.bundle", backup.Encrypted, work, ct);
            result.ChecksumOk = await BackupCrypto.Sha256Async(bundle, ct) == manifest.BundleSha256 && manifest.BundleSha256 == backup.Sha256;
            details.Add(result.ChecksumOk ? "checksum ok" : "checksum MISMATCH");

            var probe = Path.Combine(work, "probe");
            Directory.CreateDirectory(probe);
            await ctx.Git.RunAsync(probe, ["init", "-q", "--bare"], null, ct);
            var verify = await ctx.Git.RunAsync(probe, ["bundle", "verify", "-q", bundle], new() { ThrowOnError = false }, ct);
            result.BundleOk = verify.Success;
            details.Add(result.BundleOk ? "bundle ok" : "bundle INVALID: " + verify.StdErr.Trim());

            if (payload.TestRestore && result.BundleOk)
            {
                var restored = Path.Combine(work, "restore.git");
                await ctx.Git.RunAsync(work, ["clone", "--mirror", "--", bundle, restored], new() { AllowFileProtocol = true }, ct);
                var fsck = await ctx.Git.RunAsync(restored, ["fsck", "--full", "--no-progress", "--no-dangling"], new() { ThrowOnError = false, Timeout = TimeSpan.FromMinutes(30) }, ct);
                result.FsckOk = fsck.Success;
                details.Add(result.FsckOk ? "fsck ok" : "fsck FAILED: " + fsck.StdErr.Trim());
                var restoredRefs = await RefsAsync(restored, ct);
                result.RefsMatch = manifest.Refs.All(kv => restoredRefs.TryGetValue(kv.Key, out var v) && v == kv.Value);
                details.Add(result.RefsMatch ? $"{manifest.Refs.Count} refs match" : "refs DIFFER from manifest");
                result.TestRestoreOk = result.FsckOk && result.RefsMatch;
            }
            else
            {
                result.FsckOk = result.RefsMatch = result.TestRestoreOk = result.BundleOk;
                details.Add("test restore skipped");
            }
            result.Succeeded = result.ChecksumOk && result.BundleOk && result.FsckOk && result.RefsMatch;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result.Succeeded = false;
            details.Add("verification error: " + ex.Message);
        }
        finally
        {
            TryDelete(work);
        }

        result.CompletedAt = DateTime.UtcNow;
        result.Details = string.Join("; ", details);
        backup.State = result.Succeeded ? BackupState.Verified : BackupState.VerificationFailed;
        if (result.Succeeded) backup.VerifiedAt = result.CompletedAt;
        if (backup.RepositoryId is { } repoId)
        {
            if (result.Succeeded) await MarkBackedUpAsync(repoId, backup, ct);
            else db.MonitoringAlerts.Add(new MonitoringAlert { RepositoryId = repoId, Key = $"backup-verify-failed:{backup.Id}", Category = "backup-failure", Severity = AlertSeverity.Critical, Message = $"Backup verification failed: {result.Details}" });
            notifications.Publish(result.Succeeded ? "backup.verified" : "backup.verification-failed", null, repoId, new { backup.Id, result.Details });
        }
        audit.Record("backup.verified", backup.RepositoryId, null, backup.Id.ToString(), result.Succeeded ? "success" : "failure", result.Details);
        await db.SaveChangesAsync(ct);
        return Json.Serialize(new { backupId = backup.Id, result.Succeeded, result.Details });
    }

    /// <summary>Checkpoints whose commits are inside a verified backup become "independently backed up".</summary>
    private async Task MarkBackedUpAsync(Guid repoId, RepositoryBackup backup, CancellationToken ct)
    {
        var mirror = mirrors.MirrorPath(repoId);
        if (!Directory.Exists(mirror)) return;
        var cps = await db.Checkpoints.Where(c => c.RepositoryId == repoId && c.BackupState == null).ToListAsync(ct);
        foreach (var cp in cps)
            if (backup.CreatedAt >= cp.CreatedAt && await ctx.Git.ObjectExistsAsync(mirror, cp.CommitSha, ct))
            {
                cp.BackupState = DurabilityState.IndependentlyBackedUp;
                cp.BackedUpAt = DateTime.UtcNow;
            }
    }

    public async Task<string> RunRestoreAsync(RestoreJobPayload payload, CancellationToken ct)
    {
        var backup = await db.RepositoryBackups.FirstAsync(b => b.Id == payload.BackupId, ct);
        var repo = await db.Repositories.Include(r => r.Remotes).Include(r => r.Company).Include(r => r.Settings).FirstAsync(r => r.Id == backup.RepositoryId, ct);
        var work = Scratch();
        try
        {
            var bundle = await OpenAsync($"{backup.StorageKey}/repository.bundle", backup.Encrypted, work, ct);
            if (await BackupCrypto.Sha256Async(bundle, ct) != backup.Sha256) throw new GitManagerException("backup_corrupt", "Backup checksum does not match; restore aborted.", 500);

            if (payload.Target == "directory")
            {
                var dir = ctx.Paths.Authorize(payload.TargetDirectory!, repo.Company!);
                if (!WorkspacePaths.IsEmptyOrMissing(dir)) throw new ConflictException("target_not_empty", "Target directory is not empty.");
                Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
                await ctx.Git.RunAsync(Path.GetDirectoryName(dir)!, ["clone", "--", bundle, dir], new() { AllowFileProtocol = true }, ct);
                foreach (var (name, sha, _) in await ctx.Git.RemoteBranchesAsync(dir, "origin", ct))
                    if (await ctx.Git.ResolveAsync(dir, "refs/heads/" + name, ct) is null)
                        await ctx.Git.RunAsync(dir, ["branch", "--no-track", "--", name, sha], null, ct);
                await ctx.Git.RunAsync(dir, ["remote", "set-url", "origin", ctx.PrimaryRemote(repo).Url], null, ct);
                var head = await ctx.Git.HeadAsync(dir, ct);
                audit.Record("backup.restored", repo.Id, repo.CompanyId, backup.Id.ToString(), details: new { target = dir, head });
                await db.SaveChangesAsync(ct);
                return Json.Serialize(new { target = dir, head });
            }

            // Central Git server loss: re-create the repository (when missing or empty) and push every ref.
            var remote = ctx.PrimaryRemote(repo);
            var provider = providers.Get(remote.Provider);
            if (!provider.CanProvision) throw new GitManagerException("provider_cannot_provision", "Re-create the remote repository manually, then restore.", 400);
            var ns = remote.Owner ?? await provider.EnsureNamespaceAsync(repo.Company!, ct);
            await provider.EnsureNamespaceAsync(repo.Company!, ct);
            var info = await provider.GetRepositoryAsync(ns, remote.RemoteRepositoryName ?? repo.Slug, ct);
            if (info is { IsEmpty: false })
                throw new ConflictException("remote_not_empty", "The central repository exists and has commits; restore into a directory instead.");
            info ??= await provider.CreateRepositoryAsync(ns, remote.RemoteRepositoryName ?? repo.Slug, repo.Description, repo.DefaultBranch, ct);
            remote.Url = info.CloneUrl;
            remote.ProviderRepositoryId = info.ProviderRepositoryId;

            var bare = Path.Combine(work, "restore.git");
            await ctx.Git.RunAsync(work, ["clone", "--mirror", "--", bundle, bare], new() { AllowFileProtocol = true }, ct);
            var push = await ctx.Git.RunAsync(bare, ["push", "--porcelain", remote.Url, "refs/heads/*:refs/heads/*", "refs/tags/*:refs/tags/*"],
                new() { Credential = provider.GetGitCredential(remote), Network = true, AllowFileProtocol = ctx.AllowFile(repo) }, ct);
            // Local-only branches captured in the backup are restored under their original names when absent.
            var refs = await RefsAsync(bare, ct);
            foreach (var (refName, sha) in refs.Where(r => r.Key.StartsWith("refs/wings-local/heads/")))
            {
                var name = refName["refs/wings-local/heads/".Length..];
                if (!refs.ContainsKey("refs/heads/" + name))
                    await ctx.Git.RunAsync(bare, ["push", "--porcelain", remote.Url, $"{sha}:refs/heads/{name}"],
                        new() { Credential = provider.GetGitCredential(remote), Network = true, ThrowOnError = false, AllowFileProtocol = ctx.AllowFile(repo) }, ct);
            }
            var restoredHead = await ctx.Git.LsRemoteAsync(bare, remote.Url, repo.DefaultBranch, provider.GetGitCredential(remote), ct, ctx.AllowFile(repo));
            if (backup.HeadSha is not null && restoredHead != backup.HeadSha && refs.ContainsKey("refs/heads/" + repo.DefaultBranch))
                throw new GitManagerException("verification_failed", $"Restored {repo.DefaultBranch} is {restoredHead}, expected {backup.HeadSha}.", 500);
            if (remote.Owner is not null && remote.RemoteRepositoryName is not null)
                foreach (var pattern in repo.Settings?.ProtectedBranches ?? [repo.DefaultBranch])
                    try { await provider.ProtectBranchAsync(remote.Owner, remote.RemoteRepositoryName, pattern, ct); } catch (Exception ex) { logger.LogWarning(ex, "Protect after restore failed"); }
            repo.RemoteStatus = RemoteStatus.Unknown;
            var mirror = mirrors.MirrorPath(repo.Id);
            audit.Record("backup.restored.remote", repo.Id, repo.CompanyId, backup.Id.ToString(), details: new { remote = remote.Url, restoredHead, push = GitExecutor.Redact(push.StdOut).Length });
            notifications.Publish("backup.restored", repo.CompanyId, repo.Id, new { backup.Id, remote = remote.Url });
            await db.SaveChangesAsync(ct);
            return Json.Serialize(new { remote = remote.Url, head = restoredHead, refs = refs.Count });
        }
        finally
        {
            TryDelete(work);
        }
    }

    /// <summary>System-wide metadata export (companies, projects, repositories, mappings, checkpoints, recovery records).</summary>
    public async Task<string> RunMetadataBackupAsync(BackupJobPayload payload, CancellationToken ct)
    {
        var backup = await db.RepositoryBackups.FirstAsync(b => b.Id == payload.BackupId, ct);
        var work = Scratch();
        try
        {
            backup.State = BackupState.Running;
            await db.SaveChangesAsync(ct);
            var snapshot = new
            {
                exportedAt = DateTime.UtcNow,
                companies = await db.Companies.AsNoTracking().ToListAsync(ct),
                projects = await db.Projects.AsNoTracking().ToListAsync(ct),
                repositories = await db.Repositories.AsNoTracking().ToListAsync(ct),
                remotes = await db.RepositoryRemotes.AsNoTracking().Select(r => new { r.Id, r.RepositoryId, r.Name, r.Provider, r.Url, r.Owner, r.RemoteRepositoryName, r.ProviderRepositoryId }).ToListAsync(ct),
                settings = await db.RepositorySettings.AsNoTracking().ToListAsync(ct),
                workingDirectories = await db.WorkingDirectories.AsNoTracking().ToListAsync(ct),
                users = await db.Users.AsNoTracking().Select(u => new { u.Id, u.Email, u.DisplayName, u.IsSystemAdministrator, u.IsServiceAccount, u.IsActive }).ToListAsync(ct),
                memberships = await db.CompanyMemberships.AsNoTracking().Select(m => new { m.Id, m.CompanyId, m.UserId, m.Role }).ToListAsync(ct),
                permissions = await db.RepositoryPermissions.AsNoTracking().Select(p => new { p.Id, p.RepositoryId, p.UserId, p.Role }).ToListAsync(ct),
                policies = await db.PolicyDefinitions.AsNoTracking().ToListAsync(ct),
                sessions = await db.AISessions.AsNoTracking().ToListAsync(ct),
                sessionCommits = await db.AISessionCommits.AsNoTracking().ToListAsync(ct),
                checkpoints = await db.Checkpoints.AsNoTracking().ToListAsync(ct),
                recoveryOperations = await db.RecoveryOperations.AsNoTracking().ToListAsync(ct),
                backups = await db.RepositoryBackups.AsNoTracking().ToListAsync(ct),
            };
            var file = Path.Combine(work, "metadata.json");
            await File.WriteAllTextAsync(file, Json.Serialize(snapshot), ct);
            var sealedFile = await SealAsync(file, Key, ct);
            var key = $"system/metadata-{DateTime.UtcNow:yyyyMMddTHHmmssZ}-{backup.Id.ToString("N")[..8]}.json{(Key.Length > 0 ? ".enc" : "")}";
            await store.PutAsync(key, sealedFile, ct);
            backup.StorageKey = key;
            backup.Sha256 = await BackupCrypto.Sha256Async(file, ct);
            backup.SizeBytes = new FileInfo(sealedFile).Length;
            backup.State = BackupState.Verified;
            backup.CompletedAt = backup.VerifiedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Json.Serialize(new { key, backup.SizeBytes });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            backup.State = BackupState.Failed;
            backup.Error = ex.Message;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        finally { TryDelete(work); }
    }

    /// <summary>Keeps the newest N backups and always the newest verified ones; branch deletion never removes backups.</summary>
    public async Task ApplyRetentionAsync(Repository repo, CancellationToken ct)
    {
        var keep = Math.Max(3, repo.Settings?.BackupRetentionCount ?? 30);
        var all = await db.RepositoryBackups.Where(b => b.RepositoryId == repo.Id && b.State != BackupState.Expired).OrderByDescending(b => b.CreatedAt).ToListAsync(ct);
        var protectedIds = all.Where(b => b.State == BackupState.Verified).Take(_o.MinimumRetainedVerified).Select(b => b.Id).ToHashSet();
        foreach (var b in all.Skip(keep).Where(b => !protectedIds.Contains(b.Id) && !b.RetentionLocked && b.State is not (BackupState.Pending or BackupState.Running)))
        {
            try
            {
                if (b.StorageKey is not null)
                    foreach (var suffix in new[] { "/repository.bundle.enc", "/repository.bundle", "/metadata.json.enc", "/metadata.json", "/manifest.json" })
                        await store.DeleteAsync(b.StorageKey + suffix, ct);
                b.State = BackupState.Expired;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Retention could not delete backup {Backup} (object lock?)", b.Id);
            }
        }
        await db.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------------- helpers

    private async Task<string> SealAsync(string file, byte[] key, CancellationToken ct)
    {
        if (key.Length == 0) return file;
        var output = file + ".enc";
        await BackupCrypto.EncryptAsync(file, output, key, ct);
        return output;
    }

    private async Task<string> OpenAsync(string keyBase, bool encrypted, string work, CancellationToken ct)
    {
        var name = Path.GetFileName(keyBase);
        var downloaded = Path.Combine(work, name + (encrypted ? ".enc" : ""));
        await store.GetAsync(keyBase + (encrypted ? ".enc" : ""), downloaded, ct);
        if (!encrypted) return downloaded;
        var plain = Path.Combine(work, name);
        await BackupCrypto.DecryptAsync(downloaded, plain, Key, ct);
        return plain;
    }

    private async Task<string> MetadataJsonAsync(Repository repo, CancellationToken ct) => Json.Serialize(new
    {
        repository = new { repo.Id, repo.Name, repo.Slug, repo.CompanyId, repo.ProjectId, repo.DefaultBranch, repo.Description, repo.CreatedAt },
        remotes = repo.Remotes.Select(r => new { r.Name, r.Provider, r.Url, r.Owner, r.RemoteRepositoryName }),
        settings = repo.Settings,
        checkpoints = await db.Checkpoints.AsNoTracking().Where(c => c.RepositoryId == repo.Id).ToListAsync(ct),
        sessions = await db.AISessions.AsNoTracking().Where(s => s.RepositoryId == repo.Id).ToListAsync(ct),
        sessionCommits = await db.AISessionCommits.AsNoTracking().Where(s => s.RepositoryId == repo.Id).ToListAsync(ct),
        commits = await db.CommitMetadata.AsNoTracking().Where(c => c.RepositoryId == repo.Id).OrderByDescending(c => c.CommittedAt).Take(5000)
            .Select(c => new { c.Sha, c.Branch, c.Subject, c.SessionId, c.AgentId, c.ChangeOperationId, c.InitiatingUser, c.UserRequestRef, c.CommittedAt }).ToListAsync(ct),
        recoveryOperations = await db.RecoveryOperations.AsNoTracking().Where(r => r.RepositoryId == repo.Id).ToListAsync(ct),
    });

    private async Task<Dictionary<string, string>> RefsAsync(string gitDir, CancellationToken ct)
    {
        var r = await ctx.Git.RunAsync(gitDir, ["for-each-ref", "--format=%(objectname) %(refname)"], null, ct);
        return r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split(' ', 2)).Where(p => p.Length == 2)
            .Where(p => !p[1].StartsWith("refs/remotes/")).ToDictionary(p => p[1], p => p[0]);
    }

    private string Scratch()
    {
        var dir = Path.Combine(Path.GetFullPath(ctx.Options.DataDirectory), "backup-work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(dir, true);
        }
        catch { /* best effort */ }
    }

    public static BackupDto ToDto(RepositoryBackup b, BackupVerificationResult? v) => new()
    {
        Id = b.Id, RepositoryId = b.RepositoryId, Kind = b.Kind, State = b.State, StorageProvider = b.StorageProvider, StorageKey = b.StorageKey,
        SizeBytes = b.SizeBytes, Sha256 = b.Sha256, Encrypted = b.Encrypted, HeadSha = b.HeadSha,
        RefCount = Json.Deserialize<Dictionary<string, string>>(b.RefsJson)?.Count ?? 0, Trigger = b.Trigger, CreatedAt = b.CreatedAt,
        CompletedAt = b.CompletedAt, VerifiedAt = b.VerifiedAt, Error = b.Error,
        LastVerification = v is null ? null : new BackupVerificationDto
        {
            Id = v.Id, StartedAt = v.StartedAt, CompletedAt = v.CompletedAt, Succeeded = v.Succeeded, ChecksumOk = v.ChecksumOk, BundleOk = v.BundleOk,
            FsckOk = v.FsckOk, RefsMatch = v.RefsMatch, TestRestoreOk = v.TestRestoreOk, Details = v.Details
        }
    };
}

public sealed class BackupJobHandlers
{
    public sealed class Create(BackupService s) : IJobHandler
    {
        public string Type => JobTypes.BackupCreate;
        public async Task<string?> RunAsync(BackgroundJob job, CancellationToken ct) => await s.RunCreateAsync(Json.Deserialize<BackupJobPayload>(job.PayloadJson)!, ct);
    }

    public sealed class Verify(BackupService s) : IJobHandler
    {
        public string Type => JobTypes.BackupVerify;
        public async Task<string?> RunAsync(BackgroundJob job, CancellationToken ct) => await s.RunVerifyAsync(Json.Deserialize<BackupJobPayload>(job.PayloadJson)!, ct);
    }

    public sealed class Restore(BackupService s) : IJobHandler
    {
        public string Type => JobTypes.BackupRestore;
        public async Task<string?> RunAsync(BackgroundJob job, CancellationToken ct) => await s.RunRestoreAsync(Json.Deserialize<RestoreJobPayload>(job.PayloadJson)!, ct);
    }

    public sealed class Metadata(BackupService s) : IJobHandler
    {
        public string Type => JobTypes.MetadataBackup;
        public async Task<string?> RunAsync(BackgroundJob job, CancellationToken ct) => await s.RunMetadataBackupAsync(Json.Deserialize<BackupJobPayload>(job.PayloadJson)!, ct);
    }
}
