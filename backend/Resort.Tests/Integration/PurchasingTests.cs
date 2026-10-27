using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Resort.Contracts;
using Resort.Domain.Tax;
using Resort.Tests.Support;
using static Resort.Tests.Support.BookingKit;

namespace Resort.Tests.Integration;

/// <summary>Sprint 16 — suppliers and purchasing: the full purchase cycle (request → order → approval → goods received →
/// supplier invoice → payment) with stock updated, partial deliveries with what is still to come, the approval limit and
/// its second person, supplier GSTINs and input GST, cash paid from the drawer, and the database guards. Each test uses a
/// property of its own. Real API, real PostgreSQL.</summary>
public class PurchasingTests(ResortApiFactory app) : IClassFixture<ResortApiFactory>
{
    private static readonly string GoaSupplierGstin = Gst("30AAACF1234A1Z");
    private static readonly string MumbaiSupplierGstin = Gst("27AABCM5678B1Z");

    // ------------------------------------------------------------------ required demo: full purchase cycle for vegetables

    [Fact]
    public async Task A_vegetable_order_is_raised_approved_received_in_two_deliveries_invoiced_and_paid_and_stock_goes_up()
    {
        var (owner, pid) = await PropertyAsync(registered: true);
        var kitchenManager = await StaffAsync("RESTAURANT_MANAGER", pid);
        var buyer = await StaffAsync("PURCHASING", pid);
        var storekeeper = await StaffAsync("INVENTORY_MANAGER", pid);
        var accountant = await StaffAsync("ACCOUNTANT", pid);
        var stock = await StockAsync(owner, pid);
        await PutSettingAsync(owner, pid, "purchasing.approval_limit", 1000);
        var supplier = await SupplierAsync(owner, pid, "FRESH-VEG", "Mapusa Fresh Vegetables", GoaSupplierGstin);
        Assert.Equal(("30", "Goa (30)", 7), (supplier.StateCode, supplier.StateName, supplier.PaymentTermsDays));

        // The kitchen asks for vegetables.
        var request = await OkAsync<PurchaseRequestDto>(await kitchenManager.PostAsync($"{Url(pid)}/requests", new CreatePurchaseRequestRequest(stock.Store.Id, null,
            "For the weekend", [new PurchaseRequestLine(stock.Onion.Id, 20m, null), new PurchaseRequestLine(stock.Tomato.Id, 10m, "Ripe")])), HttpStatusCode.Created);
        Assert.Equal(("PR00001", "OPEN", 5m), (request.Number, request.Status, request.Items[0].OnHand));

        // The buyer raises the order from it: 20 kg onion at ₹40 and 10 kg tomato at ₹30, 5 % GST inside Goa = ₹1,155.
        var order = await OkAsync<PurchaseOrderDto>(await buyer.PostAsync($"{Url(pid)}/orders", new SavePurchaseOrderRequest(supplier.Id, stock.Store.Id, request.Id,
            null, null, [new SavePurchaseOrderLine(stock.Onion.Id, 20m, 40m, 5m), new SavePurchaseOrderLine(stock.Tomato.Id, 10m, 30m, 5m)], null)), HttpStatusCode.Created);
        Assert.Equal(("PO00001", "DRAFT", true, 1100m, 55m, 1155m, true, "PR00001"),
            (order.Number, order.Status, order.IntraState, order.Subtotal, order.TaxTotal, order.Total, order.NeedsApproval, order.RequestNumber));
        Assert.Equal("ORDERED", (await buyer.GetJsonAsync<List<PurchaseRequestDto>>($"{Url(pid)}/requests")).Single().Status);

        // Over the ₹1,000 limit: submitted, it waits for approval. The buyer can't approve; the owner (a second person) does.
        order = await OkAsync<PurchaseOrderDto>(await buyer.PostAsync($"{OrderUrl(pid, order.Id)}/submit", new PurchaseOrderStepRequest(null, order.Version)));
        Assert.Equal(("PENDING_APPROVAL", 1000m), (order.Status, order.ApprovalLimit));
        Assert.Equal(HttpStatusCode.Forbidden, (await buyer.PostAsync($"{OrderUrl(pid, order.Id)}/approve", new PurchaseOrderStepRequest(null, null))).StatusCode);
        var (code, _) = await ProblemAsync(await storekeeper.PostAsync($"{OrderUrl(pid, order.Id)}/receipts", Receive(order, (0, 12m))));
        Assert.Equal("INVALID_STATUS_TRANSITION", code);
        order = await OkAsync<PurchaseOrderDto>(await owner.PostAsync($"{OrderUrl(pid, order.Id)}/approve", new PurchaseOrderStepRequest("Weekend menu", order.Version)));
        Assert.Equal("APPROVED", order.Status);
        Assert.NotNull(order.ApprovedByName);

        // First delivery: 12 kg onion and all the tomato. 8 kg onion still to come.
        order = await OkAsync<PurchaseOrderDto>(await storekeeper.PostAsync($"{OrderUrl(pid, order.Id)}/receipts",
            Receive(order, (0, 12m), (1, 10m)) with { ChallanNumber = "DC-4411" }), HttpStatusCode.Created);
        Assert.Equal("PARTLY_RECEIVED", order.Status);
        Assert.Equal(new[] { (12m, 8m), (10m, 0m) }, order.Lines.Select(l => (l.Received, l.Remaining)));
        var grn = Assert.Single(order.Receipts);
        Assert.Equal(("GRN00001", 780m, "DC-4411"), (grn.Number, grn.Value, grn.ChallanNumber));
        Assert.Equal(order.Lines.Select(l => l.Id), grn.Lines.Select(l => l.OrderItemId));   // the order's line order, every time

        // Stock: onion 5 + 12 kg in the store at an average cost of (5 × 35 + 12 × 40) / 17; tomato 10 kg at ₹30.
        var inv = await owner.GetJsonAsync<InventoryDto>($"/api/v1/properties/{pid}/inventory");
        var onion = inv.Items.Single(i => i.Id == stock.Onion.Id);
        Assert.Equal((17m, 38.5294m), (onion.OnHand, onion.UnitCost));
        Assert.Equal((10m, 30m), (inv.Items.Single(i => i.Id == stock.Tomato.Id).OnHand, inv.Items.Single(i => i.Id == stock.Tomato.Id).UnitCost));
        var moved = (await owner.GetJsonAsync<StockMovementListDto>($"/api/v1/properties/{pid}/inventory/movements?itemId={stock.Onion.Id}")).Items[0];
        Assert.Equal(("RECEIVED", 12m, 40m, 480m, 17m, "STORE", "GRN00001 · PO00001"), (moved.Type, moved.Quantity, moved.UnitCost, moved.Value, moved.OnHandAfter, moved.LocationCode, moved.Source + " · " + moved.Note!.Split(" · ")[0]));
        Assert.Equal("PO00001 · Mapusa Fresh Vegetables", moved.Note);

        // Second delivery: the rest of the onion. Fully received; nothing more can come in.
        order = await OkAsync<PurchaseOrderDto>(await storekeeper.PostAsync($"{OrderUrl(pid, order.Id)}/receipts", Receive(order, (0, 8m))), HttpStatusCode.Created);
        Assert.Equal(("RECEIVED", 2), (order.Status, order.Receipts.Count));
        Assert.Equal("Everything on PO00001 has already come in.", (await ProblemAsync(await storekeeper.PostAsync($"{OrderUrl(pid, order.Id)}/receipts", Receive(order, (0, 1m))))).Message);

        // The accountant records the bill: onion billed at ₹42 (the order said ₹40). CGST and SGST 2.5 % each; input tax claimable.
        var bill = await OkAsync<SupplierInvoiceDto>(await accountant.PostAsync($"{OrderUrl(pid, order.Id)}/invoices", new RecordSupplierInvoiceRequest("FV/2627/118",
            Today(), null, null, [new SupplierInvoiceLineRequest(order.Lines[0].Id, 20m, 42m, 5m), new SupplierInvoiceLineRequest(order.Lines[1].Id, 10m, 30m, 5m)])),
            HttpStatusCode.Created);
        Assert.Equal(("SI00001", 1140m, 28.5m, 28.5m, 0m, 57m, 1197m, "UNPAID", true, Today().AddDays(7)),
            (bill.Number, bill.TaxableValue, bill.Cgst, bill.Sgst, bill.Igst, bill.InputTax, bill.Total, bill.Status, bill.ItcEligible, bill.DueDate));
        Assert.Equal(new[] { true, false }, bill.Lines.Select(l => l.PriceDiffers));
        Assert.Equal("Mapusa Fresh Vegetables's bill FV/2627/118 is already recorded as SI00001. A bill is recorded once.",
            await FieldErrorAsync(await accountant.PostAsync($"{OrderUrl(pid, order.Id)}/invoices", new RecordSupplierInvoiceRequest("fv/2627/118 ", Today(), null, null,
                [new SupplierInvoiceLineRequest(order.Lines[0].Id, 1m, 42m, 5m)])), "SupplierNumber"));

        // Paid in two parts: ₹500 by UPI, then the rest by bank transfer. Paying more than is left is refused.
        bill = await OkAsync<SupplierInvoiceDto>(await accountant.PostAsync($"{Url(pid)}/invoices/{bill.Id}/payments",
            new PaySupplierRequest(500m, null, "UPI", "UTR612345678", null, bill.Version)), HttpStatusCode.Created);
        Assert.Equal(("PART_PAID", 500m, 697m), (bill.Status, bill.AmountPaid, bill.Balance));
        Assert.Equal("SI00001 has ₹697 left to pay, so ₹700 is too much.",
            await FieldErrorAsync(await accountant.PostAsync($"{Url(pid)}/invoices/{bill.Id}/payments", new PaySupplierRequest(700m, null, "BANK_TRANSFER", "UTR99887766", null, null)), "Amount"));
        bill = await OkAsync<SupplierInvoiceDto>(await accountant.PostAsync($"{Url(pid)}/invoices/{bill.Id}/payments",
            new PaySupplierRequest(697m, null, "BANK_TRANSFER", "HDFC0001 UTR99887766", "Final", bill.Version)), HttpStatusCode.Created);
        Assert.Equal(("PAID", 0m), (bill.Status, bill.Balance));
        Assert.Equal(new[] { ("SP00001", "UPI"), ("SP00002", "BANK_TRANSFER") }, bill.Payments.Select(p => (p.Number, p.Method)));
        Assert.Equal(0m, (await owner.GetJsonAsync<List<SupplierDto>>($"{Url(pid)}/suppliers")).Single().Outstanding);
        var list = await accountant.GetJsonAsync<SupplierInvoiceListDto>($"{Url(pid)}/invoices");
        Assert.Equal((0m, 57m), (list.Outstanding, list.InputTax));

        // Every step is in the audit log, with what happened.
        Assert.Equal("GRN00001 on PO00001 into Main store: 12 kg Onion, 10 kg Tomato (₹780 before GST), challan DC-4411",
            await ScalarAsync($"SELECT reason FROM audit_logs WHERE entity_type = 'GoodsReceivedNote' AND entity_id = '{grn.Id}'"));
        Assert.Equal(new[] { "PurchaseOrder", "PurchaseRequest", "SupplierInvoice", "SupplierPayment" },
            (await ScalarAsync($"""
                SELECT string_agg(DISTINCT entity_type, ',' ORDER BY entity_type) FROM audit_logs
                 WHERE property_id = '{pid}' AND entity_type IN ('PurchaseRequest','PurchaseOrder','SupplierInvoice','SupplierPayment')
                """) as string)!.Split(','));
    }

