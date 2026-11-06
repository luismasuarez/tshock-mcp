using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TShock.McpServer.TShock;
using TShock.McpServer.Tools;

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

// The limiter is created before the host so the filter below can capture it
// directly. A missing dependency cannot turn the guard off at runtime.
var rateLimiter = new ToolRateLimiter(options.RateLimit, TimeProvider.System);

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

// The bridge is optional: when it is not configured the client is still
// registered and every bridge tool answers bridge_unavailable, so the agent gets
// an explanation instead of a missing tool.
builder.Services.AddSingleton<IBridgeClient>(services =>
{
    var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = options.Bridge?.Timeout ?? TimeSpan.FromSeconds(10)
    };

    if (options.Bridge is { } bridge)
    {
        http.BaseAddress = bridge.BaseUrl;
    }

    return new BridgeClient(http, options, services.GetRequiredService<ILogger<BridgeClient>>());
});

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    // Tool argument binding happens inside the handler pipeline (Microsoft.Extensions.AI
    // rejects missing or wrongly typed arguments before the method body runs). Translating
    // those failures here keeps the stable {"ok":false,"error":{...}} envelope instead of a
    // generic transport-level error.
    .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (request, cancellationToken) =>
    {
        // Count every tools/call, including invalid arguments, blocked writes and
        // multi-round-trip retries: they are invocations and a loop of them must not
        // reach TShock forever. Unknown tool names match no primitive and only spend
        // the global budget, so they cannot grow the per-tool dictionary.
        var (toolName, isWrite) = ToolRateLimitCatalog.Describe(request.MatchedPrimitive);
        var decision = rateLimiter.Acquire(toolName, isWrite);
        if (!decision.Allowed)
        {
            return CallToolErrors.RateLimited(request, decision);
        }

        try
        {
            return await next(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The client cancelled the call: let the SDK report it.
            throw;
        }
        catch (InputRequiredException)
        {
            // Multi-round-trip request (for example region_delete confirmation).
            // The SDK turns this exception into an input-required result and
            // re-invokes the tool with the client's answer; swallowing it here
            // would turn a confirmation prompt into a generic error.
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
        Build(ErrorCodes.InvalidArgument, $"Invalid arguments for tool '{toolName}'.");

    internal static CallToolResult Unexpected() =>
        Build(ErrorCodes.UnexpectedError, "The tool failed with an unexpected error.");

    internal static CallToolResult RateLimited(
        RequestContext<CallToolRequestParams> request,
        RateLimitDecision decision)
    {
        LogRateLimited(request, decision);

        return Build(
            ErrorCodes.RateLimited,
            $"The tool invocation rate limit was exceeded. Retry after {decision.RetryAfterMs} ms.",
            new JsonObject { ["retryAfterMs"] = decision.RetryAfterMs });
    }

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

    private static void LogRateLimited(
        RequestContext<CallToolRequestParams> request,
        RateLimitDecision decision)
    {
        var loggerFactory = request.Services?.GetService(typeof(ILoggerFactory)) as ILoggerFactory;
        if (loggerFactory is null)
        {
            return;
        }

        // Debug level: a denied loop must not turn the rate limiter into a log
        // flood. Never log arguments or the token.
        loggerFactory.CreateLogger(LoggerCategory).LogDebug(
            "Rate limit denied tool {Tool} scope={Scope} retryAfterMs={RetryAfterMs}",
            ToolName(request),
            decision.Scope,
            decision.RetryAfterMs);
    }

    private static CallToolResult Build(string code, string message, JsonNode? details = null) => new()
    {
        Content = [new TextContentBlock { Text = ToolResults.Error(code, message, details: details) }],
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
            foreach (var secret in new[] { options.Token, options.Bridge?.Token })
            {
                if (!string.IsNullOrEmpty(secret))
                {
                    message = message
                        .Replace(secret, "***", StringComparison.Ordinal)
                        .Replace(Uri.EscapeDataString(secret), "***", StringComparison.Ordinal);
                }
            }

            foreach (var baseUrl in new[] { options.BaseUrl, options.Bridge?.BaseUrl })
            {
                if (baseUrl is null)
                {
                    continue;
                }

                message = message
                    .Replace(baseUrl.ToString(), "<base-url>", StringComparison.Ordinal)
                    .Replace(baseUrl.GetLeftPart(UriPartial.Authority), "<base-url>", StringComparison.Ordinal);
            }
        }

        return message.Length <= MaxLoggedMessageLength
            ? message
            : string.Concat(message.AsSpan(0, MaxLoggedMessageLength), "...");
    }
}
