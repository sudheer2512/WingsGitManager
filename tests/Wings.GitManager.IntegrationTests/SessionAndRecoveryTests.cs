using System.Net;
using Wings.GitManager.Client;
using Wings.GitManager.Contracts;
using static Wings.GitManager.IntegrationTests.GitManagerFactory;

namespace Wings.GitManager.IntegrationTests;

[Collection(ApiCollection.Name)]
public class SessionAndRecoveryTests(GitManagerFactory f)
{
    private async Task<CompleteChangeResponse> ChangeAsync(Guid repoId, string agent, Guid? session, string description, Action<string> edit)
    {
        var begin = await f.Agent.Changes.BeginAsync(repoId, new BeginChangeRequest { AgentId = agent, SessionId = session, Description = description });
        edit(begin.WorkingDirectory);
        return await f.Agent.Changes.CompleteAsync(repoId, begin.OperationId, new CompleteChangeRequest());
    }

    [Fact] // Scenarios 12 & 13: concurrent sessions, isolated worktrees, ownership, no cross-commits
    public async Task Concurrent_ai_sessions_work_in_isolated_worktrees()
    {
        var repo = await f.NewRepoAsync("Parallel App", new() { ["shared.txt"] = "base\n" });
        var a = await f.Agent.Sessions.StartAsync(new StartSessionRequest { RepositoryId = repo.RepositoryId, AgentId = "agent-a", Title = "Ledger" });
        var b = await f.Agent.Sessions.StartAsync(new StartSessionRequest { RepositoryId = repo.RepositoryId, AgentId = "agent-b", Title = "Reports" });

        Assert.NotEqual(a.WorkingDirectory, b.WorkingDirectory);
        Assert.StartsWith("ai/session-", a.Branch);
        Assert.Equal(Path.Combine(f.Workspace, "acme", "parallel-app", "worktrees", $"session-{a.Id.ToString("N")[..8]}"), a.WorkingDirectory);
        Assert.Equal(a.Branch, Git(a.WorkingDirectory, "branch", "--show-current"));

        // Agent B cannot work in agent A's session.
        var steal = await Assert.ThrowsAsync<WingsGitManagerException>(() => f.Agent.Changes.BeginAsync(repo.RepositoryId, new BeginChangeRequest { AgentId = "agent-b", SessionId = a.Id, Description = "x" }));
        Assert.Equal(HttpStatusCode.Forbidden, steal.StatusCode);

        // Both work at the same time.
        var opA = await f.Agent.Changes.BeginAsync(repo.RepositoryId, new BeginChangeRequest { AgentId = "agent-a", SessionId = a.Id, Description = "Add ledger" });
        var opB = await f.Agent.Changes.BeginAsync(repo.RepositoryId, new BeginChangeRequest { AgentId = "agent-b", SessionId = b.Id, Description = "Add reports" });
        var busy = await Assert.ThrowsAsync<WingsGitManagerException>(() => f.Agent.Changes.BeginAsync(repo.RepositoryId, new BeginChangeRequest { AgentId = "agent-a", SessionId = a.Id, Description = "second" }));
        Assert.Equal("workspace_busy", busy.Code);

        Write(opA.WorkingDirectory, "ledger.txt", "ledger\n");
        Write(opB.WorkingDirectory, "reports.txt", "reports\n");
        Assert.False(File.Exists(Path.Combine(b.WorkingDirectory, "ledger.txt")));
        var doneA = await f.Agent.Changes.CompleteAsync(repo.RepositoryId, opA.OperationId, new CompleteChangeRequest());
        var doneB = await f.Agent.Changes.CompleteAsync(repo.RepositoryId, opB.OperationId, new CompleteChangeRequest());
        Assert.Equal(["ledger.txt"], doneA.Files.Select(x => x.Path));
        Assert.Equal(["reports.txt"], doneB.Files.Select(x => x.Path));
        Assert.Equal(DurabilityState.RemoteSynchronized, doneA.Durability);
        Assert.Equal(doneA.CommitSha, Git(f.BareRemote("parallel-app"), "rev-parse", a.Branch));
        Assert.Equal(doneB.CommitSha, Git(f.BareRemote("parallel-app"), "rev-parse", b.Branch));
        Assert.Equal(repo.CommitSha, Git(repo.WorkingDirectory, "rev-parse", "main")); // main untouched
        Assert.Equal(repo.CommitSha, (await f.Agent.Repositories.GetAsync(repo.RepositoryId)).Summary.LastCommitSha); // session commits are not main's head

        // Dry run lists only what the merge brings in (three-dot), even though main and branch B diverge from A.
        var admin = new WingsGitManagerClient(f.Admin);
        var dry = await admin.Repositories.MergeAsync(repo.RepositoryId, new MergeBranchRequest { SourceBranch = a.Branch, TargetBranch = b.Branch, DryRun = true });
        Assert.Equal(MergeRequestState.Mergeable, dry.State);
        Assert.Equal(["ledger.txt"], dry.Files.Select(x => x.Path));

        var sa = await f.Agent.Sessions.GetAsync(a.Id);
        Assert.Equal(1, sa.CommitCount);
        Assert.Equal(1, sa.FilesModified);
        Assert.Equal(DurabilityState.RemoteSynchronized, sa.PushStatus);
        Assert.Equal(["ledger.txt"], (await f.Agent.Sessions.ChangesAsync(a.Id)).Files.Select(x => x.Path));
    }

