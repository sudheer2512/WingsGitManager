using Microsoft.EntityFrameworkCore;
using Wings.GitManager.Contracts;
using Wings.GitManager.Core.Commits;
using Wings.GitManager.Core.Entities;
using Wings.GitManager.Infrastructure.Data;

namespace Wings.GitManager.Infrastructure.Services;

public sealed record CommitAttribution(
    Guid? OperationId = null, Guid? SessionId = null, string? AgentId = null, string? InitiatingUser = null,
    string? UserRequestRef = null, string? BuildStatus = null, string? TestStatus = null, string? CommitType = null);

/// <summary>Stores structured commit metadata (Git stays authoritative; this is the searchable index).</summary>
public sealed class CommitRecorder(GitManagerDbContext db, RepositoryContext ctx)
{
    public async Task<CommitMetadata> RecordAsync(Repository repo, string dir, string branch, string sha, CommitAttribution? attribution, CancellationToken ct)
    {
        var existing = await db.CommitMetadata.Include(c => c.Files).FirstOrDefaultAsync(c => c.RepositoryId == repo.Id && c.Sha == sha, ct)
            ?? db.CommitMetadata.Local.FirstOrDefault(c => c.RepositoryId == repo.Id && c.Sha == sha);
        if (existing is not null)
        {
            if (attribution is not null) Apply(existing, attribution);
            return existing;
        }
        var log = await ctx.Git.GetCommitAsync(dir, sha, ct) ?? throw new InvalidOperationException($"Commit {sha} not found");
        var files = await ctx.Git.CommitFilesAsync(dir, sha, ct);
        var message = log.Subject + (string.IsNullOrEmpty(log.Body) ? "" : "\n\n" + log.Body);
        var trailers = CommitMessageBuilder.ParseTrailers(message);
        var meta = new CommitMetadata
        {
            RepositoryId = repo.Id, Sha = sha, Branch = branch, Subject = Trunc(log.Subject, 440), Message = message,
            AuthorName = Trunc(log.AuthorName, 440), AuthorEmail = Trunc(log.AuthorEmail, 440), CommittedAt = log.CommittedAt,
            FilesChanged = files.Count, Additions = files.Sum(f => f.Additions), Deletions = files.Sum(f => f.Deletions),
            IsAutomated = trailers.ContainsKey(CommitMessageBuilder.OperationTrailer) || trailers.ContainsKey(CommitMessageBuilder.CheckpointTrailer)
                || log.AuthorEmail.EndsWith(ctx.Options.AgentAuthorEmailDomain, StringComparison.OrdinalIgnoreCase)
                || log.AuthorEmail.Equals(ctx.Options.ServiceCommitterEmail, StringComparison.OrdinalIgnoreCase),
            Files = files.Take(2000).Select(f => new CommitFileChange { Path = f.Path, OldPath = f.OldPath, Kind = f.Kind, Additions = f.Additions, Deletions = f.Deletions }).ToList()
        };
        // Trailers let commits created by other tools (or before a crash) be attributed after the fact.
        if (trailers.TryGetValue(CommitMessageBuilder.OperationTrailer, out var op) && Guid.TryParse(op, out var opId)) meta.ChangeOperationId = opId;
        if (trailers.TryGetValue(CommitMessageBuilder.SessionTrailer, out var ses) && Guid.TryParse(ses, out var sesId)) meta.SessionId = sesId;
        if (trailers.TryGetValue(CommitMessageBuilder.AgentTrailer, out var agent)) meta.AgentId = agent;
        if (trailers.TryGetValue(CommitMessageBuilder.UserTrailer, out var user)) meta.InitiatingUser = user;
        if (trailers.TryGetValue(CommitMessageBuilder.RequestTrailer, out var req)) meta.UserRequestRef = req;
        var colon = log.Subject.IndexOf(':');
        if (colon is > 0 and < 12) meta.CommitType = log.Subject[..colon].Split('(')[0];
        if (attribution is not null) Apply(meta, attribution);
        db.CommitMetadata.Add(meta);
        if (branch == repo.DefaultBranch && (repo.LastCommitAt is null || meta.CommittedAt >= repo.LastCommitAt))
        {
            repo.LastCommitAt = meta.CommittedAt;
            repo.LastCommitSha = sha;
        }
        return meta;
    }

