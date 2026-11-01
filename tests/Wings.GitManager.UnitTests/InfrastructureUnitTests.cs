using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wings.GitManager.Contracts;
using Wings.GitManager.Core;
using Wings.GitManager.Infrastructure.Backup;
using Wings.GitManager.Infrastructure.Git;

namespace Wings.GitManager.UnitTests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wgm-unit-" + Guid.NewGuid().ToString("N")[..8]);
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose()
    {
        try { foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal); Directory.Delete(Path, true); } catch { }
    }
}

public class BackupCryptoTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    [InlineData(1024 * 1024)]
    [InlineData(3 * 1024 * 1024 + 5)]
    public async Task Round_trips_files_of_any_size(int size)
    {
        using var t = new TempDir();
        var key = RandomNumberGenerator.GetBytes(32);
        var input = Path.Combine(t.Path, "in");
        var data = RandomNumberGenerator.GetBytes(size);
        await File.WriteAllBytesAsync(input, data);
        await BackupCrypto.EncryptAsync(input, input + ".enc", key, default);
        await BackupCrypto.DecryptAsync(input + ".enc", input + ".out", key, default);
        Assert.Equal(data, await File.ReadAllBytesAsync(input + ".out"));
        Assert.NotEqual(data.Take(64), (await File.ReadAllBytesAsync(input + ".enc")).Skip(21).Take(64));
    }

    [Fact]
    public async Task Detects_tampering_wrong_key_and_truncation()
    {
        using var t = new TempDir();
        var key = RandomNumberGenerator.GetBytes(32);
        var input = Path.Combine(t.Path, "in");
        await File.WriteAllBytesAsync(input, RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 100));
        await BackupCrypto.EncryptAsync(input, input + ".enc", key, default);

        await Assert.ThrowsAnyAsync<CryptographicException>(() => BackupCrypto.DecryptAsync(input + ".enc", input + ".x", RandomNumberGenerator.GetBytes(32), default));

        var bytes = await File.ReadAllBytesAsync(input + ".enc");
        bytes[100] ^= 0xFF;
        await File.WriteAllBytesAsync(input + ".bad", bytes);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => BackupCrypto.DecryptAsync(input + ".bad", input + ".y", key, default));

        var original = await File.ReadAllBytesAsync(input + ".enc");
        var firstChunkEnd = 17 + 4 + 1024 * 1024 + 16;
        await File.WriteAllBytesAsync(input + ".trunc", original[..firstChunkEnd]);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => BackupCrypto.DecryptAsync(input + ".trunc", input + ".z", key, default));
    }
}

public class GitExecutorTests
{
    private static GitExecutor Executor(string data) =>
        new(Options.Create(new GitManagerOptions { DataDirectory = data }), NullLogger<GitExecutor>.Instance);

    [Theory]
    [InlineData("push", "--force", "origin", "main")]
    [InlineData("push", "origin", "+main:main")]
    [InlineData("push", "--force-with-lease", "origin", "main")]
    [InlineData("push", "--mirror", "origin")]
    [InlineData("push", "--delete", "origin", "main")]
    public async Task Refuses_force_and_destructive_pushes(params string[] args)
    {
        using var t = new TempDir();
        var ex = await Assert.ThrowsAsync<GitManagerException>(() => Executor(t.Path).RunAsync(t.Path, args));
        Assert.Equal(ErrorCodes.ForcePushRefused, ex.Code);
    }

    [Theory]
    [InlineData("daemon")]
    [InlineData("filter-branch")]
    [InlineData("submodule")]
    [InlineData("-c")]
    public async Task Refuses_commands_outside_the_allow_list(string command)
    {
        using var t = new TempDir();
        var ex = await Assert.ThrowsAsync<GitManagerException>(() => Executor(t.Path).RunAsync(t.Path, [command, "x"]));
        Assert.Equal("git_command_rejected", ex.Code);
    }

    [Theory]
    [InlineData("clone", "--upload-pack=touch /tmp/pwned", "x")]
    [InlineData("log", "--output=/tmp/x")]
    [InlineData("diff", "--ext-diff")]
    [InlineData("config", "core.fsmonitor", "touch /tmp/x")]
    public async Task Refuses_command_execution_options(params string[] args)
    {
        using var t = new TempDir();
        await Assert.ThrowsAsync<GitManagerException>(() => Executor(t.Path).RunAsync(t.Path, args));
    }

    [Fact]
    public async Task Repository_hooks_are_never_executed()
    {
        using var t = new TempDir();
        var git = Executor(Path.Combine(t.Path, "data"));
        var repo = Path.Combine(t.Path, "repo");
        Directory.CreateDirectory(repo);
        await git.RunAsync(repo, ["init", "-q"]);
        var marker = Path.Combine(t.Path, "hook-ran");
        var hook = Path.Combine(repo, ".git", "hooks", "pre-commit");
        await File.WriteAllTextAsync(hook, $"#!/bin/sh\ntouch '{marker}'\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await File.WriteAllTextAsync(Path.Combine(repo, "a.txt"), "a");
        await git.RunAsync(repo, ["add", "a.txt"]);
        await git.RunAsync(repo, ["commit", "-q", "-m", "x"]);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void Redacts_credentials_from_output()
    {
        Assert.Equal("fatal: https://***@gitea.local/x.git", GitExecutor.Redact("fatal: https://user:tok@gitea.local/x.git"));
        Assert.DoesNotContain("abc123", GitExecutor.Redact("Authorization: Basic abc123"));
    }
}

public class GitParsingTests
{
    [Fact]
    public void Merges_name_status_and_numstat_including_renames()
    {
        var nameStatus = "M\0src/a.cs\0R087\0old/b.cs\0new/b.cs\0D\0gone.txt\0A\0bin.png\0";
        var numstat = "3\t1\tsrc/a.cs\0" + "2\t2\t\0old/b.cs\0new/b.cs\0" + "0\t9\tgone.txt\0" + "-\t-\tbin.png\0";
        var files = GitClient.MergeNameStatus(nameStatus, numstat);
        Assert.Equal(4, files.Count);
        Assert.Equal(new FileChangeDto("src/a.cs", FileChangeKind.Modified, null, 3, 1), files[0]);
        Assert.Equal(new FileChangeDto("new/b.cs", FileChangeKind.Renamed, "old/b.cs", 2, 2), files[1]);
        Assert.Equal(FileChangeKind.Deleted, files[2].Kind);
        Assert.Equal(9, files[2].Deletions);
        Assert.Equal(0, files[3].Additions);
    }

    [Fact]
    public void Parses_log_records_with_multiline_bodies()
    {
        var output = "abc\u001fp1 p2\u001fAlice\u001fa@x\u001f2026-10-27T10:00:00+02:00\u001f2026-10-27T10:00:00+02:00\u001ffeat: x\u001fline1\nline2\n\u001e\n" +
                     "def\u001f\u001fBob\u001fb@x\u001f2026-10-26T10:00:00Z\u001f2026-10-26T10:00:00Z\u001finit\u001f\u001e";
        var log = GitClient.ParseLog(output);
        Assert.Equal(2, log.Count);
        Assert.Equal(["p1", "p2"], log[0].Parents);
        Assert.Equal(new DateTime(2026, 10, 27, 8, 0, 0, DateTimeKind.Utc), log[0].CommittedAt);
        Assert.Equal("line1\nline2", log[0].Body);
        Assert.Empty(log[1].Parents);
    }
}
