using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wings.GitManager.Core;
using Wings.GitManager.Infrastructure.Services;
using Wings.GitManager.Infrastructure.Workers;
using System.Net.Http.Json;
using Wings.GitManager.Client;
using Wings.GitManager.Contracts;
using static Wings.GitManager.IntegrationTests.GitManagerFactory;

namespace Wings.GitManager.IntegrationTests;

[Collection(ApiCollection.Name)]
public class BackupAndSecurityTests(GitManagerFactory f)
{
    [Fact] // Scenarios 18 & 23: verified encrypted backups; restore after loss of the central Git server
    public async Task Backups_are_verified_and_restore_the_central_repository_after_server_loss()
    {
        var repo = await f.NewRepoAsync("Backup App", new() { ["src/a.txt"] = "a\n" });
        var begin = await f.Agent.Changes.BeginAsync(repo.RepositoryId, new BeginChangeRequest { AgentId = "agent", Description = "Add b" });
        Write(begin.WorkingDirectory, "src/b.txt", "b\n");
        var done = await f.Agent.Changes.CompleteAsync(repo.RepositoryId, begin.OperationId, new CompleteChangeRequest());
        Git(repo.WorkingDirectory, "branch", "local-only-experiment"); // never pushed: must still be in the backup

        var admin = new WingsGitManagerClient(f.Admin);
        var accepted = await admin.Backups.CreateAsync(repo.RepositoryId);
        var job = await f.WaitForJobAsync(accepted.JobId);
        Assert.Equal(JobState.Succeeded, job.State);
        var backup = Assert.Single(await admin.Backups.ListAsync(repo.RepositoryId));
        Assert.Equal(BackupState.Verified, backup.State);
        Assert.True(backup.Encrypted);
        Assert.Equal(done.CommitSha, backup.HeadSha);
        Assert.True(backup.LastVerification!.Succeeded && backup.LastVerification.ChecksumOk && backup.LastVerification.BundleOk && backup.LastVerification.TestRestoreOk);
        var bundleFile = Directory.GetFiles(f.Backups, "repository.bundle.enc", SearchOption.AllDirectories).Single(p => p.Contains(repo.RepositoryId.ToString("N")));
        Assert.DoesNotContain("# v2 git bundle", File.ReadAllText(bundleFile)); // encrypted at rest
        var cps = await f.Agent.Repositories.CheckpointsAsync(repo.RepositoryId);
        Assert.All(cps.Items, c => Assert.Equal(DurabilityState.IndependentlyBackedUp, c.BackupState));

        // Central server loss: the bare repository disappears entirely.
        var bare = f.BareRemote("backup-app");
        Directory.Delete(bare, true);
        var restore = await admin.Backups.RestoreAsync(repo.RepositoryId, backup.Id, new RestoreBackupRequest { Target = "remote", Approved = true });
        var restoreJob = await f.WaitForJobAsync(restore.JobId);
        Assert.True(restoreJob.State == JobState.Succeeded, restoreJob.Error);
        Assert.Equal(done.CommitSha, Git(bare, "rev-parse", "refs/heads/main"));
        Assert.Equal(Git(repo.WorkingDirectory, "rev-parse", "local-only-experiment"), Git(bare, "rev-parse", "refs/heads/local-only-experiment"));

        // Restore into a fresh directory too.
        var dir = Path.Combine(f.Workspace, "acme", "backup-app", "restored");
        var toDir = await admin.Backups.RestoreAsync(repo.RepositoryId, backup.Id, new RestoreBackupRequest { Target = "directory", TargetDirectory = dir, Approved = true });
        Assert.Equal(JobState.Succeeded, (await f.WaitForJobAsync(toDir.JobId)).State);
        Assert.Equal("b", File.ReadAllText(Path.Combine(dir, "src/b.txt")).Trim());

        // Tampering is detected by verification.
        File.SetAttributes(bundleFile, FileAttributes.Normal);
        var bytes = File.ReadAllBytes(bundleFile);
        bytes[^40] ^= 0xFF;
        File.WriteAllBytes(bundleFile, bytes);
        var verify = await admin.Backups.VerifyAsync(repo.RepositoryId, backup.Id);
        await f.WaitForJobAsync(verify.JobId);
        var after = Assert.Single(await admin.Backups.ListAsync(repo.RepositoryId));
        Assert.Equal(BackupState.VerificationFailed, after.State);
        Assert.False(after.LastVerification!.Succeeded);
    }

