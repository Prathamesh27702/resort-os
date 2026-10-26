using System.Net;
using System.Net.Http.Json;
using Npgsql;
using Resort.Contracts;
using Resort.Tests.Support;
using static Resort.Tests.Support.BookingKit;

namespace Resort.Tests.Integration;

/// <summary>Sprint 14 — the restaurant: a waiter takes an order on a phone, the kitchen sees the ticket and marks it ready,
/// the waiter serves it and charges the bill to the guest's room (or takes the payment); two waiters on one order; voids
/// and discounts need their own permission; GST from the tax engine. Real API, real PostgreSQL.</summary>
public class RestaurantTests(FolioFactory app) : IClassFixture<FolioFactory>
{
    // ------------------------------------------------------------------ required: room charge posts ₹1,250 to the folio

    [Fact]
    public async Task Waiter_takes_an_order_kitchen_marks_it_ready_and_the_bill_of_1250_rupees_goes_on_the_guests_folio()
    {
        var (owner, pid) = await OwnerAsync();
        var waiter = await StaffAsync("WAITER", pid);
        var kitchen = await StaffAsync("KITCHEN", pid);
        var menu = await MenuAsync(owner, pid);
        var table = await TableAsync(owner, pid);
        var (folio, booked, room) = await InHouseAsync(owner, pid);

        // The waiter opens table T… for two and adds 2 Paneer Tikka (₹280), Dal Makhani (₹240), 2 Butter Naan (₹60), Veg Biryani (₹330).
        var order = await CreatedAsync(await waiter.PostAsync(OrdersUrl(pid), new CreateOrderRequest("DINE_IN", table.Id, null, 2, null,
            [Line(menu.PaneerTikka, 2), Line(menu.DalMakhani, 1), Line(menu.Naan, 2), Line(menu.Biryani, 1)])));
        Assert.Matches("^O[0-9]{5}$", order.Number);
        Assert.Equal(($"Table {table.Code}", "OPEN", 1250m, 0m, 1250m), (order.Where, order.Status, order.Subtotal, order.TaxAmount, order.Total));
        Assert.All(order.Items, i => Assert.Equal("NEW", i.Status));
        Assert.Contains("send", order.NextActions);
        Assert.DoesNotContain("charge", order.NextActions);   // nothing is in the kitchen yet

        // Sent to the kitchen: one ticket with the four lines.
        order = await OkAsync(await waiter.PostAsync($"{OrderUrl(pid, order)}/send", new SendToKitchenRequest("Table by the pool", order.Version)));
        var kot = Assert.Single(order.Kots);
        Assert.Matches("^K[0-9]{5}$", kot.Number);
        Assert.All(order.Items, i => Assert.Equal(("SENT", kot.Id, "NEW"), (i.Status, i.KotId!.Value, i.KitchenStatus)));

        // Not served yet, so it can't be billed.
        var (code, message) = await ProblemAsync(await waiter.PostAsync($"{OrderUrl(pid, order)}/charge-to-room", new ChargeToRoomRequest(booked.Id, order.Version)));
        Assert.Equal("ORDER_IN_KITCHEN", code);
        Assert.StartsWith($"{kot.Number} is waiting in the kitchen.", message);

        // The kitchen display shows the ticket with its lines; the cook starts it, then marks it ready.
        var display = await kitchen.GetJsonAsync<KitchenDto>(KitchenUrl(pid));
        var ticket = display.Tickets.Single(t => t.Id == kot.Id);
        Assert.Equal(($"Table {table.Code}", order.Number, "NEW", "Table by the pool"), (ticket.Where, ticket.OrderNumber, ticket.Status, ticket.Note));
        Assert.Equal(new[] { (2, "Paneer Tikka (Full)"), (1, "Dal Makhani"), (2, "Butter Naan"), (1, "Veg Biryani") },
            ticket.Items.Select(i => ((int)i.Quantity, i.Name)));
        ticket = await KotOkAsync(await kitchen.PostAsync($"{KitchenUrl(pid)}/{kot.Id}/start", new KotStepRequest(ticket.Version)));
        Assert.Equal("PREPARING", ticket.Status);
        ticket = await KotOkAsync(await kitchen.PostAsync($"{KitchenUrl(pid)}/{kot.Id}/ready", new KotStepRequest(ticket.Version)));
        Assert.Equal("READY", ticket.Status);
        // The kitchen can't take orders.
        Assert.Equal(HttpStatusCode.Forbidden, (await kitchen.PostAsync(OrdersUrl(pid), new CreateOrderRequest("TAKEAWAY", null, null, null, null, null))).StatusCode);

        // Ready: the waiter sees it on the floor, serves it, and charges the bill to the guest's room.
        var floor = await waiter.GetJsonAsync<FloorDto>($"{OrdersUrl(pid)}/floor");
        Assert.Equal((1, 0), (floor.Tables.Single(t => t.Id == table.Id).Order!.Ready, floor.Tables.Single(t => t.Id == table.Id).Order!.InKitchen));
        order = await OkAsync(await waiter.PostAsync($"{OrderUrl(pid, order)}/kots/{kot.Id}/serve"));
        Assert.Equal("SERVED", order.Kots.Single().Status);
        Assert.Contains("charge", order.NextActions);
        var rooms = await waiter.GetJsonAsync<List<InHouseRoomDto>>($"{OrdersUrl(pid)}/rooms");
        Assert.Contains(rooms, r => r.ReservationId == booked.Id && r.UnitId == room.Id && r.Reference == booked.Reference);

        var key = new Dictionary<string, string> { ["Idempotency-Key"] = "charge-" + Guid.NewGuid() };
        var url = $"{OrderUrl(pid, order)}/charge-to-room";
        var charged = await OkAsync(await waiter.PostAsync(url, new ChargeToRoomRequest(booked.Id, order.Version), key));
        Assert.Equal(("SETTLED", "ROOM_CHARGE", folio.Id, folio.Number, booked.Reference, room.Id),
            (charged.Status, charged.Settlement, charged.FolioId!.Value, charged.FolioNumber, charged.ReservationReference, charged.UnitId!.Value));
        Assert.Empty(charged.NextActions);

        // The folio has one restaurant line of ₹1,250 on top of the room nights.
        var bill = await owner.GetJsonAsync<FolioDto>($"/api/v1/properties/{pid}/folios/{folio.Id}");
        var line = bill.Items.Single(i => i.Source == "RESTAURANT");
        Assert.Equal(("FOOD", 1m, 1250m, 1250m, 0m, 1250m, Today), (line.Category, line.Quantity, line.UnitPrice, line.Amount, line.TaxAmount, line.Total, line.ServiceDate));
        Assert.Equal($"Restaurant {order.Number} · Table {table.Code}: 2× Paneer Tikka (Full), 1× Dal Makhani, 2× Butter Naan, 1× Veg Biryani", line.Description);
        Assert.Equal(folio.Total + 1250m, bill.Total);
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM orders WHERE folio_item_id = '{line.Id}'"));

        // A retry with the same key gives the same answer; without it, the order is already settled.
        var replay = await waiter.PostAsync(url, new ChargeToRoomRequest(booked.Id, order.Version), key);
        Assert.Equal(("true", charged.SettledAt), (replay.Headers.GetValues("Idempotent-Replayed").Single(), (await OkAsync(replay)).SettledAt));
        (code, message) = await ProblemAsync(await waiter.PostAsync(url, new ChargeToRoomRequest(booked.Id, null)));
        Assert.Equal(("ORDER_SETTLED", $"{order.Number} is already settled, so it can't be changed. Open a new order for anything more."), (code, message));
        Assert.Single((await owner.GetJsonAsync<FolioDto>($"/api/v1/properties/{pid}/folios/{folio.Id}")).Items, i => i.Source == "RESTAURANT");

        // The table is free again, and the kitchen display no longer shows the ticket.
        Assert.Null((await waiter.GetJsonAsync<FloorDto>($"{OrdersUrl(pid)}/floor")).Tables.Single(t => t.Id == table.Id).Order);
        Assert.DoesNotContain((await kitchen.GetJsonAsync<KitchenDto>(KitchenUrl(pid))).Tickets, t => t.Id == kot.Id);
    }