    // ------------------------------------------------------------------ required: partial GRN, remaining quantity tracked

    [Fact]
    public async Task A_partial_delivery_leaves_the_rest_to_come_two_deliveries_at_once_never_take_more_and_the_order_can_be_closed()
    {
        var (owner, pid) = await PropertyAsync();
        var storekeeper = await StaffAsync("INVENTORY_MANAGER", pid);
        var other = await StaffAsync("PURCHASING", pid);
        var stock = await StockAsync(owner, pid);
        var supplier = await SupplierAsync(owner, pid, "FRESH-VEG", "Mapusa Fresh Vegetables", GoaSupplierGstin);
        var order = await ApprovedAsync(owner, pid, supplier, stock.Store.Id, new SavePurchaseOrderLine(stock.Onion.Id, 10m, 40m, 5m));

        order = await OkAsync<PurchaseOrderDto>(await storekeeper.PostAsync($"{OrderUrl(pid, order.Id)}/receipts", Receive(order, (0, 4m))), HttpStatusCode.Created);
        Assert.Equal(("PARTLY_RECEIVED", 4m, 6m), (order.Status, order.Lines[0].Received, order.Lines[0].Remaining));
        Assert.Equal("Only 6 kg of Onion is still to come on PO00001 (4 kg of 10 kg already in). Record the extra on a new order, or as stock received in Inventory with the reason.",
            await FieldErrorAsync(await storekeeper.PostAsync($"{OrderUrl(pid, order.Id)}/receipts", Receive(order, (0, 7m))), "Lines[0].Quantity"));

        // Two people record 4 kg at the same moment: only 6 kg is left, so one is refused.
        using (var barrier = new Barrier(2))
        {
            var a = Task.Run(() => { barrier.SignalAndWait(); return storekeeper.PostAsync($"{OrderUrl(pid, order.Id)}/receipts", Receive(order, (0, 4m))); });
            var b = Task.Run(() => { barrier.SignalAndWait(); return other.PostAsync($"{OrderUrl(pid, order.Id)}/receipts", Receive(order, (0, 4m))); });
            var results = await Task.WhenAll(a, b);
            Assert.Single(results, r => r.StatusCode == HttpStatusCode.Created);
            Assert.StartsWith("Only 2 kg of Onion is still to come on PO00001 (8 kg of 10 kg already in).",
                await FieldErrorAsync(results.Single(r => r.StatusCode != HttpStatusCode.Created), "Lines[0].Quantity"));
        }
        order = await owner.GetJsonAsync<PurchaseOrderDto>(OrderUrl(pid, order.Id));
        Assert.Equal((8m, 2m, 2), (order.Lines[0].Received, order.Lines[0].Remaining, order.Receipts.Count));
        Assert.Equal(13m, (await owner.GetJsonAsync<InventoryDto>($"/api/v1/properties/{pid}/inventory")).Items.Single(i => i.Id == stock.Onion.Id).OnHand);
        Assert.Equal(0L, await ScalarAsync($"""
            SELECT count(*) FROM purchase_order_items i
             WHERE i.order_id = '{order.Id}' AND i.received_quantity <> (SELECT coalesce(sum(g.quantity), 0) FROM grn_items g WHERE g.order_item_id = i.id)
            """));

        // A partly received order can't be cancelled; it is closed with the reason, and nothing more comes in.
        Assert.Equal("INVALID_STATUS_TRANSITION", (await ProblemAsync(await owner.PostAsync($"{OrderUrl(pid, order.Id)}/cancel", new PurchaseReasonRequest("Not needed", null)))).Code);
        Assert.Equal("Say why, in 3–300 characters (e.g. Supplier can't deliver this week).",
            await FieldErrorAsync(await owner.PostAsync($"{OrderUrl(pid, order.Id)}/close", new PurchaseReasonRequest(" ", null)), "Reason"));
        order = await OkAsync<PurchaseOrderDto>(await owner.PostAsync($"{OrderUrl(pid, order.Id)}/close", new PurchaseReasonRequest("Supplier out of onion", order.Version)));
        Assert.Equal(("CLOSED", "Supplier out of onion"), (order.Status, order.EndReason));
        Assert.Equal("PO00001 is closed, so nothing more can come in on it.",
            (await ProblemAsync(await storekeeper.PostAsync($"{OrderUrl(pid, order.Id)}/receipts", Receive(order, (0, 1m))))).Message);

        // Only what came in can be billed.
        Assert.Equal("Only 8 kg of Onion has come in and not been billed on PO00001. Bill what was received; record the goods received first if more came in.",
            await FieldErrorAsync(await owner.PostAsync($"{OrderUrl(pid, order.Id)}/invoices", new RecordSupplierInvoiceRequest("B-1", Today(), null, null,
                [new SupplierInvoiceLineRequest(order.Lines[0].Id, 10m, 40m, 5m)])), "Lines[0].Quantity"));
    }

