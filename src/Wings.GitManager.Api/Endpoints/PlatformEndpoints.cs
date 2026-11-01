using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Wings.GitManager.Contracts;
using Wings.GitManager.Core;
using Wings.GitManager.Core.Recovery;
using Wings.GitManager.Infrastructure.Services;

namespace Wings.GitManager.Api.Endpoints;

public static class PlatformEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        // ---- AI integration (Wings Code) ----
        var ai = api.MapGroup("/ai").WithTags("AI integration");
        ai.MapPost("/sessions", async (StartSessionRequest req, SessionService svc, CancellationToken ct) =>
        {
            var s = await svc.StartAsync(req, ct);
            return Results.Created($"/api/v1/ai/sessions/{s.Id}", s);
        }).WithSummary("Start an AI coding session (isolated worktree + session branch by default)");
        ai.MapGet("/sessions", (SessionService svc, Guid? repositoryId, SessionState? state, int page = 1, int pageSize = 50, CancellationToken ct = default) =>
            svc.ListAsync(repositoryId, state, page, pageSize, ct));
        ai.MapGet("/sessions/{sessionId:guid}", (Guid sessionId, SessionService svc, CancellationToken ct) => svc.GetAsync(sessionId, ct));
        ai.MapPost("/sessions/{sessionId:guid}/end", (Guid sessionId, EndSessionRequest? req, SessionService svc, CancellationToken ct) => svc.EndAsync(sessionId, req ?? new EndSessionRequest(), ct));
        ai.MapGet("/sessions/{sessionId:guid}/commits", (Guid sessionId, SessionService svc, bool includeCheckpoints = false, CancellationToken ct = default) => svc.CommitsAsync(sessionId, includeCheckpoints, ct));
        ai.MapGet("/sessions/{sessionId:guid}/changes", (Guid sessionId, SessionService svc, BrowseService browse, CancellationToken ct) => svc.ChangesAsync(sessionId, browse, ct));
        ai.MapPost("/sessions/{sessionId:guid}/recovery-preview", async (Guid sessionId, SessionService sessions, RecoveryService svc, RecoveryRequest? req, CancellationToken ct) =>
        {
            var s = await sessions.GetAsync(sessionId, ct);
            return await svc.PreviewAsync(s.RepositoryId, (req ?? new RecoveryRequest()) with { Type = RecoveryType.RevertSession, SessionId = sessionId }, ct);
        });
        ai.MapPost("/sessions/{sessionId:guid}/revert", async (Guid sessionId, SessionService sessions, RecoveryService svc, RecoveryRequest req, CancellationToken ct) =>
        {
            var s = await sessions.GetAsync(sessionId, ct);
            return await svc.ExecuteAsync(s.RepositoryId, req with { Type = RecoveryType.RevertSession, SessionId = sessionId }, RecoveryType.RevertSession, ct);
        });
        ai.MapPost("/recovery/interpret", (InterpretRequest req) => RecoveryIntentParser.Parse(req.Query, DateTime.UtcNow))
            .WithSummary("Interpret a natural-language recovery request (no repository access)");

        // ---- Dashboard, monitoring, system ----
        var m = api.MapGroup("").WithTags("Monitoring");
        m.MapGet("/dashboard/summary", (MonitoringService svc, Guid? companyId, Guid? projectId, string? user, DateTime? from, DateTime? to, CancellationToken ct) =>
            svc.DashboardAsync(companyId, projectId, user, from, to, ct));
        m.MapGet("/system/health", (MonitoringService svc, CancellationToken ct) => svc.HealthAsync(ct));
        m.MapGet("/system/storage", (MonitoringService svc, CancellationToken ct) => svc.StorageAsync(ct));
        m.MapGet("/alerts", (MonitoringService svc, bool includeResolved = false, Guid? repositoryId = null, CancellationToken ct = default) => svc.AlertsAsync(includeResolved, repositoryId, ct));
        m.MapPost("/alerts/{alertId:guid}/resolve", async (Guid alertId, MonitoringService svc, CancellationToken ct) => { await svc.ResolveAlertAsync(alertId, ct); return Results.NoContent(); });
        m.MapGet("/sync/queue", (SyncOperations svc, PushJobState? state, int take = 200, CancellationToken ct = default) => svc.QueueAsync(null, state, take, ct)).WithTags("Synchronization");
        m.MapGet("/jobs/{jobId:guid}", (Guid jobId, JobService svc, CancellationToken ct) => svc.GetAsync(jobId, ct)).WithTags("Jobs");
        m.MapGet("/jobs", (JobService svc, Guid? repositoryId, JobState? state, int take = 100, CancellationToken ct = default) => svc.ListAsync(repositoryId, state, take, ct)).WithTags("Jobs");
        m.MapGet("/audit-logs", (AdminService svc, Guid? companyId, Guid? repositoryId, string? action, string? actor, string? outcome, DateTime? from, DateTime? to, int page = 1, int pageSize = 100, CancellationToken ct = default) =>
            svc.AuditAsync(companyId, repositoryId, action, actor, outcome, from, to, page, pageSize, ct)).WithTags("Audit");

        // ---- Administration ----
        var a = api.MapGroup("").WithTags("Administration");
        a.MapGet("/me", (AdminService svc, CancellationToken ct) => svc.MeAsync(ct));
        a.MapGet("/companies", (AdminService svc, CancellationToken ct) => svc.CompaniesAsync(ct));
        a.MapPost("/companies", (CreateCompanyRequest req, AdminService svc, CancellationToken ct) => svc.CreateCompanyAsync(req, ct));
        a.MapGet("/projects", (AdminService svc, Guid? companyId, CancellationToken ct) => svc.ProjectsAsync(companyId, ct));
        a.MapGet("/users", (AdminService svc, Guid? companyId, CancellationToken ct) => svc.UsersAsync(companyId, ct));
        a.MapPost("/users", (CreateUserRequest req, AdminService svc, CancellationToken ct) => svc.CreateUserAsync(req, ct));
        a.MapPut("/users/{userId:guid}/memberships", async (Guid userId, SetMembershipRequest req, AdminService svc, CancellationToken ct) => { await svc.SetMembershipAsync(userId, req, ct); return Results.NoContent(); });
        a.MapDelete("/users/{userId:guid}/memberships/{companyId:guid}", async (Guid userId, Guid companyId, AdminService svc, CancellationToken ct) => { await svc.RemoveMembershipAsync(userId, companyId, ct); return Results.NoContent(); });
        a.MapGet("/tokens", (AdminService svc, CancellationToken ct) => svc.TokensAsync(ct));
        a.MapPost("/tokens", (CreateApiTokenRequest req, AdminService svc, CancellationToken ct) => svc.CreateTokenAsync(req, ct));
        a.MapDelete("/tokens/{tokenId:guid}", async (Guid tokenId, AdminService svc, CancellationToken ct) => { await svc.RevokeTokenAsync(tokenId, ct); return Results.NoContent(); });
        a.MapGet("/policies", (AdminService svc, CancellationToken ct) => svc.PoliciesAsync(ct));
        a.MapPut("/policies", (UpsertPolicyRequest req, AdminService svc, CancellationToken ct) => svc.UpsertPolicyAsync(req, ct));

        // ---- Gitea webhook (HMAC-SHA256 signed; anonymous) ----
        api.MapPost("/webhooks/gitea", async (HttpRequest request, IOptions<GiteaOptions> gitea, SyncOperations svc, CancellationToken ct) =>
        {
            var secret = gitea.Value.WebhookSecret;
            if (string.IsNullOrEmpty(secret)) return Results.NotFound();
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync(ct);
            var signature = request.Headers["X-Gitea-Signature"].ToString();
            var expected = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(signature.ToLowerInvariant()), Encoding.ASCII.GetBytes(expected)))
                return Results.Unauthorized();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var fullName = root.TryGetProperty("repository", out var r) && r.TryGetProperty("full_name", out var fn) ? fn.GetString() : null;
            if (fullName is null) return Results.NoContent();
            await svc.HandleWebhookAsync(fullName, root.TryGetProperty("ref", out var rf) ? rf.GetString() : null, root.TryGetProperty("after", out var af) ? af.GetString() : null, ct);
            return Results.NoContent();
        }).AllowAnonymous().WithTags("Webhooks");
    }

    public sealed record InterpretRequest(string Query);
}
