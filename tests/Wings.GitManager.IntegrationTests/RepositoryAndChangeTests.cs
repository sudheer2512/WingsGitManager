using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wings.GitManager.Client;
using Wings.GitManager.Contracts;
using Wings.GitManager.Infrastructure.Services;
using static Wings.GitManager.IntegrationTests.GitManagerFactory;

namespace Wings.GitManager.IntegrationTests;

[Collection(ApiCollection.Name)]
public class RepositoryAndChangeTests(GitManagerFactory f)
{
    [Fact] // Scenarios 1 & 3: creation, initial commit, push, verification, idempotency
    public async Task Create_repository_initializes_commits_pushes_and_verifies()
    {
        var req = new CreateRepositoryRequest { CompanyId = f.AcmeId, ProjectExternalId = "proj-create-1", ProjectName = "Customer Portal" };
        var dir = Path.Combine(f.Workspace, "acme", "customer-portal", "main");
        Write(dir, "src/app.ts", "export const app = 1;\n");
        Write(dir, ".env", "API_KEY=very-secret\n");

        var r = await f.Agent.Repositories.CreateAsync(req);

        Assert.False(r.AlreadyExisted);
        Assert.Equal("main", r.Branch);
        Assert.Equal(DurabilityState.RemoteSynchronized, r.Durability);
        Assert.Equal(40, r.CommitSha!.Length);
        Assert.Equal(dir, r.WorkingDirectory);
        Assert.True(File.Exists(Path.Combine(dir, ".gitignore")));
        Assert.Equal(r.CommitSha, Git(f.BareRemote("customer-portal"), "rev-parse", "refs/heads/main"));
        Assert.Equal(r.RemoteUrl, Git(dir, "config", "--get", "remote.origin.url"));
        var tracked = Git(dir, "ls-files");
        Assert.Contains("src/app.ts", tracked);
        Assert.DoesNotContain(".env", tracked.Split('\n'));

        var again = await f.Agent.Repositories.CreateAsync(req);
        Assert.True(again.AlreadyExisted);
        Assert.Equal(r.RepositoryId, again.RepositoryId);
        Assert.Single(Directory.GetDirectories(Path.Combine(f.Remotes, "acme")), d => d.EndsWith("customer-portal.git"));

        var detail = await f.Agent.Repositories.GetAsync(r.RepositoryId);
        Assert.Equal(HostingProviderKind.FileSystem, detail.Provider);
        Assert.Equal(DurabilityState.RemoteSynchronized, detail.Summary.LatestCheckpointDurability);
    }

    [Fact] // Scenario 2: import with full history and all branches
    public async Task Import_existing_local_repository_preserves_history_and_branches()
    {
        var dir = Path.Combine(f.Workspace, "acme", "legacy-app", "main");
        Directory.CreateDirectory(dir);
        Git(dir, "init", "-q");
        for (var i = 1; i <= 3; i++)
        {
            Write(dir, "README.md", $"version {i}\n");
            Git(dir, "add", "-A");
            Git(dir, "commit", "-q", "-m", $"commit {i}");
        }
        Git(dir, "branch", "feature/ledger");
        var head = Git(dir, "rev-parse", "HEAD");

        var r = await f.Agent.Repositories.ImportAsync(new ImportRepositoryRequest { CompanyId = f.AcmeId, ProjectExternalId = "legacy-1", ProjectName = "Legacy App", LocalPath = dir });

        Assert.Equal(head, r.CommitSha);
        Assert.Equal(DurabilityState.RemoteSynchronized, r.Durability);
        var bare = f.BareRemote("legacy-app");
        Assert.Equal("3", Git(bare, "rev-list", "--count", "main"));
        Assert.Equal(head, Git(bare, "rev-parse", "refs/heads/feature/ledger"));
        var commits = await f.Agent.Repositories.CommitsAsync(r.RepositoryId);
        Assert.Equal(3, commits.Items.Count);
        Assert.Equal(["commit 3", "commit 2", "commit 1"], commits.Items.Select(c => c.Subject));
    }

