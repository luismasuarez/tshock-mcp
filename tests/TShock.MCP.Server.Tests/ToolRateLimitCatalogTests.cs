using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using TShock.MCP.Server.Tests.TestDoubles;
using TShock.McpServer.TShock;
using TShock.McpServer.Tools;

namespace TShock.MCP.Server.Tests;

/// <summary>
/// The rate limiter classifies tools from the annotation the SDK already
/// matched. This test builds the real registration so a new tool without a
/// read-only hint, or one whose annotation disagrees with its handler, fails
/// before it can reach the limiter.
/// </summary>
public sealed class ToolRateLimitCatalogTests
{
    [Fact]
    public void EveryRegisteredTool_IsClassifiedFromItsAnnotation()
    {
        var tools = ReadTools();

        Assert.Equal(23, tools.Count);
        Assert.All(tools, tool =>
        {
            var (name, isWrite) = ToolRateLimitCatalog.Describe(tool);
            var readOnly = tool.ProtocolTool.Annotations?.ReadOnlyHint;

            Assert.Equal(tool.ProtocolTool.Name, name);
            Assert.True(readOnly.HasValue, $"{name} is missing readOnlyHint");
            Assert.Equal(readOnly == false, isWrite);
        });
    }

    [Fact]
    public void UnmatchedPrimitive_HasNoToolBucketAndIsNotWrite()
    {
        var (name, isWrite) = ToolRateLimitCatalog.Describe(null);

        Assert.Null(name);
        Assert.False(isWrite);
    }

    private static IReadOnlyList<McpServerTool> ReadTools()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TestOptions.Create(allowWrite: true));
        services.AddSingleton<ITShockClient>(new RecordingTShockClient());
        services.AddSingleton<IBridgeClient>(new RecordingBridgeClient());
        services.AddMcpServer().WithToolsFromAssembly(typeof(ReadTools).Assembly);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;

        return options.ToolCollection!.ToArray();
    }
}
