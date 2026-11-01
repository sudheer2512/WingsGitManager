using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Wings.GitManager.Core;
using Wings.GitManager.Infrastructure.Data;
using Wings.GitManager.Infrastructure.Services;

namespace Wings.GitManager.Api.Infrastructure;

/// <summary>Real-time notifications. Clients join their companies' groups; system administrators see everything.</summary>
public sealed class NotificationsHub(AccessControl access) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var companies = await access.AccessibleCompanyIdsAsync(Context.ConnectionAborted);
        if (companies is null) await Groups.AddToGroupAsync(Context.ConnectionId, "sysadmin");
        else foreach (var c in companies) await Groups.AddToGroupAsync(Context.ConnectionId, $"company:{c:N}");
        await base.OnConnectedAsync();
    }
}

/// <summary>Relays the database outbox (written by the API and the worker) to SignalR clients.</summary>
public sealed class NotificationRelay(IServiceScopeFactory scopes, IHubContext<NotificationsHub> hub, ILogger<NotificationRelay> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        long last;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GitManagerDbContext>();
            try { last = await db.NotificationEvents.MaxAsync(n => (long?)n.Id, ct) ?? 0; }
            catch (Exception) { last = 0; }
        }
        var companyCache = new Dictionary<Guid, Guid>();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<GitManagerDbContext>();
                var events = await db.NotificationEvents.AsNoTracking().Where(n => n.Id > last).OrderBy(n => n.Id).Take(200).ToListAsync(ct);
                foreach (var e in events)
                {
                    last = e.Id;
                    var company = e.CompanyId;
                    if (company is null && e.RepositoryId is { } rid)
                    {
                        if (!companyCache.TryGetValue(rid, out var cid))
                        {
                            cid = await db.Repositories.Where(r => r.Id == rid).Select(r => r.CompanyId).FirstOrDefaultAsync(ct);
                            companyCache[rid] = cid;
                        }
                        company = cid;
                    }
                    var message = new { e.Id, e.Type, e.RepositoryId, companyId = company, payload = System.Text.Json.JsonDocument.Parse(e.PayloadJson).RootElement, e.CreatedAt };
                    await hub.Clients.Group("sysadmin").SendAsync("notification", message, ct);
                    if (company is { } c && c != Guid.Empty) await hub.Clients.Group($"company:{c:N}").SendAsync("notification", message, ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Notification relay iteration failed"); }
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
        }
    }
}
