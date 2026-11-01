using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Wings.GitManager.Client;
using Wings.GitManager.Contracts;
using static Wings.GitManager.IntegrationTests.GitManagerFactory;

namespace Wings.GitManager.IntegrationTests;

/// <summary>Reads the development Gitea created by scripts/gitea-dev.sh (App_Data/dev-credentials.env).</summary>
public static class GiteaEnvironment
{
    private static readonly Lazy<Dictionary<string, string>?> Values = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Wings.GitManager.slnx"))) dir = dir.Parent;
        var file = Environment.GetEnvironmentVariable("WGM_GITEA_CREDENTIALS") ?? (dir is null ? null : Path.Combine(dir.FullName, "App_Data", "dev-credentials.env"));
        if (file is null || !File.Exists(file)) return null;
        var values = File.ReadAllLines(file).Where(l => l.Contains('=')).ToDictionary(l => l[..l.IndexOf('=')], l => l[(l.IndexOf('=') + 1)..]);
        if (!values.ContainsKey("GITEA_URL") || !values.ContainsKey("GITEA_ADMIN_TOKEN")) return null;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            return http.GetAsync(values["GITEA_URL"] + "/api/healthz").Result.IsSuccessStatusCode ? values : null;
        }
        catch { return null; }
    });

    public static bool Available => Values.Value is not null;
    public static string Url => Values.Value!["GITEA_URL"];
    public static string Token => Values.Value!["GITEA_ADMIN_TOKEN"];
    public static string User => Values.Value!["GITEA_ADMIN_USER"];
}

public sealed class GiteaFactAttribute : FactAttribute
{
    public GiteaFactAttribute()
    {
        if (!GiteaEnvironment.Available) Skip = "Gitea is not running (start it with scripts/gitea-dev.sh init && scripts/gitea-dev.sh start).";
    }
}

public sealed class GiteaFactory : GitManagerFactory
{
    public string Prefix { get; } = "wgm-it-" + Guid.NewGuid().ToString("N")[..6] + "-";

    protected override void Configure(Dictionary<string, string?> s)
    {
        if (!GiteaEnvironment.Available) return;
        s["GitManager:DefaultProvider"] = "Gitea";
        s["GitManager:AllowHttpRemotes"] = "true";
        s["Gitea:BaseUrl"] = GiteaEnvironment.Url;
        s["Gitea:Token"] = GiteaEnvironment.Token;
        s["Gitea:ServiceUser"] = GiteaEnvironment.User;
        s["Gitea:OrganizationPrefix"] = Prefix;
    }

    /// <summary>GET with retries: Gitea 28.2 intermittently panics (HTTP 500) on its first branch read after a push.</summary>
    public static async Task<JsonElement> GetJsonAsync(HttpClient api, string url)
    {
        for (var i = 0; ; i++)
        {
            var resp = await api.GetAsync(url);
            if (resp.IsSuccessStatusCode || i >= 4 || (int)resp.StatusCode < 500)
                return await resp.EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>();
            await Task.Delay(500);
        }
    }

    public HttpClient GiteaApi()
    {
        var c = new HttpClient { BaseAddress = new Uri(GiteaEnvironment.Url.TrimEnd('/') + "/api/v1/") };
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", GiteaEnvironment.Token);
        return c;
    }