    [Fact] // Scenario 14: branch conflicts detected; nothing changed on conflict; approval workflow
    public async Task Merges_detect_conflicts_and_respect_approval_policy()
    {
        var repo = await f.NewRepoAsync("Merge App", new() { ["config.txt"] = "line1\nline2\n" });
        var a = await f.Agent.Sessions.StartAsync(new StartSessionRequest { RepositoryId = repo.RepositoryId, AgentId = "agent-a" });
        var b = await f.Agent.Sessions.StartAsync(new StartSessionRequest { RepositoryId = repo.RepositoryId, AgentId = "agent-b" });
        await ChangeAsync(repo.RepositoryId, "agent-a", a.Id, "A edits config", d => Write(d, "config.txt", "line1 from A\nline2\n"));
        await ChangeAsync(repo.RepositoryId, "agent-b", b.Id, "B edits config", d => Write(d, "config.txt", "line1 from B\nline2\n"));

        // Protected main + service identity: approval required.
        var pending = await f.Agent.Repositories.MergeAsync(repo.RepositoryId, new MergeBranchRequest { SourceBranch = a.Branch, TargetBranch = "main", SessionId = a.Id });
        Assert.Equal(MergeRequestState.AwaitingApproval, pending.State);
        Assert.Equal(repo.CommitSha, Git(repo.WorkingDirectory, "rev-parse", "main"));
        var approved = await PostAsync<MergeResultDto>(f.Admin, $"/api/v1/repositories/{repo.RepositoryId}/merge-requests/{pending.MergeRequestId}/approve", new { });
        Assert.Equal(MergeRequestState.Merged, approved.State);
        Assert.Equal("line1 from A\nline2", File.ReadAllText(Path.Combine(repo.WorkingDirectory, "config.txt")).TrimEnd());
        Assert.Equal(DurabilityState.RemoteSynchronized, approved.Durability);
        Assert.Equal(SessionState.Merged, (await f.Agent.Sessions.GetAsync(a.Id)).State);

        var admin = new WingsGitManagerClient(f.Admin);
        var dry = await admin.Repositories.MergeAsync(repo.RepositoryId, new MergeBranchRequest { SourceBranch = b.Branch, TargetBranch = "main", DryRun = true });
        Assert.True(dry.DryRun);
        Assert.Equal(["config.txt"], dry.Conflicts);
        var mainBefore = Git(repo.WorkingDirectory, "rev-parse", "main");
        var conflict = await admin.Repositories.MergeAsync(repo.RepositoryId, new MergeBranchRequest { SourceBranch = b.Branch, TargetBranch = "main" });
        Assert.Equal(MergeRequestState.Conflict, conflict.State);
        Assert.Equal(mainBefore, Git(repo.WorkingDirectory, "rev-parse", "main"));
        Assert.Empty(Git(repo.WorkingDirectory, "status", "--porcelain"));
    }

