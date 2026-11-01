using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wings.GitManager.Core;

namespace Wings.GitManager.Infrastructure.Git;

public sealed record GitResult(int ExitCode, string StdOut, string StdErr, byte[]? StdOutBytes = null)
{
    public bool Success => ExitCode == 0;
}

public sealed class GitRunOptions
{
    public GitCredential? Credential { get; init; }
    public bool Network { get; init; }
    public bool ThrowOnError { get; init; } = true;
    public bool BinaryOutput { get; init; }
    public string? StandardInput { get; init; }
    public TimeSpan? Timeout { get; init; }
    /// <summary>Allow the file:// / local-path transport for this call (local clones, bundles, file-system remotes).</summary>
    public bool AllowFileProtocol { get; init; }
    /// <summary>Restricted environment overrides (author/committer identity, alternate index).</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
}

public interface IGitExecutor
{
    Task<GitResult> RunAsync(string workingDirectory, IReadOnlyList<string> args, GitRunOptions? options = null, CancellationToken ct = default);
}

/// <summary>
/// The only component that starts git processes. Commands come from an allow-list, arguments are passed
/// without a shell, repository hooks and external helpers are disabled, the system/global configuration is
/// replaced by a private one, and credentials travel in environment-provided config (never on the command line).
/// </summary>
public sealed class GitExecutor : IGitExecutor
{
    private static readonly HashSet<string> AllowedCommands =
    [
        "init", "clone", "config", "add", "rm", "mv", "commit", "commit-tree", "write-tree", "read-tree", "update-ref",
        "symbolic-ref", "rev-parse", "rev-list", "log", "show", "diff", "diff-tree", "status", "ls-files", "ls-tree",
        "ls-remote", "cat-file", "push", "fetch", "remote", "branch", "checkout", "restore", "reset", "clean", "merge",
        "merge-base", "merge-tree", "revert", "worktree", "bundle", "fsck", "count-objects", "for-each-ref", "apply",
        "check-ignore", "show-ref", "grep", "tag", "gc", "update-index", "var", "version"
    ];

    private static readonly string[] ForbiddenArgumentPrefixes =
        ["--upload-pack", "--receive-pack", "--exec", "--config", "--template", "--git-dir", "--work-tree", "--ext-diff", "--namespace", "--super-prefix", "--output", "--open-files-in-pager", "--textconv"];

    private static readonly HashSet<string> AllowedEnvironment =
    [
        "GIT_AUTHOR_NAME", "GIT_AUTHOR_EMAIL", "GIT_AUTHOR_DATE", "GIT_COMMITTER_NAME", "GIT_COMMITTER_EMAIL",
        "GIT_COMMITTER_DATE", "GIT_INDEX_FILE"
    ];

    private readonly GitManagerOptions _options;
    private readonly ILogger<GitExecutor> _logger;
    private readonly string _gitHome;
    private readonly string _hooksDir;

    public GitExecutor(IOptions<GitManagerOptions> options, ILogger<GitExecutor> logger)
    {
        _options = options.Value;
        _logger = logger;
        var data = Path.GetFullPath(_options.DataDirectory);
        _gitHome = Path.Combine(data, "git-home");
        _hooksDir = Path.Combine(data, "git-empty-hooks");
        Directory.CreateDirectory(_gitHome);
        Directory.CreateDirectory(_hooksDir);
        var globalConfig = Path.Combine(_gitHome, ".gitconfig");
        if (!File.Exists(globalConfig)) File.WriteAllText(globalConfig, "");
    }

    public async Task<GitResult> RunAsync(string workingDirectory, IReadOnlyList<string> args, GitRunOptions? options = null, CancellationToken ct = default)
    {
        options ??= new GitRunOptions();
        Validate(args);
        if (!Directory.Exists(workingDirectory))
            throw new GitManagerException("working_directory_missing", $"Working directory does not exist: {workingDirectory}", 409);

        var psi = new ProcessStartInfo(_options.GitExecutable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = options.StandardInput is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = options.BinaryOutput ? null : Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        BuildEnvironment(psi, options);
        foreach (var a in GlobalArguments(options)) psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);

        var timeout = options.Timeout ?? TimeSpan.FromSeconds(options.Network ? _options.GitNetworkTimeoutSeconds : _options.GitTimeoutSeconds);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var sw = Stopwatch.StartNew();

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new GitManagerException("git_unavailable", $"Unable to start git: {ex.Message}", 500);
        }

