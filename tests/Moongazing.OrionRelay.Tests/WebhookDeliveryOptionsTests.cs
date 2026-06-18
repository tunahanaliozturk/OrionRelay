namespace Moongazing.OrionRelay.Tests;

using System.Net;
using System.Text;

using Moongazing.OrionRelay.Delivery;
using Moongazing.OrionRelay.Diagnostics;

using Xunit;

/// <summary>
/// Coverage for <see cref="WebhookDeliveryOptions"/>. Validation is internal, so it is exercised
/// through the public surface that triggers it: the dispatcher constructor.
/// </summary>
public sealed class WebhookDeliveryOptionsTests
{
    private static WebhookDispatcher Construct(WebhookDeliveryOptions options)
    {
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Status(HttpStatusCode.OK));
        return new WebhookDispatcher(
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            options,
            new WebhookDiagnostics());
    }

    [Fact]
    public void Defaults_are_sane_and_valid()
    {
        var options = new WebhookDeliveryOptions();

        Assert.Equal(4, options.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(1), options.BaseDelay);
        Assert.Equal(TimeSpan.FromSeconds(30), options.MaxDelay);
        Assert.Equal(TimeSpan.FromSeconds(30), options.RequestTimeout);
        Assert.Equal("Orion-Signature", options.SignatureHeader);

        // The default set constructs a dispatcher without throwing.
        _ = Construct(options);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void MaxAttempts_below_one_is_rejected(int maxAttempts)
    {
        var options = new WebhookDeliveryOptions { MaxAttempts = maxAttempts };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Construct(options));
        Assert.Equal("MaxAttempts", ex.ParamName);
    }

    [Fact]
    public void MaxAttempts_of_one_is_accepted()
    {
        _ = Construct(new WebhookDeliveryOptions { MaxAttempts = 1 });
    }

    [Fact]
    public void Negative_base_delay_is_rejected()
    {
        var options = new WebhookDeliveryOptions { BaseDelay = TimeSpan.FromMilliseconds(-1) };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Construct(options));
        Assert.Equal("BaseDelay", ex.ParamName);
    }

    [Fact]
    public void Max_delay_below_base_delay_is_rejected()
    {
        var options = new WebhookDeliveryOptions
        {
            BaseDelay = TimeSpan.FromSeconds(10),
            MaxDelay = TimeSpan.FromSeconds(5),
        };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Construct(options));
        Assert.Equal("MaxDelay", ex.ParamName);
    }

    [Fact]
    public void Max_delay_equal_to_base_delay_is_accepted()
    {
        _ = Construct(new WebhookDeliveryOptions
        {
            BaseDelay = TimeSpan.FromSeconds(5),
            MaxDelay = TimeSpan.FromSeconds(5),
        });
    }

    [Fact]
    public void Zero_base_delay_is_accepted()
    {
        // Zero is not negative, so it passes validation: an opt-out of backoff.
        _ = Construct(new WebhookDeliveryOptions { BaseDelay = TimeSpan.Zero });
    }

    [Fact]
    public void RequestTimeout_is_not_validated_and_any_value_constructs()
    {
        // Documents that RequestTimeout has no guard rail; a zero or negative value is the
        // caller's responsibility (it would cancel the attempt immediately).
        _ = Construct(new WebhookDeliveryOptions { RequestTimeout = TimeSpan.Zero });
    }

    [Fact]
    public async Task A_custom_signature_header_name_is_honoured()
    {
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Status(HttpStatusCode.OK));
        using var diagnostics = new WebhookDiagnostics();
        var options = new WebhookDeliveryOptions { SignatureHeader = "X-My-Sig" };
        var dispatcher = new WebhookDispatcher(
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            options,
            diagnostics,
            new Moongazing.OrionRelay.Signing.WebhookSigner("secret"));

        await dispatcher.DispatchAsync(new WebhookMessage
        {
            Endpoint = new Uri("https://example.test/hook"),
            Body = Encoding.UTF8.GetBytes("{}"),
        });

        var request = Assert.Single(handler.Requests);
        Assert.True(request.Headers.Contains("X-My-Sig"));
        Assert.False(request.Headers.Contains("Orion-Signature"));
    }
}
