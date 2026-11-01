using System.Globalization;
using Wings.GitManager.Contracts;
using Wings.GitManager.Core;
using Wings.GitManager.Core.Safety;

namespace Wings.GitManager.Infrastructure.Git;

public sealed record GitIdentity(string Name, string Email);

public sealed record LogEntry(
    string Sha, IReadOnlyList<string> Parents, string AuthorName, string AuthorEmail,
    DateTime AuthoredAt, DateTime CommittedAt, string Subject, string Body);

public sealed record WorktreeInfo(string Path, string? HeadSha, string? Branch, bool Bare, bool Prunable);

public sealed record PushOutcome(bool Success, bool Rejected, bool AuthFailed, bool Unreachable, string Message);

/// <summary>Typed wrappers over <see cref="IGitExecutor"/> used by all services.</summary>
public sealed class GitClient(IGitExecutor git)
{
    private const char FieldSep = '\u001f';
    private const char RecordSep = '\u001e';
    private const string LogFormat = "--format=%H%x1f%P%x1f%an%x1f%ae%x1f%aI%x1f%cI%x1f%s%x1f%b%x1e";

    public IGitExecutor Executor => git;

    public Task<GitResult> RunAsync(string dir, IReadOnlyList<string> args, GitRunOptions? o = null, CancellationToken ct = default) =>
        git.RunAsync(dir, args, o, ct);

    public async Task<bool> IsRepositoryAsync(string dir, CancellationToken ct)
    {
        if (!Directory.Exists(dir)) return false;
        var r = await git.RunAsync(dir, ["rev-parse", "--is-inside-work-tree"], new() { ThrowOnError = false }, ct);
        if (!r.Success || r.StdOut.Trim() != "true") return false;
        var top = await git.RunAsync(dir, ["rev-parse", "--show-toplevel"], new() { ThrowOnError = false }, ct);
        return top.Success && SamePath(top.StdOut.Trim(), dir);
    }

    public Task InitAsync(string dir, string branch, CancellationToken ct) =>
        git.RunAsync(dir, ["init", "--initial-branch", RepoPath.Branch(branch)], null, ct);

    public async Task<string?> HeadAsync(string dir, CancellationToken ct)
    {
        var r = await git.RunAsync(dir, ["rev-parse", "--verify", "--quiet", "HEAD^{commit}"], new() { ThrowOnError = false }, ct);
        return r.Success ? r.StdOut.Trim() : null;
    }

    public async Task<string?> ResolveAsync(string dir, string rev, CancellationToken ct)
    {
        var r = await git.RunAsync(dir, ["rev-parse", "--verify", "--quiet", "--end-of-options", rev + "^{commit}"], new() { ThrowOnError = false }, ct);
        return r.Success ? r.StdOut.Trim() : null;
    }

    public async Task<string> ResolveRequiredAsync(string dir, string rev, CancellationToken ct) =>
        await ResolveAsync(dir, rev, ct) ?? throw new NotFoundException($"Revision '{rev}'");

    public async Task<string?> CurrentBranchAsync(string dir, CancellationToken ct)
    {
        var r = await git.RunAsync(dir, ["symbolic-ref", "--quiet", "--short", "HEAD"], new() { ThrowOnError = false }, ct);
        return r.Success ? r.StdOut.Trim() : null;
    }

    /// <summary>Working tree status including untracked files; renames detected.</summary>
    public async Task<IReadOnlyList<WorkingTreeChangeDto>> StatusAsync(string dir, CancellationToken ct)
    {
        var r = await git.RunAsync(dir, ["status", "--porcelain=v1", "-z", "--untracked-files=all", "--no-renames"], null, ct);
        var result = new List<WorkingTreeChangeDto>();
        var parts = r.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        foreach (var entry in parts)
        {
            if (entry.Length < 4) continue;
            var x = entry[0];
            var y = entry[1];
            var path = entry[3..];
            if (x == '?' && y == '?') { result.Add(new(path, FileChangeKind.Added, null, false, true)); continue; }
            if (x == '!') continue;
            var code = y != ' ' ? y : x;
            var kind = code switch { 'A' => FileChangeKind.Added, 'D' => FileChangeKind.Deleted, 'T' => FileChangeKind.TypeChanged, _ => FileChangeKind.Modified };
            result.Add(new(path, kind, null, x != ' ' && y == ' ', false));
        }
        return result;
    }

