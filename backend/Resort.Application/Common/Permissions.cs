namespace Resort.Application.Common;

/// <summary>Permission codes used in code. Must match rows in the <c>permissions</c> table
/// (a test verifies every constant here exists in the database).</summary>
public static class Perm
{
    public const string UserRead = "user.read";
    public const string UserCreate = "user.create";
    public const string UserUpdate = "user.update";
    public const string UserDeactivate = "user.deactivate";
    public const string RoleRead = "role.read";
    public const string RoleManage = "role.manage";

    public const string OrganizationRead = "organization.read";
    public const string OrganizationUpdate = "organization.update";
    public const string PropertyRead = "property.read";
    public const string PropertyCreate = "property.create";
    public const string PropertyUpdate = "property.update";
    public const string PropertySettingsUpdate = "property_settings.update";

    public const string AccommodationRead = "accommodation.read";
    public const string AccommodationManage = "accommodation.manage";
    public const string UnitBlockManage = "unit_block.manage";
    public const string RateRead = "rate.read";
    public const string RateManage = "rate.manage";

    public const string GuestRead = "guest.read";
    public const string GuestCreate = "guest.create";
    public const string GuestUpdate = "guest.update";
    public const string GuestDelete = "guest.delete";
    public const string GuestDocumentRead = "guest_document.read";

    public const string ReservationRead = "reservation.read";
    public const string ReservationCreate = "reservation.create";
    public const string ReservationUpdate = "reservation.update";
    public const string ReservationCancel = "reservation.cancel";

    public const string FrontDeskCheckIn = "frontdesk.checkin";
    public const string FrontDeskCheckOut = "frontdesk.checkout";
    public const string HousekeepingRead = "housekeeping.read";
    public const string HousekeepingUpdate = "housekeeping.update";

    public const string FolioRead = "folio.read";
    public const string FolioPostCharge = "folio.post_charge";
    public const string FolioAdjust = "folio.adjust";

    public const string PaymentRead = "payment.read";
    public const string PaymentCreate = "payment.create";
    public const string PaymentRefund = "payment.refund";
    public const string PaymentVoid = "payment.void";
    public const string CashDrawerOperate = "cash_drawer.operate";
    public const string CashDrawerApproveVariance = "cash_drawer.approve_variance";

    public const string TaxRead = "tax.read";
    public const string TaxManage = "tax.manage";

    public const string InvoiceRead = "invoice.read";
    public const string InvoiceCreate = "invoice.create";
    public const string InvoiceCancel = "invoice.cancel";
    public const string CreditNoteCreate = "credit_note.create";

    public const string AuditRead = "audit.read";
    public const string SystemSettings = "system.settings";

    public static readonly string[] All =
    [
        UserRead, UserCreate, UserUpdate, UserDeactivate, RoleRead, RoleManage,
        OrganizationRead, OrganizationUpdate, PropertyRead, PropertyCreate, PropertyUpdate, PropertySettingsUpdate,
        AccommodationRead, AccommodationManage, UnitBlockManage, RateRead, RateManage,
        GuestRead, GuestCreate, GuestUpdate, GuestDelete, GuestDocumentRead,
        ReservationRead, ReservationCreate, ReservationUpdate, ReservationCancel,
        FrontDeskCheckIn, FrontDeskCheckOut, HousekeepingRead, HousekeepingUpdate,
        FolioRead, FolioPostCharge, FolioAdjust,
        PaymentRead, PaymentCreate, PaymentRefund, PaymentVoid, CashDrawerOperate, CashDrawerApproveVariance,
        TaxRead, TaxManage,
        InvoiceRead, InvoiceCreate, InvoiceCancel, CreditNoteCreate,
        AuditRead, SystemSettings,
    ];
}
