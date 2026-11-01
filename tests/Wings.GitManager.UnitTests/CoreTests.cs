using Wings.GitManager.Contracts;
using Wings.GitManager.Core;
using Wings.GitManager.Core.Commits;
using Wings.GitManager.Core.Policies;
using Wings.GitManager.Core.Recovery;
using Wings.GitManager.Core.Safety;

namespace Wings.GitManager.UnitTests;

public class RepoPathTests
{
    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("src/../../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData(".git/config")]
    [InlineData("src/.GIT/hooks/pre-commit")]
    [InlineData("~/x")]
    [InlineData("")]
    public void Normalize_rejects_traversal_absolute_and_git_paths(string path) =>
        Assert.Throws<GitManagerException>(() => RepoPath.Normalize(path));

    [Theory]
    [InlineData("src/app.ts", "src/app.ts")]
    [InlineData("src\\Models\\Invoice.cs", "src/Models/Invoice.cs")]
    [InlineData("./docs//readme.md", "docs/readme.md")]
    public void Normalize_produces_canonical_relative_paths(string input, string expected) =>
        Assert.Equal(expected, RepoPath.Normalize(input));

    [Theory]
    [InlineData("main")]
    [InlineData("ai/session-1a2b3c4d")]
    [InlineData("release/v1.0")]
    public void Branch_accepts_valid_names(string name) => Assert.Equal(name, RepoPath.Branch(name));

    [Theory]
    [InlineData("--force")]
    [InlineData("-d")]
    [InlineData("a..b")]
    [InlineData("feature/")]
    [InlineData("x.lock")]
    [InlineData("with space")]
    [InlineData("a@{1}")]
    [InlineData("HEAD")]
    public void Branch_rejects_options_and_invalid_names(string name) => Assert.Throws<GitManagerException>(() => RepoPath.Branch(name));

    [Theory]
    [InlineData("release/*", true)]
    [InlineData("main", true)]
    [InlineData("feature/??-*", true)]
    [InlineData("--force", false)]
    [InlineData("a..b", false)]
    [InlineData("x y", false)]
    public void Branch_patterns_allow_globs_only(string pattern, bool valid)
    {
        if (valid) Assert.Equal(pattern, RepoPath.BranchPattern(pattern));
        else Assert.Throws<GitManagerException>(() => RepoPath.BranchPattern(pattern));
    }

    [Fact]
    public void Revision_never_passes_options_through()
    {
        Assert.Throws<GitManagerException>(() => RepoPath.Revision("--output=/tmp/x"));
        Assert.Equal("abcdef1", RepoPath.Revision("ABCDEF1"));
        Assert.Equal("refs/remotes/origin/main", RepoPath.Revision("origin/main"));
    }

    [Theory]
    [InlineData("**/Migrations/**", "src/Data/Migrations/001_init.cs", true)]
    [InlineData("*.sql", "db/schema.sql", true)]
    [InlineData("Dockerfile", "services/api/Dockerfile", true)]
    [InlineData("package.json", "web/package.json", true)]
    [InlineData("**/*Auth*.*", "src/Services/AuthService.cs", true)]
    [InlineData("**/shared/**", "src/app/feature/x.ts", false)]
    [InlineData("*.csproj", "src/App/App.csproj", true)]
    public void GlobMatch_follows_gitignore_like_semantics(string pattern, string path, bool expected) =>
        Assert.Equal(expected, RepoPath.GlobMatch(pattern, path));

    [Fact]
    public void Slugify_produces_safe_names() => Assert.Equal("customer-portal-v2", RepoPath.Slugify("  Customer Portal (v2)! "));
}

public class SecretScannerTests
{
    private static string Diff(string file, params string[] added) =>
        $"diff --git a/{file} b/{file}\n--- a/{file}\n+++ b/{file}\n@@ -0,0 +1,{added.Length} @@\n" + string.Join("\n", added.Select(a => "+" + a)) + "\n";