        try
        {
            if (options.StandardInput is not null)
            {
                await process.StandardInput.WriteAsync(options.StandardInput.AsMemory(), timeoutCts.Token);
                process.StandardInput.Close();
            }
            var stdoutTask = ReadLimitedAsync(process.StandardOutput.BaseStream, _options.MaxGitOutputBytes, timeoutCts.Token);
            var stderrTask = ReadLimitedAsync(process.StandardError.BaseStream, 1024 * 1024, timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);
            var (outBytes, outTruncated) = await stdoutTask;
            var (errBytes, _) = await stderrTask;
            if (outTruncated)
                throw new GitManagerException("git_output_too_large", $"git {args[0]} output exceeded {_options.MaxGitOutputBytes} bytes.", 413);

            var stdout = options.BinaryOutput ? "" : Encoding.UTF8.GetString(outBytes);
            var stderr = Encoding.UTF8.GetString(errBytes);
            var result = new GitResult(process.ExitCode, stdout, stderr, options.BinaryOutput ? outBytes : null);
            _logger.LogDebug("git {Command} exited {ExitCode} in {Elapsed} ms ({Dir})", Describe(args), result.ExitCode, sw.ElapsedMilliseconds, workingDirectory);

            if (!result.Success && options.ThrowOnError)
            {
                _logger.LogWarning("git {Command} failed with {ExitCode}: {Error}", Describe(args), result.ExitCode, Redact(stderr));
                throw new GitCommandException($"git {args[0]} failed: {FirstLine(Redact(stderr))}", result.ExitCode, Redact(stderr));
            }
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Kill(process);
            throw new GitManagerException("git_timeout", $"git {args[0]} timed out after {timeout.TotalSeconds:0}s.", 504);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
    }

    private IEnumerable<string> GlobalArguments(GitRunOptions o)
    {
        string Cfg(string kv) => kv;
        yield return "-c"; yield return Cfg($"core.hooksPath={_hooksDir}");
        yield return "-c"; yield return "core.fsmonitor=false";
        yield return "-c"; yield return "core.pager=cat";
        yield return "-c"; yield return "core.askPass=";
        yield return "-c"; yield return "credential.helper=";
        yield return "-c"; yield return "core.sshCommand=false";
        yield return "-c"; yield return "protocol.allow=never";
        yield return "-c"; yield return "protocol.https.allow=always";
        yield return "-c"; yield return $"protocol.http.allow={(_options.AllowHttpRemotes ? "always" : "never")}";
        yield return "-c"; yield return $"protocol.file.allow={(o.AllowFileProtocol || _options.AllowFileRemotes ? "always" : "never")}";
        yield return "-c"; yield return "transfer.fsckObjects=true";
        yield return "-c"; yield return "commit.gpgSign=false";
        yield return "-c"; yield return "tag.gpgSign=false";
        yield return "-c"; yield return "advice.detachedHead=false";
        yield return "-c"; yield return "init.defaultBranch=main";
        yield return "-c"; yield return "core.longpaths=true";
        yield return "-c"; yield return "gc.autoDetach=false";
        yield return "-c"; yield return "submodule.recurse=false";
        yield return "-c"; yield return $"user.name={_options.ServiceCommitterName}";
        yield return "-c"; yield return $"user.email={_options.ServiceCommitterEmail}";
        if (o.Credential is not null)
        {
            yield return "-c"; yield return "http.followRedirects=false";
        }
    }

