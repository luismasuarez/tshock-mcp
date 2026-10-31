using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TShock.McpServer.TShock;

TShockOptions options;
try
{
    options = TShockOptions.FromEnvironment();
}
catch (ConfigurationException ex)
{
    Console.Error.WriteLine($"tshock-mcp: configuration error: {ex.Message}");
    return 1;
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    // Configuration is environment-only by design. Ignore appsettings.json and
    // command-line arguments so a host cannot silently change the security posture.
    Args = Array.Empty<string>(),
    DisableDefaults = true
});
builder.Configuration.AddEnvironmentVariables();

// MCP uses stdout for JSON-RPC. Every log line must go to stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(console =>
{
    console.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<ITShockClient>(services => new TShockClient(
    new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        BaseAddress = options.BaseUrl,
        Timeout = options.Timeout
    },
    options,
    services.GetRequiredService<ILogger<TShockClient>>()));

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    // Tool argument binding happens inside the handler pipeline (Microsoft.Extensions.AI
    // rejects missing or wrongly typed arguments before the method body runs). Translating
    // those failures here keeps the stable {"ok":false,"error":{...}} envelope instead of a
    // generic transport-level error.
    .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (request, cancellationToken) =>
    {
        try
        {
            return await next(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The client cancelled the call: let the SDK report it.
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or FormatException)
        {
            return CallToolErrors.InvalidArguments(CallToolErrors.ToolName(request));
        }
        catch (Exception ex)
        {
            CallToolErrors.LogUnexpected(request, ex);
            return CallToolErrors.Unexpected();
        }
    }))
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
return 0;

/// <summary>
/// Builds the stable error envelope from exceptions raised while invoking a tool and
/// keeps the diagnostic log free of secrets and of the full argument payload.
/// </summary>
internal static class CallToolErrors
{
    private const string LoggerCategory = "TShock.McpServer.Tools";
    private const int MaxLoggedMessageLength = 200;

    internal static string ToolName(RequestContext<CallToolRequestParams> request) =>
        request.MatchedPrimitive?.Id ?? "unknown";

    internal static CallToolResult InvalidArguments(string toolName) =>
        Build("invalid_argument", $"Invalid arguments for tool '{toolName}'.");

    internal static CallToolResult Unexpected() =>
        Build("unexpected_error", "The tool failed with an unexpected error.");

    internal static void LogUnexpected(RequestContext<CallToolRequestParams> request, Exception exception)
    {
        var loggerFactory = request.Services?.GetService(typeof(ILoggerFactory)) as ILoggerFactory;
        if (loggerFactory is null)
        {
            return;
        }

        var options = request.Services?.GetService(typeof(TShockOptions)) as TShockOptions;
        var logger = loggerFactory.CreateLogger(LoggerCategory);

        // Only the exception type and a sanitized, truncated message are logged:
        // never the token nor the full tool arguments.
        logger.LogError(
            "Tool {Tool} threw {ExceptionType}: {Message}",
            ToolName(request),
            exception.GetType().FullName,
            Sanitize(exception.Message, options));
    }

    private static CallToolResult Build(string code, string message) => new()
    {
        Content = [new TextContentBlock { Text = ToolResults.Error(code, message) }],
        IsError = true
    };

    private static string Sanitize(string? message, TShockOptions? options)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        if (options is not null)
        {
            if (!string.IsNullOrEmpty(options.Token))
            {
                message = message
                    .Replace(options.Token, "***", StringComparison.Ordinal)
                    .Replace(Uri.EscapeDataString(options.Token), "***", StringComparison.Ordinal);
            }

            message = message
                .Replace(options.BaseUrl.ToString(), "<base-url>", StringComparison.Ordinal)
                .Replace(options.BaseUrl.GetLeftPart(UriPartial.Authority), "<base-url>", StringComparison.Ordinal);
        }

        return message.Length <= MaxLoggedMessageLength
            ? message
            : string.Concat(message.AsSpan(0, MaxLoggedMessageLength), "...");
    }
}
