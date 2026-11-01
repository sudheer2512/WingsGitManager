using System.Text;
using Microsoft.EntityFrameworkCore;
using Wings.GitManager.Contracts;
using Wings.GitManager.Core;
using Wings.GitManager.Core.Entities;
using Wings.GitManager.Core.Safety;
using Wings.GitManager.Infrastructure.Data;
using Wings.GitManager.Infrastructure.Git;

namespace Wings.GitManager.Infrastructure.Services;

/// <summary>
/// Server-side bare mirrors of each central repository. They let the UI and recovery read history even when
/// the local Wings Code working directory has been deleted, and they never prune deleted refs.
/// </summary>
public sealed class MirrorService(RepositoryContext ctx, LockService locks)
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTime> LastFetch = new();

    public string MirrorPath(Guid repositoryId) =>
        Path.Combine(Path.GetFullPath(ctx.Options.DataDirectory), "mirrors", repositoryId.ToString("N") + ".git");

    public async Task<string> EnsureAsync(Repository repo, bool forceFetch, CancellationToken ct)
    {
        var path = MirrorPath(repo.Id);
        var remote = ctx.PrimaryRemote(repo);
        var opts = new GitRunOptions { Credential = ctx.Credential(repo), Network = true, AllowFileProtocol = ctx.AllowFile(repo) };
        await using var _ = await locks.AcquireAsync("mirror:" + repo.Id.ToString("N"), TimeSpan.FromMinutes(2), ct);
        if (!Directory.Exists(Path.Combine(path, "refs")))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (Directory.Exists(path)) Directory.Delete(path, true);
            await ctx.Git.RunAsync(Path.GetDirectoryName(path)!, ["clone", "--mirror", "--", remote.Url, path], opts, ct);
            LastFetch[repo.Id] = DateTime.UtcNow;
        }
        else if (forceFetch || !LastFetch.TryGetValue(repo.Id, out var last) || DateTime.UtcNow - last > TimeSpan.FromMinutes(2))
        {
            // No --prune: refs deleted on the server stay available for recovery.
            await ctx.Git.RunAsync(path, ["fetch", "--no-prune", remote.Url, "+refs/heads/*:refs/heads/*", "+refs/tags/*:refs/tags/*"], opts, ct);
            LastFetch[repo.Id] = DateTime.UtcNow;
        }
        return path;
    }
}

public sealed class BrowseService(GitManagerDbContext db, AccessControl access, RepositoryContext ctx, MirrorService mirrors)
{
    private const int MaxTextBytes = 2 * 1024 * 1024;
    private const int MaxPatchBytes = 512 * 1024;

    /// <summary>Directory to read Git data from: the main working directory, or the server mirror when it is gone.</summary>
    public async Task<(Repository Repo, string Dir)> ReadSourceAsync(Guid repositoryId, CancellationToken ct)
    {
        var repo = await access.RequireRepositoryAsync(repositoryId, RepoAction.Read, ct, track: false);
        var wd = await ctx.MainWorkingDirectoryAsync(repositoryId, ct);
        if (wd is not null && await ctx.Git.IsRepositoryAsync(wd.Path, ct)) return (repo, wd.Path);
        return (repo, await mirrors.EnsureAsync(repo, false, ct));
    }

    private static string Rev(string? rev, Repository repo) => RepoPath.Revision(string.IsNullOrWhiteSpace(rev) ? repo.DefaultBranch : rev);

    public async Task<IReadOnlyList<TreeEntryDto>> TreeAsync(Guid repositoryId, string? rev, string? path, CancellationToken ct)
    {
        var (repo, dir) = await ReadSourceAsync(repositoryId, ct);
        var r = Rev(rev, repo);
        var args = new List<string> { "ls-tree", "-z", "-l", r };
        string prefix = "";
        if (!string.IsNullOrWhiteSpace(path))
        {
            prefix = RepoPath.Normalize(path);
            args.Add("--");
            args.Add(prefix + "/");
        }
        var res = await ctx.Git.RunAsync(dir, args, new() { ThrowOnError = false }, ct);
        if (!res.Success) throw new NotFoundException($"Path '{path}' at '{rev}'");
        return res.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            var tab = line.IndexOf('\t');
            var meta = line[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var full = line[(tab + 1)..];
            long? size = long.TryParse(meta[3], out var s) ? s : null;
            return new TreeEntryDto(full, full[(full.LastIndexOf('/') + 1)..], meta[1] == "tree" ? "dir" : meta[1] == "commit" ? "submodule" : "file", size, meta[2]);
        }).OrderBy(e => e.Type != "dir").ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<IReadOnlyList<string>> SearchFilesAsync(Guid repositoryId, string? rev, string query, CancellationToken ct)
    {
        var (repo, dir) = await ReadSourceAsync(repositoryId, ct);
        var res = await ctx.Git.RunAsync(dir, ["ls-tree", "-r", "-z", "--name-only", Rev(rev, repo)], null, ct);
        return res.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(200).ToList();
    }

