using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Resort.Contracts;
using Resort.Tests.Support;
using static Resort.Tests.Support.BookingKit;

namespace Resort.Tests.Integration;

/// <summary>Sprint 23 — reports and dashboards: occupancy, ADR and RevPAR of a month worked out by hand (a dorm bed is one
/// unit like a room; out of order is not available; a cancelled booking sells nothing; a reversed night earns nothing; the
/// nights of a stay that runs into the next month count in that month); the same in the Power BI view; the role that
/// Power BI signs in with sees the views and nothing else; money hidden without financial access and pay without salary
/// access; supplier bills and pay in the budget's actuals and the department costs; commission rates; a refresh replaces
/// the days it covers (twice at once is safe). Each test uses a property of its own. Real API, real PostgreSQL.</summary>
public class ReportTests(FrontDeskFactory app) : IClassFixture<FrontDeskFactory>
{
    // ------------------------------------------------------------------ required: ADR and RevPAR against a hand-calculated month

    /// <summary>
    /// The fixture month M (the month after next, so every booking can be made through the API): 4 garden rooms and a dorm
    /// with 2 beds, so 6 units a night. Room 4 is out of order for the nights of the 20th to the 24th (5 unit-nights).
    /// <list type="bullet">
    /// <item>A: Booking.com, room, the 1st for 10 nights, 2 adults, ₹3,500 a night = ₹35,000; a ₹1,000 dinner (not room revenue).</item>
    /// <item>B: walk-in, room, the 5th for 2 nights at ₹4,200; one night reversed (given free) = ₹4,200.</item>
    /// <item>C: walk-in, dorm bed, the 1st for 3 nights at ₹900 = ₹2,700.</item>
    /// <item>D: walk-in, room, the last 2 nights of M and 2 of the next month at ₹3,000: ₹6,000 in M.</item>
    /// <item>E: walk-in, room, cancelled: sells nothing.</item>
    /// </list>
    /// Sold = 10 + 2 + 3 + 2 = 17 unit-nights. Room revenue = 35,000 + 4,200 + 2,700 + 6,000 = ₹47,900.
    /// Available = 6 × days − 5. ADR = 47,900 / 17 = ₹2,817.65. RevPAR = 47,900 / available, e.g. 31 days: / 181 = ₹264.64.
    /// </summary>
    [Fact]
    public async Task Adr_and_revpar_of_a_month_match_the_hand_calculation()
    {
        var s = await SetupAsync();
        var rooms = await BookingKit.SetupAsync(s.Owner, s.Pid, rooms: 4);
        var dorm = await BookingKit.SetupAsync(s.Owner, s.Pid, rooms: 1, mode: "BED", beds: 2, price: 900m);
        var m = new DateOnly(Today.Year, Today.Month, 1).AddMonths(2);
        var days = DateTime.DaysInMonth(m.Year, m.Month);
        var end = m.AddDays(days - 1);
        var walkIn = await WalkInAsync(s.Owner, s.Pid);
        var bookingCom = (await s.Owner.GetJsonAsync<List<ReservationSourceDto>>($"{Url(s.Pid)}/reservation-sources")).Single(x => x.Code == "BOOKING_COM").Id;
        var guest = await CreateGuestAsync(s.Owner, "Report");

        var a = await CreatedAsync(await BookAsync(s.Owner, s.Pid, NewBooking(rooms, guest.Id, bookingCom, m, m.AddDays(10), adults: 2)));
        var b = await CreatedAsync(await BookAsync(s.Owner, s.Pid, NewBooking(rooms, guest.Id, walkIn, m.AddDays(4), m.AddDays(6))));
        var c = await CreatedAsync(await BookAsync(s.Owner, s.Pid, NewBooking(dorm, guest.Id, walkIn, m, m.AddDays(3))));
        var d = await CreatedAsync(await BookAsync(s.Owner, s.Pid, NewBooking(rooms, guest.Id, walkIn, end.AddDays(-1), end.AddDays(3))));
        var e = await CreatedAsync(await BookAsync(s.Owner, s.Pid, NewBooking(rooms, guest.Id, walkIn, m.AddDays(12), m.AddDays(14))));
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.PostAsync($"{Url(s.Pid)}/reservations/{e.Id}/cancel",
            new ReservationStatusRequest("Guest changed plans"))).StatusCode);
        // Room 4 out of order on the nights of the 20th to the 24th (none of the bookings is in it then).
        var block = await s.Owner.PostAsync($"{Url(s.Pid)}/blocks", new CreateBlockRequest(rooms.Rooms[3].Id, null, "OUT_OF_ORDER", m.AddDays(19), m.AddDays(24), "Roof leak"));
        Assert.True(block.StatusCode == HttpStatusCode.Created, await block.Content.ReadAsStringAsync());

        // The room nights on the bills, as the nightly charge would post them (it can't run ahead of time).
        await NightsAsync(a.Id, m, 10, 3_500m);
        await NightsAsync(b.Id, m.AddDays(4), 2, 4_200m);
        await NightsAsync(c.Id, m, 3, 900m);
        await NightsAsync(d.Id, end.AddDays(-1), 4, 3_000m);
        await ExecuteAsync($"""
            INSERT INTO folio_items (organization_id, property_id, folio_id, reservation_id, item_type, category, description, service_date,
                                     quantity, unit_price, amount, tax_amount, total, source, reverses_item_id, reason)
            SELECT organization_id, property_id, folio_id, reservation_id, 'REVERSAL', category, 'Reversal: ' || description, service_date,
                   -quantity, unit_price, -amount, -tax_amount, -total, source, id, 'Second night free for the delay'
              FROM folio_items WHERE reservation_id = '{b.Id}' AND service_date = '{m.AddDays(5):yyyy-MM-dd}';
            INSERT INTO folio_items (organization_id, property_id, folio_id, reservation_id, item_type, category, description, service_date,
                                     quantity, unit_price, amount, tax_amount, total, source)
            SELECT organization_id, property_id, id, reservation_id, 'CHARGE', 'FOOD', 'Dinner', '{m.AddDays(2):yyyy-MM-dd}', 1, 1000, 1000, 50, 1050, 'MANUAL'
              FROM folios WHERE reservation_id = '{a.Id}';
            """);
        await RefreshAsync(s, m, end.AddDays(3));

        var available = 6 * days - 5;
        var month = await OkAsync<OccupancyReportDto>(await s.Owner.GetAsync($"{Url(s.Pid)}/reports/occupancy?by=month&from={m:yyyy-MM-dd}&to={end:yyyy-MM-dd}"));
        var row = Assert.Single(month.Rows);
        Assert.Equal((m, available, 17, 4 * days - 5, 14, 2 * days, 3, 5), (row.Period, row.UnitsAvailable, row.UnitsSold, row.RoomsAvailable, row.RoomsSold,
            row.BedsAvailable, row.BedsSold, row.OutOfOrder));
        Assert.Equal(47_900m, row.RoomRevenue);
        Assert.Equal(2_817.65m, row.Adr);
        // RevPAR and occupancy by hand for each length of month.
        var (revPar, occupancy) = days switch
        {
            28 => (293.87m, 10.4m),   // 47,900 / 163; 17 / 163
            29 => (283.43m, 10.1m),   // 47,900 / 169; 17 / 169
            30 => (273.71m, 9.7m),    // 47,900 / 175; 17 / 175
            _ => (264.64m, 9.4m),     // 47,900 / 181; 17 / 181
        };
        Assert.Equal((revPar, occupancy), (row.RevPar, row.OccupancyPercent));
        // Arrivals A, B, C, D (E was cancelled); departures A, B, C (D leaves next month). Guest nights 2 × 10 + 2 + 3 + 2.
        Assert.Equal((4, 3, 27), (row.Arrivals, row.Departures, row.GuestNights));

        // By day: the 5th has A and B in rooms (the dorm guest left on the 4th); the 21st has one room out of order.
        var byDay = await OkAsync<OccupancyReportDto>(await s.Owner.GetAsync($"{Url(s.Pid)}/reports/occupancy?from={m:yyyy-MM-dd}&to={end:yyyy-MM-dd}"));
        Assert.Equal(days, byDay.Rows.Count);
        Assert.Equal((6, 2, 33.3m, 7_700m, 3_850m), (byDay.Rows[4].UnitsAvailable, byDay.Rows[4].UnitsSold, byDay.Rows[4].OccupancyPercent,
            byDay.Rows[4].RoomRevenue, byDay.Rows[4].Adr));
        Assert.Equal((5, 0, 1), (byDay.Rows[20].UnitsAvailable, byDay.Rows[20].UnitsSold, byDay.Rows[20].OutOfOrder));
        Assert.Equal((row.UnitsSold, row.RoomRevenue, row.Adr, row.RevPar), (byDay.Total.UnitsSold, byDay.Total.RoomRevenue, byDay.Total.Adr, byDay.Total.RevPar));

        // Revenue: the dinner is food, not rooms; the GST on it is charged.
        var revenue = await OkAsync<RevenueReportDto>(await s.Owner.GetAsync($"{Url(s.Pid)}/reports/revenue?by=month&from={m:yyyy-MM-dd}&to={end:yyyy-MM-dd}"));
        Assert.Equal((47_900m, 1_000m, 48_900m, 50m), (revenue.Total.Room, revenue.Total.Fnb, revenue.Total.Total, revenue.Total.Tax));

        // Booking sources: Booking.com sold A's 10 nights for ₹35,000 and keeps 18 % (₹6,300).
        var channels = await OkAsync<ChannelReportDto>(await s.Owner.GetAsync($"{Url(s.Pid)}/reports/channels?from={m:yyyy-MM-dd}&to={end:yyyy-MM-dd}"));
        var bcom = channels.Rows.Single(r => r.Code == "BOOKING_COM");
        Assert.Equal((10, 35_000m, 18m, 6_300m, 28_700m, 3_500m), (bcom.UnitsSold, bcom.RoomRevenue, bcom.CommissionPercent, bcom.Commission, bcom.Net, bcom.Adr));
        var walk = channels.Rows.Single(r => r.Code == "WALK_IN");
        Assert.Equal((7, 12_900m, 0m, 12_900m), (walk.UnitsSold, walk.RoomRevenue, walk.Commission, walk.Net));
        Assert.Equal((17, 47_900m, 6_300m, 41_600m), (channels.Total.UnitsSold, channels.Total.RoomRevenue, channels.Total.Commission, channels.Total.Net));

        // The Power BI view says the same.
        await using var conn = new NpgsqlConnection(app.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            $"SELECT units_available, units_sold, occupancy_percent, room_revenue, adr, revpar FROM reporting.v_monthly WHERE property_code = '{s.Code}' AND month = '{m:yyyy-MM-dd}'", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        Assert.Equal(((long)available, 17L, occupancy, 47_900m, 2_817.65m, revPar),
            (Convert.ToInt64(r[0], System.Globalization.CultureInfo.InvariantCulture), Convert.ToInt64(r[1], System.Globalization.CultureInfo.InvariantCulture), r.GetDecimal(2), r.GetDecimal(3), r.GetDecimal(4), r.GetDecimal(5)));
    }

    // ------------------------------------------------------------------ Power BI

    [Fact]
    public async Task The_power_bi_role_reads_the_views_and_nothing_else()
    {
        var s = await SetupAsync();
        await RefreshAsync(s, Today.AddDays(-3), Today);
        await using var conn = new NpgsqlConnection(app.ConnectionString);
        await conn.OpenAsync();
        async Task<object?> AsRole(string sql)
        {
            await using var tx = await conn.BeginTransactionAsync();
            await using (var set = new NpgsqlCommand("SET LOCAL ROLE resort_reporting", conn, tx)) await set.ExecuteNonQueryAsync();
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            try { return await cmd.ExecuteScalarAsync(); }
            finally { await tx.RollbackAsync(); }
        }
        Assert.Equal(4L, await AsRole($"SELECT count(*) FROM reporting.v_daily WHERE property_code = '{s.Code}'"));
        Assert.NotNull(await AsRole("SELECT count(*) FROM reporting.v_monthly"));
        Assert.NotNull(await AsRole("SELECT count(*) FROM reporting.v_gst_monthly"));
        foreach (var table in new[] { "reservations", "guests", "folio_items", "employee_compensation", "users", "reporting.daily_stats" })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => AsRole($"SELECT count(*) FROM {table}"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }
        var write = await Assert.ThrowsAsync<PostgresException>(() => AsRole("SELECT reporting.refresh(gen_random_uuid(), current_date, current_date)"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, write.SqlState);
    }

    // ------------------------------------------------------------------ who sees what

    [Fact]
    public async Task Money_is_hidden_without_financial_access_and_pay_without_salary_access()
    {
        var s = await SetupAsync();
        await RefreshAsync(s, Today.AddDays(-40), Today.AddDays(14));

        // The manager runs the place: occupancy and arrivals, no money.
        var manager = await StaffAsync("MANAGER", s.Pid);
        var dash = await OkAsync<DashboardDto>(await manager.GetAsync($"{Url(s.Pid)}/reports/dashboard"));
        Assert.Equal(new ReportAccessDto(true, false, false, true), dash.Access);
        Assert.Equal(14, dash.Ahead.Count);
        Assert.Null(dash.MonthToDate.RoomRevenue);
        Assert.Null(dash.MonthToDate.Adr);
        Assert.Null(dash.MonthToDate.Labour);
        Assert.Null(dash.LastMonth);
        Assert.Equal(12, dash.Months.Count);
        Assert.All(dash.Months, x => Assert.Null(x.Revenue));
        Assert.All(dash.Months, x => Assert.Null(x.Adr));
        Assert.Null(dash.Channels);
        Assert.Null(dash.Gst);
        Assert.NotNull(dash.Budget);
        var occ = await OkAsync<OccupancyReportDto>(await manager.GetAsync($"{Url(s.Pid)}/reports/occupancy"));
        Assert.Null(occ.Total.RoomRevenue);
        Assert.Null(occ.Total.Adr);
        foreach (var url in new[] { "revenue", "channels", "gst", "costs", "pnl", "commission" })
            Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync($"{Url(s.Pid)}/reports/{url}")).StatusCode);
        var monthM = await OkAsync<MonthReportDto>(await manager.GetAsync($"{Url(s.Pid)}/reports/month"));
        Assert.Null(monthM.Pnl);
        Assert.Null(monthM.This.TotalRevenue);

        // The accountant sees money, but not pay.
        var accountant = await StaffAsync("ACCOUNTANT", s.Pid);
        dash = await OkAsync<DashboardDto>(await accountant.GetAsync($"{Url(s.Pid)}/reports/dashboard"));
        Assert.Equal(new ReportAccessDto(true, true, false, true), dash.Access);
        Assert.Equal(0m, dash.MonthToDate.RoomRevenue);
        Assert.Null(dash.MonthToDate.Labour);
        Assert.Equal(12, dash.Months.Count);
        var costs = await OkAsync<CostReportDto>(await accountant.GetAsync($"{Url(s.Pid)}/reports/costs"));
        Assert.False(costs.PayIncluded);
        Assert.All(costs.Rows, r => Assert.Null(r.Labour));
        var pnl = await OkAsync<PnlReportDto>(await accountant.GetAsync($"{Url(s.Pid)}/reports/pnl"));
        Assert.DoesNotContain(pnl.Lines, l => l.Key == "PAY");
        Assert.Equal("Operating profit before pay", pnl.Lines[^1].Label);
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.PutAsync($"{Url(s.Pid)}/reports/commission", new SaveCommissionRatesRequest([]))).StatusCode);

        // The owner sees everything.
        dash = await OkAsync<DashboardDto>(await s.Owner.GetAsync($"{Url(s.Pid)}/reports/dashboard"));
        Assert.Equal(new ReportAccessDto(true, true, true, true), dash.Access);
        Assert.Equal(0m, dash.MonthToDate.Labour);
        pnl = await OkAsync<PnlReportDto>(await s.Owner.GetAsync($"{Url(s.Pid)}/reports/pnl"));
        Assert.Contains(pnl.Lines, l => l.Key == "PAY");

        // A housekeeper has no reports; another organisation can't see this property's.
        var hk = await StaffAsync("HOUSEKEEPER", s.Pid);
        Assert.Equal(HttpStatusCode.Forbidden, (await hk.GetAsync($"{Url(s.Pid)}/reports/dashboard")).StatusCode);
        var other = await app.CreateSecondTenantAsync();
        var stranger = await app.SignInAsync(other.OwnerEmail, other.Password);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"{Url(s.Pid)}/reports/dashboard")).StatusCode);
    }

    [Fact]
    public async Task A_report_period_that_cant_be_shown_says_why_under_the_field()
    {
        var s = await SetupAsync();
        Assert.Equal("The start date must be on or before the end date.",
            await FieldErrorAsync(await s.Owner.GetAsync($"{Url(s.Pid)}/reports/occupancy?from=2026-05-10&to=2026-05-01"), "from"));
        Assert.Equal("Choose at most a year by day, or show it by month.",
            await FieldErrorAsync(await s.Owner.GetAsync($"{Url(s.Pid)}/reports/revenue?from=2025-01-01&to=2026-03-01"), "to"));
        Assert.Equal("Choose at most 25 months.",
            await FieldErrorAsync(await s.Owner.GetAsync($"{Url(s.Pid)}/reports/pnl?from=2023-01-01&to=2026-03-01"), "to"));
        var next = new DateOnly(Today.Year, Today.Month, 1).AddMonths(1);
        Assert.EndsWith("hasn't started yet. Choose this month or an earlier one.",
            await FieldErrorAsync(await s.Owner.GetAsync($"{Url(s.Pid)}/reports/month?month={next:yyyy-MM-dd}"), "month"));
        Assert.NotNull(await FieldErrorAsync(await s.Owner.GetAsync($"{Url(s.Pid)}/reports/occupancy?by=week"), "by"));
    }

    // ------------------------------------------------------------------ budget actuals: supplier bills and pay (Sprint 22's open point)

    [Fact]
    public async Task Supplier_bills_and_pay_count_in_the_budget_and_pay_only_for_those_who_may_see_it()
    {
        var s = await SetupAsync();
        var setup = await OkAsync<ExpenseSetupDto>(await s.Owner.GetAsync($"{Url(s.Pid)}/expenses/setup"));
        var hkCentre = setup.CostCentres.Single(c => c.Code == "HK").Id;

        // A housekeeping bill of ₹1,050 (₹1,000 + 5 % GST; the property isn't GST registered, so all of it is the cost).
        var inv = $"{Url(s.Pid)}/inventory";
        var store = await OkAsync<StockLocationDto>(await s.Owner.PostAsync($"{inv}/locations", new SaveStockLocationRequest("STORE", "Main store", false, 1)), HttpStatusCode.Created);
        var group = await OkAsync<InventoryCategoryDto>(await s.Owner.PostAsync($"{inv}/categories", new SaveInventoryCategoryRequest("Cleaning", 1)), HttpStatusCode.Created);
        var units = (await s.Owner.GetJsonAsync<InventoryDto>(inv)).Units;
        var soap = await OkAsync<InventoryItemDto>(await s.Owner.PostAsync($"{inv}/items", new SaveInventoryItemRequest(group.Id, "SOAP", "Floor cleaner",
            units.Single(u => u.Code == "l").Id, 100m, null, true, null, store.Id, null)), HttpStatusCode.Created);
        var supplier = await OkAsync<SupplierDto>(await s.Owner.PostAsync($"{Url(s.Pid)}/purchasing/suppliers",
            new SaveSupplierRequest("CLEAN", "Margao Cleaning Supplies", "30MCSPL1234K1Z" + Resort.Domain.Tax.Gstin.CheckDigit("30MCSPL1234K1Z"), "30", "Ravi Naik", "+91 98220 12345", null, null, 0, null)), HttpStatusCode.Created);
        var order = await OkAsync<PurchaseOrderDto>(await s.Owner.PostAsync($"{Url(s.Pid)}/purchasing/orders",
            new SavePurchaseOrderRequest(supplier.Id, store.Id, null, null, null, [new SavePurchaseOrderLine(soap.Id, 10m, 100m, 5m)], null)), HttpStatusCode.Created);
        var orderUrl = $"{Url(s.Pid)}/purchasing/orders/{order.Id}";
        order = await OkAsync<PurchaseOrderDto>(await s.Owner.PostAsync($"{orderUrl}/submit", new PurchaseOrderStepRequest(null, order.Version)));
        order = await OkAsync<PurchaseOrderDto>(await s.Owner.PostAsync($"{orderUrl}/receipts",
            new ReceiveGoodsRequest(null, null, null, null, [new ReceiveGoodsLine(order.Lines[0].Id, 10m)])), HttpStatusCode.Created);
        var bill = await OkAsync<SupplierInvoiceDto>(await s.Owner.PostAsync($"{orderUrl}/invoices", new RecordSupplierInvoiceRequest("MCS-12", Today, null, null,
            [new SupplierInvoiceLineRequest(order.Lines[0].Id, 10m, 100m, 5m)], hkCentre)), HttpStatusCode.Created);
        Assert.Equal((1_050m, false, hkCentre, "Housekeeping"), (bill.Total, bill.ItcEligible, bill.CostCentreId, bill.CostCentreName));

        // A housekeeper on ₹1,000 a day of this month (monthly pay = days in the month × ₹1,000), from the 1st.
        var first = new DateOnly(Today.Year, Today.Month, 1);
        var dept = (await s.Owner.GetJsonAsync<List<DepartmentDto>>($"{Url(s.Pid)}/departments")).FirstOrDefault(x => x.Code == "HK")
            ?? await OkAsync<DepartmentDto>(await s.Owner.PostAsync($"{Url(s.Pid)}/departments", new SaveDepartmentRequest("HK", "Housekeeping", CostCentreId: hkCentre)), HttpStatusCode.Created);
        Assert.Equal(hkCentre, dept.CostCentreId);
        var emp = await OkAsync<EmployeeDto>(await s.Owner.PostAsync($"{Url(s.Pid)}/employees",
            new SaveEmployeeRequest("Rekha", "Gaonkar", null, null, null, dept.Id, null, "FULL_TIME", first, null, null, null, null, null, null)), HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.Created, (await s.Owner.PostAsync($"{Url(s.Pid)}/employees/{emp.Id}/compensation",
            new RecordCompensationRequest(first, "MONTHLY", DateTime.DaysInMonth(first.Year, first.Month) * 1_000m, 0m, false, false, null))).StatusCode);
        var pay = Today.Day * 1_000m;

        // The owner: the bill and the pay so far are in the housekeeping actual.
        var report = await OkAsync<BudgetReportDto>(await s.Owner.GetAsync($"{Url(s.Pid)}/budgets/report"));
        Assert.True(report.PayIncluded);
        var hk = report.Rows.Single(r => r.CostCentreCode == "HK");
        Assert.Equal((0m, 1_050m, pay, 1_050m + pay, 1_050m + pay), (hk.Expenses, hk.SupplierBills, hk.Pay, hk.ActualToDate, hk.MonthActual));

        // The accountant reads the budget but not pay: the bill is there, pay is not (and the total doesn't give it away).
        var accountant = await StaffAsync("ACCOUNTANT", s.Pid);
        report = await OkAsync<BudgetReportDto>(await accountant.GetAsync($"{Url(s.Pid)}/budgets/report"));
        Assert.False(report.PayIncluded);
        hk = report.Rows.Single(r => r.CostCentreCode == "HK");
        Assert.Equal((1_050m, null, 1_050m), (hk.SupplierBills, hk.Pay, hk.ActualToDate));
        Assert.Equal(1_050m, report.Total.ActualToDate);

        // The reports agree, after the numbers are added up.
        await RefreshAsync(s, first, Today);
        var costs = await OkAsync<CostReportDto>(await s.Owner.GetAsync($"{Url(s.Pid)}/reports/costs?from={first:yyyy-MM-dd}&to={Today:yyyy-MM-dd}"));
        var dep = costs.Departments.Single(x => x.Code == "HK");
        Assert.Equal((0m, 1_050m, pay, 1_050m + pay), (dep.Expenses, dep.SupplierBills, dep.Labour, dep.Total));
        Assert.Equal(pay, costs.Total.Labour);
        costs = await OkAsync<CostReportDto>(await accountant.GetAsync($"{Url(s.Pid)}/reports/costs?from={first:yyyy-MM-dd}&to={Today:yyyy-MM-dd}"));
        Assert.Equal((1_050m, (decimal?)null, 1_050m), (costs.Departments.Single(x => x.Code == "HK").SupplierBills, costs.Departments.Single(x => x.Code == "HK").Labour,
            costs.Departments.Single(x => x.Code == "HK").Total));

        // The bill can be moved to another department; the move is audited.
        var fo = setup.CostCentres.Single(c => c.Code == "FO").Id;
        var moved = await OkAsync<SupplierInvoiceDto>(await s.Owner.PutAsync($"{Url(s.Pid)}/purchasing/invoices/{bill.Id}/department",
            new SetSupplierInvoiceDepartmentRequest(fo, bill.Version)));
        Assert.Equal("Front office", moved.CostCentreName);
        report = await OkAsync<BudgetReportDto>(await s.Owner.GetAsync($"{Url(s.Pid)}/budgets/report"));
        Assert.Equal((1_050m, pay), (report.Rows.Single(r => r.CostCentreCode == "FO").SupplierBills, report.Rows.Single(r => r.CostCentreCode == "HK").ActualToDate));
    }

    // ------------------------------------------------------------------ commission and refresh

    [Fact]
    public async Task Commission_rates_are_set_per_property_and_go_back_to_the_default()
    {
        var s = await SetupAsync();
        var rates = await OkAsync<List<CommissionRateDto>>(await s.Owner.GetAsync($"{Url(s.Pid)}/reports/commission"));
        var bcom = rates.Single(r => r.Code == "BOOKING_COM");
        Assert.Equal((18m, 18m, false), (bcom.Percent, bcom.DefaultPercent, bcom.IsSet));
        Assert.Equal(10m, rates.Single(r => r.Code == "TRAVEL_AGENT").Percent);

        rates = await OkAsync<List<CommissionRateDto>>(await s.Owner.PutAsync($"{Url(s.Pid)}/reports/commission",
            new SaveCommissionRatesRequest([new CommissionRateRequest(bcom.SourceId, 15.5m)])));
        Assert.Equal((15.5m, true), (rates.Single(r => r.Code == "BOOKING_COM").Percent, rates.Single(r => r.Code == "BOOKING_COM").IsSet));
        Assert.Equal(15.5m, await ScalarAsync($"SELECT reporting.commission_percent('{s.Pid}', '{bcom.SourceId}')"));
        Assert.StartsWith("Enter the commission as 0 to 50 %", await FieldErrorAsync(await s.Owner.PutAsync($"{Url(s.Pid)}/reports/commission",
            new SaveCommissionRatesRequest([new CommissionRateRequest(bcom.SourceId, 51m)])), "Rates[0].Percent"));

        rates = await OkAsync<List<CommissionRateDto>>(await s.Owner.PutAsync($"{Url(s.Pid)}/reports/commission",
            new SaveCommissionRatesRequest([new CommissionRateRequest(bcom.SourceId, null)])));
        Assert.Equal((18m, false), (rates.Single(r => r.Code == "BOOKING_COM").Percent, rates.Single(r => r.Code == "BOOKING_COM").IsSet));
    }

    [Fact]
    public async Task A_refresh_replaces_the_days_it_covers_and_two_at_once_are_safe()
    {
        var s = await SetupAsync();
        var rooms = await BookingKit.SetupAsync(s.Owner, s.Pid, rooms: 2);
        var from = Today.AddDays(-20);
        var to = Today.AddDays(20);
        var fresh = await RefreshAsync(s, from, to);
        Assert.Equal((from, to), (fresh.From, fresh.To));
        Assert.NotNull(fresh.RefreshedAt);
        Assert.Equal(41L, await ScalarAsync($"SELECT count(*) FROM reporting.daily_stats WHERE property_id = '{s.Pid}'"));

        // Twice at once: one waits for the other; the days are there once.
        await Task.WhenAll(RefreshAsync(s, from, to), RefreshAsync(s, from, to), RefreshAsync(s, Today, Today.AddDays(5)));
        Assert.Equal(41L, await ScalarAsync($"SELECT count(*) FROM reporting.daily_stats WHERE property_id = '{s.Pid}'"));

        // A booking made since shows after the next refresh.
        var guest = await CreateGuestAsync(s.Owner, "Fresh");
        await CreatedAsync(await BookAsync(s.Owner, s.Pid, NewBooking(rooms, guest.Id, await WalkInAsync(s.Owner, s.Pid), Today.AddDays(2), Today.AddDays(4))));
        var dash = await OkAsync<DashboardDto>(await s.Owner.GetAsync($"{Url(s.Pid)}/reports/dashboard"));
        Assert.Equal(0, dash.Ahead.Sum(x => x.UnitsSold));
        await RefreshAsync(s, Today, Today.AddDays(20));
        dash = await OkAsync<DashboardDto>(await s.Owner.GetAsync($"{Url(s.Pid)}/reports/dashboard"));
        Assert.Equal(2, dash.Ahead.Sum(x => x.UnitsSold));
        Assert.Equal((2, 0), (dash.TodayStats.UnitsAvailable, dash.TodayStats.UnitsSold));

        // More than 800 days at once is refused, under the field.
        Assert.Equal("Refresh at most 800 days at a time (about two years).", await FieldErrorAsync(await s.Owner.PostAsync($"{Url(s.Pid)}/reports/refresh",
            new RefreshReportsRequest(Today.AddDays(-900), Today)), "To"));
        // And the database refuses it too.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync($"SELECT reporting.refresh('{s.Pid}', current_date - 900, current_date)"));
        Assert.Equal("ck_reporting_refresh_range", ex.ConstraintName);
    }

    // ------------------------------------------------------------------ setup and helpers

    private sealed record Setup(Guid Pid, TestClient Owner, string Code);

    private async Task<Setup> SetupAsync()
    {
        app.Clock.At = null;
        var owner = await app.SignInOwnerAsync();
        var code = "R" + Suffix();
        var res = await owner.PostAsync("/api/v1/properties", new CreatePropertyRequest(code, $"Report Shack {code}", null, "RESORT", "Asia/Kolkata", "INR",
            null, null, null, null, null, "30", null, "IN", null, null, new TimeOnly(14, 0), new TimeOnly(11, 0), null, null));
        Assert.True(res.StatusCode == HttpStatusCode.Created, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return new Setup((await res.Content.ReadFromJsonAsync<PropertyDto>())!.Id, owner, code);
    }

    /// <summary>One room night a night on the booking's bill, from <paramref name="from"/>.</summary>
    private Task NightsAsync(Guid reservationId, DateOnly from, int nights, decimal rate) => ExecuteAsync($"""
        INSERT INTO folio_items (organization_id, property_id, folio_id, reservation_id, item_type, category, description, service_date,
                                 quantity, unit_price, amount, tax_amount, total, source)
        SELECT f.organization_id, f.property_id, f.id, f.reservation_id, 'CHARGE', 'ROOM', 'Room night', d::date, 1, {rate}, {rate}, 0, {rate}, 'ROOM_NIGHT'
          FROM folios f, generate_series('{from:yyyy-MM-dd}'::date, '{from.AddDays(nights - 1):yyyy-MM-dd}'::date, interval '1 day') d
         WHERE f.reservation_id = '{reservationId}'
        """);

    private static async Task<ReportFreshnessDto> RefreshAsync(Setup s, DateOnly from, DateOnly to) =>
        await OkAsync<ReportFreshnessDto>(await s.Owner.PostAsync($"{Url(s.Pid)}/reports/refresh", new RefreshReportsRequest(from, to)));

    private async Task<TestClient> StaffAsync(string role, Guid pid)
    {
        var (email, pw) = await app.CreateUserAsync(role, pid);
        return await app.SignInAsync(email, pw);
    }

    private static string Url(Guid pid) => $"/api/v1/properties/{pid}";

    private static async Task<T> OkAsync<T>(HttpResponseMessage res, HttpStatusCode status = HttpStatusCode.OK)
    {
        Assert.True(res.StatusCode == status, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<T>())!;
    }

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

/// <summary>Sprint 23 acceptance: the owner's dashboard reads only the summary, so it loads in well under a second on the
/// dev seed's year of history (about 1,600 stays with their bills and payments).</summary>
public class ReportSpeedTests(ResortApiFactory app) : IClassFixture<ResortApiFactory>
{
    [Fact]
    public async Task The_owner_dashboard_loads_in_under_a_second_on_a_year_of_seed_data()
    {
        await app.Services.GetRequiredService<Resort.Infrastructure.Persistence.DevSeeder>().RunAsync(CancellationToken.None);
        var owner = await app.SignInOwnerAsync();
        var pid = await app.MainPropertyIdAsync();
        var stays = (int)(await app.WithDbAsync(async db => (object)await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .CountAsync(Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.IgnoreQueryFilters(db.Reservations), r => r.PropertyId == pid && r.Status == "CHECKED_OUT")));
        Assert.True(stays > 1_200, $"{stays} past stays");

        var watch = Stopwatch.StartNew();
        var res = await owner.PostAsync($"/api/v1/properties/{pid}/reports/refresh", new RefreshReportsRequest());
        Assert.True(res.StatusCode == HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var fresh = (await res.Content.ReadFromJsonAsync<ReportFreshnessDto>())!;
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"the full rebuild took {watch.ElapsedMilliseconds} ms ({fresh.DurationMs} ms in the database)");

        await owner.GetAsync($"/api/v1/properties/{pid}/reports/dashboard");   // warm up (the first call compiles the queries)
        var times = new List<long>();
        DashboardDto? dash = null;
        for (var i = 0; i < 5; i++)
        {
            watch.Restart();
            var r = await owner.GetAsync($"/api/v1/properties/{pid}/reports/dashboard");
            dash = (await r.Content.ReadFromJsonAsync<DashboardDto>())!;
            times.Add(watch.ElapsedMilliseconds);
        }
        times.Sort();
        Assert.True(times[2] < 1_000, $"the dashboard took {string.Join(", ", times)} ms");
        // A year of history: every one of the last 12 months has revenue, the month a year ago can be compared.
        Assert.Equal(12, dash!.Months.Count);
        Assert.All(dash.Months, m => Assert.True(m.Revenue > 0, $"{m.Label}: {m.Revenue}"));
        Assert.NotNull(dash.LastYear);
        Assert.True(dash.LastYear!.UnitsSold > 0);
        Assert.InRange(dash.Months[0].OccupancyPercent!.Value, 20m, 90m);
    }
}

