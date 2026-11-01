using Microsoft.Extensions.Options;
using Wings.GitManager.Core;
using Wings.GitManager.Core.Entities;

namespace Wings.GitManager.Infrastructure.Git;

/// <summary>
/// Working-directory restrictions: every directory the service touches must be inside a configured workspace
/// root and (by default) inside the owning company's folder. Symbolic links that escape the root are rejected.
/// </summary>
public sealed class WorkspacePaths(IOptions<GitManagerOptions> options)
{
    private readonly GitManagerOptions _o = options.Value;

    private IReadOnlyList<string>? _roots;

    /// <summary>Configured roots as canonical (symlink-free) paths, matching the paths git reports.</summary>
    public IReadOnlyList<string> Roots => _roots ??= _o.WorkspaceRoots.Select(r =>
    {
        Directory.CreateDirectory(r);
        return Canonicalize(r);
    }).ToList();

    public string DefaultMainDirectory(Company company, Project project)
    {
        if (Roots.Count == 0) throw new GitManagerException("workspace_not_configured", "No workspace root is configured.", 500);
        return Path.Combine(Roots[0], company.Slug, project.Slug, "main");
    }

    /// <summary>Directory that holds session worktrees for a main working directory (CustomerPortal/worktrees/...).</summary>
    public static string WorktreesRoot(string mainDirectory)
    {
        var full = Path.GetFullPath(mainDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var parent = Path.GetDirectoryName(full)!;
        return Path.GetFileName(full).Equals("main", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(parent, "worktrees")
            : Path.Combine(parent, Path.GetFileName(full) + ".worktrees");
    }

    public string RecoveryRoot(string mainDirectory)
    {
        var full = Path.GetFullPath(mainDirectory).TrimEnd(Path.DirectorySeparatorChar);
        return Path.Combine(Path.GetDirectoryName(full)!, "recovery");
    }

    /// <summary>Validates and normalises an absolute directory for <paramref name="company"/>.</summary>
    public string Authorize(string path, Company company)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0'))
            throw new GitManagerException(ErrorCodes.PathRejected, "Directory is empty or invalid.");
        if (!Path.IsPathFullyQualified(path))
            throw new GitManagerException(ErrorCodes.PathRejected, "Working directories must be absolute paths.");

        // Canonicalising resolves every existing link, so a link that escapes the root ends up outside it.
        var full = Canonicalize(path);
        var root = Roots.FirstOrDefault(r => IsUnder(full, r))
            ?? throw new GitManagerException(ErrorCodes.PathRejected, "Directory is outside the configured workspace roots.", 403);

        if (_o.EnforceCompanyDirectories)
        {
            var companyRoot = Path.Combine(root, company.Slug);
            if (!IsUnder(full, companyRoot) || GitClient.SamePath(full, companyRoot))
                throw new GitManagerException(ErrorCodes.PathRejected, $"Directory must be inside the company folder '{company.Slug}'.", 403);
        }
        return full;
    }

    /// <summary>Re-validates a stored path before use (it may have been replaced by a link since registration).</summary>
    public string Revalidate(string path)
    {
        var full = Canonicalize(path);
        if (!Roots.Any(r => IsUnder(full, r)))
            throw new GitManagerException(ErrorCodes.PathRejected, "Stored directory is outside the workspace roots.", 403);
        return full;
    }

    /// <summary>Absolute path with every existing symbolic link / junction component resolved.</summary>
    public static string Canonicalize(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        var hops = 0;
        foreach (var segment in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (info.Exists && info.LinkTarget is not null)
            {
                if (++hops > 40) throw new GitManagerException(ErrorCodes.PathRejected, "Too many symbolic links.");
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is not null) next = Canonicalize(target.FullName);
            }
            current = next;
        }
        return current.Length > root.Length ? current.TrimEnd(Path.DirectorySeparatorChar) : current;
    }

    public static bool IsUnder(string path, string root)
    {
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return p.StartsWith(r, comparison) || string.Equals(p, r, comparison);
    }

    public static bool IsEmptyOrMissing(string dir) =>
        !Directory.Exists(dir) || !Directory.EnumerateFileSystemEntries(dir).Any();
}
