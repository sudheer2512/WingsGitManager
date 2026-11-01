namespace Wings.GitManager.Core;

/// <summary>Expected failure mapped to a structured API error (ProblemDetails with a stable code).</summary>
public class GitManagerException(string code, string message, int statusCode = 400, object? details = null) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
    public object? Details { get; } = details;
}

public sealed class NotFoundException(string what) : GitManagerException("not_found", $"{what} was not found.", 404);

public sealed class ForbiddenException(string message = "You do not have access to this resource.")
    : GitManagerException("forbidden", message, 403);

public sealed class ConflictException(string code, string message, object? details = null)
    : GitManagerException(code, message, 409, details);

public sealed class PolicyViolationException(string code, string message, object? details = null)
    : GitManagerException(code, message, 422, details);

public sealed class GitCommandException(string message, int exitCode, string stderr)
    : GitManagerException("git_failed", message, 500)
{
    public int ExitCode { get; } = exitCode;
    public string StandardError { get; } = stderr;
}

public static class ErrorCodes
{
    public const string Validation = "validation_failed";
    public const string PathRejected = "path_rejected";
    public const string HeadMismatch = "expected_head_mismatch";
    public const string WorkspaceBusy = "workspace_busy";
    public const string HighRiskBlocked = "high_risk_blocked";
    public const string RemoteCheckpointRequired = "remote_checkpoint_required";
    public const string SecretsDetected = "secrets_detected";
    public const string ProtectedBranch = "protected_branch";
    public const string ForcePushRefused = "force_push_refused";
    public const string ApprovalRequired = "approval_required";
    public const string PreviewExpired = "preview_expired";
    public const string MergeConflict = "merge_conflict";
    public const string RemoteUnavailable = "remote_unavailable";
    public const string IdempotencyMismatch = "idempotency_key_reuse";
    public const string InvalidState = "invalid_state";
    public const string UnsafeRepository = "unsafe_repository";
    public const string LockTimeout = "lock_timeout";
}