    [Fact] // Import refuses repositories whose config could execute commands
    public async Task Import_rejects_repository_with_executable_configuration()
    {
        var dir = Path.Combine(f.Workspace, "acme", "evil-app", "main");
        Directory.CreateDirectory(dir);
        Git(dir, "init", "-q");
        Write(dir, "a.txt", "a");
        Git(dir, "add", "-A");
        Git(dir, "commit", "-q", "-m", "a");
        Git(dir, "config", "core.fsmonitor", "touch /tmp/wgm-pwned");

        var ex = await Assert.ThrowsAsync<WingsGitManagerException>(() => f.Agent.Repositories.ImportAsync(new ImportRepositoryRequest { CompanyId = f.AcmeId, ProjectExternalId = "evil-1", ProjectName = "Evil App", LocalPath = dir }));
        Assert.Equal("unsafe_repository", ex.Code);
    }

    [Fact] // Scenarios 4, 5, 7: modify, add, delete, post-change checkpoint, metadata, attribution
    public async Task Change_operation_commits_modifications_additions_and_deletions()
    {
        var repo = await f.NewRepoAsync("Invoice Service", new() { ["src/invoice.cs"] = "class Invoice { }\n", ["src/old.cs"] = "class Old { }\n", ["README.md"] = "x\n" });
        var begin = await f.Agent.Changes.BeginAsync(repo.RepositoryId, new BeginChangeRequest
        {
            AgentId = "claude-coder", Description = "Correct invoice tax calculation", InitiatingUser = "pat@acme.test", UserRequestRef = "chat-17",
            Intent = new ChangeIntent { FilesToModify = ["src/invoice.cs"], FilesToAdd = ["src/tax.cs"], FilesToDelete = ["src/old.cs"] }
        });
        Assert.True(begin.Authorized);
        Assert.Equal(RiskLevel.Medium, begin.Risk.Level);
        Assert.Equal(repo.CommitSha, begin.ExpectedHead);

        Write(begin.WorkingDirectory, "src/invoice.cs", "class Invoice { decimal Tax => 0.2m; }\n");
        Write(begin.WorkingDirectory, "src/tax.cs", "class Tax { }\n");
        File.Delete(Path.Combine(begin.WorkingDirectory, "src/old.cs"));

        var done = await f.Agent.Changes.CompleteAsync(repo.RepositoryId, begin.OperationId, new CompleteChangeRequest { BuildStatus = "succeeded", TestStatus = "passed" });

        Assert.Equal(ChangeOperationState.Completed, done.State);
        Assert.Equal(DurabilityState.RemoteSynchronized, done.Durability);
        Assert.Equal(3, done.Files.Count);
        Assert.Contains(done.Files, x => x.Path == "src/tax.cs" && x.Kind == FileChangeKind.Added);
        Assert.Contains(done.Files, x => x.Path == "src/old.cs" && x.Kind == FileChangeKind.Deleted);
        Assert.Contains(done.Files, x => x.Path == "src/invoice.cs" && x.Kind == FileChangeKind.Modified);
        Assert.StartsWith("fix: correct invoice tax calculation", done.CommitMessage);
        Assert.Equal(done.CommitSha, Git(f.BareRemote("invoice-service"), "rev-parse", "main"));
        Assert.Empty(Git(begin.WorkingDirectory, "status", "--porcelain"));

        // Automation is represented as such; the real user is recorded separately.
        Assert.Equal("Wings AI Agent (claude-coder)", Git(begin.WorkingDirectory, "log", "-1", "--format=%an"));
        Assert.Equal("Wings Git Manager", Git(begin.WorkingDirectory, "log", "-1", "--format=%cn"));
        var detail = await f.Agent.Repositories.CommitAsync(repo.RepositoryId, done.CommitSha!);
        Assert.Equal("pat@acme.test", detail.Commit.InitiatingUser);
        Assert.Equal(begin.OperationId, detail.Commit.ChangeOperationId);
        Assert.Equal("chat-17", detail.UserRequestRef);
        Assert.Equal("passed", detail.TestStatus);

        var cps = await f.Agent.Repositories.CheckpointsAsync(repo.RepositoryId);
        var post = Assert.Single(cps.Items, c => c.Type == CheckpointType.PostChange);
        Assert.Equal(done.CommitSha, post.CommitSha);
        Assert.Equal(DurabilityState.RemoteSynchronized, post.SyncState);
        Assert.Equal("claude-coder", post.AgentId);

        // Completing twice is idempotent.
        var replay = await f.Agent.Changes.CompleteAsync(repo.RepositoryId, begin.OperationId, new CompleteChangeRequest { BuildStatus = "succeeded", TestStatus = "passed" });
        Assert.Equal(done.CommitSha, replay.CommitSha);
        Assert.Equal("1", Git(begin.WorkingDirectory, "rev-list", "--count", $"{repo.CommitSha}..HEAD"));
    }