    [Fact] // Scenarios 15, 27, 29: revert an AI session, keep unrelated later work, clean status
    public async Task Reverting_a_session_preserves_unrelated_changes()
    {
        var repo = await f.NewRepoAsync("Rollback App", new() { ["app.txt"] = "app v1\n", ["api.txt"] = "api v1\n" });
        var s = await f.Agent.Sessions.StartAsync(new StartSessionRequest { RepositoryId = repo.RepositoryId, AgentId = "agent-x", Isolation = SessionIsolation.SharedWorkingDirectory });
        await ChangeAsync(repo.RepositoryId, "agent-x", s.Id, "Rewrite app screen", d => Write(d, "app.txt", "app v2 (AI rewrite)\n"));
        await ChangeAsync(repo.RepositoryId, "agent-x", s.Id, "Add helper", d => Write(d, "helper.txt", "helper\n"));
        await f.Agent.Sessions.EndAsync(s.Id, new EndSessionRequest());
        // Unrelated later work by another agent.
        await ChangeAsync(repo.RepositoryId, "agent-y", null, "Improve API", d => Write(d, "api.txt", "api v2\n"));

        var preview = await f.Agent.Sessions.RecoveryPreviewAsync(s.Id);
        Assert.Equal(RecoveryType.RevertSession, preview.Type);
        Assert.True(preview.ApprovalRequired);
        Assert.Empty(preview.ExpectedConflicts);
        Assert.Equal(["app.txt", "helper.txt"], preview.AffectedFiles.Select(x => x.Path).OrderBy(x => x));

        var notApproved = await Assert.ThrowsAsync<WingsGitManagerException>(() => f.Agent.Recovery.ExecuteAsync(repo.RepositoryId, preview, approved: false));
        Assert.Equal("approval_required", notApproved.Code);

        var result = await f.Agent.Recovery.ExecuteAsync(repo.RepositoryId, preview, approved: true);
        Assert.Equal(RecoveryStatus.Succeeded, result.Status);
        Assert.Equal("app v1", File.ReadAllText(Path.Combine(repo.WorkingDirectory, "app.txt")).Trim());
        Assert.False(File.Exists(Path.Combine(repo.WorkingDirectory, "helper.txt")));
        Assert.Equal("api v2", File.ReadAllText(Path.Combine(repo.WorkingDirectory, "api.txt")).Trim());
        Assert.Empty(Git(repo.WorkingDirectory, "status", "--porcelain"));
        Assert.StartsWith("revert: reverse changes from AI session", Git(repo.WorkingDirectory, "log", "-1", "--format=%s"));
        await Fixture_EventuallySynced(repo, result.ResultCommitSha!);
        Assert.Equal(SessionState.Reverted, (await f.Agent.Sessions.GetAsync(s.Id)).State);
    }

    private Task Fixture_EventuallySynced(RepositoryProvisionResult repo, string sha) =>
        Eventually(() => Task.FromResult(Git(f.BareRemote(Wings.GitManager.Core.Safety.RepoPath.Slugify(Path.GetFileName(Path.GetDirectoryName(repo.WorkingDirectory)!))), "rev-parse", "main") == sha));

