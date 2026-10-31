using Microsoft.Extensions.Logging.Abstractions;
using TShock.McpServer.TShock;

namespace TShock.MCP.Server.Tests.TestDoubles;

internal static class TestOptions
{
    internal const string Token = "test-token";

    internal static TShockOptions Create(
        string baseUrl = "http://127.0.0.1:7878/",
        string token = Token,
        bool allowWrite = false,
        bool showIps = false,
        TimeSpan? timeout = null,
        string? auditLogPath = null) =>
        new()
        {
            BaseUrl = new Uri(baseUrl, UriKind.Absolute),
            Token = token,
            AllowWrite = allowWrite,
            ShowIps = showIps,
            Timeout = timeout ?? TimeSpan.FromSeconds(15),
            AuditLogPath = auditLogPath
        };

    internal static TShockClient CreateClient(StubHttpMessageHandler handler, TShockOptions options) =>
        new(new HttpClient(handler, disposeHandler: false), options, NullLogger<TShockClient>.Instance);
}
