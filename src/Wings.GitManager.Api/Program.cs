using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using Scalar.AspNetCore;
using Serilog;
using Wings.GitManager.Api.Endpoints;
using Wings.GitManager.Api.Infrastructure;
using Wings.GitManager.Core;
using Wings.GitManager.Infrastructure;
using Wings.GitManager.Infrastructure.Data;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(o => o.ServiceName = "WingsGitManager");
builder.Host.UseSerilog((ctx, services, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "Wings.GitManager.Api")
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {CorrelationId}{NewLine}{Exception}"), preserveStaticLogger: true);

builder.Services.AddGitManagerInfrastructure(builder.Configuration);
if (builder.Configuration.GetValue("Worker:RunInApi", true))
    builder.Services.AddGitManagerWorkers(builder.Configuration);
builder.Services.AddGitManagerAuthentication(builder.Configuration);
builder.Services.AddSignalR();
builder.Services.AddHostedService<NotificationRelay>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNameCaseInsensitive = true);
builder.Services.AddOpenApi("v1", o =>
{
    o.AddDocumentTransformer((doc, _, _) =>
    {
        doc.Info = new OpenApiInfo
        {
            Title = "Wings Git Manager API",
            Version = "v1",
            Description = "Git repository management, automated version control, AI session tracking, backup and recovery for Wings Code. " +
                          "Authenticate with 'Authorization: Bearer wgm_…' (scoped API token) or an OIDC access token. Send Idempotency-Key on retried mutations."
        };
        doc.Components ??= new OpenApiComponents();
        doc.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        doc.Components.SecuritySchemes["bearer"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", Description = "API token (wgm_…) or OIDC access token" };
        doc.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("bearer", doc)] = [] }];
        return Task.CompletedTask;
    });
});
builder.Services.AddCors(o => o.AddPolicy("dev-ui", p => p.WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [])
    .AllowAnyHeader().AllowAnyMethod().AllowCredentials().WithExposedHeaders(CorrelationIdMiddleware.Header)));
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    // Only explicitly configured proxies are trusted (defaults to loopback when none are configured).
    foreach (var ip in builder.Configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [])
        o.KnownProxies.Add(IPAddress.Parse(ip));
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
    await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync();

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment() && app.Configuration.GetValue("Https:Redirect", false))
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseSerilogRequestLogging(o => o.GetLevel = (ctx, _, ex) => ex is not null || ctx.Response.StatusCode >= 500
    ? Serilog.Events.LogEventLevel.Error
    : ctx.Request.Path.StartsWithSegments("/health") ? Serilog.Events.LogEventLevel.Verbose : Serilog.Events.LogEventLevel.Information);
app.UseMiddleware<ErrorHandlingMiddleware>();
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.XContentTypeOptions = "nosniff";
    ctx.Response.Headers.XFrameOptions = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors("dev-ui");
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<IdempotencyMiddleware>();

app.MapOpenApi("/openapi/{documentName}.json").AllowAnonymous();
app.MapScalarApiReference("/api-docs", o => o.WithTitle("Wings Git Manager API").WithOpenApiRoutePattern("/openapi/{documentName}.json")).AllowAnonymous();

app.MapGet("/health/live", () => Results.Ok(new { status = "Healthy" })).AllowAnonymous().ExcludeFromDescription();
app.MapGet("/health/ready", async (GitManagerDbContext db, CancellationToken ct) =>
{
    try { await db.Database.ExecuteSqlRawAsync("SELECT 1", ct); return Results.Ok(new { status = "Ready" }); }
    catch (Exception ex) { return Results.Json(new { status = "NotReady", error = ex.Message }, statusCode: 503); }
}).AllowAnonymous().ExcludeFromDescription();

var api = app.MapGroup("/api/v1");
RepositoryEndpoints.Map(api);
PlatformEndpoints.Map(api);
app.MapHub<NotificationsHub>("/hubs/notifications");
app.MapFallback("/api/{**path}", (HttpContext ctx) => ErrorHandlingMiddleware.WriteAsync(ctx, 404, "not_found", "Unknown API route.", null)).AllowAnonymous();
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

public partial class Program;