    // ------------------------------------------------------------------ required: two waiters on one order → 409

    [Fact]
    public async Task Two_waiters_changing_the_same_order_at_once_one_wins_and_the_other_gets_409()
    {
        var (owner, pid) = await OwnerAsync();
        var first = await StaffAsync("WAITER", pid);
        var second = await StaffAsync("WAITER", pid);
        var menu = await MenuAsync(owner, pid);

        for (var run = 0; run < 10; run++)
        {
            var table = await TableAsync(owner, pid);
            var order = await CreatedAsync(await first.PostAsync(OrdersUrl(pid), new CreateOrderRequest("DINE_IN", table.Id, null, 2, null, [Line(menu.Naan, 1)])));

            // Both saw the same version; one adds a naan, the other a biryani, at the same moment.
            using var barrier = new Barrier(2);
            var a = Task.Run(() => { barrier.SignalAndWait(); return first.PostAsync($"{OrderUrl(pid, order)}/items", new AddOrderItemsRequest([Line(menu.Naan, 1)], order.Version)); });
            var b = Task.Run(() => { barrier.SignalAndWait(); return second.PostAsync($"{OrderUrl(pid, order)}/items", new AddOrderItemsRequest([Line(menu.Biryani, 1)], order.Version)); });
            var results = await Task.WhenAll(a, b);
            Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
            var loser = results.Single(r => r.StatusCode != HttpStatusCode.OK);
            Assert.Equal(HttpStatusCode.Conflict, loser.StatusCode);
            Assert.Equal(("CONCURRENCY_CONFLICT", $"Someone else changed {order.Number} a moment ago (another waiter, or the kitchen). Reload it to see their change, then make yours again."),
                await ProblemAsync(loser));

            // Exactly one of the two lines was added, and the totals match the lines.
            var now = await first.GetJsonAsync<OrderDto>(OrderUrl(pid, order));
            Assert.Equal(2, now.Items.Count);
            Assert.Equal(now.Items.Sum(i => i.Amount), now.Subtotal);
            Assert.NotEqual(order.Version, now.Version);

            await OkAsync(await first.PostAsync($"{OrderUrl(pid, now)}/send", new SendToKitchenRequest(null, now.Version)));
        }

        // A stale version is refused one at a time too: covers changed by one waiter, then the other's edit with the old version.
        var t2 = await TableAsync(owner, pid);
        var o = await CreatedAsync(await first.PostAsync(OrdersUrl(pid), new CreateOrderRequest("DINE_IN", t2.Id, null, 2, null, [Line(menu.Naan, 1)])));
        await OkAsync(await first.PutAsync(OrderUrl(pid, o), new UpdateOrderRequest(null, 4, "Birthday", o.Version)));
        var stale = await second.PutAsync($"{OrderUrl(pid, o)}/items/{o.Items[0].Id}", new UpdateOrderItemRequest(3, null, o.Version));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal((short)1, (await first.GetJsonAsync<OrderDto>(OrderUrl(pid, o))).Items[0].Quantity);
    }