    public async Task<IReadOnlyList<object>> GrepAsync(Guid repositoryId, string? rev, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 200) throw new GitManagerException(ErrorCodes.Validation, "Search text is required (max 200 characters).");
        var (repo, dir) = await ReadSourceAsync(repositoryId, ct);
        var r = Rev(rev, repo);
        var res = await ctx.Git.RunAsync(dir, ["grep", "-n", "-I", "-F", "-i", "--max-count=20", "-e", text, r, "--"], new() { ThrowOnError = false }, ct);
        return res.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(500).Select(l =>
        {
            var parts = l.Split(':', 4);
            return (object)new { path = parts.Length > 1 ? parts[1] : l, line = parts.Length > 2 && int.TryParse(parts[2], out var n) ? n : 0, text = parts.Length > 3 ? parts[3] : "" };
        }).ToList();
    }

    public async Task<FileContentDto> FileAsync(Guid repositoryId, string? rev, string path, CancellationToken ct)
    {
        var (repo, dir) = await ReadSourceAsync(repositoryId, ct);
        var r = Rev(rev, repo);
        var p = RepoPath.Normalize(path);
        var commit = await ctx.Git.ResolveAsync(dir, r, ct) ?? throw new NotFoundException($"Revision '{rev}'");
        var size = await ctx.Git.BlobSizeAsync(dir, commit, p, ct) ?? throw new NotFoundException($"File '{p}' at {commit[..12]}");
        var last = (await ctx.Git.LogAsync(dir, [commit], 1, 0, ct, [RepoPath.Literal(p)])).FirstOrDefault();
        string? content = null;
        var binary = false;
        var truncated = size > MaxTextBytes;
        if (!truncated)
        {
            var bytes = await ctx.Git.ShowBlobAsync(dir, commit, p, ct) ?? [];
            binary = bytes.AsSpan(0, Math.Min(bytes.Length, 8000)).IndexOf((byte)0) >= 0;
            if (!binary) content = Encoding.UTF8.GetString(bytes);
        }
        return new FileContentDto
        {
            Path = p, Ref = r, CommitSha = commit, Size = size, IsBinary = binary, Content = content, Truncated = truncated,
            Language = LanguageFor(p), LastCommit = last is null ? null : await SummaryAsync(repo.Id, last, ct)
        };
    }

    public async Task<(byte[] Bytes, string FileName)> RawFileAsync(Guid repositoryId, string? rev, string path, CancellationToken ct)
    {
        var (repo, dir) = await ReadSourceAsync(repositoryId, ct);
        var p = RepoPath.Normalize(path);
        var bytes = await ctx.Git.ShowBlobAsync(dir, Rev(rev, repo), p, ct) ?? throw new NotFoundException($"File '{p}'");
        return (bytes, p[(p.LastIndexOf('/') + 1)..]);
    }

    public async Task<PagedResult<CommitSummaryDto>> CommitsAsync(Guid repositoryId, string? rev, string? path, DateTime? since, DateTime? until,
        string? author, Guid? sessionId, int page, int pageSize, CancellationToken ct)
    {
        var (repo, dir) = await ReadSourceAsync(repositoryId, ct);
        pageSize = Math.Clamp(pageSize, 1, 200);
        page = Math.Max(1, page);
        if (sessionId is not null)
        {
            var shas = await db.AISessionCommits.AsNoTracking().Where(c => c.SessionId == sessionId).OrderByDescending(c => c.Sequence).Select(c => c.Sha).ToListAsync(ct);
            var metas = await db.CommitMetadata.AsNoTracking().Where(m => m.RepositoryId == repositoryId && shas.Contains(m.Sha)).OrderByDescending(m => m.CommittedAt).ToListAsync(ct);
            return new PagedResult<CommitSummaryDto>(metas.Skip((page - 1) * pageSize).Take(pageSize).Select(m => ToSummary(m, null)).ToList(), metas.Count, page, pageSize);
        }
        var extra = new List<string>();
        if (since is not null) extra.Add($"--since={since.Value:O}");
        if (until is not null) extra.Add($"--until={until.Value:O}");
        if (!string.IsNullOrWhiteSpace(author)) { extra.Add("--fixed-strings"); extra.Add($"--author={author}"); }
        var revArg = rev == "--all" ? "--all" : Rev(rev, repo);
        var paths = string.IsNullOrWhiteSpace(path) ? null : new[] { RepoPath.Literal(RepoPath.Normalize(path)) };
        var entries = await ctx.Git.LogAsync(dir, [revArg], pageSize + 1, (page - 1) * pageSize, ct, paths, extra);
        var list = new List<CommitSummaryDto>();
        foreach (var e in entries.Take(pageSize)) list.Add(await SummaryAsync(repo.Id, e, ct));
        var total = (page - 1) * pageSize + entries.Count + (entries.Count > pageSize ? pageSize : 0);
        return new PagedResult<CommitSummaryDto>(list, total, page, pageSize);
    }

    public async Task<CommitDetailDto> CommitAsync(Guid repositoryId, string sha, CancellationToken ct)
    {
        var (repo, dir) = await ReadSourceAsync(repositoryId, ct);
        var entry = await ctx.Git.GetCommitAsync(dir, RepoPath.Revision(sha), ct) ?? throw new NotFoundException($"Commit '{sha}'");
        var meta = await db.CommitMetadata.AsNoTracking().FirstOrDefaultAsync(m => m.RepositoryId == repositoryId && m.Sha == entry.Sha, ct);
        return new CommitDetailDto
        {
            Commit = await SummaryAsync(repo.Id, entry, ct), Message = entry.Subject + (entry.Body.Length > 0 ? "\n\n" + entry.Body : ""),
            Files = await ctx.Git.CommitFilesAsync(dir, entry.Sha, ct), UserRequestRef = meta?.UserRequestRef, BuildStatus = meta?.BuildStatus, TestStatus = meta?.TestStatus
        };
    }

    public async Task<DiffDto> DiffAsync(Guid repositoryId, string from, string to, string? path, bool includePatch, CancellationToken ct)
    {
        var (_, dir) = await ReadSourceAsync(repositoryId, ct);
        const string emptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
        var a = from == emptyTree ? emptyTree : await ctx.Git.ResolveRequiredAsync(dir, RepoPath.Revision(from), ct);
        var b = await ctx.Git.ResolveRequiredAsync(dir, RepoPath.Revision(to), ct);
        var paths = string.IsNullOrWhiteSpace(path) ? null : new[] { RepoPath.Literal(RepoPath.Normalize(path)) };
        var files = await ctx.Git.DiffFilesAsync(dir, a, b, false, paths, ct);
        var patches = new Dictionary<string, string>();
        var truncated = false;
        if (includePatch && files.Count > 0)
        {
            var raw = await ctx.Git.RunAsync(dir, ["diff", "--no-ext-diff", "--no-textconv", "--no-color", "-M", a, b, "--", .. paths ?? []], null, ct);
            var text = raw.StdOut;
            if (text.Length > MaxPatchBytes * 4) { text = text[..(MaxPatchBytes * 4)]; truncated = true; }
            foreach (var chunk in text.Split("diff --git ", StringSplitOptions.RemoveEmptyEntries))
            {
                var header = chunk.Split('\n')[0];
                var bIdx = header.LastIndexOf(" b/", StringComparison.Ordinal);
                if (bIdx < 0) continue;
                var p = header[(bIdx + 3)..].Trim();
                patches[p] = chunk.Length > MaxPatchBytes ? "diff --git " + chunk[..MaxPatchBytes] + "\n… (truncated)" : "diff --git " + chunk;
            }
        }
        return new DiffDto
        {
            FromRef = a, ToRef = b, TotalAdditions = files.Sum(f => f.Additions), TotalDeletions = files.Sum(f => f.Deletions), Truncated = truncated,
            Files = files.Select(f => new FileDiffDto
            {
                Path = f.Path, OldPath = f.OldPath, Kind = f.Kind, Additions = f.Additions, Deletions = f.Deletions,
                IsBinary = patches.TryGetValue(f.Path, out var pt) && pt.Contains("Binary files"), Patch = patches.GetValueOrDefault(f.Path)
            }).ToList()
        };
    }

    public async Task<IReadOnlyList<DeletedFileDto>> DeletedFilesAsync(Guid repositoryId, string? rev, string? pathContains, int max, CancellationToken ct)
    {
        var (repo, dir) = await ReadSourceAsync(repositoryId, ct);
        var r = await ctx.Git.RunAsync(dir, ["log", "--diff-filter=D", "--name-only", "-z", "--format=%x1e%H%x1f%cI%x1f%an", $"--max-count={Math.Clamp(max, 1, 2000)}", Rev(rev, repo), "--"], null, ct);
        var result = new List<DeletedFileDto>();
        var seen = new HashSet<string>();
        foreach (var rec in r.StdOut.Split('\u001e', StringSplitOptions.RemoveEmptyEntries))
        {
            var nl = rec.IndexOf('\0') is var z && z >= 0 ? z : rec.Length;
            var header = rec[..nl].Trim('\n');
            var f = header.Split('\u001f');
            if (f.Length < 3) continue;
            var paths = rec[Math.Min(rec.Length, nl)..].Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim('\n')).Where(x => x.Length > 0);
            foreach (var p in paths)
            {
                if (pathContains is not null && !p.Contains(pathContains, StringComparison.OrdinalIgnoreCase)) continue;
                if (!seen.Add(p)) continue; // most recent deletion only
                // Skip files that exist again at the tip (re-added later).
                if (await ctx.Git.PathExistsAtAsync(dir, Rev(rev, repo), p, ct)) continue;
                result.Add(new DeletedFileDto(p, f[0], DateTimeOffset.Parse(f[1]).UtcDateTime, f[0] + "^", f[2]));
            }
        }
        return result;
    }

    public async Task<CommitSummaryDto> SummaryAsync(Guid repositoryId, LogEntry e, CancellationToken ct)
    {
        var meta = await db.CommitMetadata.AsNoTracking().FirstOrDefaultAsync(m => m.RepositoryId == repositoryId && m.Sha == e.Sha, ct);
        var durability = await db.Checkpoints.AsNoTracking().Where(c => c.RepositoryId == repositoryId && c.CommitSha == e.Sha)
            .OrderByDescending(c => c.SyncState).Select(c => (DurabilityState?)c.SyncState).FirstOrDefaultAsync(ct);
        var trailers = Core.Commits.CommitMessageBuilder.ParseTrailers(e.Body);
        return new CommitSummaryDto
        {
            Sha = e.Sha, Subject = e.Subject, AuthorName = e.AuthorName, AuthorEmail = e.AuthorEmail, AuthoredAt = e.AuthoredAt, CommittedAt = e.CommittedAt,
            Parents = e.Parents, SessionId = meta?.SessionId ?? (trailers.TryGetValue("Wings-Session-Id", out var s) && Guid.TryParse(s, out var sg) ? sg : null),
            AgentId = meta?.AgentId ?? trailers.GetValueOrDefault("Wings-Agent-Id"),
            ChangeOperationId = meta?.ChangeOperationId, InitiatingUser = meta?.InitiatingUser ?? trailers.GetValueOrDefault("Wings-Initiated-By"),
            IsAutomated = meta?.IsAutomated ?? trailers.Count > 0, Durability = durability
        };
    }

    public static CommitSummaryDto ToSummary(CommitMetadata m, DurabilityState? durability) => new()
    {
        Sha = m.Sha, Subject = m.Subject, AuthorName = m.AuthorName, AuthorEmail = m.AuthorEmail, AuthoredAt = m.CommittedAt, CommittedAt = m.CommittedAt,
        SessionId = m.SessionId, AgentId = m.AgentId, ChangeOperationId = m.ChangeOperationId, InitiatingUser = m.InitiatingUser,
        IsAutomated = m.IsAutomated, Durability = durability
    };

    public static string LanguageFor(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var name = Path.GetFileName(path).ToLowerInvariant();
        if (name == "dockerfile") return "dockerfile";
        return ext switch
        {
            ".cs" => "csharp", ".ts" => "typescript", ".tsx" => "typescript", ".js" or ".mjs" or ".cjs" => "javascript", ".jsx" => "javascript",
            ".json" => "json", ".html" or ".htm" => "html", ".css" => "css", ".scss" => "scss", ".less" => "less", ".md" => "markdown",
            ".xml" or ".csproj" or ".props" or ".targets" or ".config" or ".resx" => "xml", ".yml" or ".yaml" => "yaml", ".sql" => "sql",
            ".py" => "python", ".java" => "java", ".go" => "go", ".rs" => "rust", ".rb" => "ruby", ".php" => "php", ".sh" => "shell",
            ".ps1" => "powershell", ".cshtml" or ".razor" => "razor", ".kt" => "kotlin", ".swift" => "swift", ".c" or ".h" => "c",
            ".cpp" or ".hpp" or ".cc" => "cpp", ".ini" => "ini", ".toml" => "ini", ".bat" or ".cmd" => "bat", ".vue" => "html",
            _ => "plaintext"
        };
    }
}
