using System.Text.RegularExpressions;
using Wings.GitManager.Contracts;

namespace Wings.GitManager.Core.Recovery;

/// <summary>
/// Deterministic interpretation of common conversational recovery requests. Wings Code's language model can
/// pass structured hints instead; this parser makes the API useful on its own and explains what it understood
/// so Wings Code can confirm with the user.
/// </summary>
public static partial class RecoveryIntentParser
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","a","an","my","your","our","you","i","me","we","it","its","this","that","these","those","to","of","in","on","at","by","for",
        "from","with","without","and","or","but","back","go","bring","restore","recover","undo","revert","show","what","changed","change",
        "changes","made","make","last","previous","version","versions","code","please","can","could","would","before","after","old","new",
        "yesterday","today","hours","hour","minutes","minute","days","day","ago","session","sessions","stopped","working","project","removing",
        "remove","deleted","delete","accidentally","files","file","page","screen","component","agent","ai","since","into","all","everything",
        "get","put","undid","did","do","is","was","were","be","been","has","have","had","keep","just","only","state","point","when","them",
        "three","two","one","four","five","six","seven","eight","nine","ten","refactoring","refactor","gitea","server","copy","latest","earlier"
    };

    public static RecoveryInterpretationDto Parse(string? query, DateTime nowUtc)
    {
        var q = (query ?? "").Trim();
        var lower = q.ToLowerInvariant();
        DateTime? since = null, until = null;
        var notes = new List<string>();

        var rel = RelativeRegex().Match(lower);
        if (rel.Success)
        {
            var n = ParseNumber(rel.Groups[1].Value);
            var unit = rel.Groups[2].Value;
            since = unit.StartsWith("minute") ? nowUtc.AddMinutes(-n) : unit.StartsWith("hour") ? nowUtc.AddHours(-n) : nowUtc.AddDays(-n);
            notes.Add($"time window: last {n} {unit}");
        }
        else if (lower.Contains("yesterday"))
        {
            since = nowUtc.Date.AddDays(-1);
            until = nowUtc.Date;
            notes.Add("time window: yesterday (UTC)");
        }
        else if (lower.Contains("today"))
        {
            since = nowUtc.Date;
            notes.Add("time window: today (UTC)");
        }
        else if (lower.Contains("last week"))
        {
            since = nowUtc.AddDays(-7);
            notes.Add("time window: last 7 days");
        }

        var summaryOnly = SummaryRegex().IsMatch(lower);
        var lastSession = lower.Contains("session") && (lower.Contains("last") || lower.Contains("previous") || lower.Contains("yesterday") || lower.Contains("latest"));
        var beforeRefactor = lower.Contains("refactor") && (lower.Contains("before") || lower.Contains("prior"));
        var deleted = lower.Contains("delete") || lower.Contains("removed") || lower.Contains("lost");
        var wholeProject = lower.Contains("project") || lower.Contains("repository") || lower.Contains("from gitea") || lower.Contains("whole app");
        var preserve = PreserveRegex().IsMatch(lower);
        var undo = UndoRegex().IsMatch(lower);
        var keywords = Keywords(q);

        RecoveryType? type;
        if (summaryOnly)
        {
            type = null;
            notes.Add("show a change summary (no recovery)");
        }
        else if (deleted && wholeProject && (lower.Contains("i ") || lower.Contains("local") || lower.Contains("accidentally") || lower.Contains("from gitea")) && keywords.Count == 0)
        {
            type = RecoveryType.CloneProject;
            notes.Add("recover a deleted local project by cloning the central repository");
        }
        else if (lower.Contains("from gitea") || (lower.Contains("recover") && wholeProject && keywords.Count == 0))
        {
            type = RecoveryType.CloneProject;
            notes.Add("restore the project from the central repository");
        }
        else if (deleted && keywords.Count > 0)
        {
            type = RecoveryType.RestoreDeleted;
            notes.Add($"restore deleted files matching: {string.Join(", ", keywords)}");
        }
        else if (lastSession || (lower.Contains("agent") && undo))
        {
            type = RecoveryType.RevertSession;
            notes.Add("revert the commits of an AI session; unrelated later changes are kept");
        }
        else if (beforeRefactor || wholeProject && keywords.Count > 0 && (lower.Contains("before") || lower.Contains("as it was")))
        {
            type = RecoveryType.RestoreProject;
            notes.Add(beforeRefactor ? "restore the project to the commit before the most recent refactoring"
                : $"restore the project to the commit before the change matching: {string.Join(", ", keywords)}");
        }
        else if (keywords.Count > 0 && (preserve || lower.Contains("restore") || lower.Contains("bring back")))
        {
            type = RecoveryType.RestoreFile;
            notes.Add($"restore only files matching: {string.Join(", ", keywords)}" + (preserve ? " (other recent work is preserved)" : ""));
        }
        else if (undo || lower.Contains("previous version") || lower.Contains("go back") || lower.Contains("stopped working") || lower.Contains("known-good") || lower.Contains("known good"))
        {
            type = RecoveryType.RevertCommits;
            notes.Add("revert the most recent change operation(s) back to the previous checkpoint");
        }
        else
        {
            type = keywords.Count > 0 ? RecoveryType.RestoreFile : RecoveryType.RevertCommits;
            notes.Add("request was not specific; candidates are listed for the user to choose");
        }

        return new RecoveryInterpretationDto
        {
            SuggestedType = type,
            Since = since,
            Until = until,
            Keywords = keywords,
            LastSession = lastSession,
            BeforeLastRefactoring = beforeRefactor,
            DeletedContent = deleted,
            ChangeSummaryOnly = summaryOnly,
            PreserveRecentWork = preserve,
            Explanation = string.Join("; ", notes)
        };
    }

    public static IReadOnlyList<string> Keywords(string text) =>
        WordRegex().Matches(text).Select(m => m.Value.ToLowerInvariant())
            .Where(w => w.Length > 2 && !StopWords.Contains(w))
            .Distinct().ToList();

    /// <summary>Scores how well a path matches keywords, splitting camelCase, kebab and snake case.</summary>
    public static double PathScore(string path, IReadOnlyList<string> keywords)
    {
        if (keywords.Count == 0) return 0;
        var tokens = CamelRegex().Replace(path, "$1 $2").ToLowerInvariant()
            .Split(['/', '.', '-', '_', ' '], StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var hits = keywords.Count(k => tokens.Contains(k) || tokens.Any(t => t.StartsWith(k) || k.StartsWith(t) && t.Length > 3));
        return (double)hits / keywords.Count;
    }

    /// <summary>Fraction of keywords present in free text (commit subjects/bodies).</summary>
    public static double TextScore(string text, IReadOnlyList<string> keywords)
    {
        if (keywords.Count == 0) return 0;
        var words = WordRegex().Matches(CamelRegex().Replace(text, "$1 $2")).Select(m => m.Value.ToLowerInvariant()).ToHashSet();
        return (double)keywords.Count(k => words.Contains(k) || words.Any(w => w.StartsWith(k))) / keywords.Count;
    }

    private static int ParseNumber(string s) => s switch
    {
        "a" or "an" or "one" => 1, "two" => 2, "three" => 3, "four" => 4, "five" => 5, "six" => 6,
        "seven" => 7, "eight" => 8, "nine" => 9, "ten" => 10, "twelve" => 12, "few" => 3,
        _ => int.TryParse(s, out var n) ? n : 1
    };

    [GeneratedRegex(@"(?:last|past)\s+(\d+|a|an|one|two|three|four|five|six|seven|eight|nine|ten|twelve|few)\s+(minutes?|hours?|days?)")]
    private static partial Regex RelativeRegex();

    [GeneratedRegex(@"\b(show|list|what)\b.*\b(changed|changes|modified|did)\b")]
    private static partial Regex SummaryRegex();

    [GeneratedRegex(@"without (removing|losing|deleting|touching)|keep(ing)? my|preserv")]
    private static partial Regex PreserveRegex();

    [GeneratedRegex(@"\b(undo|revert|roll ?back|reverse)\b")]
    private static partial Regex UndoRegex();

    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9]+")]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"([a-z0-9])([A-Z])")]
    private static partial Regex CamelRegex();
}