    // ------------------------------------------------------------------ voids, discounts and who may do what

    [Fact]
    public async Task Voids_need_pos_order_void_and_a_reason_discounts_need_pos_discount_and_the_kitchen_sees_the_void()
    {
        var (owner, pid) = await OwnerAsync();
        var waiter = await StaffAsync("WAITER", pid);
        var manager = await StaffAsync("RESTAURANT_MANAGER", pid);
        var kitchen = await StaffAsync("KITCHEN", pid);
        var menu = await MenuAsync(owner, pid);
        var table = await TableAsync(owner, pid);

        var order = await CreatedAsync(await waiter.PostAsync(OrdersUrl(pid), new CreateOrderRequest("DINE_IN", table.Id, null, 2, null,
            [Line(menu.PaneerTikka, 1), Line(menu.Biryani, 1), Line(menu.Naan, 2)])));
        // A line not sent yet is just changed or removed (the kitchen never saw it).
        order = await OkAsync(await waiter.PutAsync($"{OrderUrl(pid, order)}/items/{order.Items[2].Id}", new UpdateOrderItemRequest(3, "Extra butter", order.Version)));
        Assert.Equal((3, 180m, "Extra butter"), (order.Items[2].Quantity, order.Items[2].Amount, order.Items[2].Note));
        order = await OkAsync(await waiter.DeleteAsync($"{OrderUrl(pid, order)}/items/{order.Items[2].Id}?version={order.Version}"));
        Assert.Equal(2, order.Items.Count);
        Assert.Equal(610m, order.Subtotal);
        order = await OkAsync(await waiter.PostAsync($"{OrderUrl(pid, order)}/send", new SendToKitchenRequest(null, order.Version)));
        var biryani = order.Items.Single(i => i.Name == "Veg Biryani");

        // Sent: it can't be removed, and a waiter can't void or discount.
        var (code, message) = await ProblemAsync(await waiter.DeleteAsync($"{OrderUrl(pid, order)}/items/{biryani.Id}?version={order.Version}"));
        Assert.Equal(("ALREADY_SENT", "Veg Biryani has gone to the kitchen, so it can't be removed. A manager can void it, with a reason."), (code, message));
        Assert.Equal(HttpStatusCode.Forbidden, (await waiter.PostAsync($"{OrderUrl(pid, order)}/items/{biryani.Id}/void", new VoidRequest("Too slow", order.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await waiter.PostAsync($"{OrderUrl(pid, order)}/discount", new DiscountRequest("PERCENT", 10, "Regular", order.Version))).StatusCode);
        Assert.DoesNotContain("void", order.NextActions);

        // The manager voids it, saying why; the kitchen sees it struck through.
        Assert.Equal("Say why it is voided (at least 3 characters), e.g. Guest changed their mind.",
            await FieldErrorAsync(await manager.PostAsync($"{OrderUrl(pid, order)}/items/{biryani.Id}/void", new VoidRequest(" ", order.Version)), "Reason"));
        order = await OkAsync(await manager.PostAsync($"{OrderUrl(pid, order)}/items/{biryani.Id}/void", new VoidRequest("Guest changed their mind", order.Version)));
        Assert.Equal(("VOIDED", "Guest changed their mind", "RESTAURANT_MANAGER user"),
            (order.Items.Single(i => i.Id == biryani.Id).Status, order.Items.Single(i => i.Id == biryani.Id).VoidReason, order.Items.Single(i => i.Id == biryani.Id).VoidedByName));
        Assert.Equal(280m, order.Subtotal);
        var ticket = (await kitchen.GetJsonAsync<KitchenDto>(KitchenUrl(pid))).Tickets.Single(t => t.OrderId == order.Id);
        Assert.Equal(new[] { ("Paneer Tikka (Full)", false), ("Veg Biryani", true) }, ticket.Items.Select(i => (i.Name, i.Voided)));
        Assert.Equal("Guest changed their mind", ticket.Items[1].VoidReason);
        Assert.Equal("ALREADY_VOIDED", (await ProblemAsync(await manager.PostAsync($"{OrderUrl(pid, order)}/items/{biryani.Id}/void", new VoidRequest("Again", order.Version)))).Code);

        // A discount needs a reason, can't be more than the bill, and comes off before GST.
        Assert.Equal("Say why the discount is given (at least 3 characters), e.g. Regular guest.",
            await FieldErrorAsync(await manager.PostAsync($"{OrderUrl(pid, order)}/discount", new DiscountRequest("PERCENT", 10, null, order.Version)), "Reason"));
        Assert.Equal("A percentage discount can be at most 100%.",
            await FieldErrorAsync(await manager.PostAsync($"{OrderUrl(pid, order)}/discount", new DiscountRequest("PERCENT", 120, "Regular", order.Version)), "Value"));
        Assert.Equal($"{order.Number} comes to ₹280 before GST, so the discount can be at most that.",
            await FieldErrorAsync(await manager.PostAsync($"{OrderUrl(pid, order)}/discount", new DiscountRequest("AMOUNT", 500, "Regular", order.Version)), "Value"));
        order = await OkAsync(await manager.PostAsync($"{OrderUrl(pid, order)}/discount", new DiscountRequest("PERCENT", 10, "Regular guest", order.Version)));
        Assert.Equal((280m, 28m, 252m, 252m, "Regular guest", "RESTAURANT_MANAGER user"),
            (order.Subtotal, order.DiscountAmount, order.Net, order.Total, order.DiscountReason, order.DiscountedByName));

        // A whole order is voided by the manager with a reason; it is final, and the kitchen stops.
        order = await OkAsync(await manager.PostAsync($"{OrderUrl(pid, order)}/void", new VoidRequest("Guests left before eating", order.Version)));
        Assert.Equal(("VOIDED", 0m, "Guests left before eating"), (order.Status, order.Total, order.VoidReason));
        Assert.Equal("CANCELLED", order.Kots.Single().Status);
        Assert.DoesNotContain((await kitchen.GetJsonAsync<KitchenDto>(KitchenUrl(pid))).Tickets, t => t.OrderId == order.Id);
        Assert.Equal("ORDER_VOIDED", (await ProblemAsync(await waiter.PostAsync($"{OrderUrl(pid, order)}/items", new AddOrderItemsRequest([Line(menu.Naan, 1)], null)))).Code);

        // The audit trail has who voided and why.
        Assert.Equal(1L, await ScalarAsync(
            $"SELECT count(*) FROM audit_logs WHERE entity_type = 'Order' AND entity_id = '{order.Id}' AND reason = '{order.Number} voided: Guests left before eating'"));
    }

    // ------------------------------------------------------------------ GST

    [Fact]
    public async Task A_registered_property_charges_5_percent_GST_on_the_bill_after_discount_and_the_folio_line_matches_the_order()
    {
        var (owner, _) = await OwnerAsync();
        var pid = await NewPropertyAsync(owner);
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsync($"/api/v1/properties/{pid}/gst-registrations",
            new CreateGstRegistrationRequest("30AABCT1234C1Z9", "Test Resorts Pvt Ltd", null, "REGULAR", new DateOnly(2017, 7, 1)))).StatusCode);
        var menu = await MenuAsync(owner, pid);
        var table = await TableAsync(owner, pid);
        var (folio, booked, _) = await InHouseAsync(owner, pid);

        var order = await CreatedAsync(await owner.PostAsync(OrdersUrl(pid), new CreateOrderRequest("DINE_IN", table.Id, null, 2, null,
            [Line(menu.PaneerTikka, 2), Line(menu.DalMakhani, 1), Line(menu.Naan, 2), Line(menu.Biryani, 1)])));
        Assert.Equal((1250m, 62.5m, 1312.5m), (order.Subtotal, order.TaxAmount, order.Total));
        Assert.Equal(new[] { ("CGST", 2.5m, 31.25m), ("SGST", 2.5m, 31.25m) }, order.Taxes.Select(t => (t.Code, t.Rate, t.Amount)));
        Assert.Null(order.TaxNote);

        order = await OkAsync(await owner.PostAsync($"{OrderUrl(pid, order)}/discount", new DiscountRequest("AMOUNT", 250, "Birthday", order.Version)));
        Assert.Equal((1000m, 50m, 1050m), (order.Net, order.TaxAmount, order.Total));
        order = await ServeAllAsync(owner, pid, order);
        var charged = await OkAsync(await owner.PostAsync($"{OrderUrl(pid, order)}/charge-to-room", new ChargeToRoomRequest(booked.Id, order.Version)));

        var line = (await owner.GetJsonAsync<FolioDto>($"/api/v1/properties/{pid}/folios/{folio.Id}")).Items.Single(i => i.Source == "RESTAURANT");
        Assert.Equal((1000m, 50m, 1050m), (line.Amount, line.TaxAmount, line.Total));
        Assert.Equal(charged.Total, line.Total);
        Assert.Equal(new[] { ("CGST", 25m), ("SGST", 25m) }, line.Taxes.Select(t => (t.Code, t.Amount)));
    }

