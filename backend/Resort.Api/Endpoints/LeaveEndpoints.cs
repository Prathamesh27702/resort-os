using Resort.Api.Infrastructure;
using Resort.Application.Common;
using Resort.Application.Leave;
using Resort.Contracts;

namespace Resort.Api.Endpoints;

/// <summary>Sprint 19 — leave, holidays and payroll export: leave types and policies, holidays, balances and their
/// ledger, accruals, leave requests (asked for, approved or refused, cancelled), and the monthly payroll file.</summary>
public static class LeaveEndpoints
{
    public static void MapLeaveEndpoints(this IEndpointRouteBuilder app)
    {
        var p = app.MapGroup("/api/v1/properties/{propertyId:guid}");

        var l = p.MapGroup("/leave").WithTags("Leave");
        l.MapGet("/types", (Guid propertyId, LeaveService s, CancellationToken ct) => s.TypesAsync(propertyId, ct))
            .RequirePermission(Perm.LeaveCreate);
        l.MapPost("/types", async (Guid propertyId, SaveLeaveTypeRequest body, LeaveService s, CancellationToken ct) =>
            {
                var x = await s.SaveTypeAsync(propertyId, null, body, ct);
                return Results.Created($"/api/v1/properties/{propertyId}/leave/types/{x.Id}", x);
            })
            .Validate<SaveLeaveTypeRequest>().RequirePermission(Perm.LeaveManageBalances);
        l.MapPut("/types/{id:guid}", (Guid propertyId, Guid id, SaveLeaveTypeRequest body, LeaveService s, CancellationToken ct) =>
                s.SaveTypeAsync(propertyId, id, body, ct))
            .Validate<SaveLeaveTypeRequest>().RequirePermission(Perm.LeaveManageBalances);
        l.MapPut("/policies", (Guid propertyId, SaveLeavePolicyRequest body, LeaveService s, CancellationToken ct) => s.SavePolicyAsync(propertyId, body, ct))
            .Validate<SaveLeavePolicyRequest>().RequirePermission(Perm.LeaveManageBalances);

        l.MapGet("/holidays", (Guid propertyId, int? year, LeaveService s, CancellationToken ct) => s.HolidaysAsync(propertyId, year, ct))
            .RequirePermission(Perm.LeaveCreate);
        l.MapPost("/holidays", async (Guid propertyId, SaveHolidayRequest body, LeaveService s, CancellationToken ct) =>
            {
                var x = await s.SaveHolidayAsync(propertyId, null, body, ct);
                return Results.Created($"/api/v1/properties/{propertyId}/leave/holidays/{x.Id}", x);
            })
            .Validate<SaveHolidayRequest>().RequirePermission(Perm.LeaveManageBalances);
        l.MapPut("/holidays/{id:guid}", (Guid propertyId, Guid id, SaveHolidayRequest body, LeaveService s, CancellationToken ct) =>
                s.SaveHolidayAsync(propertyId, id, body, ct))
            .Validate<SaveHolidayRequest>().RequirePermission(Perm.LeaveManageBalances);

        l.MapGet("/balances", (Guid propertyId, LeaveService s, CancellationToken ct) => s.BalancesAsync(propertyId, ct))
            .RequirePermission(Perm.LeaveRead);
        l.MapGet("/balances/{employeeId:guid}", (Guid propertyId, Guid employeeId, LeaveService s, CancellationToken ct) =>
                s.LedgerAsync(propertyId, employeeId, ct))
            .RequirePermission(Perm.LeaveCreate);
        l.MapPost("/adjustments", (Guid propertyId, AdjustLeaveRequest body, LeaveService s, CancellationToken ct) => s.AdjustAsync(propertyId, body, ct))
            .Validate<AdjustLeaveRequest>().RequirePermission(Perm.LeaveManageBalances);
        l.MapPost("/accruals/run", (Guid propertyId, RunAccrualRequest body, LeaveService s, CancellationToken ct) => s.RunAccrualAsync(propertyId, body, ct))
            .Validate<RunAccrualRequest>().RequirePermission(Perm.LeaveManageBalances);

        l.MapGet("/me", (Guid propertyId, LeaveRequestService s, CancellationToken ct) => s.MineAsync(propertyId, ct))
            .RequirePermission(Perm.LeaveCreate);
        l.MapGet("/requests", (Guid propertyId, string? status, DateOnly? from, DateOnly? to, Guid? employeeId, LeaveRequestService s, CancellationToken ct) =>
                s.ListAsync(propertyId, status, from, to, employeeId, ct))
            .RequirePermission(Perm.LeaveRead);
        l.MapPost("/requests/preview", (Guid propertyId, CreateLeaveRequest body, LeaveRequestService s, CancellationToken ct) =>
                s.PreviewAsync(propertyId, body, ct))
            .Validate<CreateLeaveRequest>().RequirePermission(Perm.LeaveCreate);
        l.MapPost("/requests", async (Guid propertyId, CreateLeaveRequest body, LeaveRequestService s, CancellationToken ct) =>
            {
                var x = await s.CreateAsync(propertyId, body, ct);
                return Results.Created($"/api/v1/properties/{propertyId}/leave/requests/{x.Id}", x);
            })
            .Validate<CreateLeaveRequest>().RequirePermission(Perm.LeaveCreate);
        l.MapGet("/requests/{id:guid}", (Guid propertyId, Guid id, LeaveRequestService s, CancellationToken ct) => s.GetAsync(propertyId, id, ct))
            .RequirePermission(Perm.LeaveCreate);
        l.MapPost("/requests/{id:guid}/approve", (Guid propertyId, Guid id, ApproveLeaveRequest body, LeaveRequestService s, CancellationToken ct) =>
                s.ApproveAsync(propertyId, id, body, ct))
            .Validate<ApproveLeaveRequest>().RequirePermission(Perm.LeaveApprove);
        l.MapPost("/requests/{id:guid}/reject", (Guid propertyId, Guid id, RejectLeaveRequest body, LeaveRequestService s, CancellationToken ct) =>
                s.RejectAsync(propertyId, id, body, ct))
            .Validate<RejectLeaveRequest>().RequirePermission(Perm.LeaveReject);
        l.MapPost("/requests/{id:guid}/cancel", (Guid propertyId, Guid id, CancelLeaveRequest body, LeaveRequestService s, CancellationToken ct) =>
                s.CancelAsync(propertyId, id, body, ct))
            .Validate<CancelLeaveRequest>().RequirePermission(Perm.LeaveCreate);

        var pay = p.MapGroup("/payroll").WithTags("Payroll");
        pay.MapGet("/preview", (Guid propertyId, string? month, PayrollService s, CancellationToken ct) => s.PreviewAsync(propertyId, month ?? "", ct))
            .RequirePermission(Perm.PayrollExport);
        pay.MapGet("/exports", (Guid propertyId, PayrollService s, CancellationToken ct) => s.ListAsync(propertyId, ct))
            .RequirePermission(Perm.PayrollExport);
        pay.MapPost("/exports", async (Guid propertyId, CreatePayrollExportRequest body, PayrollService s, CancellationToken ct) =>
            {
                var x = await s.CreateAsync(propertyId, body, ct);
                return Results.Created($"/api/v1/properties/{propertyId}/payroll/exports/{x.Id}", x);
            })
            .Validate<CreatePayrollExportRequest>().RequirePermission(Perm.PayrollExport);
        pay.MapGet("/exports/{id:guid}/file", async (Guid propertyId, Guid id, PayrollService s, CancellationToken ct) =>
            {
                var (content, fileName) = await s.FileAsync(propertyId, id, ct);
                return Results.File(content, "text/csv; charset=utf-8", fileName);
            })
            .RequirePermission(Perm.PayrollExport);
    }
}
