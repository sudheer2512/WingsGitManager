using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wings.GitManager.Contracts;
using Wings.GitManager.Core;
using Wings.GitManager.Core.Entities;

namespace Wings.GitManager.Infrastructure.Hosting;

/// <summary>
/// Gitea REST adapter: organizations per company, private repositories, branch protection (no force push),
/// webhooks, collaborators and independent commit verification. Git transport still uses the git CLI.
/// </summary>
public sealed class GiteaHostingProvider(HttpClient http, IOptions<GiteaOptions> options, ILogger<GiteaHostingProvider> logger) : IGitHostingProvider
{
    public const string HttpClientName = "gitea";
    private readonly GiteaOptions _o = options.Value;

    public HostingProviderKind Kind => HostingProviderKind.Gitea;
    public bool CanProvision => true;

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(_o.BaseUrl) || string.IsNullOrWhiteSpace(_o.Token))
            throw new GitManagerException("gitea_not_configured", "Gitea is not configured (Gitea:BaseUrl and Gitea:Token).", 503);
    }

    public async Task<string> EnsureNamespaceAsync(Company company, CancellationToken ct)
    {
        EnsureConfigured();
        var org = company.GitNamespace ?? OrgName(company.Slug);
        using var get = await http.GetAsync($"orgs/{Uri.EscapeDataString(org)}", ct);
        if (get.IsSuccessStatusCode) return org;
        if (get.StatusCode != HttpStatusCode.NotFound) await ThrowAsync(get, "read organization", ct);
        if (!_o.AutoCreateOrganizations)
            throw new GitManagerException("namespace_missing", $"Gitea organization '{org}' does not exist and automatic creation is disabled.", 409);

        using var create = await http.PostAsJsonAsync("orgs", new
        {
            username = org,
            full_name = company.Name,
            visibility = "private",
            repo_admin_change_team_access = false
        }, ct);
        if (create.StatusCode == HttpStatusCode.UnprocessableEntity || create.StatusCode == HttpStatusCode.Conflict)
            return org; // created concurrently
        if (!create.IsSuccessStatusCode) await ThrowAsync(create, "create organization", ct);
        logger.LogInformation("Created Gitea organization {Org} for company {Company}", org, company.Id);
        return org;
    }

    public string OrgName(string slug)
    {
        var name = (_o.OrganizationPrefix + slug).Trim('-', '.', '_');
        return name.Length > 40 ? name[..40].Trim('-', '.', '_') : name;
    }

    public async Task<RemoteRepositoryInfo> CreateRepositoryAsync(string ns, string name, string? description, string defaultBranch, CancellationToken ct)
    {
        EnsureConfigured();
        using var resp = await http.PostAsJsonAsync($"orgs/{Uri.EscapeDataString(ns)}/repos", new
        {
            name,
            description = description ?? "",
            @private = true,
            auto_init = false,
            default_branch = defaultBranch,
        }, ct);
        if (resp.StatusCode == HttpStatusCode.Conflict)
            return await GetRepositoryAsync(ns, name, ct) ?? throw new GitManagerException("remote_conflict", "Remote repository exists but cannot be read.", 409);
        if (!resp.IsSuccessStatusCode) await ThrowAsync(resp, "create repository", ct);
        var repo = (await resp.Content.ReadFromJsonAsync<GiteaRepo>(ct))!;
        logger.LogInformation("Created Gitea repository {Owner}/{Name}", ns, name);
        return Map(repo);
    }

    public async Task<RemoteRepositoryInfo?> GetRepositoryAsync(string ns, string name, CancellationToken ct)
    {
        EnsureConfigured();
        using var resp = await http.GetAsync(RepoPath(ns, name), ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        if (!resp.IsSuccessStatusCode) await ThrowAsync(resp, "read repository", ct);
        return Map((await resp.Content.ReadFromJsonAsync<GiteaRepo>(ct))!);
    }

    public async Task ArchiveRepositoryAsync(string ns, string name, bool archived, CancellationToken ct)
    {
        EnsureConfigured();
        using var resp = await http.PatchAsJsonAsync(RepoPath(ns, name), new { archived }, ct);
        if (!resp.IsSuccessStatusCode) await ThrowAsync(resp, "archive repository", ct);
    }

    public async Task DeleteRepositoryAsync(string ns, string name, CancellationToken ct)
    {
        EnsureConfigured();
        using var resp = await http.DeleteAsync(RepoPath(ns, name), ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return;
        if (!resp.IsSuccessStatusCode) await ThrowAsync(resp, "delete repository", ct);
    }

    public async Task<IReadOnlyList<RemoteBranchInfo>> ListBranchesAsync(string ns, string name, CancellationToken ct)
    {
        EnsureConfigured();
        var all = new List<RemoteBranchInfo>();
        for (var page = 1; page < 100; page++)
        {
            using var resp = await http.GetAsync($"{RepoPath(ns, name)}/branches?limit=50&page={page}", ct);
            if (!resp.IsSuccessStatusCode) await ThrowAsync(resp, "list branches", ct);
            var items = await resp.Content.ReadFromJsonAsync<List<GiteaBranch>>(ct) ?? [];
            all.AddRange(items.Select(b => new RemoteBranchInfo(b.Name, b.Commit?.Id ?? "", b.Protected)));
            if (items.Count < 50) break;
        }
        return all;
    }

    public async Task<string?> GetBranchHeadAsync(string ns, string name, string branch, CancellationToken ct)
    {
        EnsureConfigured();
        using var resp = await http.GetAsync($"{RepoPath(ns, name)}/branches/{Uri.EscapeDataString(branch)}", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        if (!resp.IsSuccessStatusCode) await ThrowAsync(resp, "read branch", ct);
        return (await resp.Content.ReadFromJsonAsync<GiteaBranch>(ct))?.Commit?.Id;
    }

    public async Task<bool?> CommitExistsAsync(string ns, string name, string sha, CancellationToken ct)
    {
        EnsureConfigured();
        using var resp = await http.GetAsync($"{RepoPath(ns, name)}/git/commits/{sha}?stat=false&verification=false&files=false", ct);
        if (resp.IsSuccessStatusCode) return true;
        if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity) return false;
        logger.LogWarning("Gitea commit verification returned {Status}", resp.StatusCode);
        return null;
    }

    public async Task ProtectBranchAsync(string ns, string name, string branchPattern, CancellationToken ct)
    {
        EnsureConfigured();
        using (var list = await http.GetAsync($"{RepoPath(ns, name)}/branch_protections", ct))
        {
            if (list.IsSuccessStatusCode)
            {
                var existing = await list.Content.ReadFromJsonAsync<List<GiteaProtection>>(ct) ?? [];
                if (existing.Any(p => p.RuleName == branchPattern || p.BranchName == branchPattern)) return;
            }
        }
        using var resp = await http.PostAsJsonAsync($"{RepoPath(ns, name)}/branch_protections", new
        {
            rule_name = branchPattern,
            enable_push = true,
            enable_force_push = false,
            block_on_outdated_branch = false,
        }, ct);
        if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.Forbidden)
            await ThrowAsync(resp, "protect branch", ct);
    }

    public async Task EnsureWebhookAsync(string ns, string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_o.WebhookUrl)) return;
        EnsureConfigured();
        using (var list = await http.GetAsync($"{RepoPath(ns, name)}/hooks", ct))
        {
            if (list.IsSuccessStatusCode)
            {
                var hooks = await list.Content.ReadFromJsonAsync<List<GiteaHook>>(ct) ?? [];
                if (hooks.Any(h => h.Config?.GetValueOrDefault("url") == _o.WebhookUrl)) return;
            }
        }
        using var resp = await http.PostAsJsonAsync($"{RepoPath(ns, name)}/hooks", new
        {
            type = "gitea",
            active = true,
            events = new[] { "push", "create", "delete" },
            config = new Dictionary<string, string>
            {
                ["url"] = _o.WebhookUrl!, ["content_type"] = "json", ["secret"] = _o.WebhookSecret ?? ""
            }
        }, ct);
        if (!resp.IsSuccessStatusCode) await ThrowAsync(resp, "create webhook", ct);
    }

    public async Task SetCollaboratorAsync(string ns, string name, string username, WorkspaceRole role, CancellationToken ct)
    {
        EnsureConfigured();
        var permission = role switch { WorkspaceRole.ReadOnly => "read", WorkspaceRole.Developer => "write", _ => "admin" };
        using var resp = await http.PutAsJsonAsync($"{RepoPath(ns, name)}/collaborators/{Uri.EscapeDataString(username)}", new { permission }, ct);
        if (!resp.IsSuccessStatusCode) await ThrowAsync(resp, "set collaborator", ct);
    }

    public GitCredential? GetGitCredential(RepositoryRemote remote) =>
        string.IsNullOrEmpty(_o.Token) ? null : new GitCredential(_o.ServiceUser, _o.Token);

    public async Task<HealthComponentDto> CheckHealthAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_o.BaseUrl)) return new("gitea", "NotConfigured", "Gitea:BaseUrl is empty", null);
        var sw = Stopwatch.StartNew();
        try
        {
            using var health = await http.GetAsync(new Uri(new Uri(_o.BaseUrl.TrimEnd('/') + "/"), "api/healthz"), ct);
            using var version = await http.GetAsync("version", ct);
            var v = version.IsSuccessStatusCode ? (await version.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("version").GetString() : null;
            var status = health.IsSuccessStatusCode && version.IsSuccessStatusCode ? "Healthy" : version.StatusCode == HttpStatusCode.Unauthorized ? "Unauthorized" : "Unhealthy";
            return new("gitea", status, $"{_o.BaseUrl} version {v ?? "unknown"} (healthz {(int)health.StatusCode})", sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            return new("gitea", "Unreachable", ex.Message, sw.Elapsed.TotalMilliseconds);
        }
    }

    private static string RepoPath(string ns, string name) => $"repos/{Uri.EscapeDataString(ns)}/{Uri.EscapeDataString(name)}";

    private static RemoteRepositoryInfo Map(GiteaRepo r) =>
        new(r.Owner?.Login ?? "", r.Name, r.CloneUrl, r.Id.ToString(), r.Empty, r.DefaultBranch, r.Archived);

    private static async Task ThrowAsync(HttpResponseMessage resp, string action, CancellationToken ct)
    {
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (body.Length > 500) body = body[..500];
        var status = resp.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => 502,
            HttpStatusCode.NotFound => 404,
            _ => 502
        };
        throw new GitManagerException(ErrorCodes.RemoteUnavailable, $"Gitea failed to {action}: {(int)resp.StatusCode} {body}", status);
    }

    private sealed record GiteaOwner([property: JsonPropertyName("login")] string Login);
    private sealed record GiteaRepo(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("owner")] GiteaOwner? Owner,
        [property: JsonPropertyName("clone_url")] string CloneUrl,
        [property: JsonPropertyName("empty")] bool Empty,
        [property: JsonPropertyName("archived")] bool Archived,
        [property: JsonPropertyName("default_branch")] string? DefaultBranch);
    private sealed record GiteaCommitRef([property: JsonPropertyName("id")] string Id);
    private sealed record GiteaBranch(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("commit")] GiteaCommitRef? Commit,
        [property: JsonPropertyName("protected")] bool Protected);
    private sealed record GiteaProtection(
        [property: JsonPropertyName("rule_name")] string? RuleName,
        [property: JsonPropertyName("branch_name")] string? BranchName);
    private sealed record GiteaHook([property: JsonPropertyName("config")] Dictionary<string, string>? Config);
}