    private void BuildEnvironment(ProcessStartInfo psi, GitRunOptions o)
    {
        var inherited = new[] { "PATH", "SystemRoot", "TEMP", "TMP", "LANG", "TZ", "WINDIR", "ComSpec", "PATHEXT", "SSL_CERT_FILE", "SSL_CERT_DIR", "HTTPS_PROXY", "HTTP_PROXY", "NO_PROXY" }
            .Select(k => (k, v: System.Environment.GetEnvironmentVariable(k))).Where(x => x.v is not null).ToList();
        psi.Environment.Clear();
        foreach (var (k, v) in inherited) psi.Environment[k] = v;
        psi.Environment["HOME"] = _gitHome;
        psi.Environment["USERPROFILE"] = _gitHome;
        psi.Environment["XDG_CONFIG_HOME"] = _gitHome;
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(_gitHome, ".gitconfig");
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GCM_INTERACTIVE"] = "never";
        psi.Environment["LC_ALL"] = "C";
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        psi.Environment["GIT_ALLOW_PROTOCOL"] = o.AllowFileProtocol || _options.AllowFileRemotes
            ? (_options.AllowHttpRemotes ? "https:http:file" : "https:file")
            : (_options.AllowHttpRemotes ? "https:http" : "https");

        if (o.Credential is not null)
        {
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{o.Credential.Username}:{o.Credential.Secret}"));
            psi.Environment["GIT_CONFIG_COUNT"] = "1";
            psi.Environment["GIT_CONFIG_KEY_0"] = "http.extraHeader";
            psi.Environment["GIT_CONFIG_VALUE_0"] = $"Authorization: Basic {basic}";
        }
        if (o.Environment is not null)
        {
            foreach (var (k, v) in o.Environment)
            {
                if (!AllowedEnvironment.Contains(k)) throw new InvalidOperationException($"Environment variable {k} is not allowed for git.");
                psi.Environment[k] = v;
            }
        }
    }

    private static void Validate(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || !AllowedCommands.Contains(args[0]))
            throw new GitManagerException("git_command_rejected", $"git command '{(args.Count > 0 ? args[0] : "")}' is not allowed.", 400);
        foreach (var a in args)
        {
            if (a.Contains('\0')) throw new GitManagerException("git_command_rejected", "Argument contains NUL.", 400);
            if (ForbiddenArgumentPrefixes.Any(p => a.StartsWith(p, StringComparison.Ordinal)))
                throw new GitManagerException("git_command_rejected", $"Argument '{a}' is not allowed.", 400);
        }
        if (args[0] == "push" && args.Any(a => a is "--force" or "-f" or "--force-with-lease" or "--mirror" or "--delete" or "-d" || a.StartsWith("--force") || a.StartsWith('+')))
            throw new GitManagerException(ErrorCodes.ForcePushRefused, "Force pushes, deletions and mirror pushes are never performed automatically.", 400);
        if (args[0] == "config" && args.Skip(1).FirstOrDefault(a => !a.StartsWith('-')) is { } key && !AllowedConfigKeys.Any(k => key.StartsWith(k, StringComparison.OrdinalIgnoreCase)))
            throw new GitManagerException("git_command_rejected", $"Setting git config '{key}' is not allowed.", 400);
    }

    /// <summary>The only repository configuration keys the service writes.</summary>
    private static readonly string[] AllowedConfigKeys =
        ["remote.origin.", "branch.", "core.bare", "receive.denyNonFastForwards", "receive.denyDeletes", "core.logAllRefUpdates", "gc.reflogExpire", "extensions.worktreeConfig", "--get", "--list", "--local"];

    private static async Task<(byte[] Data, bool Truncated)> ReadLimitedAsync(Stream stream, long limit, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        var truncated = false;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + read > limit) { truncated = true; continue; }
            ms.Write(buffer, 0, read);
        }
        return (ms.ToArray(), truncated);
    }

    private static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }

    private static string Describe(IReadOnlyList<string> args) =>
        string.Join(' ', args.Take(6).Select(a => a.Length > 80 ? a[..80] + "…" : a));

    private static string FirstLine(string s)
    {
        var lines = s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith("hint:")).ToList();
        return lines.FirstOrDefault(l => l.StartsWith("fatal:") || l.StartsWith("error:")) ?? lines.FirstOrDefault() ?? "unknown error";
    }

    /// <summary>Removes anything that looks like credentials from git output before it is logged or returned.</summary>
    public static string Redact(string s) =>
        System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(s, @"(https?://)[^/@\s]+@", "$1***@"),
            @"(?i)(authorization:\s*\w+\s+)\S+", "$1***");
}