    public async Task<bool> IsCleanAsync(string dir, CancellationToken ct) => (await StatusAsync(dir, ct)).Count == 0;

    /// <summary>name-status + numstat between two trees (or the index when <paramref name="to"/> is null and cached).</summary>
    public async Task<IReadOnlyList<FileChangeDto>> DiffFilesAsync(string dir, string? from, string? to, bool cached, IReadOnlyList<string>? pathspecs, CancellationToken ct)
    {
        var baseArgs = new List<string> { "diff", "--no-ext-diff", "--no-textconv", "-M", "-z" };
        if (cached) baseArgs.Add("--cached");
        if (from is not null) baseArgs.Add(from);
        if (to is not null) baseArgs.Add(to);

        var nameStatus = await git.RunAsync(dir, [.. baseArgs.Take(5), "--name-status", .. baseArgs.Skip(5), "--", .. pathspecs ?? []], null, ct);
        var numstat = await git.RunAsync(dir, [.. baseArgs.Take(5), "--numstat", .. baseArgs.Skip(5), "--", .. pathspecs ?? []], null, ct);
        return MergeNameStatus(nameStatus.StdOut, numstat.StdOut);
    }

    /// <summary>Files changed by a single commit (against its first parent, or the empty tree for root commits).</summary>
    public async Task<IReadOnlyList<FileChangeDto>> CommitFilesAsync(string dir, string sha, CancellationToken ct)
    {
        var ns = await git.RunAsync(dir, ["diff-tree", "--no-commit-id", "-r", "-M", "-z", "--root", "--name-status", "--no-ext-diff", "--no-textconv", sha], null, ct);
        var nm = await git.RunAsync(dir, ["diff-tree", "--no-commit-id", "-r", "-M", "-z", "--root", "--numstat", "--no-ext-diff", "--no-textconv", sha], null, ct);
        return MergeNameStatus(ns.StdOut, nm.StdOut);
    }

    internal static IReadOnlyList<FileChangeDto> MergeNameStatus(string nameStatus, string numstat)
    {
        var counts = new Dictionary<string, (int add, int del)>();
        var ntokens = numstat.Split('\0');
        for (var i = 0; i < ntokens.Length; i++)
        {
            var t = ntokens[i];
            if (string.IsNullOrEmpty(t)) continue;
            var cols = t.Split('\t');
            if (cols.Length < 3) continue;
            int.TryParse(cols[0], out var add);
            int.TryParse(cols[1], out var del);
            string path;
            if (cols[2].Length == 0 && i + 2 < ntokens.Length)
            {
                // rename: "add\tdel\t\0old\0new"
                path = ntokens[i + 2];
                i += 2;
            }
            else path = cols[2];
            counts[path] = (add, del);
        }

        var result = new List<FileChangeDto>();
        var tokens = nameStatus.Split('\0');
        for (var i = 0; i < tokens.Length; i++)
        {
            var status = tokens[i];
            if (string.IsNullOrEmpty(status)) continue;
            var code = status[0];
            if ((code == 'R' || code == 'C') && i + 2 < tokens.Length)
            {
                var oldPath = tokens[i + 1];
                var newPath = tokens[i + 2];
                i += 2;
                counts.TryGetValue(newPath, out var c);
                result.Add(new FileChangeDto(newPath, code == 'R' ? FileChangeKind.Renamed : FileChangeKind.Copied, oldPath, c.add, c.del));
            }
            else if (i + 1 < tokens.Length)
            {
                var path = tokens[++i];
                counts.TryGetValue(path, out var c);
                var kind = code switch
                {
                    'A' => FileChangeKind.Added, 'D' => FileChangeKind.Deleted, 'T' => FileChangeKind.TypeChanged, _ => FileChangeKind.Modified
                };
                result.Add(new FileChangeDto(path, kind, null, c.add, c.del));
            }
        }
        return result;
    }

