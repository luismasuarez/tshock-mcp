using System.Text;
using System.Text.Json.Nodes;

namespace TShock.McpServer.TShock;

/// <summary>
/// Helpers shared by the REST and bridge clients: bounded body reads, duplicate
/// key rejection, route sanitizing and message truncation. Keeping them in one
/// place guarantees both surfaces bound responses and errors the same way.
/// </summary>
internal static class HttpPayload
{
    internal const int MaxResponseBytes = 5 * 1024 * 1024;
    internal const int MaxMessageLength = 500;

    /// <summary>
    /// Reads a response body up to <see cref="MaxResponseBytes"/>. Returns
    /// <see langword="null"/> when the payload is larger, so the caller can map
    /// that to <c>response_too_large</c> without buffering the whole body.
    /// </summary>
    internal static async Task<string?> ReadBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];

        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > MaxResponseBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Forces every parsed object to materialize so duplicate property keys are
    /// rejected deterministically: on .NET 8 the <see cref="ArgumentException"/>
    /// only surfaces on the first property access, not during parsing.
    /// </summary>
    internal static void ValidateNoDuplicateKeys(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject jsonObject:
                foreach (var property in jsonObject)
                {
                    ValidateNoDuplicateKeys(property.Value);
                }

                break;
            case JsonArray jsonArray:
                foreach (var element in jsonArray)
                {
                    ValidateNoDuplicateKeys(element);
                }

                break;
        }
    }

    /// <summary>
    /// Caps a message at <see cref="MaxMessageLength"/> characters in total,
    /// including the trailing ellipsis.
    /// </summary>
    internal static string Truncate(string value) =>
        value.Length <= MaxMessageLength
            ? value
            : string.Concat(value.AsSpan(0, MaxMessageLength - 3), "...");

    internal static string StripQuery(string path)
    {
        var separatorIndex = path.IndexOf('?', StringComparison.Ordinal);
        return separatorIndex >= 0 ? path[..separatorIndex] : path;
    }
}
