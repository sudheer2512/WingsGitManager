using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Wings.GitManager.Contracts;

namespace Wings.GitManager.Client;

/// <summary>Error returned by the Wings Git Manager API (problem details with a stable code).</summary>
public sealed class WingsGitManagerException(HttpStatusCode status, string code, string message, string? correlationId, JsonElement? details)
    : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = status;
    public string Code { get; } = code;
    public string? CorrelationId { get; } = correlationId;
    public JsonElement? Details { get; } = details;
}

public sealed class WingsGitManagerClientOptions
{
    public required Uri BaseAddress { get; init; }
    /// <summary>Scoped API token of the Wings Code AI agent service (wgm_…).</summary>
    public required string ApiToken { get; init; }
    /// <summary>Retries for transient failures (network, 502/503/504). Mutations reuse the same Idempotency-Key.</summary>
    public int MaxRetries { get; init; } = 3;
}

/// <summary>
/// Typed client for Wings Code. Every mutating call carries an Idempotency-Key so retries never duplicate commits,
/// repositories or recoveries.
/// </summary>
public sealed class WingsGitManagerClient : IDisposable
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly int _maxRetries;

    public WingsGitManagerClient(WingsGitManagerClientOptions options, HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = options.BaseAddress;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiToken);
        _http.Timeout = TimeSpan.FromMinutes(5);
        _ownsClient = true;
        _maxRetries = options.MaxRetries;
        Init();
    }

    /// <summary>Use an externally managed HttpClient (BaseAddress and Authorization must already be set).</summary>
    public WingsGitManagerClient(HttpClient http, int maxRetries = 3)
    {
        _http = http;
        _maxRetries = maxRetries;
        Init();
    }

    private void Init()
    {
        Repositories = new RepositoriesClient(this);
        Changes = new ChangesClient(this);
        Sessions = new SessionsClient(this);
        Recovery = new RecoveryClient(this);
        Sync = new SyncClient(this);
        Backups = new BackupsClient(this);
    }

    public RepositoriesClient Repositories { get; private set; } = null!;
    public ChangesClient Changes { get; private set; } = null!;
    public SessionsClient Sessions { get; private set; } = null!;
    public RecoveryClient Recovery { get; private set; } = null!;
    public SyncClient Sync { get; private set; } = null!;
    public BackupsClient Backups { get; private set; } = null!;

    public Task<JobDto> GetJobAsync(Guid jobId, CancellationToken ct = default) => GetAsync<JobDto>($"api/v1/jobs/{jobId}", ct);

    /// <summary>Polls a background job until it finishes.</summary>
    public async Task<JobDto> WaitForJobAsync(Guid jobId, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var job = await GetJobAsync(jobId, ct);
            if (job.State is JobState.Succeeded or JobState.Failed or JobState.Cancelled) return job;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Job {jobId} is still {job.State}.");
            await Task.Delay(500, ct);
        }
    }

    internal Task<T> GetAsync<T>(string path, CancellationToken ct) => SendAsync<T>(HttpMethod.Get, path, null, null, ct);

    internal Task<T> PostAsync<T>(string path, object? body, CancellationToken ct, string? idempotencyKey = null) =>
        SendAsync<T>(HttpMethod.Post, path, body, idempotencyKey ?? Guid.NewGuid().ToString("N"), ct);

    internal Task<T> PatchAsync<T>(string path, object? body, CancellationToken ct) =>
        SendAsync<T>(HttpMethod.Patch, path, body, Guid.NewGuid().ToString("N"), ct);

    internal async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, string? idempotencyKey, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, path);
            if (body is not null) req.Content = JsonContent.Create(body, body.GetType(), options: JsonOptions);
            if (idempotencyKey is not null) req.Headers.Add("Idempotency-Key", idempotencyKey);
            HttpResponseMessage resp;
            try
            {
                resp = await _http.SendAsync(req, ct);
            }
            catch (HttpRequestException) when (attempt < _maxRetries)
            {
                await Task.Delay(Backoff(attempt), ct);
                continue;
            }
            using (resp)
            {
                if (resp.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout && attempt < _maxRetries)
                {
                    await Task.Delay(Backoff(attempt), ct);
                    continue;
                }
                if (!resp.IsSuccessStatusCode) throw await ErrorAsync(resp, ct);
                if (typeof(T) == typeof(Unit) || resp.StatusCode == HttpStatusCode.NoContent) return default!;
                return (await resp.Content.ReadFromJsonAsync<T>(JsonOptions, ct))!;
            }
        }
    }

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt));

    private static async Task<WingsGitManagerException> ErrorAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var text = await resp.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(text);
            var r = doc.RootElement;
            return new WingsGitManagerException(resp.StatusCode,
                r.TryGetProperty("code", out var c) ? c.GetString() ?? "error" : "error",
                r.TryGetProperty("detail", out var d) ? d.GetString() ?? text : text,
                r.TryGetProperty("correlationId", out var cid) ? cid.GetString() : null,
                r.TryGetProperty("details", out var det) && det.ValueKind != JsonValueKind.Null ? det.Clone() : null);
        }
        catch (JsonException)
        {
            return new WingsGitManagerException(resp.StatusCode, "http_" + (int)resp.StatusCode, text.Length > 500 ? text[..500] : text, null, null);
        }
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }

    internal readonly struct Unit;
}