    public async Task<IReadOnlyList<LogEntry>> LogAsync(string dir, IReadOnlyList<string> revArgs, int max, int skip, CancellationToken ct, IReadOnlyList<string>? paths = null, IReadOnlyList<string>? extra = null)
    {
        var args = new List<string> { "log", LogFormat, $"--max-count={max}", $"--skip={skip}", "--no-ext-diff", "--no-textconv" };
        if (extra is not null) args.AddRange(extra);
        args.AddRange(revArgs);
        args.Add("--");
        if (paths is not null) args.AddRange(paths);
        var r = await git.RunAsync(dir, args, new() { ThrowOnError = false }, ct);
        if (!r.Success)
        {
            if (r.StdErr.Contains("does not have any commits") || r.StdErr.Contains("unknown revision")) return [];
            throw new GitCommandException("git log failed: " + r.StdErr.Trim(), r.ExitCode, r.StdErr);
        }
        return ParseLog(r.StdOut);
    }

    internal static IReadOnlyList<LogEntry> ParseLog(string output)
    {
        var list = new List<LogEntry>();
        foreach (var rec in output.Split(RecordSep))
        {
            var t = rec.TrimStart('\n', '\r');
            if (t.Length == 0) continue;
            var f = t.Split(FieldSep);
            if (f.Length < 8) continue;
            list.Add(new LogEntry(
                f[0], f[1].Split(' ', StringSplitOptions.RemoveEmptyEntries), f[2], f[3],
                DateTimeOffset.Parse(f[4], CultureInfo.InvariantCulture).UtcDateTime,
                DateTimeOffset.Parse(f[5], CultureInfo.InvariantCulture).UtcDateTime,
                f[6], f[7].TrimEnd()));
        }
        return list;
    }

    public async Task<LogEntry?> GetCommitAsync(string dir, string rev, CancellationToken ct)
    {
        var sha = await ResolveAsync(dir, rev, ct);
        if (sha is null) return null;
        return (await LogAsync(dir, [sha], 1, 0, ct)).FirstOrDefault();
    }

    public async Task<string> CommitAsync(string dir, string message, GitIdentity author, GitIdentity committer, bool allowEmpty, CancellationToken ct)
    {
        var args = new List<string> { "commit", "--no-verify", "--no-gpg-sign", "-F", "-", "--cleanup=verbatim" };
        if (allowEmpty) args.Add("--allow-empty");
        await git.RunAsync(dir, args, new() { StandardInput = message, Environment = IdentityEnv(author, committer) }, ct);
        return (await HeadAsync(dir, ct))!;
    }

    public static IReadOnlyDictionary<string, string> IdentityEnv(GitIdentity author, GitIdentity committer) => new Dictionary<string, string>
    {
        ["GIT_AUTHOR_NAME"] = author.Name, ["GIT_AUTHOR_EMAIL"] = author.Email,
        ["GIT_COMMITTER_NAME"] = committer.Name, ["GIT_COMMITTER_EMAIL"] = committer.Email,
    };

    /// <summary>
    /// Records the full working tree (tracked + untracked, honouring .gitignore) as a commit object without
    /// touching the real index or working tree. Used to preserve work before any reset or restoration.
    /// </summary>
    public async Task<string?> SnapshotWorkingTreeAsync(string dir, string message, GitIdentity identity, CancellationToken ct)
    {
        var head = await HeadAsync(dir, ct);
        var tmpIndex = Path.Combine(Path.GetTempPath(), $"wgm-index-{Guid.NewGuid():N}");
        try
        {
            var env = new Dictionary<string, string>(IdentityEnv(identity, identity)) { ["GIT_INDEX_FILE"] = tmpIndex };
            if (head is not null) await git.RunAsync(dir, ["read-tree", head], new() { Environment = env }, ct);
            await git.RunAsync(dir, ["add", "-A", "--", "."], new() { Environment = env }, ct);
            var tree = (await git.RunAsync(dir, ["write-tree"], new() { Environment = env }, ct)).StdOut.Trim();
            if (head is not null)
            {
                var headTree = (await git.RunAsync(dir, ["rev-parse", head + "^{tree}"], null, ct)).StdOut.Trim();
                if (headTree == tree) return null;
            }
            var args = new List<string> { "commit-tree", tree };
            if (head is not null) { args.Add("-p"); args.Add(head); }
            var r = await git.RunAsync(dir, args, new() { StandardInput = message, Environment = env }, ct);
            return r.StdOut.Trim();
        }
        finally
        {
            try { File.Delete(tmpIndex); } catch { /* best effort */ }
        }
    }