    [Fact] // Scenario 19: company isolation and repository authorization
    public async Task Other_companies_and_insufficient_roles_are_denied()
    {
        var repo = await f.NewRepoAsync("Private App", new() { ["a.txt"] = "a\n" });

        var outsider = await f.Outsider.GetAsync($"/api/v1/repositories/{repo.RepositoryId}");
        Assert.Equal(HttpStatusCode.NotFound, outsider.StatusCode); // existence is not revealed
        var outsiderList = await GetAsync<PagedResult<RepositorySummaryDto>>(f.Outsider, "/api/v1/repositories");
        Assert.DoesNotContain(outsiderList.Items, r => r.Id == repo.RepositoryId);
        var outsiderFile = await f.Outsider.GetAsync($"/api/v1/repositories/{repo.RepositoryId}/files/content?path=a.txt");
        Assert.Equal(HttpStatusCode.NotFound, outsiderFile.StatusCode);
        var outsiderCreate = await f.Outsider.PostAsJsonAsync("/api/v1/repositories", new CreateRepositoryRequest { CompanyId = f.AcmeId, ProjectExternalId = "steal", ProjectName = "Steal" });
        Assert.Equal(HttpStatusCode.NotFound, outsiderCreate.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await f.ReadOnly.GetAsync($"/api/v1/repositories/{repo.RepositoryId}")).StatusCode);
        var roBegin = await f.ReadOnly.PostAsJsonAsync($"/api/v1/repositories/{repo.RepositoryId}/changes/begin", new BeginChangeRequest { AgentId = "x", Description = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, roBegin.StatusCode);
        var roRecover = await f.ReadOnly.PostAsJsonAsync($"/api/v1/repositories/{repo.RepositoryId}/recovery/restore-file", new RecoveryRequest { Type = RecoveryType.RestoreFile, RecoveryPoint = repo.CommitSha, Paths = ["a.txt"], Approved = true });
        Assert.Equal(HttpStatusCode.Forbidden, roRecover.StatusCode);

        var anon = f.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/repositories")).StatusCode);
        anon.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wgm_not-a-real-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/repositories")).StatusCode);

        // Agent tokens cannot delete repositories (company administrator + admin scope).
        Assert.Equal(HttpStatusCode.Forbidden, (await f.AgentHttp.DeleteAsync($"/api/v1/repositories/{repo.RepositoryId}")).StatusCode);
        Assert.True(f.WithDb(db => db.AuditLogs.Any(a => a.RepositoryId == repo.RepositoryId && a.Outcome == "denied")));
    }