    [Fact] // Scenarios 16, 27, 30: selective restore, uncommitted work saved, status clean
    public async Task Restore_file_preserves_unrelated_and_uncommitted_changes()
    {
        var repo = await f.NewRepoAsync("Restore App", new() { ["src/ledger.ts"] = "ledger v1\n", ["src/api.ts"] = "api v1\n" });
        var v1 = repo.CommitSha!;
        await ChangeAsync(repo.RepositoryId, "agent", null, "Ledger v2", d => { Write(d, "src/ledger.ts", "ledger v2\n"); Write(d, "src/api.ts", "api v2\n"); });
        Write(repo.WorkingDirectory, "src/draft.ts", "uncommitted human draft\n");

        var preview = await f.Agent.Recovery.PreviewAsync(repo.RepositoryId, new RecoveryRequest { Type = RecoveryType.RestoreFile, RecoveryPoint = v1, Paths = ["src/ledger.ts"] });
        Assert.Equal(["src/ledger.ts"], preview.AffectedFiles.Select(x => x.Path));
        Assert.Contains("src/draft.ts", preview.UncommittedChanges);
        Assert.Equal(v1, preview.RecoveryCommit);

        var result = await f.Agent.Recovery.ExecuteAsync(repo.RepositoryId, preview, approved: true);
        Assert.Equal(RecoveryStatus.Succeeded, result.Status);
        Assert.NotNull(result.SafetyCheckpointId);
        Assert.Equal("ledger v1", File.ReadAllText(Path.Combine(repo.WorkingDirectory, "src/ledger.ts")).Trim());
        Assert.Equal("api v2", File.ReadAllText(Path.Combine(repo.WorkingDirectory, "src/api.ts")).Trim());
        Assert.Equal("uncommitted human draft", File.ReadAllText(Path.Combine(repo.WorkingDirectory, "src/draft.ts")).Trim());
        Assert.Contains("src/draft.ts", Git(repo.WorkingDirectory, "show", "--name-only", "--format=", "HEAD~1")); // saved in the safety checkpoint
        Assert.Empty(Git(repo.WorkingDirectory, "status", "--porcelain"));
    }

    [Fact] // Scenario C: restore deleted files
    public async Task Deleted_files_can_be_listed_and_restored()
    {
        var repo = await f.NewRepoAsync("Deleted App", new() { ["src/customer-ledger/ledger.page.ts"] = "ledger page\n", ["src/main.ts"] = "main\n" });
        await ChangeAsync(repo.RepositoryId, "agent", null, "Remove ledger page", d => Directory.Delete(Path.Combine(d, "src/customer-ledger"), true));

        var deleted = await f.Agent.Recovery.DeletedFilesAsync(repo.RepositoryId, "ledger");
        var d = Assert.Single(deleted);
        Assert.Equal("src/customer-ledger/ledger.page.ts", d.Path);

        var preview = await f.Agent.Recovery.PreviewAsync(repo.RepositoryId, new RecoveryRequest { Type = RecoveryType.RestoreDeleted, Paths = [d.Path] });
        var result = await f.Agent.Recovery.ExecuteAsync(repo.RepositoryId, preview, approved: true);
        Assert.Equal(RecoveryStatus.Succeeded, result.Status);
        Assert.Equal("ledger page", File.ReadAllText(Path.Combine(repo.WorkingDirectory, d.Path)).Trim());
    }

    [Fact] // Scenario B: project restore defaults to a side-by-side recovery worktree; rollback creates a new commit
    public async Task Restore_project_is_non_destructive_by_default()
    {
        var repo = await f.NewRepoAsync("Project Restore App", new() { ["a.txt"] = "a1\n" });
        var v1 = repo.CommitSha!;
        await ChangeAsync(repo.RepositoryId, "agent", null, "Restructure", d => { Write(d, "a.txt", "a2\n"); Write(d, "b.txt", "b\n"); });
        var head = Git(repo.WorkingDirectory, "rev-parse", "HEAD");

        var side = await f.Agent.Recovery.PreviewAsync(repo.RepositoryId, new RecoveryRequest { Type = RecoveryType.RestoreProject, RecoveryPoint = v1 });
        Assert.Equal(RecoveryStrategy.RecoveryBranchWorktree, side.Strategy);
        Assert.False(side.ApprovalRequired);
        var sideResult = await f.Agent.Recovery.ExecuteAsync(repo.RepositoryId, side, approved: false);
        Assert.Equal(RecoveryStatus.Succeeded, sideResult.Status);
        Assert.Equal(head, Git(repo.WorkingDirectory, "rev-parse", "HEAD")); // current code untouched
        Assert.Equal("a1", File.ReadAllText(Path.Combine(sideResult.TargetDirectory!, "a.txt")).Trim());
        Assert.Equal(DurabilityState.RemoteSynchronized, (await f.Agent.Recovery.GetAsync(repo.RepositoryId, sideResult.OperationId)).ResultDurability);

        var rollback = await f.Agent.Recovery.PreviewAsync(repo.RepositoryId, new RecoveryRequest { Type = RecoveryType.RestoreProject, RecoveryPoint = $"checkpoint:{(await f.Agent.Repositories.CheckpointsAsync(repo.RepositoryId)).Items.Single(c => c.Type == CheckpointType.Initial).Id}", Strategy = RecoveryStrategy.RollbackCommit });
        Assert.True(rollback.Destructive);
        var rb = await f.Agent.Recovery.ExecuteAsync(repo.RepositoryId, rollback, approved: true);
        Assert.Equal(Git(repo.WorkingDirectory, "rev-parse", $"{v1}^{{tree}}"), Git(repo.WorkingDirectory, "rev-parse", "HEAD^{tree}"));
        Assert.Equal(head, Git(repo.WorkingDirectory, "rev-parse", "HEAD~1")); // history preserved
        Assert.Empty(Git(repo.WorkingDirectory, "status", "--porcelain"));
        Assert.Equal(rb.ResultCommitSha, Git(repo.WorkingDirectory, "rev-parse", "HEAD"));
    }

