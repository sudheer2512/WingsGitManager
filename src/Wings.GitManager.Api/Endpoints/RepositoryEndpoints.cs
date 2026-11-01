using Microsoft.AspNetCore.Mvc;
using Wings.GitManager.Contracts;
using Wings.GitManager.Infrastructure.Backup;
using Wings.GitManager.Infrastructure.Services;

namespace Wings.GitManager.Api.Endpoints;

public static class RepositoryEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/repositories").WithTags("Repositories");

        g.MapPost("", async (CreateRepositoryRequest req, RepositoryService svc, CancellationToken ct) =>
        {
            var result = await svc.CreateAsync(req, ct);
            return result.AlreadyExisted ? Results.Ok(result) : Results.Created($"/api/v1/repositories/{result.RepositoryId}", result);
        }).WithSummary("Create (or idempotently return) the repository for a Wings Code project");

        g.MapGet("", (RepositoryService svc, Guid? companyId, Guid? projectId, string? search, RepositoryState? state, int page = 1, int pageSize = 50, CancellationToken ct = default) =>
            svc.ListAsync(companyId, projectId, search, state, page, pageSize, ct));
        g.MapGet("/{id:guid}", (Guid id, RepositoryService svc, CancellationToken ct) => svc.GetAsync(id, ct));
        g.MapPatch("/{id:guid}", (Guid id, UpdateRepositoryRequest req, RepositoryService svc, CancellationToken ct) => svc.UpdateAsync(id, req, ct));

        g.MapPost("/import", async (ImportRepositoryRequest req, RepositoryService svc, CancellationToken ct) =>
        {
            var result = await svc.ImportAsync(req, ct);
            return result.AlreadyExisted ? Results.Ok(result) : Results.Created($"/api/v1/repositories/{result.RepositoryId}", result);
        }).WithSummary("Import an existing Git repository (local path or remote URL) without losing history");
        // Route from the specification; the id segment is ignored ("new" is conventional) because the repository does not exist yet.
        g.MapPost("/{id}/import", async (string id, ImportRepositoryRequest req, RepositoryService svc, CancellationToken ct) =>
        {
            var result = await svc.ImportAsync(req, ct);
            return result.AlreadyExisted ? Results.Ok(result) : Results.Created($"/api/v1/repositories/{result.RepositoryId}", result);
        }).ExcludeFromDescription();

        g.MapPost("/{id:guid}/clone", async (Guid id, CloneRepositoryRequest req, RecoveryService svc, CancellationToken ct) =>
            Results.Accepted(null, await svc.ExecuteAsync(id, new RecoveryRequest
            {
                Type = RecoveryType.CloneProject, TargetDirectory = req.TargetDirectory, Branch = req.Branch, RecoveryPoint = req.CommitSha, Approved = true
            }, RecoveryType.CloneProject, ct))).WithSummary("Clone the central repository into a new authorized directory (asynchronous job)");

        g.MapPost("/{id:guid}/archive", async (Guid id, RepositoryService svc, CancellationToken ct) => { await svc.ArchiveAsync(id, ct); return Results.NoContent(); });
        g.MapDelete("/{id:guid}", async (Guid id, RepositoryService svc, bool purgeRemote = false, CancellationToken ct = default) =>
        {
            await svc.DeleteAsync(id, purgeRemote, ct);
            return Results.NoContent();
        }).WithSummary("Remove from Wings (remote archived; purging the remote requires a system administrator and a verified backup)");

        g.MapGet("/{id:guid}/status", (Guid id, RepositoryService svc, CancellationToken ct) => svc.StatusAsync(id, ct));
        g.MapGet("/{id:guid}/diff", async (Guid id, BrowseService browse, SyncOperations ops, string? from, string? to, string? path, bool patch = true, CancellationToken ct = default) =>
            from is null && to is null ? await ops.WorkingTreeDiffAsync(id, patch, ct) : await browse.DiffAsync(id, from ?? "HEAD", to ?? "HEAD", path, patch, ct))
            .WithSummary("Uncommitted changes (no from/to) or the diff between two revisions");

        // ---- Change operations ----
        var c = g.MapGroup("/{id:guid}/changes").WithTags("Change operations");
        c.MapPost("/begin", (Guid id, BeginChangeRequest req, ChangeOperationService svc, CancellationToken ct) => svc.BeginAsync(id, req, ct))
            .WithSummary("Before modifying code: policy evaluation, pre-change checkpoint, workspace lease");
        c.MapPost("/{operationId:guid}/complete", (Guid id, Guid operationId, CompleteChangeRequest req, ChangeOperationService svc, CancellationToken ct) => svc.CompleteAsync(id, operationId, req, ct))
            .WithSummary("After modifying code: stage the operation's files, scan, commit, push and verify");
        c.MapPost("/{operationId:guid}/abort", (Guid id, Guid operationId, AbortChangeRequest req, ChangeOperationService svc, CancellationToken ct) => svc.AbortAsync(id, operationId, req, ct));
        c.MapGet("/{operationId:guid}", (Guid id, Guid operationId, ChangeOperationService svc, CancellationToken ct) => svc.GetAsync(id, operationId, ct));
        c.MapGet("", (Guid id, ChangeOperationService svc, ChangeOperationState? state, int page = 1, int pageSize = 50, CancellationToken ct = default) => svc.ListAsync(id, state, page, pageSize, ct));

        // ---- Commits & history ----
        var h = g.MapGroup("/{id:guid}").WithTags("Commits and files");
        h.MapPost("/commits", (Guid id, CreateCommitRequest req, SyncOperations svc, CancellationToken ct) => svc.CommitAsync(id, req, ct));
        h.MapGet("/commits", (Guid id, BrowseService svc, [FromQuery(Name = "ref")] string? rev, string? path, DateTime? since, DateTime? until, string? author, Guid? sessionId, int page = 1, int pageSize = 50, CancellationToken ct = default) =>
            svc.CommitsAsync(id, rev, path, since, until, author, sessionId, page, pageSize, ct));
        h.MapGet("/commits/{sha}", (Guid id, string sha, BrowseService svc, CancellationToken ct) => svc.CommitAsync(id, sha, ct));
        h.MapGet("/commits/{sha}/diff", async (Guid id, string sha, BrowseService svc, string? path, bool patch = true, CancellationToken ct = default) =>
        {
            var commit = await svc.CommitAsync(id, sha, ct);
            var parent = commit.Commit.Parents.FirstOrDefault() ?? EmptyTree;
            return await svc.DiffAsync(id, parent, commit.Commit.Sha, path, patch, ct);
        });
        h.MapGet("/compare", (Guid id, BrowseService svc, [FromQuery(Name = "base")] string baseRef, string head, string? path, bool patch = true, CancellationToken ct = default) =>
            svc.DiffAsync(id, baseRef, head, path, patch, ct));
        h.MapGet("/tree", (Guid id, BrowseService svc, [FromQuery(Name = "ref")] string? rev, string? path, CancellationToken ct) => svc.TreeAsync(id, rev, path, ct));
        h.MapGet("/files/content", (Guid id, BrowseService svc, [FromQuery(Name = "ref")] string? rev, string path, CancellationToken ct) => svc.FileAsync(id, rev, path, ct));
        h.MapGet("/files/raw", async (Guid id, BrowseService svc, [FromQuery(Name = "ref")] string? rev, string path, CancellationToken ct) =>
        {
            var (bytes, name) = await svc.RawFileAsync(id, rev, path, ct);
            return Results.File(bytes, "application/octet-stream", name);
        });
        h.MapGet("/files/history", (Guid id, BrowseService svc, string path, [FromQuery(Name = "ref")] string? rev, int page = 1, int pageSize = 50, CancellationToken ct = default) =>
            svc.CommitsAsync(id, rev, path, null, null, null, null, page, pageSize, ct));
        h.MapGet("/files/search", (Guid id, BrowseService svc, string q, [FromQuery(Name = "ref")] string? rev, CancellationToken ct) => svc.SearchFilesAsync(id, rev, q, ct));
        h.MapGet("/grep", (Guid id, BrowseService svc, string q, [FromQuery(Name = "ref")] string? rev, CancellationToken ct) => svc.GrepAsync(id, rev, q, ct));
        h.MapGet("/deleted-files", (Guid id, BrowseService svc, string? pathContains, [FromQuery(Name = "ref")] string? rev, int max = 200, CancellationToken ct = default) =>
            svc.DeletedFilesAsync(id, rev, pathContains, max, ct));
        h.MapGet("/checkpoints", (Guid id, SyncOperations svc, CheckpointType? type, Guid? sessionId, int page = 1, int pageSize = 50, CancellationToken ct = default) =>
            svc.CheckpointsAsync(id, type, sessionId, page, pageSize, ct)).WithTags("Recovery");

        // ---- Branches & worktrees ----
        var b = g.MapGroup("/{id:guid}").WithTags("Branches and worktrees");
        b.MapGet("/branches", (Guid id, BranchService svc, CancellationToken ct) => svc.ListAsync(id, ct));
        b.MapPost("/branches", (Guid id, CreateBranchRequest req, BranchService svc, CancellationToken ct) => svc.CreateAsync(id, req, ct));
        b.MapPost("/branches/merge", (Guid id, MergeBranchRequest req, BranchService svc, CancellationToken ct) => svc.MergeAsync(id, req, ct))
            .WithSummary("Controlled merge with conflict detection (dryRun supported; approval when policy requires)");
        b.MapGet("/merge-requests", (Guid id, BranchService svc, CancellationToken ct) => svc.ListMergeRequestsAsync(id, ct));
        b.MapPost("/merge-requests/{mergeRequestId:guid}/approve", (Guid id, Guid mergeRequestId, BranchService svc, bool approve = true, CancellationToken ct = default) =>
            svc.ApproveAsync(id, mergeRequestId, approve, ct));
        b.MapGet("/worktrees", (Guid id, WorktreeService svc, bool includeReleased = false, CancellationToken ct = default) => svc.ListAsync(id, includeReleased, ct));
        b.MapPost("/worktrees", (Guid id, CreateWorktreeRequest req, WorktreeService svc, CancellationToken ct) => svc.CreateAsync(id, req, ct));
        b.MapPost("/worktrees/{worktreeId:guid}/release", (Guid id, Guid worktreeId, WorktreeService svc, ReleaseWorktreeRequest? req, CancellationToken ct) =>
            svc.ReleaseAsync(id, worktreeId, req ?? new ReleaseWorktreeRequest(), ct));

        // ---- Synchronization ----
        var s = g.MapGroup("/{id:guid}/sync").WithTags("Synchronization");
        s.MapPost("/push", (Guid id, SyncOperations svc, PushRequest? req, CancellationToken ct) => svc.PushAsync(id, req ?? new PushRequest(), ct));
        s.MapPost("/fetch", (Guid id, SyncOperations svc, CancellationToken ct) => svc.FetchAsync(id, ct));
        s.MapGet("/status", (Guid id, SyncOperations svc, CancellationToken ct) => svc.StatusAsync(id, ct));
        s.MapGet("/queue", (Guid id, SyncOperations svc, PushJobState? state, int take = 100, CancellationToken ct = default) => svc.QueueAsync(id, state, take, ct));
        s.MapPost("/retry", async (Guid id, SyncOperations svc, RetryRequest? req, CancellationToken ct) => Results.Ok(new { retried = await svc.RetryAsync(id, req ?? new RetryRequest(), ct) }));

        // ---- Recovery ----
        var r = g.MapGroup("/{id:guid}/recovery").WithTags("Recovery");
        r.MapPost("/preview", (Guid id, RecoveryRequest req, RecoveryService svc, CancellationToken ct) => svc.PreviewAsync(id, req, ct))
            .WithSummary("Dry run: current vs recovery commit, affected files, conflicts, strategy and expected result");
        r.MapPost("/restore-file", (Guid id, RecoveryRequest req, RecoveryService svc, CancellationToken ct) =>
            svc.ExecuteAsync(id, req, req.Type == RecoveryType.RestoreDeleted ? RecoveryType.RestoreDeleted : RecoveryType.RestoreFile, ct));
        r.MapPost("/restore-project", async (Guid id, RecoveryRequest req, RecoveryService svc, CancellationToken ct) =>
        {
            var result = await svc.ExecuteAsync(id, req, RecoveryType.RestoreProject, ct);
            return result.JobId is not null && result.Status == RecoveryStatus.Executing ? Results.Accepted($"/api/v1/repositories/{id}/recovery/operations/{result.OperationId}", result) : Results.Ok(result);
        });
        r.MapPost("/revert-session", (Guid id, RecoveryRequest req, RecoveryService svc, CancellationToken ct) => svc.ExecuteAsync(id, req, RecoveryType.RevertSession, ct));
        r.MapPost("/revert-commits", (Guid id, RecoveryRequest req, RecoveryService svc, CancellationToken ct) => svc.ExecuteAsync(id, req, RecoveryType.RevertCommits, ct));
        r.MapPost("/clone", async (Guid id, RecoveryRequest req, RecoveryService svc, CancellationToken ct) =>
        {
            var result = await svc.ExecuteAsync(id, req, RecoveryType.CloneProject, ct);
            return result.Status == RecoveryStatus.Executing ? Results.Accepted($"/api/v1/repositories/{id}/recovery/operations/{result.OperationId}", result) : Results.Ok(result);
        }).WithSummary("Recover a deleted local project from the central repository (asynchronous job)");
        r.MapGet("/operations/{operationId:guid}", (Guid id, Guid operationId, RecoveryService svc, CancellationToken ct) => svc.GetAsync(id, operationId, ct));
        r.MapGet("/operations", (Guid id, RecoveryService svc, CancellationToken ct) => svc.ListAsync(id, ct));
        r.MapPost("/candidates", (Guid id, RecoveryCandidatesRequest req, ConversationalRecoveryService svc, CancellationToken ct) => svc.CandidatesAsync(id, req, ct))
            .WithSummary("Conversational recovery: interpret a request and return ranked recovery points");
        g.MapGet("/{id:guid}/changes-summary", (Guid id, ConversationalRecoveryService svc, DateTime? since, DateTime? until, double? hours, CancellationToken ct) =>
            svc.SummaryAsync(id, since ?? DateTime.UtcNow.AddHours(-(hours ?? 24)), until ?? DateTime.UtcNow, ct)).WithTags("Recovery");

        // ---- Backups ----
        var k = g.MapGroup("/{id:guid}/backups").WithTags("Backups");
        k.MapPost("", async (Guid id, BackupService svc, CancellationToken ct) => Results.Accepted(null, await svc.RequestBackupAsync(id, "on-demand", ct)));
        k.MapGet("", (Guid id, BackupService svc, CancellationToken ct) => svc.ListAsync(id, ct));
        k.MapPost("/{backupId:guid}/verify", async (Guid id, Guid backupId, BackupService svc, CancellationToken ct) => Results.Accepted(null, await svc.RequestVerifyAsync(id, backupId, ct)));
        k.MapPost("/{backupId:guid}/restore", async (Guid id, Guid backupId, RestoreBackupRequest req, BackupService svc, CancellationToken ct) =>
            Results.Accepted(null, await svc.RequestRestoreAsync(id, backupId, req, ct)));

        // ---- Permissions ----
        g.MapGet("/{id:guid}/permissions", (Guid id, AdminService svc, CancellationToken ct) => svc.PermissionsAsync(id, ct)).WithTags("Administration");
        g.MapPut("/{id:guid}/permissions", async (Guid id, SetRepositoryPermissionRequest req, AdminService svc, CancellationToken ct) => { await svc.SetPermissionAsync(id, req, ct); return Results.NoContent(); }).WithTags("Administration");
        g.MapDelete("/{id:guid}/permissions/{userId:guid}", async (Guid id, Guid userId, AdminService svc, CancellationToken ct) => { await svc.RemovePermissionAsync(id, userId, ct); return Results.NoContent(); }).WithTags("Administration");
    }

    /// <summary>Git's well-known empty tree (diff base for root commits).</summary>
    public const string EmptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
}