    // ------------------------------------------------------------------ paid at the restaurant

    [Fact]
    public async Task A_cash_bill_needs_the_drawer_open_and_goes_into_it_and_a_card_number_is_never_taken_as_the_reference()
    {
        var (owner, pid) = await OwnerAsync();
        var waiter = await StaffAsync("WAITER", pid);
        var menu = await MenuAsync(owner, pid);
        var order = await CreatedAsync(await waiter.PostAsync(OrdersUrl(pid), new CreateOrderRequest("TAKEAWAY", null, null, null, "Pick-up 8 pm",
            [Line(menu.Biryani, 2)])));
        Assert.Equal("Takeaway", order.Where);
        order = await ServeAllAsync(waiter, pid, order);
        var url = $"{OrderUrl(pid, order)}/pay";

        await CloseAnyShiftAsync(owner, pid);
        var (code, message) = await ProblemAsync(await waiter.PostAsync(url, new PayOrderRequest("CASH", null, order.Version)));
        Assert.Equal("VALIDATION_FAILED", code);
        Assert.Contains("cash drawer isn't open, so a restaurant bill in cash can't be recorded", message);
        Assert.Equal("Don't type the card number: card details are never stored. Enter the approval code from the terminal slip (up to 12 digits), or leave it empty.",
            await FieldErrorAsync(await waiter.PostAsync(url, new PayOrderRequest("CARD", "4111111111111111", order.Version)), "Reference"));
        Assert.Equal("Restaurant bills are paid at the restaurant: choose cash, UPI, card or bank transfer.",
            await FieldErrorAsync(await waiter.PostAsync(url, new PayOrderRequest("ONLINE", null, order.Version)), "Method"));

        var shift = await OkAsync<CashDrawerSessionDto>(await owner.PostAsync($"/api/v1/properties/{pid}/cash-drawer/open", new OpenCashDrawerRequest(500m)), HttpStatusCode.Created);
        var paid = await OkAsync(await waiter.PostAsync(url, new PayOrderRequest("CASH", null, order.Version)));
        Assert.Equal(("SETTLED", "PAID", "CASH", 660m), (paid.Status, paid.Settlement, paid.PaymentMethod, paid.Total));
        var drawer = await owner.GetJsonAsync<CashDrawerSessionDto>($"/api/v1/properties/{pid}/cash-drawer/sessions/{shift.Id}");
        Assert.Equal(1160m, drawer.Expected);
        var sale = drawer.Movements!.Single(m => m.MovementType == "POS_SALE");
        Assert.Equal((660m, $"{order.Number} · Takeaway"), (sale.Amount, sale.Description));
        await CloseAnyShiftAsync(owner, pid);

        // Today's orders add up what was settled.
        var list = await waiter.GetJsonAsync<OrderListDto>($"{OrdersUrl(pid)}?status=SETTLED");
        Assert.Contains(list.Items, i => i.Id == order.Id && i.Settlement == "PAID");
        Assert.True(list.SettledTotal >= 660m);
    }