    [Fact] // Scenario 17: recovery after the local project directory was deleted
    public async Task Deleted_local_project_is_recovered_from_the_central_repository()
    {
        var repo = await f.NewRepoAsync("Lost App", new() { ["src/app.ts"] = "important\n" });
        var done = await ChangeAsync(repo.RepositoryId, "agent", null, "More work", d => Write(d, "src/more.ts", "more\n"));
        Directory.Delete(repo.WorkingDirectory, true);

        var status = await f.Agent.Repositories.StatusAsync(repo.RepositoryId);
        Assert.False(status.WorkingDirectoryExists);
        var commits = await f.Agent.Repositories.CommitsAsync(repo.RepositoryId); // served from the server-side mirror
        Assert.Equal(done.CommitSha, commits.Items[0].Sha);
        var begin = await Assert.ThrowsAsync<WingsGitManagerException>(() => f.Agent.Changes.BeginAsync(repo.RepositoryId, new BeginChangeRequest { AgentId = "a", Description = "x" }));
        Assert.Equal("working_directory_missing", begin.Code);

        var candidates = await f.Agent.Recovery.CandidatesAsync(repo.RepositoryId, new RecoveryCandidatesRequest { Query = "Recover the project I accidentally deleted." });
        Assert.Equal(RecoveryType.CloneProject, candidates.Interpretation.SuggestedType);
        Assert.Equal(done.CommitSha, candidates.Candidates[0].CommitSha);

        var preview = await f.Agent.Recovery.PreviewAsync(repo.RepositoryId, new RecoveryRequest { Type = RecoveryType.CloneProject });
        Assert.Equal(repo.WorkingDirectory, preview.TargetDirectory);
        var started = await f.Agent.Recovery.ExecuteAsync(repo.RepositoryId, preview, approved: true);
        Assert.Equal(RecoveryStatus.Executing, started.Status);
        await f.WaitForJobAsync(started.JobId!.Value);
        var op = await f.Agent.Recovery.GetAsync(repo.RepositoryId, started.OperationId);
        Assert.Equal(RecoveryStatus.Succeeded, op.Status);
        Assert.Equal(done.CommitSha, op.ResultCommitSha);
        Assert.Equal("important", File.ReadAllText(Path.Combine(repo.WorkingDirectory, "src/app.ts")).Trim());
        Assert.Equal(done.CommitSha, Git(repo.WorkingDirectory, "rev-parse", "HEAD"));
        Assert.Equal("main", Git(repo.WorkingDirectory, "branch", "--show-current"));
        Assert.Empty(Git(repo.WorkingDirectory, "status", "--porcelain"));

        // The recovered directory is the working directory again.
        var next = await ChangeAsync(repo.RepositoryId, "agent", null, "Work after recovery", d => Write(d, "src/after.ts", "after\n"));
        Assert.Equal(DurabilityState.RemoteSynchronized, next.Durability);
    }

