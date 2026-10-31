namespace TShock.MCP.Server.Tests.TestDoubles;

internal static class QueryString
{
    /// <summary>Decodes a request URI query back into key/value pairs.</summary>
    internal static List<KeyValuePair<string, string>> Parse(Uri uri)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        var raw = uri.Query;

        if (raw.StartsWith('?'))
        {
            raw = raw[1..];
        }

        if (raw.Length == 0)
        {
            return pairs;
        }

        foreach (var segment in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = segment.IndexOf('=', StringComparison.Ordinal);
            var key = separator >= 0 ? segment[..separator] : segment;
            var value = separator >= 0 ? segment[(separator + 1)..] : string.Empty;
            pairs.Add(new KeyValuePair<string, string>(
                Uri.UnescapeDataString(key),
                Uri.UnescapeDataString(value)));
        }

        return pairs;
    }

    internal static string? Value(Uri uri, string key) =>
        Parse(uri)
            .Where(pair => string.Equals(pair.Key, key, StringComparison.Ordinal))
            .Select(pair => pair.Value)
            .SingleOrDefault();

    internal static int Count(Uri uri, string key) =>
        Parse(uri).Count(pair => string.Equals(pair.Key, key, StringComparison.Ordinal));
}