public sealed class RepositoriesClient(WingsGitManagerClient c)
{
    /// <summary>Creates the repository for a new Wings Code project (idempotent per company + project id).</summary>
    public Task<RepositoryProvisionResult> CreateAsync(CreateRepositoryRequest request, CancellationToken ct = default, string? idempotencyKey = null) =>
        c.PostAsync<RepositoryProvisionResult>("api/v1/repositories", request, ct, idempotencyKey);
    public Task<RepositoryProvisionResult> ImportAsync(ImportRepositoryRequest request, CancellationToken ct = default) =>
        c.PostAsync<RepositoryProvisionResult>("api/v1/repositories/import", request, ct);
    public Task<RepositoryDetailDto> GetAsync(Guid id, CancellationToken ct = default) => c.GetAsync<RepositoryDetailDto>($"api/v1/repositories/{id}", ct);
    public Task<PagedResult<RepositorySummaryDto>> ListAsync(Guid? companyId = null, int page = 1, int pageSize = 50, CancellationToken ct = default) =>
        c.GetAsync<PagedResult<RepositorySummaryDto>>($"api/v1/repositories?page={page}&pageSize={pageSize}{(companyId is null ? "" : $"&companyId={companyId}")}", ct);
    public Task<RepositoryStatusDto> StatusAsync(Guid id, CancellationToken ct = default) => c.GetAsync<RepositoryStatusDto>($"api/v1/repositories/{id}/status", ct);
    public Task<PagedResult<CommitSummaryDto>> CommitsAsync(Guid id, string? @ref = null, int page = 1, int pageSize = 50, CancellationToken ct = default) =>
        c.GetAsync<PagedResult<CommitSummaryDto>>($"api/v1/repositories/{id}/commits?page={page}&pageSize={pageSize}{(@ref is null ? "" : "&ref=" + Uri.EscapeDataString(@ref))}", ct);
    public Task<CommitDetailDto> CommitAsync(Guid id, string sha, CancellationToken ct = default) => c.GetAsync<CommitDetailDto>($"api/v1/repositories/{id}/commits/{sha}", ct);
    public Task<FileContentDto> FileAsync(Guid id, string path, string? @ref = null, CancellationToken ct = default) =>
        c.GetAsync<FileContentDto>($"api/v1/repositories/{id}/files/content?path={Uri.EscapeDataString(path)}{(@ref is null ? "" : "&ref=" + Uri.EscapeDataString(@ref))}", ct);
    public Task<DiffDto> CompareAsync(Guid id, string @base, string head, CancellationToken ct = default) =>
        c.GetAsync<DiffDto>($"api/v1/repositories/{id}/compare?base={Uri.EscapeDataString(@base)}&head={Uri.EscapeDataString(head)}", ct);
    public Task<IReadOnlyList<BranchDto>> BranchesAsync(Guid id, CancellationToken ct = default) => c.GetAsync<IReadOnlyList<BranchDto>>($"api/v1/repositories/{id}/branches", ct);
    public Task<MergeResultDto> MergeAsync(Guid id, MergeBranchRequest request, CancellationToken ct = default) => c.PostAsync<MergeResultDto>($"api/v1/repositories/{id}/branches/merge", request, ct);
    public Task<PagedResult<CheckpointDto>> CheckpointsAsync(Guid id, int page = 1, CancellationToken ct = default) =>
        c.GetAsync<PagedResult<CheckpointDto>>($"api/v1/repositories/{id}/checkpoints?page={page}", ct);
}

