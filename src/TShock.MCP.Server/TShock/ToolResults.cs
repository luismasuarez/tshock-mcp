using System.Text.Json;
using System.Text.Json.Nodes;

namespace TShock.McpServer.TShock;

/// <summary>
/// Serializes tool output into a stable envelope:
/// <c>{"ok":true,"data":...}</c> or <c>{"ok":false,"error":{...}}</c>.
/// A string is used deliberately so every MCP host renders the same payload.
/// </summary>
public static class ToolResults
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false
    };

    public static string Ok(JsonNode? data) =>
        new JsonObject
        {
            ["ok"] = true,
            ["data"] = data
        }.ToJsonString(SerializerOptions);

    public static string From(TShockResult result) =>
        result.Ok
            ? Ok(result.Body)
            : Error(result.ErrorCode ?? ErrorCodes.UnknownError, result.ErrorMessage ?? "Unknown error", result.StatusCode);

    public static string From(BridgeResult result) =>
        result.Ok
            ? Ok(result.Body)
            : Error(
                result.ErrorCode ?? ErrorCodes.UnknownError,
                result.ErrorMessage ?? "Unknown error",
                result.StatusCode,
                result.ErrorDetails);

    /// <param name="details">
    /// Optional machine-readable context added inside the <c>error</c> object. It
    /// never replaces the stable <c>code</c>, <c>message</c> or <c>httpStatus</c>.
    /// </param>
    public static string Error(string code, string message, int statusCode = 0, JsonNode? details = null)
    {
        var error = new JsonObject
        {
            ["code"] = code,
            ["message"] = message
        };

        if (statusCode > 0)
        {
            error["httpStatus"] = statusCode;
        }

        if (details is not null)
        {
            error["details"] = details;
        }

        return new JsonObject
        {
            ["ok"] = false,
            ["error"] = error
        }.ToJsonString(SerializerOptions);
    }
}
