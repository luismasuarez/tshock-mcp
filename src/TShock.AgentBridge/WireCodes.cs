namespace TShock.AgentBridge;

/// <summary>
/// Error discriminators emitted in the bridge's JSON bodies. This is the
/// bridge wire protocol, not the MCP envelope: the gateway maps the ones it
/// understands to <c>TShock.McpServer.TShock.ErrorCodes</c> and collapses the
/// rest into <c>bridge_error</c>. The contract is documented in
/// <c>docs/bridge.md</c> and verified against <see cref="All"/>.
/// </summary>
internal static class WireCodes
{
    public const string Unauthorized = "unauthorized";
    public const string NotFound = "not_found";
    public const string InternalError = "internal_error";
    public const string InvalidJson = "invalid_json";
    public const string MethodNotAllowed = "method_not_allowed";
    public const string InvalidCursor = "invalid_cursor";
    public const string InvalidLimit = "invalid_limit";
    public const string CursorExpired = "cursor_expired";
    public const string NameRequired = "name_required";
    public const string InvalidSize = "invalid_size";
    public const string InvalidPosition = "invalid_position";
    public const string OutOfBounds = "out_of_bounds";
    public const string OwnerNotAllowed = "owner_not_allowed";
    public const string RegionNotFound = "region_not_found";
    public const string RegionExists = "region_exists";
    public const string CreateFailed = "create_failed";
    public const string DeleteFailed = "delete_failed";

    /// <summary>Every wire code, in documentation order.</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        Unauthorized,
        NotFound,
        InternalError,
        InvalidJson,
        MethodNotAllowed,
        InvalidCursor,
        InvalidLimit,
        CursorExpired,
        NameRequired,
        InvalidSize,
        InvalidPosition,
        OutOfBounds,
        OwnerNotAllowed,
        RegionNotFound,
        RegionExists,
        CreateFailed,
        DeleteFailed
    };
}
