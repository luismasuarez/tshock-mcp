using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TShock.MCP.Server.Tests.TestDoubles;
using TShock.McpServer.TShock;
using TShock.McpServer.Tools;

namespace TShock.MCP.Server.Tests;

/// <summary>
/// Asserts the annotation contract clients see in tools/list. Every tool must
/// declare the four hints explicitly: the SDK omits the ones it was not given,
/// and it defaults destructive and openWorld to true, which would misrepresent
/// the read-only tools.
/// </summary>
public sealed class ToolAnnotationTests
{
    private static readonly IReadOnlyDictionary<
        string,
        (bool ReadOnly, bool Destructive, bool Idempotent, bool OpenWorld)> Expected =
        new Dictionary<string, (bool, bool, bool, bool)>(StringComparer.Ordinal)
        {
            // Read tools: no side effects, same result for the same input, bounded domain.
            ["server_status"] = (true, false, true, false),
            ["world_info"] = (true, false, true, false),
            ["players_list"] = (true, false, true, false),
            ["player_info"] = (true, false, true, false),
            ["server_motd"] = (true, false, true, false),
            ["server_rules"] = (true, false, true, false),
            ["users_list"] = (true, false, true, false),
            ["user_info"] = (true, false, true, false),
            ["groups_list"] = (true, false, true, false),
            ["group_info"] = (true, false, true, false),
            ["bans_list"] = (true, false, true, false),
            ["ban_info"] = (true, false, true, false),
            ["events_since"] = (true, false, true, false),
            ["regions_list"] = (true, false, true, false),
            ["region_info"] = (true, false, true, false),

            // Write tools: the values match the handler's actual behaviour.
            ["broadcast"] = (false, false, false, false),
            ["kick_player"] = (false, true, false, false),
            ["kill_player"] = (false, true, false, false),
            ["mute_player"] = (false, true, false, false),
            ["save_world"] = (false, false, true, false),
            ["reload_config"] = (false, false, true, false),
            ["region_create"] = (false, false, false, false),
            ["region_delete"] = (false, true, false, false)
        };

    [Fact]
    public void EveryTool_DeclaresAllFourHints()
    {
        var annotations = ReadAnnotations();

        Assert.Equal(Expected.Count, annotations.Count);
        Assert.Equal(
            Expected.Keys.OrderBy(name => name, StringComparer.Ordinal),
            annotations.Keys.OrderBy(name => name, StringComparer.Ordinal));

        foreach (var (name, annotation) in annotations)
        {
            Assert.True(annotation.ReadOnlyHint.HasValue, $"{name} is missing readOnlyHint");
            Assert.True(annotation.DestructiveHint.HasValue, $"{name} is missing destructiveHint");
            Assert.True(annotation.IdempotentHint.HasValue, $"{name} is missing idempotentHint");
            Assert.True(annotation.OpenWorldHint.HasValue, $"{name} is missing openWorldHint");
        }
    }

    [Fact]
    public void EveryTool_DeclaresTheHintsItsHandlerBehavesLike()
    {
        var annotations = ReadAnnotations();

        foreach (var (name, expected) in Expected)
        {
            Assert.True(annotations.TryGetValue(name, out var actual), $"{name} is not registered");
            Assert.Equal(expected.ReadOnly, actual!.ReadOnlyHint!.Value);
            Assert.Equal(expected.Destructive, actual.DestructiveHint!.Value);
            Assert.Equal(expected.Idempotent, actual.IdempotentHint!.Value);
            Assert.Equal(expected.OpenWorld, actual.OpenWorldHint!.Value);
        }
    }

    /// <summary>
    /// Builds the same registration the server uses, so the assertions cover what
    /// is serialized and not just the attribute values.
    /// </summary>
    private static IReadOnlyDictionary<string, ToolAnnotations> ReadAnnotations()
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
            tool => tool.ProtocolTool.Annotations!,
            StringComparer.Ordinal);
    }
}
