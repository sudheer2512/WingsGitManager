using Microsoft.EntityFrameworkCore;
using Wings.GitManager.Contracts;
using Wings.GitManager.Core;
using Wings.GitManager.Core.Recovery;
using Wings.GitManager.Core.Safety;
using Wings.GitManager.Infrastructure.Data;
using Wings.GitManager.Infrastructure.Git;

namespace Wings.GitManager.Infrastructure.Services;

/// <summary>
/// Turns conversational requests ("undo the changes you made in the last session", "restore the invoice
/// calculation code from yesterday") into ranked, machine-readable recovery candidates that Wings Code can
/// explain to the user before asking for approval. It never executes anything itself.
/// </summary>
public sealed class ConversationalRecoveryService(GitManagerDbContext db, BrowseService browse, RepositoryContext ctx)
{
    public async Task<RecoveryCandidatesResponse> CandidatesAsync(Guid repositoryId, RecoveryCandidatesRequest req, CancellationToken ct)
    {
        var (repo, dir) = await browse.ReadSourceAsync(repositoryId, ct);
        var now = DateTime.UtcNow;
        var parsed = RecoveryIntentParser.Parse(req.Query, now);
        var interp = parsed with
        {
            Since = req.Since ?? parsed.Since,
            Until = req.Until ?? parsed.Until,
            Keywords = req.PathContains is { Length: > 0 } pc ? parsed.Keywords.Append(pc.ToLowerInvariant()).Distinct().ToList() : parsed.Keywords,
            SuggestedType = req.SessionId is not null && parsed.SuggestedType is null or RecoveryType.RevertCommits ? RecoveryType.RevertSession : parsed.SuggestedType
        };
        var limit = Math.Clamp(req.Limit, 1, 50);

        if (interp.ChangeSummaryOnly)
        {
            var summary = await SummaryAsync(repositoryId, interp.Since ?? now.AddHours(-24), interp.Until ?? now, ct);
            return new RecoveryCandidatesResponse { Interpretation = interp, ChangeSummary = summary };
        }

        var candidates = interp.SuggestedType switch
        {
            RecoveryType.RevertSession => await SessionCandidatesAsync(repositoryId, req, interp, ct),
            RecoveryType.RestoreFile => await FileCandidatesAsync(repo.Id, dir, repo.DefaultBranch, interp, ct),
            RecoveryType.RestoreDeleted => await DeletedCandidatesAsync(repositoryId, interp, ct),
            RecoveryType.RestoreProject => await RefactorCandidatesAsync(repositoryId, dir, repo.DefaultBranch, interp, ct),
            RecoveryType.CloneProject => await CloneCandidatesAsync(repositoryId, dir, repo.DefaultBranch, ct),
            _ => await OperationCandidatesAsync(repositoryId, interp, ct),
        };
        var ordered = candidates.OrderByDescending(c => c.Score).ThenByDescending(c => c.Timestamp).Take(limit).ToList();
        var ambiguous = ordered.Count > 1 && ordered[0].Score - ordered[1].Score < 0.15;
        return new RecoveryCandidatesResponse { Interpretation = interp, Candidates = ordered, Ambiguous = ambiguous };
    }

    private async Task<List<RecoveryCandidateDto>> SessionCandidatesAsync(Guid repoId, RecoveryCandidatesRequest req, RecoveryInterpretationDto i, CancellationToken ct)
    {
        var q = db.AISessions.AsNoTracking().Where(s => s.RepositoryId == repoId && s.State != SessionState.Reverted);
        if (req.SessionId is not null) q = q.Where(s => s.Id == req.SessionId);
        if (req.AgentId is not null) q = q.Where(s => s.AgentId == req.AgentId);
        if (i.Since is not null) q = q.Where(s => s.LastActivityAt >= i.Since);
        if (i.Until is not null) q = q.Where(s => s.CreatedAt < i.Until);
        var sessions = await q.OrderByDescending(s => s.LastActivityAt).Take(20).ToListAsync(ct);
        var result = new List<RecoveryCandidateDto>();
        var rank = 0;
        foreach (var s in sessions)
        {
            var commits = await db.AISessionCommits.AsNoTracking().Where(c => c.SessionId == s.Id && !c.IsCheckpointOnly).OrderByDescending(c => c.Sequence).Select(c => c.Sha).ToListAsync(ct);
            if (commits.Count == 0) continue;
            var files = await db.CommitMetadata.AsNoTracking().Where(m => m.RepositoryId == repoId && commits.Contains(m.Sha)).SelectMany(m => m.Files.Select(f => f.Path)).Distinct().Take(50).ToListAsync(ct);
            var durability = await db.Checkpoints.AsNoTracking().Where(c => c.RepositoryId == repoId && c.CommitSha == s.StartingCommitSha).Select(c => (DurabilityState?)c.SyncState).FirstOrDefaultAsync(ct);
            result.Add(new RecoveryCandidateDto
            {
                RecoveryPoint = s.StartingCommitSha, CommitSha = commits[0], SessionId = s.Id, Timestamp = s.LastActivityAt, Durability = durability,
                Description = $"AI session by {s.AgentId}{(s.Title is null ? "" : $" — {s.Title}")}: {commits.Count} commit(s), {files.Count} file(s), {s.State}",
                MatchingPaths = files, Score = 1.0 - rank++ * 0.2, SuggestedType = RecoveryType.RevertSession, SuggestedCommits = commits.ToArray()
            });
        }
        return result;
    }

