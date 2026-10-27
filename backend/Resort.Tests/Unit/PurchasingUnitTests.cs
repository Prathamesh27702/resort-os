using Resort.Domain.Purchasing;

namespace Resort.Tests.Unit;

/// <summary>Sprint 16 — purchase arithmetic that needs no database: line value and GST (CGST + SGST inside a state, IGST
/// across), the approval limit, what is still to come in, and the status of an order and a supplier invoice.</summary>
public class PurchasingUnitTests
{
    [Theory]
    [InlineData(20, 40, 800)]
    [InlineData(12.5, 38.75, 484.38)]   // 484.375 rounds half away from zero
    [InlineData(0.333, 3, 1)]           // 0.999
    public void A_line_is_quantity_times_price_to_the_paisa(decimal quantity, decimal price, decimal taxable) =>
        Assert.Equal(taxable, PurchaseMath.Taxable(quantity, price));

    [Fact]
    public void Inside_the_state_GST_is_split_into_CGST_and_SGST_and_across_states_it_is_IGST()
    {
        Assert.Equal((21m, 21m, 0m), PurchaseMath.Tax(840m, 5m, intraState: true));
        Assert.Equal((7.5m, 7.5m, 0m), PurchaseMath.Tax(300m, 5m, intraState: true));
        Assert.Equal((0m, 0m, 270m), PurchaseMath.Tax(1500m, 18m, intraState: false));
        Assert.Equal((0.01m, 0.01m, 0m), PurchaseMath.Tax(0.85m, 3m, intraState: true));   // 0.01275 each → 0.01
        Assert.Equal((0m, 0m, 0m), PurchaseMath.Tax(500m, 0m, intraState: true));
    }

    [Theory]
    [InlineData(10000, 10000, false)]
    [InlineData(10000.01, 10000, true)]
    [InlineData(0, 0, false)]
    public void An_order_needs_approval_only_when_its_total_is_over_the_limit(decimal total, decimal limit, bool needs) =>
        Assert.Equal(needs, PurchaseMath.NeedsApproval(total, limit));

    [Fact]
    public void An_order_is_received_when_every_line_has_come_in()
    {
        Assert.Equal(PurchaseOrderStatuses.PartlyReceived, PurchaseMath.StatusAfterReceipt([(20m, 12m), (10m, 10m)]));
        Assert.Equal(PurchaseOrderStatuses.Received, PurchaseMath.StatusAfterReceipt([(20m, 20m), (10m, 10m)]));
        Assert.Equal(8m, PurchaseMath.Remaining(20m, 12m));
    }

    [Fact]
    public void A_supplier_invoice_is_unpaid_part_paid_or_paid()
    {
        Assert.Equal(SupplierInvoiceStatuses.Unpaid, PurchaseMath.InvoiceStatus(1197m, 0m));
        Assert.Equal(SupplierInvoiceStatuses.PartPaid, PurchaseMath.InvoiceStatus(1197m, 500m));
        Assert.Equal(SupplierInvoiceStatuses.Paid, PurchaseMath.InvoiceStatus(1197m, 1197m));
    }

    [Fact]
    public void Only_the_GST_rates_in_use_are_allowed() =>
        Assert.Equal([0m, 0.25m, 3m, 5m, 12m, 18m, 28m, 40m], PurchaseMath.GstRates);
}
