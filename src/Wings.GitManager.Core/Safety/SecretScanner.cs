using System.Text.RegularExpressions;
using Wings.GitManager.Contracts;

namespace Wings.GitManager.Core.Safety;

/// <summary>
/// Scans added lines and file names for credentials before they are committed. Rules are deliberately
/// conservative (high-signal formats) so that blocking mode does not stop ordinary work.
/// </summary>
public static partial class SecretScanner
{
    private sealed record Rule(string Name, Regex Pattern);

    private static readonly Rule[] Rules =
    [
        new("private-key", new Regex(@"-----BEGIN (?:RSA |EC |DSA |OPENSSH |PGP |ENCRYPTED )?PRIVATE KEY( BLOCK)?-----", RegexOptions.Compiled)),
        new("aws-access-key", new Regex(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b", RegexOptions.Compiled)),
        new("aws-secret-key", new Regex(@"(?i)aws.{0,20}(?:secret|private).{0,20}['""][0-9a-zA-Z/+]{40}['""]", RegexOptions.Compiled)),
        new("github-token", new Regex(@"\b(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{36,}\b|\bgithub_pat_[A-Za-z0-9_]{50,}\b", RegexOptions.Compiled)),
        new("gitlab-token", new Regex(@"\bglpat-[A-Za-z0-9\-_]{20,}\b", RegexOptions.Compiled)),
        new("slack-token", new Regex(@"\bxox[abprs]-[A-Za-z0-9-]{10,}\b", RegexOptions.Compiled)),
        new("stripe-secret", new Regex(@"\b(?:sk|rk)_live_[A-Za-z0-9]{20,}\b", RegexOptions.Compiled)),
        new("google-api-key", new Regex(@"\bAIza[0-9A-Za-z\-_]{35}\b", RegexOptions.Compiled)),
        new("anthropic-key", new Regex(@"\bsk-ant-[A-Za-z0-9\-_]{20,}\b", RegexOptions.Compiled)),
        new("openai-key", new Regex(@"\bsk-(?:proj-)?[A-Za-z0-9]{32,}\b", RegexOptions.Compiled)),
        new("azure-storage-key", new Regex(@"AccountKey=[A-Za-z0-9+/=]{60,}", RegexOptions.Compiled)),
        new("connection-string-password", new Regex(@"(?i)(?:password|pwd)\s*=\s*(?!\s*;|\s*['""]?\s*$|\{|\$\{|%|<)[^;'""\s]{6,}", RegexOptions.Compiled)),
        new("jwt", new Regex(@"\beyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b", RegexOptions.Compiled)),
        new("generic-secret-assignment", new Regex(@"(?i)\b(?:api[_-]?key|secret[_-]?key|client[_-]?secret|access[_-]?token|auth[_-]?token)\b\s*[:=]\s*['""][A-Za-z0-9_\-+/=]{24,}['""]", RegexOptions.Compiled)),
    ];

    private static readonly string[] SensitiveFileNames =
        [".env", ".env.local", ".env.production", "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519", ".npmrc", ".pypirc", "credentials.json", ".git-credentials", ".netrc"];

    private static readonly string[] SensitiveExtensions = [".pem", ".key", ".pfx", ".p12", ".jks", ".keystore", ".ppk"];

    /// <summary>Paths that are never staged regardless of repository settings.</summary>
    public static readonly string[] AlwaysExcludedPatterns =
        [".env", ".env.*", "*.pem", "*.pfx", "*.p12", "*.key", "id_rsa", "id_ed25519", ".git-credentials", ".netrc", "**/secrets.json"];

    public static bool IsSensitiveFile(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        if (name.Equals(".env.example", StringComparison.OrdinalIgnoreCase) || name.Equals(".env.sample", StringComparison.OrdinalIgnoreCase))
            return false;
        return SensitiveFileNames.Contains(name, StringComparer.OrdinalIgnoreCase)
            || name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)
            || SensitiveExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Scans a unified diff (git diff --cached -U0) and reports findings on added lines.</summary>
    public static IReadOnlyList<SecretFindingDto> ScanUnifiedDiff(string diff)
    {
        var findings = new List<SecretFindingDto>();
        string? file = null;
        var line = 0;
        foreach (var raw in diff.Split('\n'))
        {
            if (raw.StartsWith("+++ "))
            {
                var target = raw[4..].Trim();
                file = target == "/dev/null" ? null : target.StartsWith("b/") ? target[2..] : target;
                if (file is not null && IsSensitiveFile(file))
                    findings.Add(new SecretFindingDto(file, 0, "sensitive-file", "File type is never committed"));
                continue;
            }
            if (raw.StartsWith("@@"))
            {
                var m = HunkRegex().Match(raw);
                line = m.Success ? int.Parse(m.Groups[1].Value) : 0;
                continue;
            }
            if (file is null) continue;
            if (raw.StartsWith('+') && !raw.StartsWith("+++"))
            {
                var text = raw[1..];
                foreach (var rule in Rules)
                {
                    var match = rule.Pattern.Match(text);
                    if (match.Success && !LooksLikePlaceholder(match.Value))
                        findings.Add(new SecretFindingDto(file, line, rule.Name, Mask(match.Value)));
                }
                line++;
            }
            else if (!raw.StartsWith('-'))
            {
                line++;
            }
        }
        return findings;
    }

    private static bool LooksLikePlaceholder(string value)
    {
        var v = value.ToLowerInvariant();
        return v.Contains("example") || v.Contains("xxxxxxxx") || v.Contains("placeholder") || v.Contains("changeme")
            || v.Contains("your_") || v.Contains("your-") || v.Contains("<") || v.Contains("dummy");
    }

    private static string Mask(string value) =>
        value.Length <= 8 ? new string('*', value.Length) : value[..4] + new string('*', Math.Min(12, value.Length - 6)) + value[^2..];

    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(\d+)")]
    private static partial Regex HunkRegex();
}
