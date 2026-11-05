using System.Text.RegularExpressions;
using Xunit;

namespace TShock.AgentBridge.Tests;

/// <summary>
/// Locks the bridge wire-code contract. These values are internal to the HTTP
/// layer, but the gateway maps them by name, so a typo or an undocumented code
/// would silently become <c>bridge_error</c> or leak to an agent.
/// </summary>
public sealed class WireCodesTests
{
    private static readonly string[] ExpectedWireCodes =
    {
        "unauthorized",
        "not_found",
        "internal_error",
        "invalid_json",
        "method_not_allowed",
        "invalid_cursor",
        "invalid_limit",
        "cursor_expired",
        "name_required",
        "invalid_size",
        "invalid_position",
        "out_of_bounds",
        "owner_not_allowed",
        "region_not_found",
        "region_exists",
        "create_failed",
        "delete_failed"
    };

    [Fact]
    public void WireSet_IsExactlyTheDocumentedContract()
    {
        Assert.Equal(
            ExpectedWireCodes.OrderBy(code => code, StringComparer.Ordinal),
            WireCodes.All.OrderBy(code => code, StringComparer.Ordinal));
    }

    [Fact]
    public void WireSet_HasNoDuplicatesAndUsesSnakeCase()
    {
        Assert.Equal(
            WireCodes.All.Count,
            WireCodes.All.Distinct(StringComparer.Ordinal).Count());
        Assert.All(WireCodes.All, code => Assert.Matches("^[a-z][a-z0-9_]*$", code));
    }

    [Fact]
    public void WireSet_MatchesTheCanonicalDocumentationTable()
    {
        var documented = ReadCodeTable("docs/bridge.md", "BRIDGE WIRE CODES");

        AssertSetEquality(WireCodes.All, documented, "docs/bridge.md");
    }

    [Theory]
    [InlineData(WireCodes.NameRequired, 400)]
    [InlineData(WireCodes.InvalidSize, 400)]
    [InlineData(WireCodes.InvalidPosition, 400)]
    [InlineData(WireCodes.OutOfBounds, 400)]
    [InlineData(WireCodes.InvalidJson, 400)]
    [InlineData(WireCodes.OwnerNotAllowed, 403)]
    [InlineData(WireCodes.RegionNotFound, 404)]
    [InlineData(WireCodes.RegionExists, 409)]
    [InlineData(WireCodes.CreateFailed, 500)]
    [InlineData(WireCodes.DeleteFailed, 500)]
    public void StatusForRegionError_MapsEveryRegionWireCode(string code, int expectedStatus)
    {
        Assert.Equal(expectedStatus, BridgeServer.StatusForRegionError(code));
    }

    [Fact]
    public void WireCodes_DoNotLeakAsLiterals()
    {
        var offenders = new List<string>();
        foreach (var file in SourceFiles("src/TShock.AgentBridge"))
        {
            var text = File.ReadAllText(file);
            foreach (var code in WireCodes.All)
            {
                if (text.Contains($"\"{code}\"", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetRelativePath(RepoRoot, file)}: \"{code}\"");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Use WireCodes constants instead of string literals:" + Environment.NewLine +
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
            $"Documented but not in WireCodes.All: [{string.Join(", ", extra)}].");
    }

    private static IEnumerable<string> SourceFiles(string relativePath)
    {
        var root = Path.Combine(RepoRoot, relativePath);
        return Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsUnder(path, "obj") && !IsUnder(path, "bin"))
            .Where(path => Path.GetFileName(path) != "WireCodes.cs");
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
