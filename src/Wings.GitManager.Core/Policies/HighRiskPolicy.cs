using Wings.GitManager.Contracts;
using Wings.GitManager.Core.Safety;

namespace Wings.GitManager.Core.Policies;

/// <summary>Settings of the "high-risk" policy (stored as JSON in PolicyDefinitions; most specific scope wins).</summary>
public sealed class HighRiskPolicySettings
{
    public const string Key = "high-risk";

    public bool RequireVerifiedRemoteCheckpoint { get; set; } = true;
    public bool AllowAdministratorOverride { get; set; } = true;
    public int MultipleFileDeleteThreshold { get; set; } = 3;
    public int LargeChangeFileThreshold { get; set; } = 25;
    public int MediumChangeFileThreshold { get; set; } = 10;
    public int RenameThreshold { get; set; } = 10;
    public bool SourceDirectoryDeletionIsHighRisk { get; set; } = true;

    public List<string> MigrationPatterns { get; set; } =
        ["**/Migrations/**", "**/migrations/**", "**/migration/**", "**/db/migrate/**", "**/alembic/**", "**/flyway/**", "**/*.sql"];

    public List<string> AuthPatterns { get; set; } =
        ["**/auth/**", "**/authentication/**", "**/authorization/**", "**/identity/**", "**/security/**",
         "**/*Auth*.*", "**/*auth*.*", "**/*Login*.*", "**/*Permission*.*", "**/*.guard.ts", "**/*Jwt*.*", "**/*Oidc*.*"];

    public List<string> DeploymentPatterns { get; set; } =
        ["Dockerfile", "**/Dockerfile", "**/docker-compose*.yml", "**/docker-compose*.yaml", "**/*.bicep", "**/*.tf",
         "**/k8s/**", "**/kubernetes/**", "**/helm/**", "**/.github/workflows/**", "**/azure-pipelines*.yml", ".gitlab-ci.yml",
         "**/web.config", "**/appsettings.Production.json", "**/nginx*.conf", "**/deploy/**", "**/deployment/**"];

    public List<string> SharedLibraryPatterns { get; set; } =
        ["**/shared/**", "**/common/**", "**/libs/**", "**/*.Shared/**", "**/*.Common/**", "**/packages/**"];

    public List<string> DependencyManifests { get; set; } =
        ["package.json", "package-lock.json", "yarn.lock", "pnpm-lock.yaml", "*.csproj", "Directory.Packages.props", "Directory.Build.props",
         "packages.config", "nuget.config", "requirements.txt", "pyproject.toml", "Pipfile.lock", "poetry.lock", "pom.xml",
         "build.gradle", "build.gradle.kts", "go.mod", "go.sum", "Cargo.toml", "Cargo.lock", "Gemfile", "Gemfile.lock", "composer.json"];

    public List<string> HighRiskCategories { get; set; } =
        ["large-refactor", "refactor", "restructure", "replace-implementation", "multi-project", "bulk-rename", "bulk-delete", "dependency-update", "migration"];
}

public sealed record RiskAssessment(
    RiskLevel Level,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> AffectedPaths,
    IReadOnlyList<string> AffectedModules,
    bool RequiresVerifiedRemoteCheckpoint)
{
    public RiskAssessmentDto ToDto() => new()
    {
        Level = Level, Reasons = Reasons, AffectedPaths = AffectedPaths, AffectedModules = AffectedModules,
        RequiresVerifiedRemoteCheckpoint = RequiresVerifiedRemoteCheckpoint
    };
}

/// <summary>Classifies change operations before execution (declared intent) and after (actual file changes).</summary>
public static class HighRiskPolicyEngine
{
    public static RiskAssessment EvaluateIntent(ChangeIntent intent, HighRiskPolicySettings settings, Func<string, bool>? isTrackedDirectory = null)
    {
        var reasons = new List<string>();
        var deletes = intent.FilesToDelete.Select(RepoPath.Normalize).ToList();
        var dirDeletes = intent.DirectoriesToDelete.Select(RepoPath.Normalize).ToList();
        var modifies = intent.FilesToModify.Select(RepoPath.Normalize).ToList();
        var adds = intent.FilesToAdd.Select(RepoPath.Normalize).ToList();
        var renames = intent.Renames.Select(r => (From: RepoPath.Normalize(r.From), To: RepoPath.Normalize(r.To))).ToList();

        if (dirDeletes.Count > 0 && settings.SourceDirectoryDeletionIsHighRisk)
            reasons.Add($"Deletes {dirDeletes.Count} director{(dirDeletes.Count == 1 ? "y" : "ies")}: {string.Join(", ", dirDeletes.Take(5))}");
        if (deletes.Count >= settings.MultipleFileDeleteThreshold)
            reasons.Add($"Deletes {deletes.Count} files (threshold {settings.MultipleFileDeleteThreshold})");
        if (renames.Count >= settings.RenameThreshold)
            reasons.Add($"Renames or moves {renames.Count} files (threshold {settings.RenameThreshold})");

        var touched = modifies.Concat(deletes).Concat(renames.Select(r => r.From)).Distinct().ToList();
        var total = Math.Max(intent.EstimatedFileCount ?? 0, touched.Count + adds.Count + renames.Count);
        if (total >= settings.LargeChangeFileThreshold)
            reasons.Add($"Large-scale change across {total} files (threshold {settings.LargeChangeFileThreshold})");

        AddPatternReasons(touched.Concat(adds), settings, reasons, modifiedExisting: touched);

        foreach (var category in intent.Categories.Select(c => c.Trim().ToLowerInvariant()).Distinct())
            if (settings.HighRiskCategories.Contains(category, StringComparer.OrdinalIgnoreCase))
                reasons.Add($"Operation category '{category}' is high risk");

        var level = reasons.Count > 0 ? RiskLevel.High
            : total >= settings.MediumChangeFileThreshold || deletes.Count > 0 ? RiskLevel.Medium
            : RiskLevel.Low;
        var affected = touched.Concat(adds).Concat(dirDeletes).Concat(renames.Select(r => r.To)).Distinct().ToList();
        return new RiskAssessment(level, reasons, affected, Modules(affected),
            level == RiskLevel.High && settings.RequireVerifiedRemoteCheckpoint);
    }

