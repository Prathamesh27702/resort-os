using Resort.Domain.Payments;

namespace Resort.Tests.Unit;

/// <summary>Sprint 10 — payment arithmetic, the status machine and the card-number guard. The database checks the same
/// limits (fn_payment_money_check, payment_status_transitions, ck_payments_no_card_number).</summary>
public class PaymentUnitTests
{
    [Theory]
    [InlineData(5000, 0, 5000)]
    [InlineData(5000, 1500, 3500)]
    [InlineData(5000, 5000, 0)]
    [InlineData(5000, 6000, 0)]   // never below 0
    public void What_can_be_refunded_is_captured_minus_already_refunded(decimal amount, decimal refunded, decimal left) =>
        Assert.Equal(left, PaymentMath.Refundable(amount, refunded));

    [Theory]
    [InlineData(5000, 0, 0, 5000)]
    [InlineData(5000, 3000, 0, 2000)]
    [InlineData(5000, 3000, 500, 1500)]
    [InlineData(5000, 5000, 0, 0)]
    public void On_account_is_what_no_folio_has_and_no_refund_took(decimal amount, decimal allocated, decimal fromAccount, decimal onAccount) =>
        Assert.Equal(onAccount, PaymentMath.OnAccount(amount, allocated, fromAccount));

    [Theory]
    [InlineData(4000, 1500, "PARTIALLY_REFUNDED")]
    [InlineData(4000, 4000, "REFUNDED")]
    public void A_refund_leaves_the_payment_partly_or_fully_refunded(decimal amount, decimal refunded, string status) =>
        Assert.Equal(status, PaymentMath.StatusAfterRefund(amount, refunded));

    [Theory]
    [InlineData(5000, 4950, -50)]   // the demo: ₹50 short
    [InlineData(5000, 5000, 0)]
    [InlineData(5000, 5020, 20)]
    public void The_variance_is_counted_minus_expected(decimal expected, decimal counted, decimal variance) =>
        Assert.Equal(variance, PaymentMath.Variance(expected, counted));

    [Fact]
    public void The_status_machine_matches_the_brief()
    {
        Assert.True(PaymentStatuses.CanMove(null, "CAPTURED"));
        Assert.True(PaymentStatuses.CanMove("PENDING", "AUTHORIZED"));
        Assert.True(PaymentStatuses.CanMove("AUTHORIZED", "CAPTURED"));
        Assert.True(PaymentStatuses.CanMove("CAPTURED", "PARTIALLY_REFUNDED"));
        Assert.True(PaymentStatuses.CanMove("PARTIALLY_REFUNDED", "REFUNDED"));
        Assert.True(PaymentStatuses.CanMove("CAPTURED", "CANCELLED"));
        Assert.False(PaymentStatuses.CanMove(null, "REFUNDED"));
        Assert.False(PaymentStatuses.CanMove("REFUNDED", "CAPTURED"));
        Assert.False(PaymentStatuses.CanMove("FAILED", "CAPTURED"));
        Assert.False(PaymentStatuses.CanMove("PARTIALLY_REFUNDED", "CANCELLED"));   // part refunded: refund the rest instead
        Assert.All(PaymentStatuses.All, s => Assert.True(PaymentStatuses.Transitions.ContainsKey(s)));
    }

    [Theory]
    [InlineData("4111 1111 1111 1111", true)]          // Visa test number
    [InlineData("5555-5555-5555-4444", true)]          // Mastercard test number
    [InlineData("card 378282246310005 exp 12/29", true)]   // Amex, inside a note
    [InlineData("4111 1111 1111 1112", false)]         // fails the Luhn check
    [InlineData("412345678901", false)]                // a 12-digit UPI reference
    [InlineData("HDFCR52026102012345678", false)]      // an RTGS UTR: 17 digits, but not Luhn-valid
    [InlineData("Approval 0A12BC RRN 123456789012", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void Something_that_looks_like_a_card_number_is_spotted(string? text, bool card) =>
        Assert.Equal(card, PaymentMath.LooksLikeCardNumber(text));

    [Theory]
    [InlineData("0A12BC", 2)]
    [InlineData("RRN 123456789012", 12)]
    [InlineData("4111 1111 1111 1111", 16)]   // spaces and dashes don't break a run
    [InlineData("12-34 ab 567", 4)]
    public void The_longest_digit_run_ignores_spaces_and_dashes(string text, int run) =>
        Assert.Equal(run, PaymentMath.LongestDigitRun(text));

    [Fact]
    public void Luhn_accepts_valid_numbers_only()
    {
        Assert.True(PaymentMath.Luhn("79927398713"));
        Assert.False(PaymentMath.Luhn("79927398710"));
    }
}