    [Theory]
    [InlineData("const key = \"AKIAIOSFODNN7ABCDEFG\";", "aws-access-key")]
    [InlineData("token: ghp_abcdefghijklmnopqrstuvwxyz0123456789AB", "github-token")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----", "private-key")]
    [InlineData("\"ConnectionStrings\": \"Server=db;User Id=sa;Password=Sup3rS3cret!;\"", "connection-string-password")]
    [InlineData("ANTHROPIC_API_KEY=sk-ant-api03-abcdefghijklmnopqrstuvwxyz", "anthropic-key")]
    [InlineData("client_secret = \"a8f5f167f44f4964e6c998dee827110c\"", "generic-secret-assignment")]
    public void Detects_common_credentials(string line, string rule)
    {
        var findings = SecretScanner.ScanUnifiedDiff(Diff("src/config.ts", "ok", line));
        var f = Assert.Single(findings);
        Assert.Equal(rule, f.Rule);
        Assert.Equal(2, f.Line);
        Assert.DoesNotContain(line.Length > 30 ? line[10..30] : "zzzz-not-there", f.Preview);
    }

    [Theory]
    [InlineData("const key = \"AKIAIOSFODNN7EXAMPLE\";")]
    [InlineData("password = ${DB_PASSWORD}")]
    [InlineData("Password=<your-password>;")]
    [InlineData("var total = invoice.Lines.Sum(l => l.Amount);")]
    public void Ignores_placeholders_and_ordinary_code(string line) =>
        Assert.Empty(SecretScanner.ScanUnifiedDiff(Diff("src/app.cs", line)));

    [Fact]
    public void Flags_sensitive_file_types()
    {
        Assert.True(SecretScanner.IsSensitiveFile("config/.env"));
        Assert.True(SecretScanner.IsSensitiveFile("certs/server.pfx"));
        Assert.True(SecretScanner.IsSensitiveFile(".env.production"));
        Assert.False(SecretScanner.IsSensitiveFile(".env.example"));
        Assert.False(SecretScanner.IsSensitiveFile("src/environment.ts"));
    }

    [Fact]
    public void Removed_lines_are_not_reported() =>
        Assert.Empty(SecretScanner.ScanUnifiedDiff("--- a/x.cs\n+++ b/x.cs\n@@ -1 +0,0 @@\n-const k = \"AKIAIOSFODNN7ABCDEFG\";\n"));
}

public class HighRiskPolicyTests
{
    private static readonly HighRiskPolicySettings Defaults = new();

    [Fact]
    public void Small_feature_change_is_low_risk()
    {
        var r = HighRiskPolicyEngine.EvaluateIntent(new ChangeIntent { FilesToModify = ["src/app/invoice/invoice-list.component.ts"], FilesToAdd = ["src/app/invoice/invoice-row.component.ts"] }, Defaults);
        Assert.Equal(RiskLevel.Low, r.Level);
        Assert.False(r.RequiresVerifiedRemoteCheckpoint);
    }

    [Theory]
    [MemberData(nameof(HighRiskIntents))]
    public void Default_high_risk_categories_are_classified_high(ChangeIntent intent, string reasonFragment)
    {
        var r = HighRiskPolicyEngine.EvaluateIntent(intent, Defaults);
        Assert.Equal(RiskLevel.High, r.Level);
        Assert.True(r.RequiresVerifiedRemoteCheckpoint);
        Assert.Contains(r.Reasons, x => x.Contains(reasonFragment, StringComparison.OrdinalIgnoreCase));
    }