    // ------------------------------------------------------------------ tables, rooms and the menu

    [Fact]
    public async Task A_table_has_one_open_order_room_service_is_only_for_in_house_rooms_and_a_checked_out_guest_cant_be_charged()
    {
        var (owner, pid) = await OwnerAsync();
        var waiter = await StaffAsync("WAITER", pid);
        var menu = await MenuAsync(owner, pid);
        var table = await TableAsync(owner, pid);

        // Two waiters open the same table at the same moment: one order.
        using var barrier = new Barrier(2);
        var tries = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            barrier.SignalAndWait();
            return waiter.PostAsync(OrdersUrl(pid), new CreateOrderRequest("DINE_IN", table.Id, null, 2, null, null));
        })).ToArray();
        var results = await Task.WhenAll(tries);
        var created = await CreatedAsync(results.Single(r => r.StatusCode == HttpStatusCode.Created));
        var (code, message) = await ProblemAsync(results.Single(r => r.StatusCode != HttpStatusCode.Created));
        Assert.Equal("TABLE_HAS_ORDER", code);
        Assert.Equal($"Table {table.Code} already has open order {created.Number} (opened by WAITER user). Add the dishes to that order, or settle it first.", message);

        // Room service goes to a room someone is checked in to.
        var (_, booked, room) = await InHouseAsync(owner, pid);
        var empty = (await SetupAsync(owner, pid, rooms: 1)).Rooms[0];
        Assert.Equal($"Nobody is checked in to {empty.Name}, so it can't order room service. Choose an occupied room.",
            await FieldErrorAsync(await waiter.PostAsync(OrdersUrl(pid), new CreateOrderRequest("ROOM_SERVICE", null, empty.Id, null, null, null)), "UnitId"));
        var rs = await CreatedAsync(await waiter.PostAsync(OrdersUrl(pid), new CreateOrderRequest("ROOM_SERVICE", null, room.Id, null, null, [Line(menu.Naan, 2)])));
        Assert.Equal((room.Name, booked.Reference), (rs.Where, rs.ReservationReference));
        rs = await ServeAllAsync(waiter, pid, rs);

        // The guest checks out first (balance settled at the desk): the order can't go on the room any more.
        await PayFolioAsync(owner, pid, booked.Id);
        await FrontDeskTests.OkAsync(await owner.PostAsync(FrontDeskTests.CheckOutUrl(pid, booked), new CheckOutRequest(null, "Leaving early")));
        Assert.Equal($"{booked.Reference} has checked out, so nothing more can go on the room's bill. Take the payment at the restaurant instead.",
            await FieldErrorAsync(await waiter.PostAsync($"{OrderUrl(pid, rs)}/charge-to-room", new ChargeToRoomRequest(null, rs.Version)), "ReservationId"));
        Assert.DoesNotContain((await waiter.GetJsonAsync<List<InHouseRoomDto>>($"{OrdersUrl(pid)}/rooms")), r => r.ReservationId == booked.Id);
    }

    [Fact]
    public async Task Portions_and_choices_are_priced_from_the_menu_sold_out_dishes_are_refused_and_a_price_change_leaves_old_orders_alone()
    {
        var (owner, pid) = await OwnerAsync();
        var waiter = await StaffAsync("WAITER", pid);
        var menu = await MenuAsync(owner, pid);
        var table = await TableAsync(owner, pid);
        var tikka = menu.PaneerTikka;
        var half = tikka.Variants.Single(v => v.Name == "Half");
        var spice = tikka.ModifierGroups.Single(g => g.Name == "Spice level");
        var addOns = tikka.ModifierGroups.Single(g => g.Name == "Add-ons");
        async Task<string?> Error(OrderLineRequest l, string field) =>
            await FieldErrorAsync(await waiter.PostAsync(OrdersUrl(pid), new CreateOrderRequest("TAKEAWAY", null, null, null, null, [l])), field);

        Assert.Equal("Choose the portion of Paneer Tikka: Half or Full.", await Error(new OrderLineRequest(tikka.Id, null, [spice.Modifiers[0].Id], 1, null), "Items[0].VariantId"));
        Assert.Equal("Choose the spice level for Paneer Tikka.", await Error(new OrderLineRequest(tikka.Id, half.Id, null, 1, null), "Items[0].ModifierIds"));
        Assert.Equal("Choose one spice level for Paneer Tikka, not 2.",
            await Error(new OrderLineRequest(tikka.Id, half.Id, [spice.Modifiers[0].Id, spice.Modifiers[1].Id], 1, null), "Items[0].ModifierIds"));
        Assert.Equal("The quantity must be 1–99.", await Error(new OrderLineRequest(tikka.Id, half.Id, [spice.Modifiers[0].Id], 0, null), "Items[0].Quantity"));

        // Half (₹160) + Extra cheese (₹40), less spicy: ₹200 each.
        var order = await CreatedAsync(await waiter.PostAsync(OrdersUrl(pid), new CreateOrderRequest("DINE_IN", table.Id, null, 2, null,
            [new OrderLineRequest(tikka.Id, half.Id, [spice.Modifiers[0].Id, addOns.Modifiers[0].Id], 2, "No onion")])));
        var line = Assert.Single(order.Items);
        Assert.Equal(("Paneer Tikka", "Half", 200m, 400m), (line.Name, line.VariantName, line.UnitPrice, line.Amount));
        Assert.Equal(new[] { "Less spicy", "Extra cheese (+₹40)" }, line.Modifiers);
        order = await OkAsync(await waiter.PostAsync($"{OrderUrl(pid, order)}/send", new SendToKitchenRequest(null, order.Version)));
        var kitchen = await StaffAsync("KITCHEN", pid);
        var ticketLine = (await kitchen.GetJsonAsync<KitchenDto>(KitchenUrl(pid))).Tickets.Single(t => t.OrderId == order.Id).Items.Single();
        Assert.Equal(("Paneer Tikka (Half)", "Less spicy, Extra cheese", "No onion"), (ticketLine.Name, ticketLine.Modifiers, ticketLine.Note));

        // The kitchen runs out: a waiter can't order it now. (The kitchen can't mark it sold out; the restaurant manager can.)
        Assert.Equal(HttpStatusCode.Forbidden, (await kitchen.PostAsync($"/api/v1/properties/{pid}/menu/items/{menu.Biryani.Id}/availability", new SetAvailabilityRequest(false))).StatusCode);
        var manager = await StaffAsync("RESTAURANT_MANAGER", pid);
        Assert.False((await OkAsync<MenuItemDto>(await manager.PostAsync($"/api/v1/properties/{pid}/menu/items/{menu.Biryani.Id}/availability", new SetAvailabilityRequest(false)))).IsAvailable);
        Assert.Equal("Veg Biryani is sold out for now. The kitchen switches it back on when it has more.",
            await Error(Line(menu.Biryani, 1), "Items[0].MenuItemId"));

        // The price of the half portion goes up; the order already taken keeps its price.
        var saved = await OkAsync<MenuItemDto>(await owner.PutAsync($"/api/v1/properties/{pid}/menu/items/{tikka.Id}", Save(tikka, half.Id, 180m)));
        Assert.Equal(180m, saved.Variants.Single(v => v.Name == "Half").Price);
        Assert.Equal(400m, (await waiter.GetJsonAsync<OrderDto>(OrderUrl(pid, order))).Items.Single().Amount);
    }

    [Fact]
    public async Task Sent_order_lines_settled_orders_and_kitchen_tickets_cant_be_changed_in_the_database()
    {
        var (owner, pid) = await OwnerAsync();
        var menu = await MenuAsync(owner, pid);
        var order = await CreatedAsync(await owner.PostAsync(OrdersUrl(pid), new CreateOrderRequest("TAKEAWAY", null, null, null, null, [Line(menu.Naan, 1)])));
        order = await ServeAllAsync(owner, pid, order);
        await OkAsync(await owner.PostAsync($"{OrderUrl(pid, order)}/pay", new PayOrderRequest("UPI", "UPI-REF-1234", order.Version)));
        foreach (var sql in new[]
                 {
                     $"DELETE FROM order_items WHERE order_id = '{order.Id}'",
                     $"UPDATE order_items SET quantity = 5 WHERE order_id = '{order.Id}'",
                     $"UPDATE orders SET total = 1 WHERE id = '{order.Id}'",
                     $"DELETE FROM orders WHERE id = '{order.Id}'",
                     $"UPDATE kot_items SET quantity = 5 WHERE kot_id = '{order.Kots[0].Id}'",
                     $"DELETE FROM kots WHERE id = '{order.Kots[0].Id}'",
                 })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(sql));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }
    }

    // ------------------------------------------------------------------ helpers

    internal sealed record Menu(MenuItemDto PaneerTikka, MenuItemDto DalMakhani, MenuItemDto Naan, MenuItemDto Biryani);

    /// <summary>A small menu of its own: Paneer Tikka with portions (Half ₹160, Full ₹280) and choices, Dal Makhani ₹240,
    /// Butter Naan ₹60, Veg Biryani ₹330.</summary>
    private static async Task<Menu> MenuAsync(TestClient owner, Guid pid)
    {
        var s = Suffix();
        var cat = await OkAsync<MenuCategoryDto>(await owner.PostAsync($"/api/v1/properties/{pid}/menu/categories", new SaveMenuCategoryRequest("Mains " + s, 1)),
            HttpStatusCode.Created);
        async Task<MenuItemDto> Item(string code, string name, decimal price, IReadOnlyList<SaveMenuVariant>? variants = null, IReadOnlyList<SaveModifierGroup>? groups = null) =>
            await OkAsync<MenuItemDto>(await owner.PostAsync($"/api/v1/properties/{pid}/menu/items",
                new SaveMenuItemRequest(cat.Id, $"{code}-{s}", name, null, "VEG", price, true, 0, variants, groups, null)), HttpStatusCode.Created);
        return new Menu(
            await Item("PT", "Paneer Tikka", 280m, [new SaveMenuVariant(null, "Half", 160m), new SaveMenuVariant(null, "Full", 280m)],
                [new SaveModifierGroup(null, "Spice level", 1, 1, [new SaveModifier(null, "Less spicy", 0m), new SaveModifier(null, "Extra spicy", 0m)]),
                 new SaveModifierGroup(null, "Add-ons", 0, 2, [new SaveModifier(null, "Extra cheese", 40m), new SaveModifier(null, "Mint chutney", 20m)])]),
            await Item("DM", "Dal Makhani", 240m),
            await Item("BN", "Butter Naan", 60m),
            await Item("VB", "Veg Biryani", 330m));
    }

    private static SaveMenuItemRequest Save(MenuItemDto i, Guid variantId, decimal price) => new(i.CategoryId, i.Code, i.Name, i.Description, i.FoodType, i.Price,
        i.IsActive, i.SortOrder, i.Variants.Select(v => new SaveMenuVariant(v.Id, v.Name, v.Id == variantId ? price : v.Price)).ToList(),
        i.ModifierGroups.Select(g => new SaveModifierGroup(g.Id, g.Name, g.MinSelect, g.MaxSelect, g.Modifiers.Select(m => new SaveModifier(m.Id, m.Name, m.PriceDelta)).ToList())).ToList(),
        i.Version);

    /// <summary>A plain line; Paneer Tikka is ordered Full and less spicy.</summary>
    private static OrderLineRequest Line(MenuItemDto item, short quantity) =>
        item.Variants.Count == 0 ? new(item.Id, null, null, quantity, null)
            : new(item.Id, item.Variants.Single(v => v.Name == "Full").Id, [item.ModifierGroups.Single(g => g.MinSelect > 0).Modifiers[0].Id], quantity, null);

    private static async Task<RestaurantTableDto> TableAsync(TestClient owner, Guid pid) =>
        await OkAsync<RestaurantTableDto>(await owner.PostAsync($"/api/v1/properties/{pid}/menu/tables",
            new SaveRestaurantTableRequest("T" + Suffix()[..5], 4, "Garden", 0)), HttpStatusCode.Created);

    /// <summary>Sends what is new, has the kitchen mark it ready, and serves it.</summary>
    private async Task<OrderDto> ServeAllAsync(TestClient c, Guid pid, OrderDto order)
    {
        if (order.Items.Any(i => i.Status == "NEW"))
            order = await OkAsync(await c.PostAsync($"{OrderUrl(pid, order)}/send", new SendToKitchenRequest(null, order.Version)));
        var owner = await app.SignInOwnerAsync();
        foreach (var k in order.Kots.Where(k => k.Status is "NEW" or "PREPARING"))
            await KotOkAsync(await owner.PostAsync($"{KitchenUrl(pid)}/{k.Id}/ready", new KotStepRequest(null)));
        foreach (var k in order.Kots.Where(k => k.Status != "SERVED" && k.Status != "CANCELLED"))
            order = await OkAsync(await c.PostAsync($"{OrderUrl(pid, order)}/kots/{k.Id}/serve"));
        return order;
    }

    /// <summary>A guest checked in today for two nights, their main folio and room.</summary>
    private static async Task<(FolioDto Folio, ReservationDto Booked, RoomDto Room)> InHouseAsync(TestClient owner, Guid pid)
    {
        var stay = await SetupAsync(owner, pid, rooms: 1);
        var guest = await CreateGuestAsync(owner, "Asha", "Rao");
        var booked = await BookingKit.CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, guest.Id, await WalkInAsync(owner, pid), Today, Today.AddDays(2))));
        await FrontDeskTests.OkAsync(await owner.PostAsync(FrontDeskTests.CheckInUrl(pid, booked), new CheckInRequest(null)));
        var folios = await owner.GetJsonAsync<List<FolioListItemDto>>($"/api/v1/properties/{pid}/reservations/{booked.Id}/folios");
        return (await owner.GetJsonAsync<FolioDto>($"/api/v1/properties/{pid}/folios/{folios.Single(f => f.IsPrimary).Id}"), booked, stay.Rooms[0]);
    }

    /// <summary>Pays everything a booking owes (by UPI, so no drawer is needed).</summary>
    private static async Task PayFolioAsync(TestClient owner, Guid pid, Guid reservationId)
    {
        var folios = await owner.GetJsonAsync<List<FolioListItemDto>>($"/api/v1/properties/{pid}/reservations/{reservationId}/folios");
        foreach (var f in folios.Where(f => f.Balance > 0))
            Assert.Equal(HttpStatusCode.Created, (await owner.PostAsync($"/api/v1/properties/{pid}/payments",
                new CreatePaymentRequest("UPI", f.Balance, f.Id, reservationId, "UPI123456"),
                new Dictionary<string, string> { ["Idempotency-Key"] = "pay-" + Guid.NewGuid() })).StatusCode);
    }

    private static async Task<Guid> NewPropertyAsync(TestClient c)
    {
        var code = "P" + Suffix();
        var res = await c.PostAsync("/api/v1/properties", new CreatePropertyRequest(code, $"Beach Shack {code}", null, "RESORT", "Asia/Kolkata", "INR",
            null, null, null, null, null, "30", null, "IN", null, null, new TimeOnly(14, 0), new TimeOnly(11, 0), null, null));
        Assert.True(res.StatusCode == HttpStatusCode.Created, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<PropertyDto>())!.Id;
    }

    private static async Task CloseAnyShiftAsync(TestClient c, Guid pid)
    {
        var drawer = await c.GetJsonAsync<CashDrawerDto>($"/api/v1/properties/{pid}/cash-drawer");
        if (drawer.Current is { } open)
            await OkAsync<CashDrawerSessionDto>(await c.PostAsync($"/api/v1/properties/{pid}/cash-drawer/close", new CloseCashDrawerRequest(open.Expected)));
    }

    private async Task<(TestClient Owner, Guid PropertyId)> OwnerAsync() => (await app.SignInOwnerAsync(), await app.MainPropertyIdAsync());

    private async Task<TestClient> StaffAsync(string role, Guid pid)
    {
        var (email, pw) = await app.CreateUserAsync(role, pid);
        return await app.SignInAsync(email, pw);
    }

    private static string OrdersUrl(Guid pid) => $"/api/v1/properties/{pid}/orders";
    private static string OrderUrl(Guid pid, OrderDto o) => $"/api/v1/properties/{pid}/orders/{o.Id}";
    private static string KitchenUrl(Guid pid) => $"/api/v1/properties/{pid}/kitchen/kots";

    private static Task<OrderDto> CreatedAsync(HttpResponseMessage res) => OkAsync<OrderDto>(res, HttpStatusCode.Created);
    private static Task<OrderDto> OkAsync(HttpResponseMessage res) => OkAsync<OrderDto>(res);
    private static Task<KotDto> KotOkAsync(HttpResponseMessage res) => OkAsync<KotDto>(res);

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