    [Fact] // Scenario 28: conversational recovery integration
    public async Task Conversational_recovery_finds_sessions_files_and_summaries()
    {
        var repo = await f.NewRepoAsync("Chat App", new() { ["src/billing/InvoiceCalculation.cs"] = "decimal Total => 100;\n", ["src/ui/home.ts"] = "home\n" });
        await ChangeAsync(repo.RepositoryId, "agent", null, "Change invoice calculation", d => Write(d, "src/billing/InvoiceCalculation.cs", "decimal Total => 120;\n"));
        var s1 = await f.Agent.Sessions.StartAsync(new StartSessionRequest { RepositoryId = repo.RepositoryId, AgentId = "agent-1", Isolation = SessionIsolation.SharedWorkingDirectory, Title = "older" });
        await ChangeAsync(repo.RepositoryId, "agent-1", s1.Id, "Home tweak", d => Write(d, "src/ui/home.ts", "home 2\n"));
        var s2 = await f.Agent.Sessions.StartAsync(new StartSessionRequest { RepositoryId = repo.RepositoryId, AgentId = "agent-2", Isolation = SessionIsolation.SharedWorkingDirectory, Title = "latest" });
        await ChangeAsync(repo.RepositoryId, "agent-2", s2.Id, "Home redesign", d => Write(d, "src/ui/home.ts", "home 3\n"));

        var undo = await f.Agent.Recovery.CandidatesAsync(repo.RepositoryId, new RecoveryCandidatesRequest { Query = "Undo the changes you made in the last session." });
        Assert.Equal(RecoveryType.RevertSession, undo.Interpretation.SuggestedType);
        Assert.Equal(s2.Id, undo.Candidates[0].SessionId);

        var file = await f.Agent.Recovery.CandidatesAsync(repo.RepositoryId, new RecoveryCandidatesRequest { Query = "Restore the invoice calculation code" });
        Assert.Equal(RecoveryType.RestoreFile, file.Interpretation.SuggestedType);
        Assert.Contains(file.Candidates, c => c.SuggestedPaths!.Contains("src/billing/InvoiceCalculation.cs") && c.CommitSha == repo.CommitSha);

        var summary = await f.Agent.Recovery.CandidatesAsync(repo.RepositoryId, new RecoveryCandidatesRequest { Query = "Show me what changed in the last three hours." });
        Assert.True(summary.Interpretation.ChangeSummaryOnly);
        Assert.Equal(4, summary.ChangeSummary!.CommitCount); // initial commit + 3 changes
        Assert.Contains("agent-2", summary.ChangeSummary.Agents);
        Assert.Contains("4 commit(s)", summary.ChangeSummary.Narrative);

        var before = await f.Agent.Recovery.CandidatesAsync(repo.RepositoryId, new RecoveryCandidatesRequest { Query = "Restore the project as it was before the home redesign" });
        Assert.Equal(RecoveryType.RestoreProject, before.Interpretation.SuggestedType);
        Assert.False(before.Ambiguous);
        Assert.Contains("Home redesign", before.Candidates[0].Description, StringComparison.OrdinalIgnoreCase);
        var none = await f.Agent.Recovery.CandidatesAsync(repo.RepositoryId, new RecoveryCandidatesRequest { Query = "Restore the project as it was before the zebra migration" });
        Assert.Empty(none.Candidates);
        Assert.False(none.Ambiguous);

        var back = await f.Agent.Recovery.CandidatesAsync(repo.RepositoryId, new RecoveryCandidatesRequest { Query = "The customer portal stopped working after your changes. Go back to the previous version." });
        Assert.Equal(RecoveryType.RevertCommits, back.Interpretation.SuggestedType);
        Assert.NotEmpty(back.Candidates);
        var preview = await f.Agent.Recovery.PreviewAsync(repo.RepositoryId, new RecoveryRequest { Type = RecoveryType.RevertCommits, CommitShas = back.Candidates[0].SuggestedCommits });
        Assert.Equal(["src/ui/home.ts"], preview.AffectedFiles.Select(x => x.Path));
    }
}