public sealed class ChangesClient(WingsGitManagerClient c)
{
    /// <summary>Call before modifying files. Throws <see cref="WingsGitManagerException"/> with code high_risk_blocked when protection fails.</summary>
    public Task<BeginChangeResponse> BeginAsync(Guid repositoryId, BeginChangeRequest request, CancellationToken ct = default) =>
        c.PostAsync<BeginChangeResponse>($"api/v1/repositories/{repositoryId}/changes/begin", request, ct);
    /// <summary>Call after modifying files: commits the operation's changes and reports durability.</summary>
    public Task<CompleteChangeResponse> CompleteAsync(Guid repositoryId, Guid operationId, CompleteChangeRequest request, CancellationToken ct = default) =>
        c.PostAsync<CompleteChangeResponse>($"api/v1/repositories/{repositoryId}/changes/{operationId}/complete", request, ct, $"complete-{operationId:N}");
    public Task<AbortChangeResponse> AbortAsync(Guid repositoryId, Guid operationId, AbortChangeRequest request, CancellationToken ct = default) =>
        c.PostAsync<AbortChangeResponse>($"api/v1/repositories/{repositoryId}/changes/{operationId}/abort", request, ct);
    public Task<ChangeOperationDto> GetAsync(Guid repositoryId, Guid operationId, CancellationToken ct = default) =>
        c.GetAsync<ChangeOperationDto>($"api/v1/repositories/{repositoryId}/changes/{operationId}", ct);

    /// <summary>
    /// Convenience wrapper: begin → run <paramref name="modify"/> in the returned working directory → complete
    /// (or abort, preserving the work, if <paramref name="modify"/> throws).
    /// </summary>
    public async Task<CompleteChangeResponse> RunAsync(Guid repositoryId, BeginChangeRequest begin, Func<BeginChangeResponse, Task<CompleteChangeRequest>> modify, CancellationToken ct = default)
    {
        var op = await BeginAsync(repositoryId, begin, ct);
        CompleteChangeRequest complete;
        try
        {
            complete = await modify(op);
        }
        catch
        {
            await AbortAsync(repositoryId, op.OperationId, new AbortChangeRequest { Mode = AbortMode.PreserveAndReset, Reason = "modification failed" }, CancellationToken.None);
            throw;
        }
        return await CompleteAsync(repositoryId, op.OperationId, complete with { ExpectedHead = complete.ExpectedHead }, ct);
    }
}

public sealed class SessionsClient(WingsGitManagerClient c)
{
    public Task<SessionDto> StartAsync(StartSessionRequest request, CancellationToken ct = default) => c.PostAsync<SessionDto>("api/v1/ai/sessions", request, ct);
    public Task<SessionDto> GetAsync(Guid sessionId, CancellationToken ct = default) => c.GetAsync<SessionDto>($"api/v1/ai/sessions/{sessionId}", ct);
    public Task<SessionDto> EndAsync(Guid sessionId, EndSessionRequest request, CancellationToken ct = default) => c.PostAsync<SessionDto>($"api/v1/ai/sessions/{sessionId}/end", request, ct);
    public Task<IReadOnlyList<CommitSummaryDto>> CommitsAsync(Guid sessionId, CancellationToken ct = default) => c.GetAsync<IReadOnlyList<CommitSummaryDto>>($"api/v1/ai/sessions/{sessionId}/commits", ct);
    public Task<DiffDto> ChangesAsync(Guid sessionId, CancellationToken ct = default) => c.GetAsync<DiffDto>($"api/v1/ai/sessions/{sessionId}/changes", ct);
    public Task<RecoveryPlanDto> RecoveryPreviewAsync(Guid sessionId, RecoveryRequest? request = null, CancellationToken ct = default) =>
        c.PostAsync<RecoveryPlanDto>($"api/v1/ai/sessions/{sessionId}/recovery-preview", request ?? new RecoveryRequest(), ct);
    public Task<RecoveryPlanDto> RevertAsync(Guid sessionId, RecoveryRequest request, CancellationToken ct = default) =>
        c.PostAsync<RecoveryPlanDto>($"api/v1/ai/sessions/{sessionId}/revert", request, ct);
    public Task<RecoveryInterpretationDto> InterpretAsync(string query, CancellationToken ct = default) =>
        c.PostAsync<RecoveryInterpretationDto>("api/v1/ai/recovery/interpret", new { query }, ct);
}