    public Task UpdateRefAsync(string dir, string refName, string newSha, string? oldSha, CancellationToken ct)
    {
        var args = new List<string> { "update-ref", "-m", "wings-git-manager", refName, newSha };
        if (oldSha is not null) args.Add(oldSha);
        return git.RunAsync(dir, args, null, ct);
    }

    public async Task<string?> LsRemoteAsync(string dir, string remote, string branch, GitCredential? cred, CancellationToken ct, bool allowFile = false)
    {
        var r = await git.RunAsync(dir, ["ls-remote", "--heads", remote, "refs/heads/" + branch],
            new() { Credential = cred, Network = true, ThrowOnError = true, AllowFileProtocol = allowFile, Timeout = TimeSpan.FromSeconds(60) }, ct);
        var line = r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return line?.Split('\t')[0].Trim();
    }

    /// <summary>Non-force push of <paramref name="sha"/> to refs/heads/<paramref name="branch"/>.</summary>
    public async Task<PushOutcome> PushAsync(string dir, string remote, string sha, string branch, GitCredential? cred, CancellationToken ct, bool allowFile = false)
    {
        var r = await git.RunAsync(dir, ["push", "--porcelain", remote, $"{sha}:refs/heads/{RepoPath.Branch(branch)}"],
            new() { Credential = cred, Network = true, ThrowOnError = false, AllowFileProtocol = allowFile }, ct);
        var text = (r.StdOut + "\n" + r.StdErr).Trim();
        if (r.Success) return new PushOutcome(true, false, false, false, GitExecutor.Redact(text));
        var lower = text.ToLowerInvariant();
        var rejected = lower.Contains("[rejected]") || lower.Contains("non-fast-forward") || lower.Contains("fetch first") || lower.Contains("stale info") || lower.Contains("denying non-fast-forward");
        var auth = lower.Contains("authentication failed") || lower.Contains("403") || lower.Contains("401") || lower.Contains("could not read username") || lower.Contains("permission denied") || lower.Contains("unauthorized");
        var unreachable = lower.Contains("could not resolve host") || lower.Contains("failed to connect") || lower.Contains("connection refused") || lower.Contains("timed out") || lower.Contains("unable to access") && !auth || lower.Contains("does not appear to be a git repository") || lower.Contains("502") || lower.Contains("503");
        return new PushOutcome(false, rejected, auth && !rejected, unreachable && !auth && !rejected, GitExecutor.Redact(text));
    }

    /// <summary>Fetches all branches of <paramref name="remoteUrl"/> into the local refs/remotes/origin/* tracking refs.</summary>
    public Task FetchAsync(string dir, string remoteUrl, GitCredential? cred, CancellationToken ct, bool allowFile = false) =>
        git.RunAsync(dir, ["fetch", "--no-tags", "--prune", remoteUrl, "+refs/heads/*:refs/remotes/origin/*"],
            new() { Credential = cred, Network = true, AllowFileProtocol = allowFile }, ct);

    public async Task<bool> IsAncestorAsync(string dir, string ancestor, string descendant, CancellationToken ct)
    {
        var r = await git.RunAsync(dir, ["merge-base", "--is-ancestor", ancestor, descendant], new() { ThrowOnError = false }, ct);
        return r.ExitCode == 0;
    }

    public async Task<bool> ObjectExistsAsync(string dir, string sha, CancellationToken ct)
    {
        var r = await git.RunAsync(dir, ["cat-file", "-e", sha + "^{commit}"], new() { ThrowOnError = false }, ct);
        return r.Success;
    }

