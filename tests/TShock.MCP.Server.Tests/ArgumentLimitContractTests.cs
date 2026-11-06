using System.ComponentModel.DataAnnotations;
using System.Reflection;
using ModelContextProtocol.Server;
using TShock.McpServer.Tools;

namespace TShock.MCP.Server.Tests;

/// <summary>
/// Prevents a new string argument from reaching the network without a limit.
/// The table is the contract; the reflection pass is the guard.
/// </summary>
public sealed class ArgumentLimitContractTests
{
    private static readonly Dictionary<(string Tool, string Parameter), int> Expected = new()
    {
        [("broadcast", "message")] = ArgumentLimits.BroadcastMessage,
        [("kick_player", "player")] = ArgumentLimits.PlayerName,
        [("kick_player", "reason")] = ArgumentLimits.KickReason,
        [("kill_player", "player")] = ArgumentLimits.PlayerName,
        [("kill_player", "from")] = ArgumentLimits.KillSource,
        [("mute_player", "player")] = ArgumentLimits.PlayerName,
        [("player_info", "player")] = ArgumentLimits.PlayerName,
        [("user_info", "user")] = ArgumentLimits.AccountName,
        [("user_info", "type")] = ArgumentLimits.LookupType,
        [("group_info", "group")] = ArgumentLimits.GroupName,
        [("region_info", "name")] = ArgumentLimits.RegionName,
        [("region_create", "name")] = ArgumentLimits.RegionName,
        [("region_create", "owner")] = ArgumentLimits.RegionOwner,
        [("region_delete", "name")] = ArgumentLimits.RegionName
    };

    [Fact]
    public void EveryStringToolParameterDeclaresItsContractLimit()
    {
        var actual = new Dictionary<(string Tool, string Parameter), int>();

        foreach (var type in typeof(ReadTools).Assembly.GetTypes())
        {
            if (type.GetCustomAttribute<McpServerToolTypeAttribute>() is null)
            {
                continue;
            }

            foreach (var method in type.GetMethods(
                         BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static |
                         BindingFlags.DeclaredOnly))
            {
                var tool = method.GetCustomAttribute<McpServerToolAttribute>();
                if (tool is null)
                {
                    continue;
                }

                var toolName = tool.Name ?? method.Name;
                foreach (var parameter in method.GetParameters())
                {
                    if (parameter.ParameterType != typeof(string))
                    {
                        continue;
                    }

                    var maxLength = parameter.GetCustomAttribute<MaxLengthAttribute>()?.Length;
                    Assert.True(
                        maxLength is > 0,
                        $"{toolName}.{parameter.Name} must declare [MaxLength] with a positive value.");

                    actual[(toolName, parameter.Name!)] = maxLength!.Value;
                }
            }
        }

        foreach (var (key, expected) in Expected)
        {
            Assert.True(
                actual.TryGetValue(key, out var value),
                $"Missing limit for {key.Tool}.{key.Parameter}.");
            Assert.Equal(expected, value);
        }

        Assert.Equal(Expected.Count, actual.Count);
    }
}