    public static RiskAssessment EvaluateActual(IReadOnlyList<FileChangeDto> changes, HighRiskPolicySettings settings)
    {
        var reasons = new List<string>();
        var deletes = changes.Where(c => c.Kind == FileChangeKind.Deleted).Select(c => c.Path).ToList();
        var renames = changes.Count(c => c.Kind == FileChangeKind.Renamed);
        var existing = changes.Where(c => c.Kind is FileChangeKind.Modified or FileChangeKind.Deleted or FileChangeKind.Renamed)
            .Select(c => c.OldPath ?? c.Path).ToList();

        if (deletes.Count >= settings.MultipleFileDeleteThreshold)
            reasons.Add($"Deleted {deletes.Count} files");
        var deletedDirs = deletes.Select(d => d.Contains('/') ? d[..d.LastIndexOf('/')] : "").Where(d => d != "")
            .GroupBy(d => d).Where(g => g.Count() >= settings.MultipleFileDeleteThreshold).Select(g => g.Key).ToList();
        if (deletedDirs.Count > 0 && settings.SourceDirectoryDeletionIsHighRisk)
            reasons.Add($"Removed most of directory {string.Join(", ", deletedDirs.Take(3))}");
        if (renames >= settings.RenameThreshold)
            reasons.Add($"Renamed or moved {renames} files");
        if (changes.Count >= settings.LargeChangeFileThreshold)
            reasons.Add($"Changed {changes.Count} files");
        AddPatternReasons(changes.Select(c => c.Path), settings, reasons, existing);

        var level = reasons.Count > 0 ? RiskLevel.High : changes.Count >= settings.MediumChangeFileThreshold || deletes.Count > 0 ? RiskLevel.Medium : RiskLevel.Low;
        var paths = changes.Select(c => c.Path).ToList();
        return new RiskAssessment(level, reasons, paths, Modules(paths), level == RiskLevel.High && settings.RequireVerifiedRemoteCheckpoint);
    }

    private static void AddPatternReasons(IEnumerable<string> paths, HighRiskPolicySettings s, List<string> reasons, IReadOnlyCollection<string> modifiedExisting)
    {
        var list = paths.Distinct().ToList();
        Check(list, s.MigrationPatterns, "Changes database migration scripts", reasons);
        Check(list, s.AuthPatterns, "Modifies authentication or authorization code", reasons);
        Check(list, s.DeploymentPatterns, "Modifies deployment configuration", reasons);
        Check(modifiedExisting, s.SharedLibraryPatterns, "Changes shared libraries used by multiple modules", reasons);
        Check(modifiedExisting, s.DependencyManifests, "Changes project dependency definitions", reasons);
    }

    private static void Check(IEnumerable<string> paths, IEnumerable<string> patterns, string reason, List<string> reasons)
    {
        var hits = paths.Where(p => patterns.Any(pat => RepoPath.GlobMatch(pat, p))).Take(5).ToList();
        if (hits.Count > 0) reasons.Add($"{reason}: {string.Join(", ", hits)}");
    }

    /// <summary>Module = first directory (or first two when the first is a conventional container such as src/).</summary>
    public static IReadOnlyList<string> Modules(IEnumerable<string> paths) =>
        paths.Select(p =>
        {
            var parts = p.Split('/');
            if (parts.Length == 1) return "(root)";
            return parts[0] is "src" or "apps" or "packages" or "libs" or "projects" && parts.Length > 2 ? parts[0] + "/" + parts[1] : parts[0];
        }).Distinct().OrderBy(m => m).ToList();
}