    [Fact] // Scenario 6: pre-existing uncommitted work becomes a separate pre-change checkpoint
    public async Task Pre_change_checkpoint_commits_unrelated_uncommitted_work_separately()
    {
        var repo = await f.NewRepoAsync("Checkpoint App", new() { ["a.txt"] = "a\n" });
        Write(repo.WorkingDirectory, "manual-edit.txt", "edited by a human outside Wings Code\n");

        var begin = await f.Agent.Changes.BeginAsync(repo.RepositoryId, new BeginChangeRequest { AgentId = "agent", Description = "Add b" });
        Assert.NotEqual(repo.CommitSha, begin.ExpectedHead);
        Assert.Equal(CheckpointType.PreChange, begin.PreChangeCheckpoint!.Type);
        Assert.StartsWith("checkpoint: before Add b", Git(repo.WorkingDirectory, "log", "-1", "--format=%s"));
        Assert.Contains("manual-edit.txt", Git(repo.WorkingDirectory, "show", "--name-only", "--format=", "HEAD"));

        Write(begin.WorkingDirectory, "b.txt", "b\n");
        var done = await f.Agent.Changes.CompleteAsync(repo.RepositoryId, begin.OperationId, new CompleteChangeRequest());
        Assert.Equal(["b.txt"], done.Files.Select(x => x.Path));
    }

    [Fact] // Scenario 8 + 9: high-risk blocking with remote verification, and audited administrator override
    public async Task High_risk_change_is_blocked_until_the_checkpoint_is_verified_on_the_remote()
    {
        var repo = await f.NewRepoAsync("Risky App", new() { ["src/legacy/a.cs"] = "a", ["src/legacy/b.cs"] = "b" });
        Write(repo.WorkingDirectory, "notes.txt", "uncommitted work that must be protected first\n");
        var bare = f.BareRemote("risky-app");
        var hidden = bare + ".offline";
        Directory.Move(bare, hidden); // central server unavailable
        try
        {
            var intent = new ChangeIntent { DirectoriesToDelete = ["src/legacy"] };
            var ex = await Assert.ThrowsAsync<WingsGitManagerException>(() =>
                f.Agent.Changes.BeginAsync(repo.RepositoryId, new BeginChangeRequest { AgentId = "agent", Description = "Remove legacy module", Intent = intent }));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, ex.StatusCode);
            Assert.Equal("high_risk_blocked", ex.Code);
            Assert.Contains("director", ex.Details!.Value.GetProperty("risk").GetProperty("reasons")[0].GetString());

            // The service identity cannot override; a human repository administrator can, and it is audited.
            var ex2 = await Assert.ThrowsAsync<WingsGitManagerException>(() => f.Agent.Changes.BeginAsync(repo.RepositoryId,
                new BeginChangeRequest { AgentId = "agent", Description = "Remove legacy module", Intent = intent, Override = new PolicyOverrideInput("please") }));
            Assert.Equal("high_risk_blocked", ex2.Code);
            var admin = new WingsGitManagerClient(f.Admin);
            var overridden = await admin.Changes.BeginAsync(repo.RepositoryId,
                new BeginChangeRequest { AgentId = "agent", Description = "Remove legacy module", Intent = intent, Override = new PolicyOverrideInput("Emergency fix approved by CTO") });
            Assert.True(overridden.OverrideApplied);
            await admin.Changes.AbortAsync(repo.RepositoryId, overridden.OperationId, new AbortChangeRequest { Mode = AbortMode.KeepChanges });
            Assert.True(f.WithDb(db => db.AuditLogs.Any(a => a.Action == "policy.override" && a.RepositoryId == repo.RepositoryId)));
            Assert.True(f.WithDb(db => db.AuditLogs.Any(a => a.Action == "change.blocked" && a.RepositoryId == repo.RepositoryId)));
            Assert.True(f.WithDb(db => db.ChangeOperations.Count(o => o.RepositoryId == repo.RepositoryId && o.State == ChangeOperationState.PolicyBlocked)) >= 2);
        }
        finally
        {
            Directory.Move(hidden, bare);
        }

