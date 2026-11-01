using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Serilog.Context;
using Wings.GitManager.Core;
using Wings.GitManager.Core.Entities;
using Wings.GitManager.Infrastructure;
using Wings.GitManager.Infrastructure.Data;

namespace Wings.GitManager.Api.Infrastructure;

/// <summary>Accepts or creates X-Correlation-Id, echoes it and adds it to every log event.</summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string Header = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var id = context.Request.Headers[Header].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(id) || id.Length > 100) id = Guid.NewGuid().ToString("N");
        context.Items["CorrelationId"] = id;
        context.TraceIdentifier = id;
        context.Response.Headers[Header] = id;
        using (LogContext.PushProperty("CorrelationId", id))
            await next(context);
    }
}

/// <summary>Maps expected failures to RFC 7807 problem details with a stable error code and the correlation id.</summary>
public sealed class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (GitManagerException ex) when (!context.Response.HasStarted)
        {
            if (ex.StatusCode >= 500) logger.LogError(ex, "Request failed: {Code}", ex.Code);
            else logger.LogInformation("Request rejected: {Code} {Message}", ex.Code, ex.Message);
            await WriteAsync(context, ex.StatusCode, ex.Code, ex.Message, ex.Details);
        }
        catch (DbUpdateConcurrencyException) when (!context.Response.HasStarted)
        {
            await WriteAsync(context, 409, "concurrency_conflict", "The resource was modified concurrently; reload and retry.", null);
        }
        catch (BadHttpRequestException ex) when (!context.Response.HasStarted)
        {
            await WriteAsync(context, 400, ErrorCodes.Validation, ex.Message, null);
        }
        catch (System.Text.Json.JsonException ex) when (!context.Response.HasStarted)
        {
            await WriteAsync(context, 400, ErrorCodes.Validation, "Invalid JSON: " + ex.Message, null);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            context.Response.StatusCode = 499;
        }
        catch (Exception ex) when (!context.Response.HasStarted)
        {
            logger.LogError(ex, "Unhandled error");
            await WriteAsync(context, 500, "internal_error", "An unexpected error occurred. Quote the correlation id when reporting it.", null);
        }
    }

    public static Task WriteAsync(HttpContext context, int status, string code, string message, object? details)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        var correlation = context.Items["CorrelationId"] as string;
        return context.Response.WriteAsJsonAsync(new
        {
            type = $"https://wings.dev/errors/{code}",
            title = code.Replace('_', ' '),
            status,
            detail = message,
            code,
            correlationId = correlation,
            details
        });
    }
}

/// <summary>
/// Idempotency-Key support for mutating requests: the first response (status &lt; 500) is stored per caller and
/// replayed for retries with the same key and body; reusing a key with a different body is rejected.
/// </summary>
public sealed class IdempotencyMiddleware(RequestDelegate next)
{
    public const string Header = "Idempotency-Key";

    public async Task InvokeAsync(HttpContext context, GitManagerDbContext db)
    {
        var key = context.Request.Headers[Header].FirstOrDefault();
        if (string.IsNullOrEmpty(key) || HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method) || context.User.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }
        if (key.Length > 200) { await ErrorHandlingMiddleware.WriteAsync(context, 400, ErrorCodes.Validation, "Idempotency-Key is too long.", null); return; }

        context.Request.EnableBuffering();
        string body;
        using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true)) body = await reader.ReadToEndAsync();
        context.Request.Body.Position = 0;
        var principal = context.User.FindFirst(ClaimNames.UserId)?.Value ?? "anonymous";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{context.Request.Method} {context.Request.Path}{context.Request.QueryString}\n{body}")));

        var existing = await db.IdempotencyRecords.AsNoTracking().FirstOrDefaultAsync(r => r.Principal == principal && r.Key == key);
        if (existing is not null)
        {
            if (existing.RequestHash != hash) { await ErrorHandlingMiddleware.WriteAsync(context, 422, ErrorCodes.IdempotencyMismatch, "This Idempotency-Key was used with a different request.", null); return; }
            if (!existing.Completed) { await ErrorHandlingMiddleware.WriteAsync(context, 409, "request_in_progress", "A request with this Idempotency-Key is still being processed.", null); return; }
            context.Response.StatusCode = existing.StatusCode;
            context.Response.ContentType = "application/json";
            context.Response.Headers["Idempotent-Replayed"] = "true";
            if (existing.ResponseBody is not null) await context.Response.WriteAsync(existing.ResponseBody);
            return;
        }

        var record = new IdempotencyRecord { Key = key, Principal = principal, Method = context.Request.Method, Path = context.Request.Path, RequestHash = hash };
        db.IdempotencyRecords.Add(record);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { await ErrorHandlingMiddleware.WriteAsync(context, 409, "request_in_progress", "A request with this Idempotency-Key is being processed.", null); return; }
        db.ChangeTracker.Clear();

        var original = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next(context);
        }
        catch
        {
            // Failures are not replayed: a retry with the same key re-executes (e.g. once the Git server is back).
            context.Response.Body = original;
            await db.IdempotencyRecords.Where(r => r.Id == record.Id).ExecuteDeleteAsync();
            throw;
        }
        context.Response.Body = original;
        buffer.Position = 0;
        var responseText = await new StreamReader(buffer).ReadToEndAsync();
        buffer.Position = 0;
        await buffer.CopyToAsync(original);
        if (context.Response.StatusCode is >= 200 and < 300)
            await db.IdempotencyRecords.Where(r => r.Id == record.Id).ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Completed, true).SetProperty(r => r.StatusCode, context.Response.StatusCode).SetProperty(r => r.ResponseBody, responseText));
        else
            await db.IdempotencyRecords.Where(r => r.Id == record.Id).ExecuteDeleteAsync();
    }
}
