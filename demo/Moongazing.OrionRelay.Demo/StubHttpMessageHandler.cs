namespace Moongazing.OrionRelay.Demo;

using System.Net;

/// <summary>
/// In-memory <see cref="HttpMessageHandler"/> that returns scripted outcomes instead of touching
/// the network, so the retry/backoff loop, telemetry, and observer can be exercised deterministically.
/// Each queued step is either an HTTP status to return or a transport fault to throw; once the
/// script is exhausted the final step repeats.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Step> steps;
    private Step last;

    public StubHttpMessageHandler(IEnumerable<Step> script)
    {
        steps = new Queue<Step>(script);
        last = steps.Count > 0 ? steps.Peek() : Step.Responds(HttpStatusCode.OK);
    }

    /// <summary>Total calls the handler received (one per HTTP attempt).</summary>
    public int CallCount { get; private set; }

    /// <summary>The signature header value seen on the most recent request, if any.</summary>
    public string? LastSignatureHeader { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;

        if (request.Headers.TryGetValues("Orion-Signature", out var values))
        {
            LastSignatureHeader = values.FirstOrDefault();
        }

        var step = steps.Count > 0 ? steps.Dequeue() : last;
        last = step;

        if (step.Fault is not null)
        {
            throw new HttpRequestException(step.Fault);
        }

        return Task.FromResult(new HttpResponseMessage(step.Status!.Value)
        {
            RequestMessage = request,
        });
    }

    public readonly record struct Step(HttpStatusCode? Status, string? Fault)
    {
        public static Step Responds(HttpStatusCode status) => new(status, null);

        public static Step Transport(string message) => new(null, message);
    }
}
