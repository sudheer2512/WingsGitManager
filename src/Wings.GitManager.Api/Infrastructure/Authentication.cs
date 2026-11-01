using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wings.GitManager.Core;
using Wings.GitManager.Infrastructure;
using Wings.GitManager.Infrastructure.Data;
using Wings.GitManager.Infrastructure.Services;

namespace Wings.GitManager.Api.Infrastructure;

/// <summary>
/// Scoped API tokens ("wgm_…") for service-to-service calls (Wings Code AI agent service, automation) and the UI.
/// Only SHA-256 hashes are stored. Accepted as "Authorization: Bearer" or, for SignalR, the access_token query.
/// </summary>
public sealed class ApiTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, GitManagerDbContext db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiToken";

    public static string? ReadToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return header[7..].Trim();
        if (request.Path.StartsWithSegments("/hubs") && request.Query.TryGetValue("access_token", out var q)) return q.ToString();
        return null;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = ReadToken(Request);
        if (token is null || !token.StartsWith(AdminService.TokenPrefix, StringComparison.Ordinal)) return AuthenticateResult.NoResult();
        var hash = AdminService.Hash(token);
        var row = await db.ApiTokens.Include(t => t.User).FirstOrDefaultAsync(t => t.TokenHash == hash);
        var now = DateTime.UtcNow;
        if (row is null || row.RevokedAt is not null || row.ExpiresAt < now || row.User is not { IsActive: true })
            return AuthenticateResult.Fail("Invalid or expired API token.");
        if (row.LastUsedAt is null || now - row.LastUsedAt > TimeSpan.FromMinutes(5))
        {
            await db.ApiTokens.Where(t => t.Id == row.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedAt, now));
        }
        var claims = new List<Claim>
        {
            new(ClaimNames.UserId, row.UserId.ToString()),
            new(ClaimTypes.Name, row.User.DisplayName),
            new(ClaimTypes.Email, row.User.Email),
            new("token_id", row.Id.ToString()),
        };
        if (row.User.IsSystemAdministrator && row.Scopes.Contains(Scopes.Admin)) claims.Add(new(ClaimNames.SystemAdmin, "true"));
        if (row.User.IsServiceAccount) claims.Add(new(ClaimNames.ServiceAccount, "true"));
        claims.AddRange(row.Scopes.Select(s => new Claim(ClaimNames.Scope, s)));
        var identity = new ClaimsIdentity(claims, SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}

public static class AuthenticationSetup
{
    public const string Combined = "Wings";

    /// <summary>API tokens always; OIDC (e.g. the Wings Authentication server) when Authentication:Oidc:Authority is set.</summary>
    public static IServiceCollection AddGitManagerAuthentication(this IServiceCollection services, IConfiguration config)
    {
        var authority = config["Authentication:Oidc:Authority"];
        var builder = services.AddAuthentication(o =>
            {
                o.DefaultScheme = Combined;
                o.DefaultChallengeScheme = Combined;
            })
            .AddPolicyScheme(Combined, "API token or OIDC bearer", o =>
            {
                o.ForwardDefaultSelector = ctx =>
                {
                    var token = ApiTokenAuthenticationHandler.ReadToken(ctx.Request);
                    return token is null || token.StartsWith(AdminService.TokenPrefix) || string.IsNullOrEmpty(authority)
                        ? ApiTokenAuthenticationHandler.SchemeName
                        : JwtBearerDefaults.AuthenticationScheme;
                };
            })
            .AddScheme<AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(ApiTokenAuthenticationHandler.SchemeName, null);

        if (!string.IsNullOrEmpty(authority))
        {
            builder.AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, o =>
            {
                o.Authority = authority;
                o.Audience = config["Authentication:Oidc:Audience"];
                o.RequireHttpsMetadata = config.GetValue("Authentication:Oidc:RequireHttpsMetadata", true);
                o.MapInboundClaims = false;
                o.Events = new JwtBearerEvents
                {
                    OnMessageReceived = ctx =>
                    {
                        if (ctx.Request.Path.StartsWithSegments("/hubs") && ctx.Request.Query.TryGetValue("access_token", out var t)) ctx.Token = t;
                        return Task.CompletedTask;
                    },
                    // Map the external identity to a provisioned Wings user; unknown users are rejected.
                    OnTokenValidated = async ctx =>
                    {
                        var db = ctx.HttpContext.RequestServices.GetRequiredService<GitManagerDbContext>();
                        var sub = ctx.Principal!.FindFirstValue("sub");
                        var email = ctx.Principal.FindFirstValue("email")?.ToLowerInvariant();
                        var subject = $"{ctx.Principal.FindFirstValue("iss")}|{sub}";
                        var user = await db.Users.FirstOrDefaultAsync(u => u.ExternalSubject == subject)
                                   ?? (email is null ? null : await db.Users.FirstOrDefaultAsync(u => u.Email == email && u.ExternalSubject == null));
                        if (user is null || !user.IsActive) { ctx.Fail("User is not provisioned in Wings Git Manager."); return; }
                        if (user.ExternalSubject is null) { user.ExternalSubject = subject; await db.SaveChangesAsync(); }
                        var id = new ClaimsIdentity(
                        [
                            new Claim(ClaimNames.UserId, user.Id.ToString()), new Claim(ClaimTypes.Email, user.Email), new Claim(ClaimTypes.Name, user.DisplayName)
                        ]);
                        if (user.IsSystemAdministrator) id.AddClaim(new Claim(ClaimNames.SystemAdmin, "true"));
                        if (user.IsServiceAccount) id.AddClaim(new Claim(ClaimNames.ServiceAccount, "true"));
                        foreach (var s in Scopes.All) id.AddClaim(new Claim(ClaimNames.Scope, s));
                        ctx.Principal.AddIdentity(id);
                    }
                };
            });
        }
        services.AddAuthorization(o => o.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        return services;
    }
}
