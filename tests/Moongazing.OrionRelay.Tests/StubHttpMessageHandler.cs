namespace Moongazing.OrionRelay.Tests;

using System.Net;

/// <summary>
/// A test handler that replays a scripted sequence of outcomes (a status code to return, or an
/// exception to throw) and records every request it sees. When the script is exhausted it keeps
/// replaying the last entry.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage>[] steps;
    private int index;

    public StubHttpMessageHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] steps)
    {
        this.steps = steps;
    }

    public List<HttpRequestMessage> Requests { get; } = [];

    public static Func<HttpRequestMessage, HttpResponseMessage> Status(HttpStatusCode code) =>
        _ => new HttpResponseMessage(code);

    public static Func<HttpRequestMessage, HttpResponseMessage> Throw(Exception ex) =>
        _ => throw ex;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var step = steps[Math.Min(index, steps.Length - 1)];
        index++;
        try
        {
            return Task.FromResult(step(request));
        }
        catch (Exception ex)
        {
            return Task.FromException<HttpResponseMessage>(ex);
        }
    }
}
