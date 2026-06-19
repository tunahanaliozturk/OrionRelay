using Moongazing.OrionRelay.Demo;
using Moongazing.OrionRelay.Diagnostics;

Console.WriteLine();
Console.WriteLine("###############################################################");
Console.WriteLine("#                                                             #");
Console.WriteLine("#   OrionRelay - runnable demo (no real network)             #");
Console.WriteLine("#   Outbound webhook delivery: HMAC-SHA256 signing + retries  #");
Console.WriteLine("#                                                             #");
Console.WriteLine("###############################################################");

// 1. Sender-side signing.
SigningDemo.Run();

// 2. Receiver-side verification, including tamper and replay rejection.
VerificationDemo.Run();

// 3. Real dispatcher over an in-memory stub: retries, backoff, fail-fast, observer hook.
using (var deliveryDiagnostics = new WebhookDiagnostics())
{
    await RetryDemo.RunAsync(deliveryDiagnostics);
}

// 4. Per-attempt telemetry captured through a MeterListener.
await TelemetryDemo.RunAsync();

// 5. Exhausted deliveries routed to a bounded in-memory dead-letter sink.
using (var deadLetterDiagnostics = new WebhookDiagnostics())
{
    await DeadLetterDemo.RunAsync(deadLetterDiagnostics);
}

DemoConsole.Banner("Demo complete");
Console.WriteLine("   All feature demos ran to completion without touching the network.");
Console.WriteLine();
