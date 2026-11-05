namespace TShock.McpServer.TShock;

/// <summary>
/// Error discriminators the gateway reads from the AgentBridge HTTP bodies.
/// These are internal to the bridge protocol, not part of the MCP envelope:
/// tools must translate them to <see cref="ErrorCodes"/> values and must never
/// forward one to an agent. The complete wire set is documented in
/// <c>docs/bridge.md</c>.
/// </summary>
public static class BridgeWireCodes
{
    /// <summary>The requested region does not exist (HTTP 404).</summary>
    public const string RegionNotFound = "region_not_found";

    /// <summary>A region with that name already exists (HTTP 409).</summary>
    public const string RegionExists = "region_exists";

    /// <summary>The bridge owner policy rejected the owner (HTTP 403).</summary>
    public const string OwnerNotAllowed = "owner_not_allowed";
}