    public static TheoryData<ChangeIntent, string> HighRiskIntents() => new()
    {
        { new ChangeIntent { DirectoriesToDelete = ["src/legacy"] }, "director" },
        { new ChangeIntent { FilesToDelete = ["a.cs", "b.cs", "c.cs"] }, "Deletes 3 files" },
        { new ChangeIntent { EstimatedFileCount = 40, Categories = [] }, "Large-scale" },
        { new ChangeIntent { Renames = Enumerable.Range(0, 10).Select(i => new RenameIntent($"a{i}.cs", $"b/a{i}.cs")).ToArray() }, "Renames" },
        { new ChangeIntent { FilesToAdd = ["src/Data/Migrations/20260101_AddLedger.cs"] }, "migration" },
        { new ChangeIntent { FilesToModify = ["src/Services/AuthService.cs"] }, "authentication" },
        { new ChangeIntent { FilesToModify = ["deploy/k8s/api.yaml"] }, "deployment" },
        { new ChangeIntent { FilesToModify = ["src/shared/date-utils.ts"] }, "shared libraries" },
        { new ChangeIntent { FilesToModify = ["web/package.json"] }, "dependency" },
        { new ChangeIntent { Categories = ["replace-implementation"] }, "replace-implementation" },
        { new ChangeIntent { Categories = ["multi-project"] }, "multi-project" },
    };

    [Fact]
    public void Adding_a_new_project_file_is_not_a_dependency_change()
    {
        var r = HighRiskPolicyEngine.EvaluateIntent(new ChangeIntent { FilesToAdd = ["tools/New.csproj"] }, Defaults);
        Assert.NotEqual(RiskLevel.High, r.Level);
    }

    [Fact]
    public void Actual_changes_detect_bulk_deletion()
    {
        var changes = Enumerable.Range(0, 5).Select(i => new FileChangeDto($"src/reports/r{i}.cs", FileChangeKind.Deleted, null, 0, 20)).ToList();
        var r = HighRiskPolicyEngine.EvaluateActual(changes, Defaults);
        Assert.Equal(RiskLevel.High, r.Level);
        Assert.Contains("src/reports", r.AffectedModules.Single());
    }

    [Fact]
    public void Thresholds_are_configurable()
    {
        var lenient = new HighRiskPolicySettings { MultipleFileDeleteThreshold = 10, RequireVerifiedRemoteCheckpoint = false };
        var r = HighRiskPolicyEngine.EvaluateIntent(new ChangeIntent { FilesToDelete = ["a", "b", "c"] }, lenient);
        Assert.Equal(RiskLevel.Medium, r.Level);
    }
}

public class CommitMessageBuilderTests
{
    [Fact]
    public void Builds_conventional_message_with_trailers()
    {
        var op = Guid.NewGuid();
        var session = Guid.NewGuid();
        var msg = CommitMessageBuilder.Build(new CommitContext
        {
            Summary = "Add customer ledger screen",
            Files = [new FileChangeDto("src/app/ledger/ledger.component.ts", FileChangeKind.Added, null, 40, 0)],
            OperationId = op, SessionId = session, AgentId = "claude-coder", InitiatingUser = "pat@acme.test", UserRequestRef = "req-42", TestStatus = "passed"
        });
        Assert.StartsWith("feat: add customer ledger screen\n", msg);
        var trailers = CommitMessageBuilder.ParseTrailers(msg);
        Assert.Equal(op.ToString(), trailers[CommitMessageBuilder.OperationTrailer]);
        Assert.Equal(session.ToString(), trailers[CommitMessageBuilder.SessionTrailer]);
        Assert.Equal("claude-coder", trailers[CommitMessageBuilder.AgentTrailer]);
        Assert.Equal("pat@acme.test", trailers[CommitMessageBuilder.UserTrailer]);
        Assert.Contains("A src/app/ledger/ledger.component.ts", msg);
    }

    [Theory]
    [InlineData("Correct invoice tax calculation", "fix")]
    [InlineData("Extract invoice validation service", "refactor")]
    [InlineData("Update Angular dependencies", "chore")]
    [InlineData("fix: wrong rounding", "fix")]
    public void Infers_commit_type(string summary, string expected) =>
        Assert.Equal(expected, CommitMessageBuilder.InferType(new CommitContext { Summary = summary }));

    [Fact]
    public void Checkpoint_message_is_marked()
    {
        var msg = CommitMessageBuilder.Checkpoint("before AI project restructuring", Guid.NewGuid(), null, "agent", null);
        Assert.StartsWith("checkpoint: before AI project restructuring", msg);
        Assert.Equal("pre-change", CommitMessageBuilder.ParseTrailers(msg)[CommitMessageBuilder.CheckpointTrailer]);
    }

