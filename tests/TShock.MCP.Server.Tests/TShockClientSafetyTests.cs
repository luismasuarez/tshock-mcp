using System.Net;
using Microsoft.Extensions.Logging;
using TShock.MCP.Server.Tests.TestDoubles;
using TShock.McpServer.TShock;

namespace TShock.MCP.Server.Tests;

/// <summary>
/// Covers the client's diagnostics and audit-file handling, which are not part
/// of the response mapping exercised by <see cref="TShockClientTests"/>.
/// </summary>
public sealed class TShockClientSafetyTests
{
    private const string Operation = "server_status";
    private const string Path = "/v2/server/status";

    [Fact]
    public async Task CallAsync_UnexpectedExceptionLogsWarningWithoutTokenBaseUrlOrStackTrace()
    {
        const string token = "diag-secret-token";
        var options = TestOptions.Create(token: token);
        var logger = new RecordingLogger<TShockClient>();
        var exception = new InvalidOperationException(
            $"Unexpected failure calling {options.BaseUrl} with token={token}");
        var handler = StubHttpMessageHandler.Throws(exception);
        var client = new TShockClient(new HttpClient(handler, disposeHandler: false), options, logger);

        var result = await client.CallAsync(Operation, Path);

        Assert.False(result.Ok);
        Assert.Equal("unexpected_error", result.ErrorCode);
        Assert.Equal(nameof(InvalidOperationException), result.ErrorMessage);

        var warning = Assert.Single(
            logger.Entries,
            entry => entry.Level == LogLevel.Warning &&
                     entry.Message.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));

        Assert.Contains("***", warning.Message, StringComparison.Ordinal);
        Assert.Contains("<base-url>", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(token, warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(options.BaseUrl.ToString(), warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallAsync_RestrictsNewAuditFileToOwnerOnPosix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var auditPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"tshock-audit-mode-{Guid.NewGuid():N}.jsonl");

        try
        {
            var handler = StubHttpMessageHandler.Respond("""{"ok":true}""");
            var options = TestOptions.Create(auditLogPath: auditPath);
            var client = TestOptions.CreateClient(handler, options);

            var result = await client.CallAsync(Operation, Path);

            Assert.True(result.Ok);
            Assert.True(File.Exists(auditPath));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(auditPath));
        }
        finally
        {
            if (File.Exists(auditPath))
            {
                File.Delete(auditPath);
            }
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        internal List<LogEntry> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message);
}