    private async Task<List<RecoveryCandidateDto>> OperationCandidatesAsync(Guid repoId, RecoveryInterpretationDto i, CancellationToken ct)
    {
        var q = db.ChangeOperations.AsNoTracking().Where(o => o.RepositoryId == repoId && o.State == ChangeOperationState.Completed && o.ResultCommitSha != null);
        if (i.Since is not null) q = q.Where(o => o.CompletedAt >= i.Since);
        if (i.Until is not null) q = q.Where(o => o.CompletedAt < i.Until);
        var ops = await q.OrderByDescending(o => o.CompletedAt).Take(10).ToListAsync(ct);
        var reverted = (await db.RecoveryOperations.AsNoTracking().Where(r => r.RepositoryId == repoId && r.Status == RecoveryStatus.Succeeded)
            .Select(r => r.SelectedCommits).ToListAsync(ct)).SelectMany(c => c).ToHashSet();
        var result = new List<RecoveryCandidateDto>();
        var rank = 0;
        foreach (var o in ops.Where(o => !reverted.Contains(o.ResultCommitSha!)))
        {
            var cp = o.PreChangeCheckpointId is { } id ? await db.Checkpoints.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct) : null;
            var files = await db.CommitMetadata.AsNoTracking().Where(m => m.RepositoryId == repoId && m.Sha == o.ResultCommitSha).SelectMany(m => m.Files.Select(f => f.Path)).Take(50).ToListAsync(ct);
            result.Add(new RecoveryCandidateDto
            {
                RecoveryPoint = o.StartCommitSha, CommitSha = o.ResultCommitSha!, CheckpointId = cp?.Id, SessionId = o.SessionId, Timestamp = o.CompletedAt ?? o.CreatedAt,
                Durability = cp?.SyncState, Description = $"Undo \"{o.Description}\" by {o.AgentId}; return to the verified checkpoint before it",
                MatchingPaths = files, Score = 1.0 - rank++ * 0.25, SuggestedType = RecoveryType.RevertCommits, SuggestedCommits = [o.ResultCommitSha!]
            });
        }
        return result;
    }

    private async Task<List<RecoveryCandidateDto>> FileCandidatesAsync(Guid repoId, string dir, string branch, RecoveryInterpretationDto i, CancellationToken ct)
    {
        if (i.Keywords.Count == 0) return [];
        var extra = new List<string> { "--name-only" };
        if (i.Until is not null) extra.Add($"--until={i.Until.Value:O}");
        var r = await ctx.Git.RunAsync(dir, ["log", "--format=%x1e%H%x1f%cI%x1f%s", "--name-only", "--max-count=400", .. extra.Skip(1), branch, "--"], null, ct);
        var result = new List<RecoveryCandidateDto>();
        var seenVersions = new Dictionary<string, int>();
        foreach (var rec in r.StdOut.Split('\u001e', StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = rec.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var head = lines[0].Split('\u001f');
            if (head.Length < 3) continue;
            var when = DateTimeOffset.Parse(head[1]).UtcDateTime;
            var matches = lines.Skip(1).Select(p => (Path: p.Trim(), Score: RecoveryIntentParser.PathScore(p, i.Keywords))).Where(x => x.Score >= 0.5).ToList();
            if (matches.Count == 0) continue;
            var paths = matches.Select(m => m.Path).ToArray();
            var existing = new List<string>();
            foreach (var p in paths) if (await ctx.Git.PathExistsAtAsync(dir, head[0], p, ct)) existing.Add(p);
            if (existing.Count == 0) continue;
            var key = string.Join('|', existing.OrderBy(x => x));
            var version = seenVersions.GetValueOrDefault(key);
            seenVersions[key] = version + 1;
            var inWindow = i.Since is null || when >= i.Since;
            var recency = version == 0 ? 0.3 : 0.3 / (version + 1);
            result.Add(new RecoveryCandidateDto
            {
                RecoveryPoint = head[0], CommitSha = head[0], Timestamp = when, MatchingPaths = existing, SuggestedPaths = existing.ToArray(),
                Description = $"{string.Join(", ", existing.Take(3))} as of \"{head[2]}\" ({when:yyyy-MM-dd HH:mm} UTC)",
                Score = matches.Max(m => m.Score) * 0.6 + recency + (inWindow ? 0.1 : 0) + (i.Until is not null && version == 0 ? 0.2 : 0),
                SuggestedType = RecoveryType.RestoreFile,
                Durability = await db.Checkpoints.AsNoTracking().Where(c => c.RepositoryId == repoId && c.CommitSha == head[0]).Select(c => (DurabilityState?)c.SyncState).FirstOrDefaultAsync(ct)
            });
            if (result.Count >= 30) break;
        }
        return result;
    }

    private async Task<List<RecoveryCandidateDto>> DeletedCandidatesAsync(Guid repoId, RecoveryInterpretationDto i, CancellationToken ct)
    {
        var deleted = await browse.DeletedFilesAsync(repoId, null, null, 500, ct);
        return deleted.Select(d => (d, score: i.Keywords.Count == 0 ? 0.5 : RecoveryIntentParser.PathScore(d.Path, i.Keywords)))
            .Where(x => x.score >= 0.5 && (i.Since is null || x.d.DeletedAt >= i.Since))
            .GroupBy(x => x.d.DeletedInCommit)
            .Select(g => new RecoveryCandidateDto
            {
                RecoveryPoint = g.Key + "^", CommitSha = g.Key, Timestamp = g.First().d.DeletedAt, MatchingPaths = g.Select(x => x.d.Path).ToList(),
                SuggestedPaths = g.Select(x => x.d.Path).ToArray(), SuggestedType = RecoveryType.RestoreDeleted, Score = g.Max(x => x.score),
                Description = $"Restore {g.Count()} deleted file(s): {string.Join(", ", g.Select(x => x.d.Path).Take(3))} (deleted {g.First().d.DeletedAt:yyyy-MM-dd HH:mm} UTC)"
            }).ToList();
    }

    private async Task<List<RecoveryCandidateDto>> RefactorCandidatesAsync(Guid repoId, string dir, string branch, RecoveryInterpretationDto i, CancellationToken ct)
    {
        var entries = await ctx.Git.LogAsync(dir, [branch], 300, 0, ct);
        var result = new List<RecoveryCandidateDto>();
        var rank = 0;
        bool Matches(LogEntry e) => i.BeforeLastRefactoring
            ? e.Subject.Contains("refactor", StringComparison.OrdinalIgnoreCase) || e.Subject.Contains("restructur", StringComparison.OrdinalIgnoreCase)
            : i.Keywords.Count > 0 && Core.Recovery.RecoveryIntentParser.TextScore(e.Subject + " " + e.Body, i.Keywords) >= 0.5;
        foreach (var e in entries.Where(Matches).Take(5))
        {
            if (e.Parents.Count == 0) continue;
            result.Add(new RecoveryCandidateDto
            {
                RecoveryPoint = e.Parents[0], CommitSha = e.Sha, Timestamp = e.CommittedAt, SuggestedType = RecoveryType.RestoreProject,
                Description = $"Project state just before \"{e.Subject}\" ({e.CommittedAt:yyyy-MM-dd HH:mm} UTC)", Score = 1.0 - rank++ * 0.25,
                Durability = await db.Checkpoints.AsNoTracking().Where(c => c.RepositoryId == repoId && c.CommitSha == e.Parents[0]).Select(c => (DurabilityState?)c.SyncState).FirstOrDefaultAsync(ct)
            });
        }
        // Pre-change checkpoints of operations declared as refactoring are also good restore points.
        var ops = await db.ChangeOperations.AsNoTracking().Where(o => o.RepositoryId == repoId && o.State == ChangeOperationState.Completed && o.IntentJson != null && i.BeforeLastRefactoring && o.IntentJson.Contains("refactor"))
            .OrderByDescending(o => o.CompletedAt).Take(3).ToListAsync(ct);
        foreach (var o in ops.Where(o => result.All(r => r.RecoveryPoint != o.StartCommitSha)))
            result.Add(new RecoveryCandidateDto
            {
                RecoveryPoint = o.StartCommitSha, CommitSha = o.ResultCommitSha ?? o.StartCommitSha, Timestamp = o.CreatedAt, SuggestedType = RecoveryType.RestoreProject,
                CheckpointId = o.PreChangeCheckpointId, Description = $"Pre-change checkpoint before \"{o.Description}\"", Score = 0.9 - rank++ * 0.2
            });
        return result;
    }

    private async Task<List<RecoveryCandidateDto>> CloneCandidatesAsync(Guid repoId, string dir, string branch, CancellationToken ct)
    {
        var head = await ctx.Git.ResolveAsync(dir, "refs/heads/" + branch, ct);
        var verified = await db.Checkpoints.AsNoTracking().Where(c => c.RepositoryId == repoId && c.SyncState == DurabilityState.RemoteSynchronized)
            .OrderByDescending(c => c.CreatedAt).FirstOrDefaultAsync(ct);
        var list = new List<RecoveryCandidateDto>();
        if (head is not null)
            list.Add(new RecoveryCandidateDto
            {
                RecoveryPoint = branch, CommitSha = head, Timestamp = DateTime.UtcNow, SuggestedType = RecoveryType.CloneProject, Score = 1.0,
                Description = $"Latest '{branch}' on the central Git server ({head[..12]})", Durability = DurabilityState.RemoteSynchronized
            });
        if (verified is not null && verified.CommitSha != head)
            list.Add(new RecoveryCandidateDto
            {
                RecoveryPoint = verified.CommitSha, CommitSha = verified.CommitSha, CheckpointId = verified.Id, Timestamp = verified.CreatedAt,
                SuggestedType = RecoveryType.CloneProject, Score = 0.6, Durability = verified.SyncState,
                Description = $"Last verified checkpoint: {verified.Description} ({verified.CreatedAt:yyyy-MM-dd HH:mm} UTC)"
            });
        return list;
    }

    public async Task<ChangeSummaryDto> SummaryAsync(Guid repositoryId, DateTime since, DateTime until, CancellationToken ct)
    {
        var (repo, dir) = await browse.ReadSourceAsync(repositoryId, ct);
        var entries = await ctx.Git.LogAsync(dir, ["--all"], 500, 0, ct, null, [$"--since={since:O}", $"--until={until:O}"]);
        entries = entries.Where(e => !e.Subject.StartsWith("checkpoint:")).ToList();
        var commits = new List<CommitSummaryDto>();
        var files = new Dictionary<string, FileChangeDto>();
        foreach (var e in entries)
        {
            commits.Add(await browse.SummaryAsync(repo.Id, e, ct));
            foreach (var f in await ctx.Git.CommitFilesAsync(dir, e.Sha, ct))
                files[f.Path] = files.TryGetValue(f.Path, out var prev) ? prev with { Additions = prev.Additions + f.Additions, Deletions = prev.Deletions + f.Deletions } : f;
        }
        var agents = commits.Where(c => c.AgentId is not null).Select(c => c.AgentId!).Distinct().ToList();
        var sessions = commits.Where(c => c.SessionId is not null).Select(c => c.SessionId!.Value).Distinct().ToList();
        var adds = files.Values.Sum(f => f.Additions);
        var dels = files.Values.Sum(f => f.Deletions);
        var narrative = commits.Count == 0
            ? $"No commits between {since:yyyy-MM-dd HH:mm} and {until:yyyy-MM-dd HH:mm} UTC."
            : $"{commits.Count} commit(s) between {since:yyyy-MM-dd HH:mm} and {until:yyyy-MM-dd HH:mm} UTC changed {files.Count} file(s) (+{adds}/-{dels}). " +
              (agents.Count > 0 ? $"AI agents involved: {string.Join(", ", agents)} across {sessions.Count} session(s). " : "") +
              $"Most changed areas: {string.Join(", ", Core.Policies.HighRiskPolicyEngine.Modules(files.Keys).Take(5))}. " +
              $"Latest: \"{commits[0].Subject}\".";
        return new ChangeSummaryDto
        {
            Since = since, Until = until, CommitCount = commits.Count, FilesChanged = files.Count, Additions = adds, Deletions = dels,
            Commits = commits, Files = files.Values.OrderByDescending(f => f.Additions + f.Deletions).Take(200).ToList(), Agents = agents, Sessions = sessions, Narrative = narrative
        };
    }
}
