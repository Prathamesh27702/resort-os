using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Resort.Infrastructure.Payments;

namespace Resort.Tests.Support;

/// <summary>The real API with the fake Razorpay (no keys set, so the API uses it) answering quickly: retries wait about
/// a millisecond and a try that hangs gives up after a second. Tests drive the fake (a guest paying, a refund going
/// through) and deliver its signed webhooks to the real webhook endpoint, duplicated, out of order or tampered.</summary>
public sealed class RazorpayFactory : ResortApiFactory
{
    public FakeRazorpayServer Fake => Services.GetRequiredService<FakeRazorpayServer>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Razorpay:RetryBaseDelayMs", "1");
        builder.UseSetting("Razorpay:TimeoutSeconds", "1");
    }

    /// <summary>POSTs a webhook to /api/v1/webhooks/razorpay as Razorpay would (no sign-in), with its own signature unless
    /// another is given.</summary>
    public async Task<HttpResponseMessage> DeliverAsync(FakeWebhook hook, string? signature = null, byte[]? body = null)
    {
        using var http = CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/razorpay") { Content = new ByteArrayContent(body ?? hook.Body) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (signature != "") req.Headers.Add("X-Razorpay-Signature", signature ?? hook.Signature);
        req.Headers.Add("X-Razorpay-Event-Id", hook.EventId);
        return await http.SendAsync(req);
    }
}
