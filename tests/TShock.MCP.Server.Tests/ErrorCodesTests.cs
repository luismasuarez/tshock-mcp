using System.Text.RegularExpressions;
using TShock.McpServer.TShock;

namespace TShock.MCP.Server.Tests;

/// <summary>
/// Locks the public error-code contract. The codes are what clients branch on,
/// so a typo, a rename or an undocumented addition must fail here rather than
/// reach an agent.
/// </summary>
public sealed class ErrorCodesTests
{
    private static readonly string[] ExpectedPublicCodes =
    {
        "invalid_argument",
        "write_disabled",
        "rate_limited",
        "confirmation_required",
        "confirmation_declined",
        "rest_error",
        "network_error",
        "timeout",
        "cancelled",
        "invalid_response",
        "response_too_large",
        "unexpected_error",
        "unknown_error",
        "bridge_unavailable",
        "bridge_timeout",
        "bridge_auth_error",
        "bridge_error",
        "cursor_expired",
        "region_not_found",
        "region_exists",
        "owner_not_allowed"
    };

    [Fact]
    public void PublicSet_IsExactlyTheDocumentedContract()
    {
        Assert.Equal(
            ExpectedPublicCodes.OrderBy(code => code, StringComparer.Ordinal),
            ErrorCodes.All.OrderBy(code => code, StringComparer.Ordinal));
    }

    [Fact]
    public void PublicSet_HasNoDuplicatesAndUsesSnakeCase()
    {
        Assert.Equal(
            ErrorCodes.All.Count,
            ErrorCodes.All.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ErrorCodes.All, code => Assert.Matches("^[a-z][a-z0-9_]*$", code));
    }

    [Fact]
    public void PublicSet_MatchesTheCanonicalDocumentationTable()
    {
        var documented = ReadCodeTable("docs/architecture.md", "ERROR CODES");

        AssertSetEquality(ErrorCodes.All, documented, "docs/architecture.md");
    }

    [Fact]
    public void IsKnown_AcceptsOnlyThePublicSet()
    {
        Assert.All(ErrorCodes.All, code => Assert.True(ErrorCodes.IsKnown(code)));
        Assert.False(ErrorCodes.IsKnown(null));
        Assert.False(ErrorCodes.IsKnown("not_a_real_code"));
    }

    [Fact]
    public void ErrorConstructors_DoNotUseStringLiterals()
    {
        var patterns = new[]
        {
            @"ToolResults\.Error\(\s*""",
            @"BridgeResult\.Failure\(\s*""",
            @"TShockResult\.TransportError\(\s*""",
            @"Build\(\s*"""
        }.Select(pattern => new Regex(pattern, RegexOptions.Compiled)).ToArray();

        var offenders = new List<string>();
        foreach (var file in SourceFiles("src/TShock.MCP.Server"))
        {
            var text = File.ReadAllText(file);
            foreach (var match in patterns.SelectMany(pattern => pattern.Matches(text).Cast<Match>()))
            {
                var line = text[..match.Index].Count(character => character == '\n') + 1;
                offenders.Add(
                    $"{Path.GetRelativePath(RepoRoot, file)}:{line}: " +
                    match.Value.Replace('\n', ' ').Replace('\r', ' '));
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Use ErrorCodes constants instead of string literals:" + Environment.NewLine +
            string.Join(Environment.NewLine, offenders));
    }

    private static HashSet<string> ReadCodeTable(string relativePath, string marker)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot, relativePath));
        var begin = $"<!-- BEGIN {marker} -->";
        var end = $"<!-- END {marker} -->";
        var start = text.IndexOf(begin, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing '{begin}' in {relativePath}.");
        var blockStart = start + begin.Length;
        var blockEnd = text.IndexOf(end, blockStart, StringComparison.Ordinal);
        Assert.True(blockEnd > blockStart, $"Missing '{end}' in {relativePath}.");

        return Regex
            .Matches(text[blockStart..blockEnd], @"^\|\s*`([a-z0-9_]+)`", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static void AssertSetEquality(
        IEnumerable<string> expected,
        IReadOnlySet<string> documented,
        string source)
    {
        var expectedSet = expected.ToHashSet(StringComparer.Ordinal);
        var missing = expectedSet.Except(documented, StringComparer.Ordinal).OrderBy(code => code).ToArray();
        var extra = documented.Except(expectedSet, StringComparer.Ordinal).OrderBy(code => code).ToArray();

        Assert.True(
            missing.Length == 0 && extra.Length == 0,
            $"{source} is out of sync. Missing in docs: [{string.Join(", ", missing)}]. " +
            $"Documented but not in ErrorCodes.All: [{string.Join(", ", extra)}].");
    }

    private static IEnumerable<string> SourceFiles(string relativePath)
    {
        var root = Path.Combine(RepoRoot, relativePath);
        return Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsUnder(path, "obj") && !IsUnder(path, "bin"))
            .Where(path => Path.GetFileName(path) is not ("ErrorCodes.cs" or "BridgeWireCodes.cs"));
    }

    private static bool IsUnder(string path, string directory) =>
        path.Split(Path.DirectorySeparatorChar).Contains(directory, StringComparer.Ordinal);

    private static string RepoRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null &&
                   !File.Exists(Path.Combine(directory.FullName, "tshock-mcp.sln")))
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);
            return directory!.FullName;
        }
    }
}
