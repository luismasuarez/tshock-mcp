using ModelContextProtocol.Server;

namespace TShock.McpServer.Tools;

/// <summary>
/// Classifies the primitive matched by the SDK for the rate limiter. The
/// classification comes from the registered annotation, not from a second
/// hand-maintained table: a new tool is classified as soon as it declares its
/// <c>readOnlyHint</c>, and a missing hint falls back to the stricter write
/// bucket instead of silently bypassing the write budget.
/// </summary>
internal static class ToolRateLimitCatalog
{
    /// <summary>
    /// Returns the canonical tool name and whether the write bucket applies.
    /// <paramref name="primitive"/> is null when the client asked for a tool that
    /// does not exist; that call only spends the global budget.
    /// </summary>
    internal static (string? Name, bool IsWrite) Describe(IMcpServerPrimitive? primitive)
    {
        if (primitive is not McpServerTool tool)
        {
            return (null, false);
        }

        var annotations = tool.ProtocolTool.Annotations;
        return (tool.ProtocolTool.Name, annotations?.ReadOnlyHint != true);
    }
}
