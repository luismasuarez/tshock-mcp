namespace TShock.McpServer.TShock;

/// <summary>
/// Stable error codes of the MCP result envelope. Clients branch on these
/// strings, so they are part of the public contract: add new codes, never
/// rename or reuse an existing one. The canonical table lives in
/// <c>docs/architecture.md</c> and is verified against <see cref="All"/>.
/// </summary>
public static class ErrorCodes
{
    // Validation and operator policy.
    /// <summary>An argument is missing, empty or has the wrong type.</summary>
    public const string InvalidArgument = "invalid_argument";

    /// <summary>A write tool was called while writes are disabled.</summary>
    public const string WriteDisabled = "write_disabled";

    /// <summary>A destructive bridge call needs explicit confirmation.</summary>
    public const string ConfirmationRequired = "confirmation_required";

    /// <summary>The operator declined the confirmation prompt.</summary>
    public const string ConfirmationDeclined = "confirmation_declined";

    // TShock REST and shared transport failures.
    /// <summary>TShock answered with an HTTP error or an error field.</summary>
    public const string RestError = "rest_error";

    /// <summary>The TShock endpoint could not be reached.</summary>
    public const string NetworkError = "network_error";

    /// <summary>The configured timeout elapsed.</summary>
    public const string Timeout = "timeout";

    /// <summary>The host cancelled the operation.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>The response is not valid JSON or has the wrong shape.</summary>
    public const string InvalidResponse = "invalid_response";

    /// <summary>The response exceeded the internal read limit.</summary>
    public const string ResponseTooLarge = "response_too_large";

    /// <summary>An unclassified failure reached the envelope.</summary>
    public const string UnexpectedError = "unexpected_error";

    /// <summary>
    /// Internal fallback used when a failure reaches the envelope without a
    /// code. It should never surface in normal operation; it is documented so
    /// the public set stays closed.
    /// </summary>
    public const string UnknownError = "unknown_error";

    // AgentBridge failures.
    /// <summary>The bridge is not configured or cannot be reached.</summary>
    public const string BridgeUnavailable = "bridge_unavailable";

    /// <summary>The bridge did not answer within its timeout.</summary>
    public const string BridgeTimeout = "bridge_timeout";

    /// <summary>The bridge rejected the bearer token.</summary>
    public const string BridgeAuthError = "bridge_auth_error";

    /// <summary>The bridge answered with an error that has no specific mapping.</summary>
    public const string BridgeError = "bridge_error";

    /// <summary>The event cursor is no longer available.</summary>
    public const string CursorExpired = "cursor_expired";

    /// <summary>The requested region does not exist.</summary>
    public const string RegionNotFound = "region_not_found";

    /// <summary>A region with that name already exists.</summary>
    public const string RegionExists = "region_exists";

    /// <summary>The bridge owner policy rejected the requested owner.</summary>
    public const string OwnerNotAllowed = "owner_not_allowed";

    /// <summary>
    /// Every public code, in documentation order. Tests compare this set with
    /// the canonical table so an undocumented code cannot be added silently.
    /// </summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        InvalidArgument,
        WriteDisabled,
        ConfirmationRequired,
        ConfirmationDeclined,
        RestError,
        NetworkError,
        Timeout,
        Cancelled,
        InvalidResponse,
        ResponseTooLarge,
        UnexpectedError,
        UnknownError,
        BridgeUnavailable,
        BridgeTimeout,
        BridgeAuthError,
        BridgeError,
        CursorExpired,
        RegionNotFound,
        RegionExists,
        OwnerNotAllowed
    };

    /// <summary>True when <paramref name="code"/> belongs to the public set.</summary>
    public static bool IsKnown(string? code) =>
        code is not null && All.Contains(code, StringComparer.Ordinal);
}