    public async Task<int> CountAsync(string dir, string range, CancellationToken ct)
    {
        var r = await git.RunAsync(dir, ["rev-list", "--count", range], new() { ThrowOnError = false }, ct);
        return r.Success && int.TryParse(r.StdOut.Trim(), out var n) ? n : 0;
    }

    public async Task<IReadOnlyList<(string Name, string Sha, DateTime Date)>> LocalBranchesAsync(string dir, CancellationToken ct)
    {
        var r = await git.RunAsync(dir, ["for-each-ref", "--format=%(refname:short)%1f%(objectname)%1f%(committerdate:iso-strict)", "refs/heads/"], null, ct);
        return ParseRefs(r.StdOut);
    }

    public async Task<IReadOnlyList<(string Name, string Sha, DateTime Date)>> RemoteBranchesAsync(string dir, string remote, CancellationToken ct)
    {
        var r = await git.RunAsync(dir, ["for-each-ref", "--format=%(refname:short)%1f%(objectname)%1f%(committerdate:iso-strict)", $"refs/remotes/{remote}/"], null, ct);
        return ParseRefs(r.StdOut).Where(x => !x.Name.EndsWith("/HEAD") && x.Name != remote)
            .Select(x => (x.Name[(remote.Length + 1)..], x.Sha, x.Date)).ToList();
    }

    private static List<(string Name, string Sha, DateTime Date)> ParseRefs(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\u001f')).Where(f => f.Length >= 3)
            .Select(f => (f[0], f[1], DateTime.TryParse(f[2], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d) ? d : DateTime.MinValue)).ToList();

    public async Task<IReadOnlyList<WorktreeInfo>> WorktreeListAsync(string dir, CancellationToken ct)
    {
        var r = await git.RunAsync(dir, ["worktree", "list", "--porcelain"], null, ct);
        var list = new List<WorktreeInfo>();
        string? path = null, head = null, branch = null; bool bare = false, prunable = false;
        foreach (var line in (r.StdOut + "\n\n").Split('\n'))
        {
            if (line.Length == 0)
            {
                if (path is not null) list.Add(new WorktreeInfo(path, head, branch, bare, prunable));
                path = head = branch = null; bare = prunable = false;
                continue;
            }
            if (line.StartsWith("worktree ")) path = line[9..];
            else if (line.StartsWith("HEAD ")) head = line[5..];
            else if (line.StartsWith("branch ")) branch = line[7..].Replace("refs/heads/", "");
            else if (line == "bare") bare = true;
            else if (line.StartsWith("prunable")) prunable = true;
        }
        return list;
    }

    public async Task<byte[]?> ShowBlobAsync(string dir, string rev, string path, CancellationToken ct)
    {
        var r = await git.RunAsync(dir, ["cat-file", "blob", $"{rev}:{path}"], new() { BinaryOutput = true, ThrowOnError = false }, ct);
        return r.Success ? r.StdOutBytes : null;
    }

    public async Task<long?> BlobSizeAsync(string dir, string rev, string path, CancellationToken ct)
    {
        var r = await git.RunAsync(dir, ["cat-file", "-s", $"{rev}:{path}"], new() { ThrowOnError = false }, ct);
        return r.Success && long.TryParse(r.StdOut.Trim(), out var n) ? n : null;
    }

    public async Task<bool> PathExistsAtAsync(string dir, string rev, string path, CancellationToken ct)
    {
        var r = await git.RunAsync(dir, ["cat-file", "-e", $"{rev}:{path}"], new() { ThrowOnError = false }, ct);
        return r.Success;
    }

    public async Task<string?> GetConfigAsync(string dir, string key, CancellationToken ct)
    {
        var r = await git.RunAsync(dir, ["config", "--local", "--get", key], new() { ThrowOnError = false }, ct);
        return r.Success ? r.StdOut.Trim() : null;
    }

    public static bool SamePath(string a, string b)
    {
        static string N(string p)
        {
            var full = Path.GetFullPath(p).TrimEnd('/', '\\');
            // macOS: /var -> /private/var
            if (full.StartsWith("/private/", StringComparison.Ordinal)) full = full["/private".Length..];
            return full;
        }
        return string.Equals(N(a), N(b), OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