    public override async ValueTask DisposeAsync()
    {
        if (GiteaEnvironment.Available)
        {
            // Remove the organizations (and repositories) this run created in the development Gitea.
            using var api = GiteaApi();
            foreach (var org in new[] { Prefix + "acme", Prefix + "globex" })
            {
                var list = await api.GetAsync($"orgs/{org}/repos?limit=50");
                if (!list.IsSuccessStatusCode) continue;
                foreach (var r in await list.Content.ReadFromJsonAsync<List<JsonElement>>() ?? [])
                    await api.DeleteAsync($"repos/{org}/{r.GetProperty("name").GetString()}");
                await api.DeleteAsync($"orgs/{org}");
            }
        }
        await base.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class GiteaCollection : ICollectionFixture<GiteaFactory>
{
    public const string Name = "gitea";
}

[Collection(GiteaCollection.Name)]
public class GiteaTests(GiteaFactory f)
{
    [GiteaFact] // Automatic provisioning on Gitea: organization per company, private repo, protected branch, verified commit
    public async Task Provisions_private_repository_in_company_organization_and_verifies_commits()
    {
        var repo = await f.NewRepoAsync("Gitea Portal", new() { ["src/app.ts"] = "export {}\n" });
        Assert.Equal(DurabilityState.RemoteSynchronized, repo.Durability);
        var org = f.Prefix + "acme";
        Assert.Equal($"{GiteaEnvironment.Url}/{org}/gitea-portal.git", repo.RemoteUrl);
        Assert.DoesNotContain(GiteaEnvironment.Token, Git(repo.WorkingDirectory, "config", "--list")); // credentials never stored in git config

        using var api = f.GiteaApi();
        var giteaRepo = await GiteaFactory.GetJsonAsync(api, $"repos/{org}/gitea-portal");
        Assert.True(giteaRepo.GetProperty("private").GetBoolean());
        var orgInfo = await GiteaFactory.GetJsonAsync(api, $"orgs/{org}");
        Assert.Equal("private", orgInfo.GetProperty("visibility").GetString());
        var branch = await GiteaFactory.GetJsonAsync(api, $"repos/{org}/gitea-portal/branches/main");
        Assert.Equal(repo.CommitSha, branch.GetProperty("commit").GetProperty("id").GetString());
        Assert.True(branch.GetProperty("protected").GetBoolean());
        var protections = await api.GetFromJsonAsync<List<JsonElement>>($"repos/{org}/gitea-portal/branch_protections");
        var mainRule = protections!.Single(p => p.GetProperty("rule_name").GetString() == "main");
        Assert.False(mainRule.GetProperty("enable_force_push").GetBoolean());

        // Change operation through Wings Git Manager → commit verified on Gitea.
        var begin = await f.Agent.Changes.BeginAsync(repo.RepositoryId, new BeginChangeRequest { AgentId = "agent", Description = "Add customer ledger screen" });
        Write(begin.WorkingDirectory, "src/ledger.ts", "export const ledger = 1;\n");
        var done = await f.Agent.Changes.CompleteAsync(repo.RepositoryId, begin.OperationId, new CompleteChangeRequest());
        Assert.Equal(DurabilityState.RemoteSynchronized, done.Durability);
        var commit = await api.GetAsync($"repos/{org}/gitea-portal/git/commits/{done.CommitSha}");
        Assert.Equal(HttpStatusCode.OK, commit.StatusCode);

        // Idempotent: no duplicate Gitea repository.
        var again = await f.Agent.Repositories.CreateAsync(new CreateRepositoryRequest { CompanyId = f.AcmeId, ProjectExternalId = f.WithDb(db => db.Projects.Single(p => p.Name == "Gitea Portal").ExternalId!), ProjectName = "Gitea Portal" });
        Assert.True(again.AlreadyExisted);
        var repos = await api.GetFromJsonAsync<List<JsonElement>>($"orgs/{org}/repos?limit=50");
        Assert.Single(repos!, r => r.GetProperty("name").GetString() == "gitea-portal");
    }

    [GiteaFact] // Sessions push their own branches to Gitea; the protected main branch rejects force pushes
    public async Task Session_branches_sync_to_gitea_and_force_push_is_rejected_by_the_server()
    {
        var repo = await f.NewRepoAsync("Gitea Sessions", new() { ["a.txt"] = "a\n" });
        var s = await f.Agent.Sessions.StartAsync(new StartSessionRequest { RepositoryId = repo.RepositoryId, AgentId = "agent-g" });
        var begin = await f.Agent.Changes.BeginAsync(repo.RepositoryId, new BeginChangeRequest { AgentId = "agent-g", SessionId = s.Id, Description = "Session work" });
        Write(begin.WorkingDirectory, "b.txt", "b\n");
        var done = await f.Agent.Changes.CompleteAsync(repo.RepositoryId, begin.OperationId, new CompleteChangeRequest());
        Assert.Equal(DurabilityState.RemoteSynchronized, done.Durability);
        using var api = f.GiteaApi();
        var branch = await GiteaFactory.GetJsonAsync(api, $"repos/{f.Prefix}acme/gitea-sessions/branches/{Uri.EscapeDataString(s.Branch)}");
        Assert.Equal(done.CommitSha, branch.GetProperty("commit").GetProperty("id").GetString());

        // Defence in depth: even a manual force push (outside Wings) is refused by Gitea's branch protection.
        var clone = Path.Combine(f.Root, "manual-" + Guid.NewGuid().ToString("N")[..6]);
        var authed = repo.RemoteUrl.Replace("://", $"://{GiteaEnvironment.User}:{GiteaEnvironment.Token}@");
        Git(f.Root, "clone", "-q", authed, clone);
        Git(clone, "commit", "-q", "--amend", "-m", "rewritten history");
        var ex = Assert.Throws<InvalidOperationException>(() => Git(clone, "push", "-q", "--force", "origin", "main"));
        Assert.Contains("protected", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [GiteaFact] // Disaster recovery: the Gitea repository is lost and is restored from the independent backup
    public async Task Repository_deleted_from_gitea_is_restored_from_backup()
    {
        var repo = await f.NewRepoAsync("Gitea DR", new() { ["src/a.txt"] = "a\n" });
        var begin = await f.Agent.Changes.BeginAsync(repo.RepositoryId, new BeginChangeRequest { AgentId = "agent", Description = "More" });
        Write(begin.WorkingDirectory, "src/b.txt", "b\n");
        var done = await f.Agent.Changes.CompleteAsync(repo.RepositoryId, begin.OperationId, new CompleteChangeRequest());

        var admin = new WingsGitManagerClient(f.Admin);
        var backupJob = await admin.Backups.CreateAsync(repo.RepositoryId);
        Assert.Equal(JobState.Succeeded, (await f.WaitForJobAsync(backupJob.JobId)).State);
        var backup = Assert.Single(await admin.Backups.ListAsync(repo.RepositoryId));
        Assert.Equal(BackupState.Verified, backup.State);

        using var api = f.GiteaApi();
        var org = f.Prefix + "acme";
        Assert.Equal(HttpStatusCode.NoContent, (await api.DeleteAsync($"repos/{org}/gitea-dr")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.GetAsync($"repos/{org}/gitea-dr")).StatusCode);

        var restore = await admin.Backups.RestoreAsync(repo.RepositoryId, backup.Id, new RestoreBackupRequest { Target = "remote", Approved = true });
        var job = await f.WaitForJobAsync(restore.JobId);
        Assert.True(job.State == JobState.Succeeded, job.Error);
        var branch = await GiteaFactory.GetJsonAsync(api, $"repos/{org}/gitea-dr/branches/main");
        Assert.Equal(done.CommitSha, branch.GetProperty("commit").GetProperty("id").GetString());

        // Work continues against the restored server.
        await f.Agent.Sync.FetchAsync(repo.RepositoryId);
        var next = await f.Agent.Changes.BeginAsync(repo.RepositoryId, new BeginChangeRequest { AgentId = "agent", Description = "After DR" });
        Write(next.WorkingDirectory, "src/c.txt", "c\n");
        Assert.Equal(DurabilityState.RemoteSynchronized, (await f.Agent.Changes.CompleteAsync(repo.RepositoryId, next.OperationId, new CompleteChangeRequest())).Durability);
    }

    [GiteaFact] // Import an existing remote repository (history preserved) into a new Gitea repository
    public async Task Imports_remote_repository_with_history()
    {
        using var api = f.GiteaApi();
        var sourceName = "import-source-" + Guid.NewGuid().ToString("N")[..6];
        var created = await api.PostAsJsonAsync("user/repos", new { name = sourceName, @private = true, auto_init = true, default_branch = "main" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        try
        {
            var source = $"{GiteaEnvironment.Url}/{GiteaEnvironment.User}/{sourceName}.git";
            var result = await f.Agent.Repositories.ImportAsync(new ImportRepositoryRequest
            {
                CompanyId = f.AcmeId, ProjectExternalId = "imp-" + sourceName, ProjectName = "Imported " + sourceName,
                SourceRemote = new RemoteCredentialInput { Url = source, Username = GiteaEnvironment.User, Secret = GiteaEnvironment.Token }
            });
            Assert.Equal(DurabilityState.RemoteSynchronized, result.Durability);
            var srcHead = (await GiteaFactory.GetJsonAsync(api, $"repos/{GiteaEnvironment.User}/{sourceName}/branches/main")).GetProperty("commit").GetProperty("id").GetString();
            Assert.Equal(srcHead, result.CommitSha);
            Assert.StartsWith($"{GiteaEnvironment.Url}/{f.Prefix}acme/", result.RemoteUrl);
            Assert.True(File.Exists(Path.Combine(result.WorkingDirectory, "README.md")));
        }
        finally
        {
            await api.DeleteAsync($"repos/{GiteaEnvironment.User}/{sourceName}");
        }
    }
}