        // Server back: the checkpoint (including the uncommitted notes) is pushed and verified before work starts.
        var begin = await f.Agent.Changes.BeginAsync(repo.RepositoryId, new BeginChangeRequest { AgentId = "agent", Description = "Remove legacy module", Intent = new ChangeIntent { DirectoriesToDelete = ["src/legacy"] } });
        Assert.Equal(RiskLevel.High, begin.Risk.Level);
        Assert.Equal(DurabilityState.RemoteSynchronized, begin.PreChangeCheckpoint!.SyncState);
        Assert.Equal(begin.ExpectedHead, Git(bare, "rev-parse", "main"));
        Assert.Contains("notes.txt", Git(bare, "ls-tree", "--name-only", "main"));
        Directory.Delete(Path.Combine(begin.WorkingDirectory, "src/legacy"), true);
        var done = await f.Agent.Changes.CompleteAsync(repo.RepositoryId, begin.OperationId, new CompleteChangeRequest());
        Assert.Equal(2, done.Files.Count(x => x.Kind == FileChangeKind.Deleted));
        Assert.Contains(done.RecoveryOptions, o => o.Contains(begin.ExpectedHead));
    }

    [Fact] // Scenarios 10 & 11: remote failure, durable queue, automatic retries, verification
    public async Task Commits_are_queued_when_the_remote_fails_and_pushed_when_it_returns()
    {
        var repo = await f.NewRepoAsync("Offline App", new() { ["a.txt"] = "a\n" });
        var bare = f.BareRemote("offline-app");
        Directory.Move(bare, bare + ".down");
        CompleteChangeResponse done;
        try
        {
            var begin = await f.Agent.Changes.BeginAsync(repo.RepositoryId, new BeginChangeRequest { AgentId = "agent", Description = "Add feature" });
            Write(begin.WorkingDirectory, "feature.txt", "feature\n");
            done = await f.Agent.Changes.CompleteAsync(repo.RepositoryId, begin.OperationId, new CompleteChangeRequest());
            Assert.Equal(DurabilityState.RemotePushPending, done.Durability);
            var queue = await f.Agent.Sync.QueueAsync(repo.RepositoryId);
            var job = Assert.Single(queue, q => q.CommitSha == done.CommitSha);
            Assert.Equal(PushJobState.RetryScheduled, job.State);
            Assert.NotNull(job.LastError);
            var cp = (await f.Agent.Repositories.CheckpointsAsync(repo.RepositoryId)).Items.First(c => c.CommitSha == done.CommitSha);
            Assert.NotEqual(DurabilityState.RemoteSynchronized, cp.SyncState); // never shown as remotely safe
        }
        finally
        {
            Directory.Move(bare + ".down", bare);
        }

        await Eventually(async () =>
        {
            var cp = (await f.Agent.Repositories.CheckpointsAsync(repo.RepositoryId)).Items.First(c => c.CommitSha == done.CommitSha);
            return cp.SyncState == DurabilityState.RemoteSynchronized;
        }, TimeSpan.FromSeconds(40), "queued push was not retried and verified");
        Assert.Equal(done.CommitSha, Git(bare, "rev-parse", "main"));
        Assert.Contains(await f.Agent.Sync.QueueAsync(repo.RepositoryId), q => q.CommitSha == done.CommitSha && q.State == PushJobState.Succeeded && q.RetryCount >= 1);
        Assert.True(f.WithDb(db => db.SyncAttempts.Count(a => a.RepositoryId == repo.RepositoryId && a.Outcome == PushJobState.RetryScheduled)) >= 1);
    }

    [Fact] // Scenario 22: never force-push over a diverged remote
    public async Task Diverged_remote_is_reported_as_conflict_without_force_pushing()
    {
        var repo = await f.NewRepoAsync("Diverge App", new() { ["a.txt"] = "a\n" });
        var bare = f.BareRemote("diverge-app");
        var other = Path.Combine(f.Root, "other-clone-" + Guid.NewGuid().ToString("N")[..6]);
        Git(f.Root, "clone", "-q", bare, other);
        Write(other, "someone-else.txt", "pushed directly by another developer\n");
        Git(other, "add", "-A");
        Git(other, "commit", "-q", "-m", "external work");
        Git(other, "push", "-q", "origin", "main");
        var externalHead = Git(other, "rev-parse", "HEAD");

        var begin = await f.Agent.Changes.BeginAsync(repo.RepositoryId, new BeginChangeRequest { AgentId = "agent", Description = "Local change" });
        Write(begin.WorkingDirectory, "local.txt", "local\n");
        var done = await f.Agent.Changes.CompleteAsync(repo.RepositoryId, begin.OperationId, new CompleteChangeRequest());

        Assert.Equal(DurabilityState.RemoteDiverged, done.Durability);
        Assert.Equal(externalHead, Git(bare, "rev-parse", "main")); // remote untouched
        var job = (await f.Agent.Sync.QueueAsync(repo.RepositoryId)).First(q => q.CommitSha == done.CommitSha);
        Assert.Equal(PushJobState.Conflict, job.State);
        Assert.Contains("No force push", job.LastError);
        Assert.Equal(RemoteStatus.Diverged, (await f.Agent.Sync.StatusAsync(repo.RepositoryId)).RemoteStatus);
        Assert.Equal(done.CommitSha, Git(begin.WorkingDirectory, "rev-parse", "HEAD")); // local work preserved
    }

    [Fact] // Scenario 21: secrets block the commit; excluded files are never staged
    public async Task Secret_scanning_blocks_commits_and_operation_can_be_completed_after_fixing()
    {
        var repo = await f.NewRepoAsync("Secret App", new() { ["a.txt"] = "a\n" });
        var begin = await f.Agent.Changes.BeginAsync(repo.RepositoryId, new BeginChangeRequest { AgentId = "agent", Description = "Add storage client" });
        Write(begin.WorkingDirectory, "src/storage.ts", "const awsKey = 'AKIAIOSFODNN7ABCDEFG';\n");
        Write(begin.WorkingDirectory, "certs/server.pem", "-----BEGIN PRIVATE KEY-----\n"); // ignored by the generated .gitignore
        Write(begin.WorkingDirectory, "deploy/id_ed25519", "-----BEGIN OPENSSH PRIVATE KEY-----\n"); // not ignored: excluded by policy

        var ex = await Assert.ThrowsAsync<WingsGitManagerException>(() => f.Agent.Changes.CompleteAsync(repo.RepositoryId, begin.OperationId, new CompleteChangeRequest()));
        Assert.Equal("secrets_detected", ex.Code);
        Assert.Equal(repo.CommitSha, Git(begin.WorkingDirectory, "rev-parse", "HEAD"));
        Assert.Empty(Git(begin.WorkingDirectory, "diff", "--cached", "--name-only"));
        Assert.Equal(ChangeOperationState.PolicyBlocked, (await f.Agent.Changes.GetAsync(repo.RepositoryId, begin.OperationId)).State);

        Write(begin.WorkingDirectory, "src/storage.ts", "const awsKey = process.env.AWS_KEY;\n");
        var done = await f.Agent.Changes.CompleteAsync(repo.RepositoryId, begin.OperationId, new CompleteChangeRequest { Summary = "add storage client" });
        Assert.Equal(["src/storage.ts"], done.Files.Select(x => x.Path));
        Assert.Contains("deploy/id_ed25519", done.SkippedFiles);
        Assert.DoesNotContain("pem", Git(begin.WorkingDirectory, "ls-files"));
        Assert.DoesNotContain("id_ed25519", Git(begin.WorkingDirectory, "ls-files"));
    }

    [Fact] // Scenario 24: interrupted operations are reconciled
    public async Task Interrupted_operations_can_be_reconciled()
    {
        var repo = await f.NewRepoAsync("Interrupted App", new() { ["a.txt"] = "a\n" });

        // (a) Lease expired while the agent was still editing.
        var op1 = await f.Agent.Changes.BeginAsync(repo.RepositoryId, new BeginChangeRequest { AgentId = "agent", Description = "Slow change" });
        Write(op1.WorkingDirectory, "slow.txt", "slow\n");
        await f.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<Wings.GitManager.Infrastructure.Data.GitManagerDbContext>();
            await db.ChangeOperations.Where(o => o.Id == op1.OperationId).ExecuteUpdateAsync(s => s.SetProperty(o => o.LeaseExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
            await sp.GetRequiredService<ChangeOperationService>().ReconcileAsync(startup: false, default);
        });
        Assert.Equal(ChangeOperationState.Interrupted, (await f.Agent.Changes.GetAsync(repo.RepositoryId, op1.OperationId)).State);
        var done1 = await f.Agent.Changes.CompleteAsync(repo.RepositoryId, op1.OperationId, new CompleteChangeRequest());
        Assert.Equal(ChangeOperationState.Completed, done1.State);
        Assert.Contains(done1.Files, x => x.Path == "slow.txt");

        // (b) Crash after git commit but before bookkeeping (state Committing).
        var op2 = await f.Agent.Changes.BeginAsync(repo.RepositoryId, new BeginChangeRequest { AgentId = "agent", Description = "Crashy change" });
        Write(op2.WorkingDirectory, "crash.txt", "crash\n");
        Git(op2.WorkingDirectory, "add", "-A");
        Git(op2.WorkingDirectory, "commit", "-q", "-m", $"feat: crashy change\n\nWings-Operation-Id: {op2.OperationId}");
        var sha = Git(op2.WorkingDirectory, "rev-parse", "HEAD");
        await f.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<Wings.GitManager.Infrastructure.Data.GitManagerDbContext>();
            await db.ChangeOperations.Where(o => o.Id == op2.OperationId).ExecuteUpdateAsync(s => s.SetProperty(o => o.State, ChangeOperationState.Committing));
            await sp.GetRequiredService<ChangeOperationService>().ReconcileAsync(startup: true, default);
        });
        var reconciled = await f.Agent.Changes.GetAsync(repo.RepositoryId, op2.OperationId);
        Assert.Equal(ChangeOperationState.Completed, reconciled.State);
        Assert.Equal(sha, reconciled.ResultCommitSha);
        await Eventually(async () => (await f.Agent.Sync.StatusAsync(repo.RepositoryId)).Branches.Single(b => b.Branch == "main").RemoteSha == sha);
    }

    [Fact] // Abort preserves the agent's work on a recovery branch and restores the starting point
    public async Task Abort_preserves_work_and_resets_workspace()
    {
        var repo = await f.NewRepoAsync("Abort App", new() { ["a.txt"] = "a\n" });
        var begin = await f.Agent.Changes.BeginAsync(repo.RepositoryId, new BeginChangeRequest { AgentId = "agent", Description = "Experiment" });
        Write(begin.WorkingDirectory, "a.txt", "changed\n");
        Write(begin.WorkingDirectory, "new.txt", "new\n");
        var abort = await f.Agent.Changes.AbortAsync(repo.RepositoryId, begin.OperationId, new AbortChangeRequest { Reason = "user cancelled" });
        Assert.Equal(ChangeOperationState.Aborted, abort.State);
        Assert.NotNull(abort.PreservedBranch);
        Assert.Equal("a\n", File.ReadAllText(Path.Combine(begin.WorkingDirectory, "a.txt")));
        Assert.False(File.Exists(Path.Combine(begin.WorkingDirectory, "new.txt")));
        Assert.Equal("changed", Git(begin.WorkingDirectory, "show", $"{abort.PreservedBranch}:a.txt"));
        Assert.Equal("new", Git(begin.WorkingDirectory, "show", $"{abort.PreservedBranch}:new.txt"));
        await Eventually(() => Task.FromResult(Directory.Exists(f.BareRemote("abort-app")) &&
                                                 Git(f.BareRemote("abort-app"), "branch", "--list", abort.PreservedBranch!).Length > 0));
    }

    [Fact] // Scenario 25: Idempotency-Key replay and mismatch detection
    public async Task Idempotency_keys_replay_responses_and_reject_reuse()
    {
        var body = new CreateRepositoryRequest { CompanyId = f.AcmeId, ProjectExternalId = "idem-1", ProjectName = "Idem App" };
        async Task<HttpResponseMessage> Send(object b, string key)
        {
            var msg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/repositories") { Content = JsonContent.Create(b) };
            msg.Headers.Add("Idempotency-Key", key);
            return await f.AgentHttp.SendAsync(msg);
        }
        var first = await Send(body, "key-1");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var second = await Send(body, "key-1");
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal("true", second.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        var mismatch = await Send(body with { ProjectName = "Other" }, "key-1");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, mismatch.StatusCode);
        Assert.Equal(1, f.WithDb(db => db.Repositories.Count(r => r.Name == "Idem App")));
    }

    [Fact] // Scenario 26: large repositories
    public async Task Handles_large_repositories()
    {
        var files = Enumerable.Range(0, 3000).ToDictionary(i => $"src/module{i % 30}/file{i}.cs", i => $"// file {i}\nclass C{i} {{ }}\n");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var repo = await f.NewRepoAsync("Big App", files);
        Assert.Equal(DurabilityState.RemoteSynchronized, repo.Durability);
        var begin = await f.Agent.Changes.BeginAsync(repo.RepositoryId, new BeginChangeRequest
        {
            AgentId = "agent", Description = "Rename namespaces", Intent = new ChangeIntent { EstimatedFileCount = 300, Categories = ["refactor"] }
        });
        foreach (var i in Enumerable.Range(0, 300)) Write(begin.WorkingDirectory, $"src/module{i % 30}/file{i}.cs", $"// file {i} v2\nclass C{i} {{ }}\n");
        var done = await f.Agent.Changes.CompleteAsync(repo.RepositoryId, begin.OperationId, new CompleteChangeRequest());
        Assert.Equal(300, done.Files.Count);
        var tree = await GetAsync<List<TreeEntryDto>>(f.AgentHttp, $"/api/v1/repositories/{repo.RepositoryId}/tree?path=src/module7");
        Assert.Equal(100, tree.Count);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(90), $"took {sw.Elapsed}");
    }
}
