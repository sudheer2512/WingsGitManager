using System.Text;
using System.Text.RegularExpressions;
using Wings.GitManager.Contracts;

namespace Wings.GitManager.Core.Commits;

public sealed record CommitContext
{
    public string? Summary { get; init; }
    public string? Description { get; init; }
    public string? CommitType { get; init; }
    public string? ExplicitMessage { get; init; }
    public IReadOnlyList<FileChangeDto> Files { get; init; } = [];
    public Guid? OperationId { get; init; }
    public Guid? SessionId { get; init; }
    public string? AgentId { get; init; }
    public string? InitiatingUser { get; init; }
    public string? UserRequestRef { get; init; }
    public string? BuildStatus { get; init; }
    public string? TestStatus { get; init; }
}

/// <summary>Builds conventional commit messages with machine-readable Wings trailers.</summary>
public static partial class CommitMessageBuilder
{
    public const string OperationTrailer = "Wings-Operation-Id";
    public const string SessionTrailer = "Wings-Session-Id";
    public const string AgentTrailer = "Wings-Agent-Id";
    public const string UserTrailer = "Wings-Initiated-By";
    public const string RequestTrailer = "Wings-Request-Ref";
    public const string CheckpointTrailer = "Wings-Checkpoint";
    public const string RecoveryTrailer = "Wings-Recovery-Id";

    private static readonly string[] KnownTypes = ["feat", "fix", "refactor", "chore", "docs", "test", "style", "perf", "build", "ci", "revert", "checkpoint"];

    public static string Build(CommitContext ctx)
    {
        var subject = ctx.ExplicitMessage is { Length: > 0 } explicitMessage
            ? explicitMessage.Trim().Split('\n')[0]
            : $"{InferType(ctx)}: {Describe(ctx)}";
        subject = Truncate(subject.Replace('\r', ' '), 100);

        var sb = new StringBuilder(subject).Append("\n\n");
        if (ctx.ExplicitMessage is { Length: > 0 } m && m.Contains('\n'))
            sb.Append(m[(m.IndexOf('\n') + 1)..].Trim()).Append("\n\n");
        else if (!string.IsNullOrWhiteSpace(ctx.Description) && ctx.Summary is not null && ctx.Description != ctx.Summary)
            sb.Append(Truncate(ctx.Description!.Trim(), 2000)).Append("\n\n");

        if (ctx.Files.Count > 0)
        {
            sb.Append("Files:\n");
            foreach (var f in ctx.Files.Take(50))
                sb.Append("  ").Append(Letter(f.Kind)).Append(' ').Append(f.OldPath is not null && f.Kind == FileChangeKind.Renamed ? $"{f.OldPath} -> {f.Path}" : f.Path).Append('\n');
            if (ctx.Files.Count > 50) sb.Append($"  ... and {ctx.Files.Count - 50} more\n");
            sb.Append('\n');
        }
        if (ctx.BuildStatus is not null || ctx.TestStatus is not null)
            sb.Append($"Build: {ctx.BuildStatus ?? "not run"}; Tests: {ctx.TestStatus ?? "not run"}\n\n");

        AppendTrailer(sb, OperationTrailer, ctx.OperationId?.ToString());
        AppendTrailer(sb, SessionTrailer, ctx.SessionId?.ToString());
        AppendTrailer(sb, AgentTrailer, ctx.AgentId);
        AppendTrailer(sb, UserTrailer, ctx.InitiatingUser);
        AppendTrailer(sb, RequestTrailer, ctx.UserRequestRef);
        return sb.ToString().TrimEnd() + "\n";
    }

    public static string Checkpoint(string description, Guid? operationId, Guid? sessionId, string? agentId, string? user)
    {
        var sb = new StringBuilder(Truncate($"checkpoint: {description}", 100)).Append("\n\n");
        sb.Append("Automatic checkpoint created by Wings Git Manager to protect existing work.\n\n");
        AppendTrailer(sb, CheckpointTrailer, "pre-change");
        AppendTrailer(sb, OperationTrailer, operationId?.ToString());
        AppendTrailer(sb, SessionTrailer, sessionId?.ToString());
        AppendTrailer(sb, AgentTrailer, agentId);
        AppendTrailer(sb, UserTrailer, user);
        return sb.ToString().TrimEnd() + "\n";
    }

