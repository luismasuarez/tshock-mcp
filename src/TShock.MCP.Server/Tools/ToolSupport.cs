using System.Globalization;
using System.Text;
using TShock.McpServer.TShock;

namespace TShock.McpServer.Tools;

/// <summary>
/// Contract limits for text arguments. They are constants, not environment
/// settings: the limit is part of the tool contract that clients see in
/// <c>inputSchema</c>, so an operator cannot silently change it.
/// </summary>
internal static class ArgumentLimits
{
    /// <summary>Two short chat lines; enough for an operator notice without flooding chat.</summary>
    internal const int BroadcastMessage = 256;

    /// <summary>Shown to the kicked player and kept in TShock logs.</summary>
    internal const int KickReason = 200;

    /// <summary>Attribution is a player name, so it matches the bridge's player-name cap.</summary>
    internal const int KillSource = 64;

    internal const int PlayerName = 64;
    internal const int AccountName = 64;
    internal const int GroupName = 64;

    /// <summary>Closed set: "name" or "id".</summary>
    internal const int LookupType = 16;

    /// <summary>Matches the bridge contract for a region name and owner.</summary>
    internal const int RegionName = 128;
    internal const int RegionOwner = 128;
}

/// <summary>
/// Shared argument validation. Every check runs before the network, so an
/// invalid value never reaches TShock or the bridge.
/// </summary>
internal static class ToolSupport
{
    /// <summary>
    /// Returns the stable error envelope for an unusable text argument, or
    /// <see langword="null"/> when the value is valid.
    /// </summary>
    /// <remarks>
    /// Length is counted in Unicode code points, matching JSON Schema's
    /// <c>maxLength</c>. <see cref="string.Length"/> counts UTF-16 code units and
    /// would reject non-BMP text that the contract accepts. Control characters and
    /// invisible format characters (except ZWNJ/ZWJ) are rejected because these
    /// values reach chat, logs and the model context, where CRLF, ANSI, bidi and
    /// zero-width text can forge a line or spoof a name.
    /// </remarks>
    internal static string? RequireText(string? value, string parameterName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ToolResults.Error(
                ErrorCodes.InvalidArgument,
                $"'{parameterName}' must not be empty.");
        }

        if (ExceedsMaxCodePoints(value, maxLength))
        {
            return ToolResults.Error(
                ErrorCodes.InvalidArgument,
                $"'{parameterName}' must be at most {maxLength} characters.");
        }

        if (ContainsDisallowedCharacters(value))
        {
            return ToolResults.Error(
                ErrorCodes.InvalidArgument,
                $"'{parameterName}' must not contain control or invisible formatting characters.");
        }

        return null;
    }

    /// <summary>
    /// Region names travel as a single URL path segment, so they also reject
    /// '/'. The length and control checks are the shared ones.
    /// </summary>
    internal static string? RequireRegionName(string? name)
    {
        if (RequireText(name, "name", ArgumentLimits.RegionName) is { } invalid)
        {
            return invalid;
        }

        return name!.Contains('/')
            ? ToolResults.Error(
                ErrorCodes.InvalidArgument,
                "'name' must not contain '/' or control characters.")
            : null;
    }

    /// <summary>Drops whitespace-only optional values so they are not sent upstream.</summary>
    internal static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool ExceedsMaxCodePoints(string value, int maxLength)
    {
        // A string can never contain more code points than UTF-16 code units, so
        // this fast path avoids enumerating the common, valid case.
        if (value.Length <= maxLength)
        {
            return false;
        }

        var count = 0;
        foreach (var _ in value.EnumerateRunes())
        {
            if (++count > maxLength)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// ZWNJ and ZWJ are format characters but legitimate in emoji sequences and in
    /// several scripts, so they are the only format characters allowed.
    /// </summary>
    private const int ZeroWidthNonJoiner = 0x200C;
    private const int ZeroWidthJoiner = 0x200D;

    private static bool ContainsDisallowedCharacters(string value)
    {
        foreach (var rune in value.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator)
            {
                return true;
            }

            if (category == UnicodeCategory.Format &&
                rune.Value is not (ZeroWidthNonJoiner or ZeroWidthJoiner))
            {
                return true;
            }
        }

        return false;
    }
}