    [Fact] // Revoked tokens stop working immediately
    public async Task Revoked_tokens_are_rejected()
    {
        var client = await f.UserClientAsync("temp@acme.test", false, f.AcmeId, WorkspaceRole.ReadOnly, ["repos:read"]);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/me")).StatusCode);
        var tokens = await GetAsync<List<ApiTokenDto>>(f.Admin, "/api/v1/tokens");
        var mine = tokens.Single(t => t.Name == "temp@acme.test");
        await f.Admin.DeleteAsync($"/api/v1/tokens/{mine.Id}");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact] // Scenario 20: path traversal and working-directory restrictions
    public async Task Path_traversal_and_foreign_directories_are_rejected()
    {
        var repo = await f.NewRepoAsync("Path App", new() { ["a.txt"] = "a\n" });

        foreach (var path in new[] { "../../../../etc/passwd", "/etc/passwd", ".git/config", "a.txt/../../x" })
        {
            var resp = await f.AgentHttp.GetAsync($"/api/v1/repositories/{repo.RepositoryId}/files/content?path={Uri.EscapeDataString(path)}");
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            Assert.Contains("path_rejected", await resp.Content.ReadAsStringAsync());
        }
        var badRef = await f.AgentHttp.GetAsync($"/api/v1/repositories/{repo.RepositoryId}/commits?ref=--output=/tmp/x");
        Assert.Equal(HttpStatusCode.BadRequest, badRef.StatusCode);

        async Task<HttpResponseMessage> Create(string dir) => await f.AgentHttp.PostAsJsonAsync("/api/v1/repositories",
            new CreateRepositoryRequest { CompanyId = f.AcmeId, ProjectExternalId = "p-" + Guid.NewGuid().ToString("N")[..6], ProjectName = "Bad " + Guid.NewGuid().ToString("N")[..4], WorkingDirectory = dir });
        Assert.Equal(HttpStatusCode.Forbidden, (await Create(Path.Combine(f.Root, "outside"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Create(Path.Combine(f.Workspace, "globex", "x", "main"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Create(Path.Combine(f.Workspace, "acme", "..", "globex", "y"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Create("relative/path")).StatusCode);

        // A link inside the company folder that points elsewhere is rejected.
        var outside = Path.Combine(f.Root, "outside-target");
        Directory.CreateDirectory(outside);
        var link = Path.Combine(f.Workspace, "acme", "sneaky");
        Directory.CreateSymbolicLink(link, outside);
        Assert.Equal(HttpStatusCode.Forbidden, (await Create(Path.Combine(link, "main"))).StatusCode);
        Assert.Empty(Directory.GetFileSystemEntries(outside));
    }

    [Fact] // Health, dashboard, OpenAPI, structured errors and correlation ids
    public async Task Platform_endpoints_report_state()
    {
        var repo = await f.NewRepoAsync("Dash App", new() { ["a.txt"] = "a\n" });
        var dash = await GetAsync<DashboardSummaryDto>(f.Admin, "/api/v1/dashboard/summary");
        Assert.True(dash.TotalRepositories >= 1);
        Assert.True(dash.CommitsToday >= 1);
        var health = await GetAsync<SystemHealthDto>(f.Admin, "/api/v1/system/health");
        Assert.Contains(health.Components, c => c.Name == "database" && c.Status == "Healthy");
        Assert.Contains(health.Components, c => c.Name == "git" && c.Status == "Healthy");
        Assert.Equal(HttpStatusCode.OK, (await f.CreateClient().GetAsync("/health/ready")).StatusCode);

        var openapi = await f.CreateClient().GetStringAsync("/openapi/v1.json");
        Assert.Contains("/api/v1/repositories/{id}/changes/begin", openapi);
        Assert.Contains("/api/v1/repositories/{id}/recovery/preview", openapi);

        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/repositories/{Guid.NewGuid()}");
        req.Headers.Add("X-Correlation-Id", "test-corr-123");
        var missing = await f.AgentHttp.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("test-corr-123", missing.Headers.GetValues("X-Correlation-Id").Single());
        var problem = await missing.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("not_found", problem.GetProperty("code").GetString());
        Assert.Equal("test-corr-123", problem.GetProperty("correlationId").GetString());

        var audit = await GetAsync<PagedResult<AuditLogDto>>(f.Admin, $"/api/v1/audit-logs?repositoryId={repo.RepositoryId}");
        Assert.Contains(audit.Items, a => a.Action == "repository.created");
    }

    [Fact] // Monitoring: missing working directory and missing verified backup raise alerts; resolution when cleared
    public async Task Monitoring_raises_and_resolves_alerts()
    {
        var repo = await f.NewRepoAsync("Monitored App", new() { ["a.txt"] = "a\n" });
        var hidden = repo.WorkingDirectory + ".moved";
        Directory.Move(repo.WorkingDirectory, hidden);
        await f.WithScopeAsync(sp => sp.GetRequiredService<MonitoringService>().RunChecksAsync(default));
        var alerts = await GetAsync<List<AlertDto>>(f.Admin, $"/api/v1/alerts?repositoryId={repo.RepositoryId}");
        Assert.Contains(alerts, a => a.Category == "working-directory-missing" && a.Severity == AlertSeverity.Warning);

        Directory.Move(hidden, repo.WorkingDirectory);
        await f.WithScopeAsync(sp => sp.GetRequiredService<MonitoringService>().RunChecksAsync(default));
        alerts = await GetAsync<List<AlertDto>>(f.Admin, $"/api/v1/alerts?repositoryId={repo.RepositoryId}");
        Assert.DoesNotContain(alerts, a => a.Category == "working-directory-missing");
        Assert.Contains(alerts, a => a.Category == "unverified-backup"); // no verified backup yet
        var storage = await GetAsync<StorageReportDto>(f.Admin, "/api/v1/system/storage");
        Assert.Contains(storage.Volumes, v => v.Purpose == "workspace");
        Assert.Contains(storage.Repositories, r => r.RepositoryId == repo.RepositoryId && r.GitObjectBytes > 0);
    }

    [Fact] // File-system watching supplements the change API: external edits are detected and (optionally) checkpointed
    public async Task File_watcher_detects_and_checkpoints_external_changes()
    {
        var repo = await f.NewRepoAsync("Watched App", new() { ["a.txt"] = "a\n" });
        var detail = await f.Agent.Repositories.GetAsync(repo.RepositoryId);
        var settings = detail.Settings with { AutoCommitExternalChanges = true };
        Assert.Equal(System.Net.HttpStatusCode.OK, (await f.Admin.PatchAsJsonAsync($"/api/v1/repositories/{repo.RepositoryId}", new UpdateRepositoryRequest { Settings = settings }, GitManagerFactory.Json)).StatusCode);

        var watcher = new FileWatcherService(f.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new MonitoringOptions { FileWatchDebounceMilliseconds = 500 }), NullLogger<FileWatcherService>.Instance);
        await watcher.StartAsync(default);
        try
        {
            await Task.Delay(4000); // watcher registration
            Write(repo.WorkingDirectory, "edited-in-ide.txt", "a developer edited this outside Wings Code\n");
            await Eventually(() => Task.FromResult(Git(repo.WorkingDirectory, "log", "-1", "--format=%s").Contains("record changes made outside Wings Code")),
                TimeSpan.FromSeconds(30), "external change was not checkpointed");
            Assert.Empty(Git(repo.WorkingDirectory, "status", "--porcelain"));
            var cps = await f.Agent.Repositories.CheckpointsAsync(repo.RepositoryId);
            Assert.Contains(cps.Items, c => c.Type == CheckpointType.External);
            await Eventually(() => Task.FromResult(Git(f.BareRemote("watched-app"), "rev-parse", "main") == Git(repo.WorkingDirectory, "rev-parse", "HEAD")),
                TimeSpan.FromSeconds(30), "external checkpoint was not pushed");
        }
        finally
        {
            await watcher.StopAsync(default);
        }
    }
}
