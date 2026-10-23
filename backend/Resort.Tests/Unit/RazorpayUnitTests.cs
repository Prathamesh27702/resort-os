using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Resort.Domain.Payments;
using Resort.Infrastructure.Payments;

namespace Resort.Tests.Unit;

/// <summary>Sprint 11 — the pieces of Razorpay that need no server: the signatures (HMAC-SHA256, checked against values
/// computed with openssl), rupees and paise, how far along an online payment is (so out-of-order webhooks can't move it
/// back), and when the fake Razorpay is used.</summary>
public class RazorpayUnitTests
{
    private static RazorpayPaymentGateway Gateway(string? webhookSecret = "whsec_test", string? keySecret = "key_secret") =>
        new(new HttpClient(), Options.Create(new RazorpayOptions { KeyId = "rzp_test_x", KeySecret = keySecret, WebhookSecret = webhookSecret }),
            NullLogger<RazorpayPaymentGateway>.Instance);

    [Fact]
    public void The_webhook_signature_is_the_HMAC_SHA256_of_the_raw_body_with_the_webhook_secret()
    {
        var body = Encoding.UTF8.GetBytes("""{"event":"payment.captured"}""");
        const string expected = "4f463a57dd128675850163391f0311888616d57bccca75c774c9cdb28134f851";   // openssl dgst -sha256 -hmac whsec_test
        Assert.Equal(expected, RazorpayPaymentGateway.Hmac("whsec_test", body));
        var g = Gateway();
        Assert.True(g.VerifyWebhook(body, expected));
        Assert.True(g.VerifyWebhook(body, expected.ToUpperInvariant()));
        Assert.False(g.VerifyWebhook(Encoding.UTF8.GetBytes("""{"event":"payment.captured" }"""), expected));   // one space more
        Assert.False(g.VerifyWebhook(body, expected[..^1] + "0"));
        Assert.False(g.VerifyWebhook(body, null));
        Assert.False(g.VerifyWebhook(body, ""));
        Assert.False(Gateway(webhookSecret: null).VerifyWebhook(body, RazorpayPaymentGateway.Hmac("", body)));   // no secret: nothing passes
    }

    [Fact]
    public void Checkouts_signature_is_the_HMAC_of_order_and_payment_with_the_key_secret()
    {
        const string expected = "be3ac4fc04c433f1c5c9b199040d39bf759554b77032558c21d89231336aa314";
        Assert.True(Gateway().VerifyCheckout("order_IluGWxBm9U8zJ8", "pay_IH4NVgf4Dreq1l", expected));
        Assert.False(Gateway().VerifyCheckout("order_IluGWxBm9U8zJ8", "pay_other", expected));
    }

    [Theory]
    [InlineData(7000, 700000)]
    [InlineData(1, 100)]
    [InlineData(1499.99, 149999)]
    [InlineData(0.01, 1)]
    public void Amounts_are_whole_paise_at_the_gateway(decimal rupees, long paise)
    {
        Assert.Equal(paise, PaymentMath.ToPaise(rupees));
        Assert.Equal(rupees, PaymentMath.FromPaise(paise));
    }

    [Fact]
    public void An_online_payment_only_moves_forward_whatever_order_the_webhooks_come_in()
    {
        string?[] order = [null, "PENDING", "AUTHORIZED", "FAILED", "CAPTURED"];
        for (var i = 1; i < order.Length; i++)
            Assert.True(PaymentStatuses.GatewayRank(order[i]) > PaymentStatuses.GatewayRank(order[i - 1]));
        // captured before authorized: authorized is behind it; failed after captured: also behind it
        Assert.True(PaymentStatuses.GatewayRank("AUTHORIZED") < PaymentStatuses.GatewayRank("CAPTURED"));
        Assert.True(PaymentStatuses.GatewayRank("FAILED") < PaymentStatuses.GatewayRank("CAPTURED"));
        Assert.Equal(PaymentStatuses.GatewayRank("CAPTURED"), PaymentStatuses.GatewayRank("REFUNDED"));
        // the gateway may create a payment authorised, and a failed refund gives money back to it
        Assert.True(PaymentStatuses.CanMove(null, "AUTHORIZED"));
        Assert.Equal(["PARTIALLY_REFUNDED", "CAPTURED"], PaymentStatuses.GatewayReturns["REFUNDED"]);
        Assert.Equal(["CASH", "UPI", "CARD", "BANK_TRANSFER"], PaymentMethods.Desk);
        Assert.Equal("Online (Razorpay)", PaymentMethods.Words("ONLINE"));
    }

    [Theory]
    [InlineData(null, null, false, true)]           // no keys in development or tests: the fake
    [InlineData("rzp_test_abc", null, false, false)] // test keys: the real Razorpay (test mode)
    [InlineData(null, null, true, false)]            // production never uses the fake
    [InlineData("rzp_test_abc", "Fake", false, true)]
    [InlineData(null, "Live", false, false)]
    public void The_fake_is_used_without_keys_outside_production(string? keyId, string? mode, bool production, bool fake) =>
        Assert.Equal(fake, new RazorpayOptions { KeyId = keyId, Mode = mode }.UseFake(production));

    [Fact]
    public void The_fake_is_refused_in_production() =>
        Assert.Throws<InvalidOperationException>(() => new RazorpayOptions { Mode = "Fake" }.UseFake(production: true));
}