    // ------------------------------------------------------------------ approval: limit, second person, reject, roles

    [Fact]
    public async Task Orders_over_the_limit_need_a_second_person_to_approve_and_a_rejected_order_goes_back_to_draft_with_its_reason()
    {
        var (owner, pid) = await PropertyAsync();
        var manager = await StaffAsync("MANAGER", pid);
        var gm = await StaffAsync("GENERAL_MANAGER", pid);
        var stock = await StockAsync(owner, pid);
        var supplier = await SupplierAsync(owner, pid, "FRESH-VEG", "Mapusa Fresh Vegetables", GoaSupplierGstin);

        // ₹9,450 is within the default ₹10,000 limit: approved on submit, by no one.
        var small = await DraftAsync(manager, pid, supplier, stock.Store.Id, new SavePurchaseOrderLine(stock.Onion.Id, 225m, 40m, 5m));
        small = await OkAsync<PurchaseOrderDto>(await manager.PostAsync($"{OrderUrl(pid, small.Id)}/submit", new PurchaseOrderStepRequest(null, small.Version)));
        Assert.Equal(("APPROVED", 9450m, 10000m, false, null), (small.Status, small.Total, small.ApprovalLimit, small.NeedsApproval, small.ApprovedByName));

        // ₹10,500 is over it. The owner raised it, so the owner can't approve it; a manager can't approve at all; the GM can.
        var big = await DraftAsync(owner, pid, supplier, stock.Store.Id, new SavePurchaseOrderLine(stock.Onion.Id, 250m, 40m, 5m));
        Assert.True(big.NeedsApproval);
        big = await OkAsync<PurchaseOrderDto>(await owner.PostAsync($"{OrderUrl(pid, big.Id)}/submit", new PurchaseOrderStepRequest(null, big.Version)));
        Assert.Equal("PENDING_APPROVAL", big.Status);
        var (code, message) = await ProblemAsync(await owner.PostAsync($"{OrderUrl(pid, big.Id)}/approve", new PurchaseOrderStepRequest(null, null)));
        Assert.Equal(("SELF_APPROVAL", "You raised PO00002, so you can't approve it yourself. Orders over ₹10,000 need a second person: ask someone else with purchase approval (owner or general manager) to approve it."),
            (code, message));
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsync($"{OrderUrl(pid, big.Id)}/approve", new PurchaseOrderStepRequest(null, null))).StatusCode);

        // A submitted order can't be edited. The GM rejects it: back to draft with the reason; the owner halves it and resubmits.
        Assert.Equal("INVALID_STATUS_TRANSITION", (await ProblemAsync(await owner.PutAsync(OrderUrl(pid, big.Id),
            new SavePurchaseOrderRequest(supplier.Id, stock.Store.Id, null, null, null, [new SavePurchaseOrderLine(stock.Onion.Id, 100m, 40m, 5m)], null)))).Code);
        big = await OkAsync<PurchaseOrderDto>(await gm.PostAsync($"{OrderUrl(pid, big.Id)}/reject", new PurchaseReasonRequest("Too much onion for one week", big.Version)));
        Assert.Equal(("DRAFT", "Too much onion for one week", null), (big.Status, big.RejectedReason, big.SubmittedAt));
        var stale = big.Version;
        big = await OkAsync<PurchaseOrderDto>(await owner.PutAsync(OrderUrl(pid, big.Id),
            new SavePurchaseOrderRequest(supplier.Id, stock.Store.Id, null, null, "Halved", [new SavePurchaseOrderLine(stock.Onion.Id, 125m, 40m, 5m)], big.Version)));
        Assert.Equal(5250m, big.Total);
        Assert.Equal("CONCURRENCY_CONFLICT", (await ProblemAsync(await owner.PostAsync($"{OrderUrl(pid, big.Id)}/submit", new PurchaseOrderStepRequest(null, stale)))).Code);
        big = await OkAsync<PurchaseOrderDto>(await owner.PostAsync($"{OrderUrl(pid, big.Id)}/submit", new PurchaseOrderStepRequest(null, big.Version)));
        Assert.Equal("APPROVED", big.Status);

        // A raised limit is read at submit: ₹50,000.
        await PutSettingAsync(owner, pid, "purchasing.approval_limit", 50000);
        var list = await manager.GetJsonAsync<PurchaseOrderListDto>($"{Url(pid)}/orders");
        Assert.Equal((50000m, 0), (list.ApprovalLimit, list.Waiting));

        // An approved order nothing has come in on can be cancelled.
        small = await OkAsync<PurchaseOrderDto>(await manager.PostAsync($"{OrderUrl(pid, small.Id)}/cancel", new PurchaseReasonRequest("Ordered by phone instead", small.Version)));
        Assert.Equal("CANCELLED", small.Status);
    }

    // ------------------------------------------------------------------ suppliers and input GST

    [Fact]
    public async Task Supplier_GSTINs_are_checked_an_unregistered_supplier_charges_no_GST_and_another_state_is_IGST()
    {
        var (owner, pid) = await PropertyAsync();
        var stock = await StockAsync(owner, pid);
        var url = $"{Url(pid)}/suppliers";

        var bad = GoaSupplierGstin[..14] + (GoaSupplierGstin[14] == 'A' ? 'B' : 'A');
        Assert.Equal($"The last character (check digit) should be {GoaSupplierGstin[14]} for this GSTIN; check it for a typing mistake.",
            await FieldErrorAsync(await owner.PostAsync(url, Supplier("X1", "Bad Gstin Traders", bad)), "Gstin"));
        Assert.Equal("The GSTIN is registered in Goa (30), not Maharashtra (27). Leave the state to the GSTIN or check the GSTIN.",
            await FieldErrorAsync(await owner.PostAsync(url, Supplier("X1", "Wrong State Traders", GoaSupplierGstin) with { StateCode = "27" }), "StateCode"));
        Assert.StartsWith("Choose the state the supplier supplies from.",
            await FieldErrorAsync(await owner.PostAsync(url, Supplier("FISH", "Anjuna Fishermen", null)), "StateCode"));
        var goa = await SupplierAsync(owner, pid, "GOA", "Goa Traders", GoaSupplierGstin);
        Assert.Equal("Goa Traders already has the GSTIN " + GoaSupplierGstin + ". A GSTIN belongs to one supplier.",
            await FieldErrorAsync(await owner.PostAsync(url, Supplier("GOA2", "Goa Traders Again", GoaSupplierGstin.ToLowerInvariant())), "Gstin"));

        // Unregistered: no GST on the order, nor on the bill.
        var fish = await OkAsync<SupplierDto>(await owner.PostAsync(url, Supplier("FISH", "Anjuna Fishermen", null) with { StateCode = "30" }), HttpStatusCode.Created);
        Assert.Equal("Anjuna Fishermen has no GSTIN, so they can't charge GST. Set the GST to 0 %, or add their GSTIN in Suppliers.",
            await FieldErrorAsync(await owner.PostAsync($"{Url(pid)}/orders", new SavePurchaseOrderRequest(fish.Id, stock.Store.Id, null, null, null,
                [new SavePurchaseOrderLine(stock.Onion.Id, 5m, 40m, 5m)], null)), "Lines[0].GstRate"));
        var fishOrder = await ApprovedAsync(owner, pid, fish, stock.Store.Id, new SavePurchaseOrderLine(stock.Onion.Id, 5m, 40m, 0m));
        fishOrder = await OkAsync<PurchaseOrderDto>(await owner.PostAsync($"{OrderUrl(pid, fishOrder.Id)}/receipts", Receive(fishOrder, (0, 5m))), HttpStatusCode.Created);
        Assert.Equal("Anjuna Fishermen has no GSTIN, so their bill can't carry GST. Enter 0 %, or add their GSTIN in Suppliers first.",
            await FieldErrorAsync(await owner.PostAsync($"{OrderUrl(pid, fishOrder.Id)}/invoices", new RecordSupplierInvoiceRequest("12", Today(), null, null,
                [new SupplierInvoiceLineRequest(fishOrder.Lines[0].Id, 5m, 40m, 5m)])), "Lines[0].GstRate"));
        var fishBill = await OkAsync<SupplierInvoiceDto>(await owner.PostAsync($"{OrderUrl(pid, fishOrder.Id)}/invoices", new RecordSupplierInvoiceRequest("12", Today(), null, null,
            [new SupplierInvoiceLineRequest(fishOrder.Lines[0].Id, 5m, 40m, 0m)])), HttpStatusCode.Created);
        Assert.Equal((0m, 200m, false), (fishBill.InputTax, fishBill.Total, fishBill.ItcEligible));

        // Another state: IGST, 18 %. The property has no GST registration, so the input tax can't be claimed.
        var mumbai = await SupplierAsync(owner, pid, "MUMBAI", "Mumbai Kitchen Supplies", MumbaiSupplierGstin);
        var order = await ApprovedAsync(owner, pid, mumbai, stock.Store.Id, new SavePurchaseOrderLine(stock.Oil.Id, 10m, 150m, 18m));
        Assert.Equal((false, 270m, 1770m), (order.IntraState, order.TaxTotal, order.Total));
        order = await OkAsync<PurchaseOrderDto>(await owner.PostAsync($"{OrderUrl(pid, order.Id)}/receipts", Receive(order, (0, 10m))), HttpStatusCode.Created);
        var bill = await OkAsync<SupplierInvoiceDto>(await owner.PostAsync($"{OrderUrl(pid, order.Id)}/invoices", new RecordSupplierInvoiceRequest("MKS-0091", Today(),
            Today().AddDays(30), null, [new SupplierInvoiceLineRequest(order.Lines[0].Id, 10m, 150m, 18m)])), HttpStatusCode.Created);
        Assert.Equal((0m, 0m, 270m, 1770m, false), (bill.Cgst, bill.Sgst, bill.Igst, bill.Total, bill.ItcEligible));
        var suppliers = await owner.GetJsonAsync<List<SupplierDto>>(url);
        Assert.Equal(1770m, suppliers.Single(s => s.Id == mumbai.Id).Outstanding);
        Assert.Equal(goa.Id, suppliers.Single(s => s.Code == "GOA").Id);

        // The kitchen can't see suppliers or orders.
        var kitchen = await StaffAsync("KITCHEN", pid);
        Assert.Equal(HttpStatusCode.Forbidden, (await kitchen.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await kitchen.GetAsync($"{Url(pid)}/orders")).StatusCode);
    }

    // ------------------------------------------------------------------ cash from the drawer, idempotency, database guards

    [Fact]
    public async Task Cash_paid_to_a_supplier_leaves_the_drawer_a_repeated_payment_is_paid_once_and_records_can_never_be_changed()
    {
        var (owner, pid) = await PropertyAsync();
        var stock = await StockAsync(owner, pid);
        var supplier = await SupplierAsync(owner, pid, "FRESH-VEG", "Mapusa Fresh Vegetables", GoaSupplierGstin);
        var order = await ApprovedAsync(owner, pid, supplier, stock.Store.Id, new SavePurchaseOrderLine(stock.Tomato.Id, 20m, 30m, 0m));
        order = await OkAsync<PurchaseOrderDto>(await owner.PostAsync($"{OrderUrl(pid, order.Id)}/receipts", Receive(order, (0, 20m))), HttpStatusCode.Created);
        var bill = await OkAsync<SupplierInvoiceDto>(await owner.PostAsync($"{OrderUrl(pid, order.Id)}/invoices", new RecordSupplierInvoiceRequest("C-77", Today(), null, null,
            [new SupplierInvoiceLineRequest(order.Lines[0].Id, 20m, 30m, 0m)])), HttpStatusCode.Created);
        var pay = $"{Url(pid)}/invoices/{bill.Id}/payments";

        Assert.StartsWith("The Front desk cash drawer isn't open, so a supplier payment in cash can't be recorded.",
            await FieldErrorAsync(await owner.PostAsync(pay, new PaySupplierRequest(100m, null, "CASH", null, null, null)), "Method"));
        Assert.Equal("Enter the UTR (bank transfer or UPI) or the cheque number, 3–60 characters, so the payment can be matched to the bank statement.",
            await FieldErrorAsync(await owner.PostAsync(pay, new PaySupplierRequest(100m, null, "CHEQUE", null, null, null)), "Reference"));
        var shift = await OkAsync<CashDrawerSessionDto>(await owner.PostAsync($"/api/v1/properties/{pid}/cash-drawer/open", new OpenCashDrawerRequest(500m)), HttpStatusCode.Created);
        Assert.Equal("The cash drawer should hold ₹500, so ₹600 can't be paid from it. Pay part in cash, or pay by bank transfer, UPI or cheque.",
            await FieldErrorAsync(await owner.PostAsync(pay, new PaySupplierRequest(600m, null, "CASH", null, null, null)), "Amount"));

        // ₹400 in cash, sent twice with the same key: paid once.
        var key = new Dictionary<string, string> { ["Idempotency-Key"] = "supplier-" + Guid.NewGuid() };
        var body = new PaySupplierRequest(400m, null, "CASH", null, "Paid to the driver", null);
        var first = await OkAsync<SupplierInvoiceDto>(await owner.PostAsync(pay, body, key), HttpStatusCode.Created);
        var again = await owner.PostAsync(pay, body, key);
        Assert.Equal("true", again.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal((200m, 1), (first.Balance, first.Payments.Count));
        Assert.Single((await owner.GetJsonAsync<SupplierInvoiceListDto>($"{Url(pid)}/invoices")).Items.Single().Payments);
        var drawer = await owner.GetJsonAsync<CashDrawerSessionDto>($"/api/v1/properties/{pid}/cash-drawer/sessions/{shift.Id}");
        Assert.Equal(100m, drawer.Expected);
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM cash_drawer_movements WHERE session_id = '{shift.Id}' AND movement_type = 'PAY_OUT' AND amount = -400 AND supplier_payment_id IS NOT NULL"));

        // The database refuses to change what is recorded.
        foreach (var sql in new[]
                 {
                     $"UPDATE goods_received_notes SET value = 1 WHERE order_id = '{order.Id}'",
                     $"UPDATE grn_items SET quantity = 1 WHERE order_item_id = '{order.Lines[0].Id}'",
                     $"UPDATE supplier_invoices SET total = 1 WHERE id = '{bill.Id}'",
                     $"UPDATE supplier_payments SET amount = 1 WHERE invoice_id = '{bill.Id}'",
                     $"DELETE FROM purchase_orders WHERE id = '{order.Id}'",
                     $"UPDATE purchase_order_items SET received_quantity = quantity + 1 WHERE id = '{order.Lines[0].Id}'",
                 })
            await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(sql));
    }

    // ------------------------------------------------------------------ helpers

    private sealed record Stock(StockLocationDto Store, InventoryItemDto Onion, InventoryItemDto Tomato, InventoryItemDto Oil);

    private async Task<Stock> StockAsync(TestClient owner, Guid pid)
    {
        var url = $"/api/v1/properties/{pid}/inventory";
        var store = await OkAsync<StockLocationDto>(await owner.PostAsync($"{url}/locations", new SaveStockLocationRequest("STORE", "Main store", false, 1)), HttpStatusCode.Created);
        await OkAsync<StockLocationDto>(await owner.PostAsync($"{url}/locations", new SaveStockLocationRequest("KITCHEN", "Kitchen", true, 2)), HttpStatusCode.Created);
        var group = await OkAsync<InventoryCategoryDto>(await owner.PostAsync($"{url}/categories", new SaveInventoryCategoryRequest("Vegetables", 1)), HttpStatusCode.Created);
        var units = (await owner.GetJsonAsync<InventoryDto>(url)).Units;
        async Task<InventoryItemDto> Item(string code, string name, string unit, decimal cost, decimal? opening) =>
            await OkAsync<InventoryItemDto>(await owner.PostAsync($"{url}/items", new SaveInventoryItemRequest(group.Id, code, name, units.Single(u => u.Code == unit).Id,
                cost, null, true, opening, store.Id, null)), HttpStatusCode.Created);
        return new Stock(store, await Item("ONION", "Onion", "kg", 35m, 5m), await Item("TOMATO", "Tomato", "kg", 28m, null), await Item("OIL", "Cooking oil", "l", 140m, null));
    }

    private static SaveSupplierRequest Supplier(string code, string name, string? gstin) =>
        new(code, name, gstin, null, "Ravi Naik", "+91 98220 12345", "accounts@example.in", "Mapusa market, Goa", 7, null);

    private static async Task<SupplierDto> SupplierAsync(TestClient c, Guid pid, string code, string name, string gstin) =>
        await OkAsync<SupplierDto>(await c.PostAsync($"{Url(pid)}/suppliers", Supplier(code, name, gstin)), HttpStatusCode.Created);

    private static async Task<PurchaseOrderDto> DraftAsync(TestClient c, Guid pid, SupplierDto supplier, Guid store, params SavePurchaseOrderLine[] lines) =>
        await OkAsync<PurchaseOrderDto>(await c.PostAsync($"{Url(pid)}/orders", new SavePurchaseOrderRequest(supplier.Id, store, null, null, null, lines, null)),
            HttpStatusCode.Created);

    /// <summary>A draft under the limit, submitted (so approved on submit).</summary>
    private static async Task<PurchaseOrderDto> ApprovedAsync(TestClient c, Guid pid, SupplierDto supplier, Guid store, params SavePurchaseOrderLine[] lines)
    {
        var order = await DraftAsync(c, pid, supplier, store, lines);
        order = await OkAsync<PurchaseOrderDto>(await c.PostAsync($"{OrderUrl(pid, order.Id)}/submit", new PurchaseOrderStepRequest(null, order.Version)));
        Assert.Equal("APPROVED", order.Status);
        return order;
    }

    private static ReceiveGoodsRequest Receive(PurchaseOrderDto order, params (int Line, decimal Quantity)[] lines) =>
        new(null, null, null, null, lines.Select(l => new ReceiveGoodsLine(order.Lines[l.Line].Id, l.Quantity)).ToList());

    private static async Task PutSettingAsync(TestClient c, Guid pid, string key, decimal value) =>
        Assert.Equal(HttpStatusCode.OK, (await c.PutAsync($"/api/v1/properties/{pid}/settings/{key}",
            new PutPropertySettingRequest(JsonSerializer.SerializeToElement(value)))).StatusCode);

    private async Task<(TestClient Owner, Guid PropertyId)> PropertyAsync(bool registered = false)
    {
        var owner = await app.SignInOwnerAsync();
        var code = "P" + Suffix();
        var res = await owner.PostAsync("/api/v1/properties", new CreatePropertyRequest(code, $"Beach Shack {code}", null, "RESORT", "Asia/Kolkata", "INR",
            null, null, null, null, null, "30", null, "IN", null, null, new TimeOnly(14, 0), new TimeOnly(11, 0), null, null));
        Assert.True(res.StatusCode == HttpStatusCode.Created, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        var pid = (await res.Content.ReadFromJsonAsync<PropertyDto>())!.Id;
        if (registered)
            Assert.Equal(HttpStatusCode.Created, (await owner.PostAsync($"/api/v1/properties/{pid}/gst-registrations",
                new CreateGstRegistrationRequest("30AABCT1234C1Z9", "Test Resorts Pvt Ltd", null, "REGULAR", new DateOnly(2017, 7, 1)))).StatusCode);
        return (owner, pid);
    }

    private async Task<TestClient> StaffAsync(string role, Guid pid)
    {
        var (email, pw) = await app.CreateUserAsync(role, pid);
        return await app.SignInAsync(email, pw);
    }

    private static DateOnly Today() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata")).DateTime);

    private static string Gst(string first14) => first14 + Gstin.CheckDigit(first14);
    private static string Url(Guid pid) => $"/api/v1/properties/{pid}/purchasing";
    private static string OrderUrl(Guid pid, Guid id) => $"{Url(pid)}/orders/{id}";

    private static async Task<T> OkAsync<T>(HttpResponseMessage res, HttpStatusCode status = HttpStatusCode.OK)
    {
        Assert.True(res.StatusCode == status, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<T>())!;
    }

    private static Task<(string? Code, string? Message)> ProblemAsync(HttpResponseMessage res) => FrontDeskTests.ProblemAsync(res);

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(app.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(app.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
