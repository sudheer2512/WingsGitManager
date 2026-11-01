using System.Text.RegularExpressions;

namespace Wings.GitManager.Core.Safety;

/// <summary>Validation for repository-relative paths and Git ref names received from callers.</summary>
public static partial class RepoPath
{
    /// <summary>Normalises a repository-relative path and rejects traversal, absolute paths and .git access.</summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new GitManagerException(ErrorCodes.PathRejected, "Path is empty.");
        if (path.Contains('\0') || path.Length > 1024)
            throw new GitManagerException(ErrorCodes.PathRejected, "Path contains invalid characters.");

        var p = path.Replace('\\', '/').Trim();
        if (p.StartsWith('/') || p.StartsWith('~') || (p.Length >= 2 && p[1] == ':'))
            throw new GitManagerException(ErrorCodes.PathRejected, $"Absolute paths are not allowed: '{path}'.");

        var parts = new List<string>();
        foreach (var segment in p.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
                throw new GitManagerException(ErrorCodes.PathRejected, $"Path traversal is not allowed: '{path}'.");
            if (segment.Equals(".git", StringComparison.OrdinalIgnoreCase))
                throw new GitManagerException(ErrorCodes.PathRejected, "Paths inside .git are not allowed.");
            parts.Add(segment);
        }
        if (parts.Count == 0)
            throw new GitManagerException(ErrorCodes.PathRejected, $"Invalid path '{path}'.");
        return string.Join('/', parts);
    }

    /// <summary>Pathspec that git treats literally (no globbing or magic from the caller).</summary>
    public static string Literal(string normalizedPath) => ":(literal)" + normalizedPath;

    /// <summary>Validates a branch name using git's check-ref-format rules (plus a conservative character set).</summary>
    public static string Branch(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || !BranchRegex().IsMatch(name)
            || name.StartsWith('-') || name.StartsWith('/') || name.EndsWith('/') || name.EndsWith(".lock")
            || name.Contains("..") || name.Contains("//") || name.Contains("@{") || name.EndsWith('.') || name == "HEAD")
            throw new GitManagerException(ErrorCodes.Validation, $"Invalid branch name '{name}'.");
        return name;
    }

    /// <summary>Validates a branch pattern used for protection rules (branch name characters plus * and ?).</summary>
    public static string BranchPattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > 200 || !BranchPatternRegex().IsMatch(pattern) || pattern.StartsWith('-') || pattern.Contains(".."))
            throw new GitManagerException(ErrorCodes.Validation, $"Invalid branch pattern '{pattern}'.");
        return pattern;
    }

    /// <summary>Accepts a full or abbreviated hexadecimal object id.</summary>
    public static string Sha(string sha)
    {
        if (string.IsNullOrWhiteSpace(sha) || !ShaRegex().IsMatch(sha))
            throw new GitManagerException(ErrorCodes.Validation, $"Invalid commit id '{sha}'.");
        return sha.ToLowerInvariant();
    }

    /// <summary>Accepts a commit id or branch name (never an option such as "--output").</summary>
    public static string Revision(string rev)
    {
        if (string.IsNullOrWhiteSpace(rev)) throw new GitManagerException(ErrorCodes.Validation, "Revision is empty.");
        if (ShaRegex().IsMatch(rev)) return rev.ToLowerInvariant();
        if (rev.StartsWith("origin/", StringComparison.Ordinal)) return "refs/remotes/origin/" + Branch(rev["origin/".Length..]);
        return Branch(rev);
    }

    public static bool IsFullSha(string? sha) => sha is not null && (sha.Length == 40 || sha.Length == 64) && ShaRegex().IsMatch(sha);

    /// <summary>Glob match supporting *, ** and ? against a normalised path.</summary>
    public static bool GlobMatch(string pattern, string path)
    {
        var regex = "^" + Regex.Escape(pattern.Replace('\\', '/').TrimStart('/'))
            .Replace(@"\*\*/", "(?:.*/)?")
            .Replace(@"\*\*", ".*")
            .Replace(@"\*", "[^/]*")
            .Replace(@"\?", "[^/]") + "$";
        if (Regex.IsMatch(path, regex, RegexOptions.IgnoreCase)) return true;
        // Patterns without a slash match a file or directory name at any depth (gitignore semantics).
        if (!pattern.Contains('/'))
            return path.Split('/').Any(seg => Regex.IsMatch(seg, regex, RegexOptions.IgnoreCase));
        // A directory pattern matches everything below it.
        return path.StartsWith(pattern.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);
    }

    public static string Slugify(string value)
    {
        var slug = SlugRegex().Replace(value.Trim().ToLowerInvariant(), "-").Trim('-');
        if (slug.Length > 60) slug = slug[..60].Trim('-');
        return string.IsNullOrEmpty(slug) ? "repo" : slug;
    }

    [GeneratedRegex(@"^[A-Za-z0-9._/\-]+$")]
    private static partial Regex BranchRegex();

    [GeneratedRegex(@"^[A-Za-z0-9._/*?\-]+$")]
    private static partial Regex BranchPatternRegex();

    [GeneratedRegex(@"^[0-9a-fA-F]{7,64}$")]
    private static partial Regex ShaRegex();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex SlugRegex();
}
