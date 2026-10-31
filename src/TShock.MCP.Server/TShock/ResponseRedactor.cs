using System.Net;
using System.Text.Json.Nodes;

namespace TShock.McpServer.TShock;

/// <summary>
/// Removes sensitive values from a TShock REST response before it reaches the
/// model. Redaction is recursive so nested objects and arrays are covered.
/// </summary>
public static class ResponseRedactor
{
    public const string RedactedValue = "<redacted>";

    /// <summary>
    /// Replaces the value of every <c>token</c> and <c>password</c> property
    /// (case-insensitive) with <see cref="RedactedValue"/>. When
    /// <paramref name="redactIps"/> is true, every <c>ip</c> property and every
    /// value of an identity field (such as TShock's ban <c>identifier</c>) that
    /// parses as an IP address is redacted as well. Version strings that happen to
    /// look like an IPv4 address (for example <c>1.4.4.9</c>) are not touched
    /// because their property name is not an identity field. Numbers, booleans and
    /// non-address strings are left untouched. Credentials are always redacted;
    /// IP addresses are opt-in through <paramref name="redactIps"/>.
    /// The node is mutated in place and returned for convenience.
    /// </summary>
    public static JsonNode? Redact(JsonNode? node, bool redactIps = true)
    {
        switch (node)
        {
            case JsonObject jsonObject:
                RedactObject(jsonObject, redactIps);
                break;
            case JsonArray jsonArray:
                RedactArray(jsonArray, redactIps);
                break;
        }

        return node;
    }

    private static void RedactObject(JsonObject jsonObject, bool redactIps)
    {
        foreach (var property in jsonObject.ToList())
        {
            if (IsAlwaysSensitive(property.Key) || (redactIps && IsIpKey(property.Key)))
            {
                jsonObject[property.Key] = JsonValue.Create(RedactedValue);
                continue;
            }

            if (redactIps && IsIdentityField(property.Key) && IsIpStringValue(property.Value))
            {
                jsonObject[property.Key] = JsonValue.Create(RedactedValue);
                continue;
            }

            Redact(property.Value, redactIps);
        }
    }

    private static void RedactArray(JsonArray jsonArray, bool redactIps)
    {
        for (var index = 0; index < jsonArray.Count; index++)
        {
            if (redactIps && IsIpStringValue(jsonArray[index]))
            {
                jsonArray[index] = JsonValue.Create(RedactedValue);
                continue;
            }

            Redact(jsonArray[index], redactIps);
        }
    }

    /// <summary>
    /// True when the value is a JSON string (not a number or boolean) that
    /// <see cref="IPAddress.TryParse(string?, out IPAddress?)"/> accepts.
    /// </summary>
    private static bool IsIpStringValue(JsonNode? node) =>
        node is JsonValue value &&
        value.TryGetValue<string>(out var text) &&
        IPAddress.TryParse(text, out _);

    private static bool IsAlwaysSensitive(string propertyName) =>
        propertyName.Equals("token", StringComparison.OrdinalIgnoreCase) ||
        propertyName.Equals("password", StringComparison.OrdinalIgnoreCase);

    private static bool IsIpKey(string propertyName) =>
        propertyName.Equals("ip", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Fields that may carry a network identifier instead of a human-readable
    /// value. TShock uses <c>identifier</c> for bans, which can be an IP address.
    /// </summary>
    private static bool IsIdentityField(string propertyName) =>
        propertyName.Equals("identifier", StringComparison.OrdinalIgnoreCase) ||
        propertyName.Equals("address", StringComparison.OrdinalIgnoreCase) ||
        propertyName.Equals("host", StringComparison.OrdinalIgnoreCase) ||
        propertyName.Equals("hostname", StringComparison.OrdinalIgnoreCase) ||
        propertyName.Equals("remote", StringComparison.OrdinalIgnoreCase) ||
        propertyName.Equals("endpoint", StringComparison.OrdinalIgnoreCase) ||
        propertyName.Equals("lastip", StringComparison.OrdinalIgnoreCase);
}