    /// <summary>Records every commit in (from, to] (first-parent and merged commits).</summary>
    public async Task<IReadOnlyList<CommitMetadata>> RecordRangeAsync(Repository repo, string dir, string branch, string? from, string to, CommitAttribution? attribution, CancellationToken ct)
    {
        var range = from is null ? new[] { to } : new[] { $"{from}..{to}" };
        var entries = await ctx.Git.LogAsync(dir, range, 500, 0, ct, extra: ["--reverse"]);
        var list = new List<CommitMetadata>();
        foreach (var e in entries) list.Add(await RecordAsync(repo, dir, branch, e.Sha, attribution, ct));
        return list;
    }

    private static void Apply(CommitMetadata m, CommitAttribution a)
    {
        m.ChangeOperationId = a.OperationId ?? m.ChangeOperationId;
        m.SessionId = a.SessionId ?? m.SessionId;
        m.AgentId = a.AgentId ?? m.AgentId;
        m.InitiatingUser = a.InitiatingUser ?? m.InitiatingUser;
        m.UserRequestRef = a.UserRequestRef ?? m.UserRequestRef;
        m.BuildStatus = a.BuildStatus ?? m.BuildStatus;
        m.TestStatus = a.TestStatus ?? m.TestStatus;
        m.CommitType = a.CommitType ?? m.CommitType;
        if (a.OperationId is not null || a.AgentId is not null) m.IsAutomated = true;
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n];
}

public sealed class CheckpointService(GitManagerDbContext db, RepositoryContext ctx)
{
    public async Task<Checkpoint> CreateAsync(Repository repo, string dir, string branch, string sha, CheckpointType type, string? description,
        CommitAttribution attribution, CancellationToken ct)
    {
        var known = await db.Branches.AsNoTracking().Where(b => b.RepositoryId == repo.Id && b.Name == branch).Select(b => b.RemoteHeadSha).FirstOrDefaultAsync(ct);
        var state = DurabilityState.LocalCommit;
        DateTime? verifiedAt = null;
        if (known is not null && (known == sha || await ctx.Git.ObjectExistsAsync(dir, known, ct) && await ctx.Git.IsAncestorAsync(dir, sha, known, ct)))
        {
            // Remote containment was verified earlier; still re-verified before high-risk work.
            state = DurabilityState.RemoteSynchronized;
            verifiedAt = DateTime.UtcNow;
        }
        var cp = new Checkpoint
        {
            RepositoryId = repo.Id, ProjectId = repo.ProjectId, UserId = attribution.InitiatingUser, SessionId = attribution.SessionId,
            AgentId = attribution.AgentId, ChangeOperationId = attribution.OperationId, Branch = branch, CommitSha = sha, Type = type,
            Description = description, SyncState = state, RemoteVerifiedAt = verifiedAt,
            BuildStatus = attribution.BuildStatus, TestStatus = attribution.TestStatus
        };
        db.Checkpoints.Add(cp);
        repo.LatestCheckpointId = cp.Id;
        return cp;
    }

    public static CheckpointDto ToDto(Checkpoint c) => new()
    {
        Id = c.Id, RepositoryId = c.RepositoryId, ProjectId = c.ProjectId, UserId = c.UserId, SessionId = c.SessionId, AgentId = c.AgentId,
        ChangeOperationId = c.ChangeOperationId, Branch = c.Branch, CommitSha = c.CommitSha, CreatedAt = c.CreatedAt, Description = c.Description,
        Type = c.Type, SyncState = c.SyncState, BackupState = c.BackupState, RemoteVerifiedAt = c.RemoteVerifiedAt,
        BuildStatus = c.BuildStatus, TestStatus = c.TestStatus
    };
}
