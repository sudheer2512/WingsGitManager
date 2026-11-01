using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Wings.GitManager.Client;
using Wings.GitManager.Contracts;
using Wings.GitManager.Infrastructure.Data;
using Wings.GitManager.Infrastructure.Git;

namespace Wings.GitManager.IntegrationTests;

/// <summary>
/// Runs the real API (in-process) with SQLite, real git, file-system bare remotes and encrypted file-system
/// backups in an isolated temporary directory. Background workers run as in production (sync queue + jobs).
/// </summary>
public class GitManagerFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public string Root { get; } = WorkspacePaths.Canonicalize(Path.Combine(Path.GetTempPath(), "wgm-it-" + Guid.NewGuid().ToString("N")[..8]));
    public string Workspace => Path.Combine(Root, "ws");
    public string Remotes => Path.Combine(Root, "remotes");
    public string Backups => Path.Combine(Root, "backups");

    public HttpClient Admin { get; private set; } = null!;
    public WingsGitManagerClient Agent { get; private set; } = null!;
    public HttpClient AgentHttp { get; private set; } = null!;
    public HttpClient ReadOnly { get; private set; } = null!;
    public HttpClient Outsider { get; private set; } = null!;
    public Guid AcmeId { get; private set; }
    public Guid GlobexId { get; private set; }

    protected virtual void Configure(Dictionary<string, string?> s) { }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(Root);
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:GitManager"] = $"Data Source={Path.Combine(Root, "db.sqlite")}",
            ["GitManager:WorkspaceRoots:0"] = Workspace,
            ["GitManager:DataDirectory"] = Path.Combine(Root, "data"),
            ["GitManager:DefaultProvider"] = "FileSystem",
            ["GitManager:AllowFileRemotes"] = "true",
            ["GitManager:SynchronousPushTimeoutSeconds"] = "20",
            ["FileSystemRemote:Root"] = Remotes,
            ["Backup:FileSystemRoot"] = Backups,
            ["Backup:EncryptionKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["Worker:RunInApi"] = "true",
            ["Worker:EnableMonitoring"] = "false",
            ["Worker:EnableBackups"] = "false",
            ["Worker:EnableFileWatcher"] = "false",
            ["Sync:PollSeconds"] = "1",
            ["Sync:BaseRetrySeconds"] = "1",
            ["Sync:MaxRetrySeconds"] = "2",
            ["Bootstrap:TokenFile"] = Path.Combine(Root, "admin-token.txt"),
            ["Serilog:MinimumLevel:Default"] = "Warning",
            ["Serilog:WriteTo:0:Args:path"] = Path.Combine(Root, "logs", "test-.log"),
        };
        Configure(settings);
        foreach (var (k, v) in settings) builder.UseSetting(k, v);
        builder.UseEnvironment("Testing");
    }

    public async Task InitializeAsync()
    {
        Admin = CreateClient();
        var token = (await File.ReadAllTextAsync(Path.Combine(Root, "admin-token.txt"))).Trim();
        Admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        AcmeId = (await PostAsync<CompanyDto>(Admin, "/api/v1/companies", new CreateCompanyRequest("Acme Corp", "acme", "acme-ext"))).Id;
        GlobexId = (await PostAsync<CompanyDto>(Admin, "/api/v1/companies", new CreateCompanyRequest("Globex", "globex", null))).Id;

        AgentHttp = await UserClientAsync("wings-code@acme.test", true, AcmeId, WorkspaceRole.Developer, Wings.GitManager.Core.Scopes.AgentService);
        Agent = new WingsGitManagerClient(AgentHttp);
        ReadOnly = await UserClientAsync("viewer@acme.test", false, AcmeId, WorkspaceRole.ReadOnly, [Wings.GitManager.Core.Scopes.ReposRead]);
        Outsider = await UserClientAsync("dev@globex.test", false, GlobexId, WorkspaceRole.CompanyAdministrator, Wings.GitManager.Core.Scopes.AgentService);
    }

    public async Task<HttpClient> UserClientAsync(string email, bool service, Guid company, WorkspaceRole role, string[] scopes)
    {
        var user = await PostAsync<UserDto>(Admin, "/api/v1/users", new CreateUserRequest(email, email, false, service, company, role));
        var token = await PostAsync<CreatedApiTokenDto>(Admin, "/api/v1/tokens", new CreateApiTokenRequest { Name = email, UserId = user.Id, Scopes = scopes });
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Secret);
        return client;
    }

    public static async Task<T> PostAsync<T>(HttpClient c, string url, object body)
    {
        var resp = await c.PostAsJsonAsync(url, body, Json);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"{(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
        return (await resp.Content.ReadFromJsonAsync<T>(Json))!;
    }

    public static async Task<T> GetAsync<T>(HttpClient c, string url)
    {
        var resp = await c.GetAsync(url);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"{(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
        return (await resp.Content.ReadFromJsonAsync<T>(Json))!;
    }

    public static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);

    public T WithDb<T>(Func<GitManagerDbContext, T> f)
    {
        using var scope = Services.CreateScope();
        return f(scope.ServiceProvider.GetRequiredService<GitManagerDbContext>());
    }

    public async Task WithScopeAsync(Func<IServiceProvider, Task> f)
    {
        using var scope = Services.CreateScope();
        await f(scope.ServiceProvider);
    }

    /// <summary>Creates a repository for a fresh project with optional initial files.</summary>
    public async Task<RepositoryProvisionResult> NewRepoAsync(string name, Dictionary<string, string>? files = null, Guid? company = null, HostingProviderKind? provider = null)
    {
        var companySlug = company is null || company == AcmeId ? "acme" : "globex";
        var slug = Wings.GitManager.Core.Safety.RepoPath.Slugify(name);
        var dir = Path.Combine(Workspace, companySlug, slug, "main");
        if (files is not null)
            foreach (var (path, content) in files) Write(dir, path, content);
        return await Agent.Repositories.CreateAsync(new CreateRepositoryRequest
        {
            CompanyId = company ?? AcmeId, ProjectExternalId = "proj-" + Guid.NewGuid().ToString("N")[..8], ProjectName = name, Provider = provider
        });
    }

    public static void Write(string dir, string path, string content)
    {
        var full = Path.Combine(dir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <summary>Plain git (test-side inspection only).</summary>
    public static string Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["GIT_AUTHOR_NAME"] = psi.Environment["GIT_COMMITTER_NAME"] = "Test Human";
        psi.Environment["GIT_AUTHOR_EMAIL"] = psi.Environment["GIT_COMMITTER_EMAIL"] = "human@acme.test";
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("protocol.file.allow=always");
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("init.defaultBranch=main");
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
        return stdout.Trim();
    }

    public string BareRemote(string slug, string company = "acme") => Path.Combine(Remotes, company, slug + ".git");

    public static async Task Eventually(Func<Task<bool>> condition, TimeSpan? timeout = null, string? because = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(because ?? "Condition was not met in time.");
            await Task.Delay(300);
        }
    }

    public async Task<JobDto> WaitForJobAsync(Guid jobId) => await Agent.WaitForJobAsync(jobId, TimeSpan.FromSeconds(90));

    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try
        {
            foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Root, true);
        }
        catch { /* best effort */ }
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<GitManagerFactory>
{
    public const string Name = "api";
}