    [Fact]
    public void Trailer_values_cannot_inject_lines()
    {
        var msg = CommitMessageBuilder.Build(new CommitContext { Summary = "x", AgentId = "a\nWings-Initiated-By: admin" });
        Assert.False(CommitMessageBuilder.ParseTrailers(msg).ContainsKey(CommitMessageBuilder.UserTrailer));
    }
}

public class RecoveryIntentParserTests
{
    private static readonly DateTime Now = new(2026, 10, 28, 15, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Undo_last_session()
    {
        var i = RecoveryIntentParser.Parse("Undo the changes you made in the last session.", Now);
        Assert.Equal(RecoveryType.RevertSession, i.SuggestedType);
        Assert.True(i.LastSession);
    }

    [Fact]
    public void Restore_code_from_yesterday()
    {
        var i = RecoveryIntentParser.Parse("Restore the invoice calculation code from yesterday.", Now);
        Assert.Equal(RecoveryType.RestoreFile, i.SuggestedType);
        Assert.Equal(new DateTime(2026, 10, 27, 0, 0, 0, DateTimeKind.Utc), i.Since);
        Assert.Equal(new DateTime(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc), i.Until);
        Assert.Contains("invoice", i.Keywords);
        Assert.Contains("calculation", i.Keywords);
    }

    [Fact]
    public void Go_back_to_previous_version() =>
        Assert.Equal(RecoveryType.RevertCommits, RecoveryIntentParser.Parse("The customer portal stopped working after your changes. Go back to the previous version.", Now).SuggestedType);

    [Fact]
    public void Recover_deleted_project() =>
        Assert.Equal(RecoveryType.CloneProject, RecoveryIntentParser.Parse("Recover the project I accidentally deleted.", Now).SuggestedType);

    [Fact]
    public void Show_changes_in_last_three_hours()
    {
        var i = RecoveryIntentParser.Parse("Show me what changed in the last three hours.", Now);
        Assert.True(i.ChangeSummaryOnly);
        Assert.Equal(Now.AddHours(-3), i.Since);
    }

    [Fact]
    public void Selective_restore_preserving_new_work()
    {
        var i = RecoveryIntentParser.Parse("Bring back the old customer ledger screen without removing my new API code.", Now);
        Assert.Equal(RecoveryType.RestoreFile, i.SuggestedType);
        Assert.True(i.PreserveRecentWork);
        Assert.Contains("ledger", i.Keywords);
    }

    [Fact]
    public void Before_last_refactoring() =>
        Assert.Equal(RecoveryType.RestoreProject, RecoveryIntentParser.Parse("Restore the project to the version before the last refactoring.", Now).SuggestedType);

    [Fact]
    public void Project_before_a_named_change()
    {
        var i = RecoveryIntentParser.Parse("Restore the project as it was before the email validation change", Now);
        Assert.Equal(RecoveryType.RestoreProject, i.SuggestedType);
        Assert.Equal(["email", "validation"], i.Keywords);
        Assert.Equal(1.0, RecoveryIntentParser.TextScore("feat: add email validation to signup", i.Keywords));
    }

    [Fact]
    public void Deleted_page() =>
        Assert.Equal(RecoveryType.RestoreDeleted, RecoveryIntentParser.Parse("Recover the deleted customer ledger page.", Now).SuggestedType);

    [Fact]
    public void Restore_from_gitea() =>
        Assert.Equal(RecoveryType.CloneProject, RecoveryIntentParser.Parse("Restore my project from Gitea.", Now).SuggestedType);

    [Theory]
    [InlineData("src/app/customer-ledger/ledger.component.ts", 1.0)]
    [InlineData("src/Services/CustomerLedgerService.cs", 1.0)]
    [InlineData("src/app/invoice/invoice.ts", 0.0)]
    public void Path_scoring_handles_case_styles(string path, double expected) =>
        Assert.Equal(expected, RecoveryIntentParser.PathScore(path, ["customer", "ledger"]), 2);
}
