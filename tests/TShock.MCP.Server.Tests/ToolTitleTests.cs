using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TShock.MCP.Server.Tests.TestDoubles;
using TShock.McpServer.TShock;
using TShock.McpServer.Tools;

namespace TShock.MCP.Server.Tests;

/// <summary>
/// Asserts the display-title contract clients see in <c>tools/list</c>. The
/// expected table is independent from the production constants so a changed
/// label fails the suite until the contract is updated on purpose.
/// </summary>
public sealed class ToolTitleTests
{
    /// <summary>Long enough for a clear imperative label, short enough for a button.</summary>
    private const int MaxTitleLength = 40;

    /// <summary>
    /// English, imperative verb + object, sentence case. Acronyms and jargon
    /// are rejected on purpose: the title is UI copy, not a description.
    /// </summary>
    private const string DisplayPolicy = "^[A-Z][a-z0-9]*( [a-z0-9]+)*$";

    private static readonly IReadOnlyDictionary<string, string> Expected =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["server_status"] = "Get server status",
            ["world_info"] = "Get world details",
            ["players_list"] = "List players",
            ["player_info"] = "Get player details",
            ["server_motd"] = "Get message of the day",
            ["server_rules"] = "Get server rules",
            ["users_list"] = "List accounts",
            ["user_info"] = "Get account details",
            ["groups_list"] = "List groups",
            ["group_info"] = "Get group details",
            ["bans_list"] = "List bans",
            ["ban_info"] = "Get ban details",
            ["events_since"] = "Get server events",
            ["regions_list"] = "List regions",
            ["region_info"] = "Get region details",
            ["broadcast"] = "Send message to all",
            ["kick_player"] = "Kick player",
            ["kill_player"] = "Kill player",
            ["mute_player"] = "Mute player",
            ["save_world"] = "Save world",
            ["reload_config"] = "Reload configuration",
            ["region_create"] = "Create region",
            ["region_delete"] = "Delete region"
        };

    [Fact]
    public void EveryTool_DeclaresTheExpectedTitle()
    {
        var tools = ReadTools();

        Assert.Equal(Expected.Count, tools.Count);
        Assert.Equal(
            Expected.Keys.OrderBy(name => name, StringComparer.Ordinal),
            tools.Keys.OrderBy(name => name, StringComparer.Ordinal));

        foreach (var (name, expected) in Expected)
        {
            Assert.True(tools.TryGetValue(name, out var tool), $"{name} is not registered");
            Assert.Equal(expected, tool!.Title);
        }
    }

    [Fact]
    public void EveryTitle_FollowsTheDisplayPolicy()
    {
        var titles = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (name, tool) in ReadTools())
        {
            var title = tool.Title;

            Assert.False(string.IsNullOrWhiteSpace(title), $"{name} is missing a title");
            Assert.NotEqual(name, title);
            Assert.InRange(title!.Length, 1, MaxTitleLength);
            Assert.Matches(DisplayPolicy, title);
            Assert.DoesNotContain("  ", title, StringComparison.Ordinal);
            Assert.False(title.EndsWith('.'));
            Assert.True(titles.Add(title), $"{name} duplicates the title '{title}'");
        }
    }

    [Fact]
    public void EveryTitle_IsMirroredInAnnotationsForOlderClients()
    {
        foreach (var (name, tool) in ReadTools())
        {
            Assert.NotNull(tool.Annotations);
            Assert.Equal(tool.Title, tool.Annotations!.Title);
        }
    }

    /// <summary>
    /// Builds the same registration the server uses, so the assertions cover
    /// what <c>tools/list</c> serializes and not just the attribute values.
    /// </summary>
    private static IReadOnlyDictionary<string, Tool> ReadTools()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TestOptions.Create(allowWrite: true));
        services.AddSingleton<ITShockClient>(new RecordingTShockClient());
        services.AddSingleton<IBridgeClient>(new RecordingBridgeClient());
        services.AddMcpServer().WithToolsFromAssembly(typeof(ReadTools).Assembly);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;

        return options.ToolCollection!.ToDictionary(
            tool => tool.ProtocolTool.Name!,
            tool => tool.ProtocolTool,
            StringComparer.Ordinal);
    }
}
