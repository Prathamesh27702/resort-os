using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Resort.Application.Common;
using Resort.Application.Notifications;
using Resort.Infrastructure.Notifications;

namespace Resort.Tests.Support;

/// <summary>The real API with the fake WhatsApp and the fake email provider (no keys set, so the API uses them) and a clock
/// a test can pin. Tests run the jobs and the dispatcher themselves (both background services are off), and deliver the fake
/// WhatsApp's signed status webhooks to the real webhook endpoint, duplicated, out of order or tampered.</summary>
public sealed class NotificationsFactory : ResortApiFactory
{
    public PinnedClock Clock { get; } = new();
    public FakeWhatsAppServer WhatsApp => Services.GetRequiredService<FakeWhatsAppServer>();
    public FakeEmailServer Email => Services.GetRequiredService<FakeEmailServer>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Notifications:PublicUrl", "https://book.test.local");
        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(Clock);
        });
    }

    /// <summary>Runs the dispatcher once, as the background service would: sends up to 100 due messages.</summary>
    public async Task<int> DispatchAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchAsync(100, CancellationToken.None);
    }

    /// <summary>Runs the scheduler once for a property, as the background service does every minute.</summary>
    public async Task<int> SchedulerAsync(Guid propertyId)
    {
        await using var scope = Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<Outbox>();
        var p = await outbox.PropertyAsync(propertyId, CancellationToken.None);
        scope.ServiceProvider.GetRequiredService<SystemActor>().ActFor(p.OrganizationId);
        return await scope.ServiceProvider.GetRequiredService<NotificationJobs>().RunDueAsync(p, CancellationToken.None);
    }

    /// <summary>POSTs a webhook to /api/v1/webhooks/whatsapp as Meta would (no sign-in), with its own signature unless
    /// another is given ("" = no signature header).</summary>
    public async Task<HttpResponseMessage> DeliverAsync(FakeWhatsAppWebhook hook, string? signature = null, byte[]? body = null)
    {
        using var http = CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/whatsapp") { Content = new ByteArrayContent(body ?? hook.Body) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (signature != "") req.Headers.Add("X-Hub-Signature-256", signature ?? hook.Signature);
        return await http.SendAsync(req);
    }
}
