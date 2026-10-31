using System.Net;
using System.Text;

namespace TShock.MCP.Server.Tests.TestDoubles;

/// <summary>
/// Deterministic fake transport: every response comes from a caller-supplied
/// delegate, so no test ever touches the network.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

    internal StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        _handler = handler;
    }

    internal int CallCount { get; private set; }

    internal Uri? LastRequestUri { get; private set; }

    internal static StubHttpMessageHandler Respond(
        string body,
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string mediaType = "application/json") =>
        new((_, _) => Task.FromResult(BuildResponse(body, statusCode, mediaType)));

    internal static StubHttpMessageHandler Throws(Exception exception) =>
        new((_, _) => Task.FromException<HttpResponseMessage>(exception));

    internal static HttpResponseMessage BuildResponse(
        string body,
        HttpStatusCode statusCode,
        string mediaType = "application/json") =>
        new(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType)
        };

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        CallCount++;
        LastRequestUri = request.RequestUri;
        return _handler(request, cancellationToken);
    }
}