    public static string InferType(CommitContext ctx)
    {
        if (ctx.CommitType is { Length: > 0 } t && KnownTypes.Contains(t.ToLowerInvariant())) return t.ToLowerInvariant();
        var text = $"{ctx.Summary} {ctx.Description}".ToLowerInvariant();
        if (FixRegex().IsMatch(text)) return "fix";
        if (text.Contains("refactor") || text.Contains("extract") || text.Contains("restructure") || text.Contains("rename")) return "refactor";
        if (text.Contains("test")) return "test";
        if (text.Contains("readme") || text.Contains("documentation") || text.Contains("docs")) return "docs";
        if (text.Contains("upgrade") || text.Contains("dependenc") || text.Contains("bump")) return "chore";
        if (ctx.Files.Count > 0)
        {
            if (ctx.Files.All(f => IsDoc(f.Path))) return "docs";
            if (ctx.Files.All(f => IsTest(f.Path))) return "test";
            if (ctx.Files.All(f => f.Kind == FileChangeKind.Renamed)) return "refactor";
            if (ctx.Files.All(f => f.Kind == FileChangeKind.Deleted)) return "chore";
            if (ctx.Files.Any(f => f.Kind == FileChangeKind.Added)) return "feat";
        }
        if (text.Contains("add") || text.Contains("create") || text.Contains("implement") || text.Contains("new ")) return "feat";
        return "chore";
    }

    private static string Describe(CommitContext ctx)
    {
        var text = (ctx.Summary ?? ctx.Description)?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            text = text.Split('\n')[0].TrimEnd('.');
            text = PrefixRegex().Replace(text, "");
            return char.ToLowerInvariant(text[0]) + text[1..];
        }
        if (ctx.Files.Count == 1) return $"{Verb(ctx.Files[0].Kind)} {ctx.Files[0].Path}";
        var dirs = HighRiskModules(ctx.Files);
        return $"update {ctx.Files.Count} files in {dirs}";
    }

    private static string HighRiskModules(IReadOnlyList<FileChangeDto> files) =>
        string.Join(", ", Policies.HighRiskPolicyEngine.Modules(files.Select(f => f.Path)).Take(3));

    private static string Verb(FileChangeKind k) => k switch
    {
        FileChangeKind.Added => "add",
        FileChangeKind.Deleted => "remove",
        FileChangeKind.Renamed => "rename",
        _ => "update"
    };

    private static char Letter(FileChangeKind k) => k switch
    {
        FileChangeKind.Added => 'A', FileChangeKind.Deleted => 'D', FileChangeKind.Renamed => 'R', FileChangeKind.Copied => 'C', _ => 'M'
    };

    private static bool IsDoc(string p) => p.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || p.StartsWith("docs/", StringComparison.OrdinalIgnoreCase);
    private static bool IsTest(string p) => p.Contains("test", StringComparison.OrdinalIgnoreCase) || p.Contains(".spec.", StringComparison.OrdinalIgnoreCase);

    private static void AppendTrailer(StringBuilder sb, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        sb.Append(key).Append(": ").Append(value.Replace('\n', ' ').Replace('\r', ' ').Trim()).Append('\n');
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>Parses Wings trailers ("Key: value" lines) from a commit message.</summary>
    public static IReadOnlyDictionary<string, string> ParseTrailers(string message)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in message.Split('\n'))
        {
            var idx = line.IndexOf(": ", StringComparison.Ordinal);
            if (idx > 0 && line.StartsWith("Wings-", StringComparison.Ordinal))
                result[line[..idx]] = line[(idx + 2)..].Trim();
        }
        return result;
    }

    [GeneratedRegex(@"\b(fix|bug|correct|repair|resolve|broken|error)\b")]
    private static partial Regex FixRegex();

    [GeneratedRegex(@"^(feat|fix|refactor|chore|docs|test|style|perf|build|ci|revert|checkpoint)(\([^)]*\))?:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex PrefixRegex();
}