public sealed class RecoveryClient(WingsGitManagerClient c)
{
    /// <summary>Conversational entry point: ranked recovery points for a user's request.</summary>
    public Task<RecoveryCandidatesResponse> CandidatesAsync(Guid repositoryId, RecoveryCandidatesRequest request, CancellationToken ct = default) =>
        c.PostAsync<RecoveryCandidatesResponse>($"api/v1/repositories/{repositoryId}/recovery/candidates", request, ct);
    public Task<RecoveryPlanDto> PreviewAsync(Guid repositoryId, RecoveryRequest request, CancellationToken ct = default) =>
        c.PostAsync<RecoveryPlanDto>($"api/v1/repositories/{repositoryId}/recovery/preview", request, ct);
    /// <summary>Executes a previewed recovery. Set <c>Approved = true</c> only after the user confirmed the preview.</summary>
    public Task<RecoveryPlanDto> ExecuteAsync(Guid repositoryId, RecoveryPlanDto preview, bool approved, CancellationToken ct = default)
    {
        var path = preview.Type switch
        {
            RecoveryType.RestoreFile or RecoveryType.RestoreDeleted => "restore-file",
            RecoveryType.RestoreProject => "restore-project",
            RecoveryType.RevertSession => "revert-session",
            RecoveryType.RevertCommits => "revert-commits",
            _ => "clone"
        };
        return c.PostAsync<RecoveryPlanDto>($"api/v1/repositories/{repositoryId}/recovery/{path}",
            new RecoveryRequest { Type = preview.Type, PreviewOperationId = preview.OperationId, Approved = approved }, ct, $"recovery-{preview.OperationId:N}");
    }
    public Task<RecoveryPlanDto> GetAsync(Guid repositoryId, Guid operationId, CancellationToken ct = default) =>
        c.GetAsync<RecoveryPlanDto>($"api/v1/repositories/{repositoryId}/recovery/operations/{operationId}", ct);
    public Task<ChangeSummaryDto> ChangeSummaryAsync(Guid repositoryId, double hours, CancellationToken ct = default) =>
        c.GetAsync<ChangeSummaryDto>($"api/v1/repositories/{repositoryId}/changes-summary?hours={hours.ToString(System.Globalization.CultureInfo.InvariantCulture)}", ct);
    public Task<IReadOnlyList<DeletedFileDto>> DeletedFilesAsync(Guid repositoryId, string? pathContains = null, CancellationToken ct = default) =>
        c.GetAsync<IReadOnlyList<DeletedFileDto>>($"api/v1/repositories/{repositoryId}/deleted-files{(pathContains is null ? "" : "?pathContains=" + Uri.EscapeDataString(pathContains))}", ct);
}

public sealed class SyncClient(WingsGitManagerClient c)
{
    public Task<SyncStatusDto> StatusAsync(Guid repositoryId, CancellationToken ct = default) => c.GetAsync<SyncStatusDto>($"api/v1/repositories/{repositoryId}/sync/status", ct);
    public Task<SyncStatusDto> PushAsync(Guid repositoryId, PushRequest request, CancellationToken ct = default) => c.PostAsync<SyncStatusDto>($"api/v1/repositories/{repositoryId}/sync/push", request, ct);
    public Task<FetchResultDto> FetchAsync(Guid repositoryId, CancellationToken ct = default) => c.PostAsync<FetchResultDto>($"api/v1/repositories/{repositoryId}/sync/fetch", null, ct);
    public Task<IReadOnlyList<PushJobDto>> QueueAsync(Guid repositoryId, CancellationToken ct = default) => c.GetAsync<IReadOnlyList<PushJobDto>>($"api/v1/repositories/{repositoryId}/sync/queue", ct);
}

public sealed class BackupsClient(WingsGitManagerClient c)
{
    public Task<AcceptedJobDto> CreateAsync(Guid repositoryId, CancellationToken ct = default) => c.PostAsync<AcceptedJobDto>($"api/v1/repositories/{repositoryId}/backups", null, ct);
    public Task<IReadOnlyList<BackupDto>> ListAsync(Guid repositoryId, CancellationToken ct = default) => c.GetAsync<IReadOnlyList<BackupDto>>($"api/v1/repositories/{repositoryId}/backups", ct);
    public Task<AcceptedJobDto> VerifyAsync(Guid repositoryId, Guid backupId, CancellationToken ct = default) => c.PostAsync<AcceptedJobDto>($"api/v1/repositories/{repositoryId}/backups/{backupId}/verify", null, ct);
    public Task<AcceptedJobDto> RestoreAsync(Guid repositoryId, Guid backupId, RestoreBackupRequest request, CancellationToken ct = default) =>
        c.PostAsync<AcceptedJobDto>($"api/v1/repositories/{repositoryId}/backups/{backupId}/restore", request, ct);
}
