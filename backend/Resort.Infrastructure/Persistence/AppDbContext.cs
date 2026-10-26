using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Domain.Accommodation;
using Resort.Domain.Audit;
using Resort.Domain.Common;
using Resort.Domain.Folios;
using Resort.Domain.FrontDesk;
using Resort.Domain.Guests;
using Resort.Domain.Housekeeping;
using Resort.Domain.Restaurant;
using Resort.Domain.Identity;
using Resort.Domain.Invoicing;
using Resort.Domain.Organizations;
using Resort.Domain.Payments;
using Resort.Domain.Rates;
using Resort.Domain.Reservations;
using Resort.Domain.Tax;

namespace Resort.Infrastructure.Persistence;

/// <summary>
/// The schema is owned by the SQL files in /db/migrations; this model only maps onto it
/// (EF migrations are deliberately not used). Names are converted to snake_case.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options, ICurrentUser currentUser)
    : DbContext(options), IAppDbContext
{
    /// <summary>Read by the tenant query filters. NULL for anonymous callers, which makes every
    /// tenant-scoped query return nothing unless the code explicitly calls IgnoreQueryFilters().</summary>
    internal Guid? CurrentOrganizationId => currentUser.OrganizationId;

    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Property> Properties => Set<Property>();
    public DbSet<PropertySetting> PropertySettings => Set<PropertySetting>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<LoginEvent> LoginEvents => Set<LoginEvent>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Amenity> Amenities => Set<Amenity>();
    public DbSet<AccommodationType> AccommodationTypes => Set<AccommodationType>();
    public DbSet<AccommodationTypeAmenity> AccommodationTypeAmenities => Set<AccommodationTypeAmenity>();
    public DbSet<AccommodationUnit> AccommodationUnits => Set<AccommodationUnit>();
    public DbSet<Bed> Beds => Set<Bed>();
    public DbSet<UnitImage> UnitImages => Set<UnitImage>();
    public DbSet<UnitBlock> UnitBlocks => Set<UnitBlock>();
    public DbSet<Guest> Guests => Set<Guest>();
    public DbSet<GuestAddress> GuestAddresses => Set<GuestAddress>();
    public DbSet<GuestDocument> GuestDocuments => Set<GuestDocument>();
    public DbSet<GuestPreference> GuestPreferences => Set<GuestPreference>();
    public DbSet<GuestConsent> GuestConsents => Set<GuestConsent>();
    public DbSet<ReservationSource> ReservationSources => Set<ReservationSource>();
    public DbSet<RatePlan> RatePlans => Set<RatePlan>();
    public DbSet<RatePlanPrice> RatePlanPrices => Set<RatePlanPrice>();
    public DbSet<Package> Packages => Set<Package>();
    public DbSet<PackageComponent> PackageComponents => Set<PackageComponent>();
    public DbSet<Reservation> Reservations => Set<Reservation>();
    public DbSet<ReservationUnit> ReservationUnits => Set<ReservationUnit>();
    public DbSet<ReservationGuest> ReservationGuests => Set<ReservationGuest>();
    public DbSet<ReservationRateBreakdown> ReservationRateBreakdowns => Set<ReservationRateBreakdown>();
    public DbSet<ReservationNote> ReservationNotes => Set<ReservationNote>();
    public DbSet<ReservationStatusChange> ReservationStatusHistory => Set<ReservationStatusChange>();
    public DbSet<IdempotencyKey> IdempotencyKeys => Set<IdempotencyKey>();
    public DbSet<UnitHousekeepingStatus> UnitHousekeepingStatus => Set<UnitHousekeepingStatus>();
    public DbSet<UnitHousekeepingChange> UnitHousekeepingHistory => Set<UnitHousekeepingChange>();
    public DbSet<Folio> Folios => Set<Folio>();
    public DbSet<FolioItem> FolioItems => Set<FolioItem>();
    public DbSet<FolioItemTax> FolioItemTaxes => Set<FolioItemTax>();
    public DbSet<GstRegistration> GstRegistrations => Set<GstRegistration>();
    public DbSet<TaxCategory> TaxCategories => Set<TaxCategory>();
    public DbSet<TaxRule> TaxRules => Set<TaxRule>();
    public DbSet<TaxRuleSlab> TaxRuleSlabs => Set<TaxRuleSlab>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PaymentAllocation> PaymentAllocations => Set<PaymentAllocation>();
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<PaymentGatewayOrder> PaymentGatewayOrders => Set<PaymentGatewayOrder>();
    public DbSet<WebhookEvent> WebhookEvents => Set<WebhookEvent>();
    public DbSet<InvoiceSeries> InvoiceSeries => Set<InvoiceSeries>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceItem> InvoiceItems => Set<InvoiceItem>();
    public DbSet<InvoiceItemTax> InvoiceItemTaxes => Set<InvoiceItemTax>();
    public DbSet<CreditNote> CreditNotes => Set<CreditNote>();
    public DbSet<CreditNoteItem> CreditNoteItems => Set<CreditNoteItem>();
    public DbSet<CreditNoteItemTax> CreditNoteItemTaxes => Set<CreditNoteItemTax>();
    public DbSet<DebitNote> DebitNotes => Set<DebitNote>();
    public DbSet<DebitNoteItem> DebitNoteItems => Set<DebitNoteItem>();
    public DbSet<DebitNoteItemTax> DebitNoteItemTaxes => Set<DebitNoteItemTax>();
    public DbSet<Receipt> Receipts => Set<Receipt>();
    public DbSet<EInvoiceRecord> EInvoiceRecords => Set<EInvoiceRecord>();
    public DbSet<CashDrawer> CashDrawers => Set<CashDrawer>();
    public DbSet<CashDrawerSession> CashDrawerSessions => Set<CashDrawerSession>();
    public DbSet<CashDrawerMovement> CashDrawerMovements => Set<CashDrawerMovement>();
    public DbSet<HousekeepingTask> HousekeepingTasks => Set<HousekeepingTask>();
    public DbSet<HousekeepingChecklistTemplate> HousekeepingChecklistTemplates => Set<HousekeepingChecklistTemplate>();
    public DbSet<HousekeepingChecklistItem> HousekeepingChecklistItems => Set<HousekeepingChecklistItem>();
    public DbSet<MaintenanceAsset> MaintenanceAssets => Set<MaintenanceAsset>();
    public DbSet<MaintenanceTicket> MaintenanceTickets => Set<MaintenanceTicket>();
    public DbSet<MaintenanceComment> MaintenanceComments => Set<MaintenanceComment>();
    public DbSet<MaintenanceAttachment> MaintenanceAttachments => Set<MaintenanceAttachment>();
    public DbSet<MenuCategory> MenuCategories => Set<MenuCategory>();
    public DbSet<MenuItem> MenuItems => Set<MenuItem>();
    public DbSet<MenuItemVariant> MenuItemVariants => Set<MenuItemVariant>();
    public DbSet<ModifierGroup> ModifierGroups => Set<ModifierGroup>();
    public DbSet<Modifier> Modifiers => Set<Modifier>();
    public DbSet<RestaurantTable> RestaurantTables => Set<RestaurantTable>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<Kot> Kots => Set<Kot>();
    public DbSet<KotItem> KotItems => Set<KotItem>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Organization>().ToTable("organizations");
        b.Entity<Property>().ToTable("properties");
        b.Entity<PropertySetting>(e =>
        {
            e.ToTable("property_settings");
            e.Property(x => x.Value).HasColumnType("jsonb");
        });
        b.Entity<SystemSetting>(e =>
        {
            e.ToTable("system_settings");
            e.HasKey(x => x.Key);
            e.Property(x => x.Value).HasColumnType("jsonb");
        });
        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasMany(x => x.Roles).WithOne().HasForeignKey(x => x.UserId);
        });
        b.Entity<Permission>(e =>
        {
            e.ToTable("permissions");
            e.HasKey(x => x.Code);
        });
        b.Entity<Role>(e =>
        {
            e.ToTable("roles");
            e.HasMany(x => x.Permissions).WithOne().HasForeignKey(x => x.RoleId);
        });
        b.Entity<RolePermission>(e =>
        {
            e.ToTable("role_permissions");
            e.HasKey(x => new { x.RoleId, x.PermissionCode });
        });
        b.Entity<UserRole>(e =>
        {
            e.ToTable("user_roles");
            e.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId);
        });
        b.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_tokens");
            // Declared so EF inserts the replacement token before updating the old row that points to it.
            e.HasOne<RefreshToken>().WithMany().HasForeignKey(x => x.ReplacedById);
        });
        b.Entity<LoginEvent>(e =>
        {
            e.ToTable("login_events");
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
        });
        b.Entity<AuditLog>(e =>
        {
            e.ToTable("audit_logs");
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Changes).HasColumnType("jsonb");
        });

        // ---- Sprint 2: accommodation (database/migrations/V004__accommodation.sql)
        b.Entity<Amenity>().ToTable("amenities");
        b.Entity<AccommodationType>(e =>
        {
            e.ToTable("accommodation_types");
            e.Property(x => x.SizeSqm).HasColumnType("numeric(6,1)");
            e.HasMany(x => x.Amenities).WithOne().HasForeignKey(x => x.AccommodationTypeId);
        });
        b.Entity<AccommodationTypeAmenity>(e =>
        {
            e.ToTable("accommodation_type_amenities");
            e.HasKey(x => new { x.AccommodationTypeId, x.AmenityId });
        });
        b.Entity<AccommodationUnit>(e =>
        {
            e.ToTable("accommodation_units");
            e.HasOne(x => x.Type).WithMany().HasForeignKey(x => x.AccommodationTypeId);
            e.HasMany(x => x.Beds).WithOne().HasForeignKey(x => x.UnitId);
        });
        b.Entity<Bed>().ToTable("beds");
        b.Entity<UnitImage>().ToTable("unit_images");
        b.Entity<UnitBlock>(e =>
        {
            e.ToTable("unit_blocks");
            // "period" is a generated column used only by the database's EXCLUDE constraint; not mapped.
            e.Ignore(x => x.IsActive);
        });

        // ---- Sprint 3: guests (database/migrations/V005__guests.sql)
        b.Entity<Guest>(e =>
        {
            e.ToTable("guests");
            // Generated (STORED) columns used for search; never written by the app.
            e.Property(x => x.SearchText).HasComputedColumnSql("search_text", stored: true);
            e.Property(x => x.PhoneDigits).HasComputedColumnSql("phone_digits", stored: true);
            e.Property(x => x.Nationality).HasColumnType("char(2)");
            e.HasMany(x => x.Addresses).WithOne().HasForeignKey(x => x.GuestId);
            e.HasMany(x => x.Preferences).WithOne().HasForeignKey(x => x.GuestId);
            e.Ignore(x => x.IsActive);
            e.Ignore(x => x.FullName);
        });
        b.Entity<GuestAddress>(e =>
        {
            e.ToTable("guest_addresses");
            e.Property(x => x.StateCode).HasColumnType("char(2)");
            e.Property(x => x.CountryCode).HasColumnType("char(2)");
            e.Ignore(x => x.IsEmpty);
        });
        b.Entity<GuestDocument>(e =>
        {
            e.ToTable("guest_documents");
            e.Property(x => x.IssuingCountry).HasColumnType("char(2)");
        });
        b.Entity<GuestPreference>().ToTable("guest_preferences");
        b.Entity<GuestConsent>().ToTable("guest_consents");

        // ---- Sprint 4: rates and packages (database/migrations/V006__rates.sql)
        b.Entity<ReservationSource>(e =>
        {
            e.ToTable("reservation_sources");
            e.Property(x => x.CommissionPercent).HasColumnType("numeric(5,2)");
        });
        b.Entity<RatePlan>().ToTable("rate_plans");
        b.Entity<RatePlanPrice>(e =>
        {
            e.ToTable("rate_plan_prices");
            e.Property(x => x.Price).HasColumnType("numeric(12,2)");
            e.Property(x => x.ExtraAdult).HasColumnType("numeric(12,2)");
            e.Property(x => x.ExtraChild).HasColumnType("numeric(12,2)");
        });
        b.Entity<Package>(e =>
        {
            e.ToTable("packages");
            e.Property(x => x.Price).HasColumnType("numeric(12,2)");
            e.HasOne(x => x.Type).WithMany().HasForeignKey(x => x.AccommodationTypeId);
            e.HasMany(x => x.Components).WithOne().HasForeignKey(x => x.PackageId);
        });
        b.Entity<PackageComponent>(e =>
        {
            e.ToTable("package_components");
            e.Property(x => x.SharePercent).HasColumnType("numeric(5,2)");
            e.Property(x => x.Amount).HasColumnType("numeric(12,2)");
        });

        // ---- Sprint 5: reservations (database/migrations/V007__reservations.sql)
        b.Entity<Reservation>(e =>
        {
            e.ToTable("reservations");
            e.Property(x => x.TotalAmount).HasColumnType("numeric(12,2)");
            e.Property(x => x.Currency).HasColumnType("char(3)");
            e.HasMany(x => x.Units).WithOne().HasForeignKey(x => x.ReservationId);
            e.HasMany(x => x.Guests).WithOne().HasForeignKey(x => x.ReservationId);
            e.Ignore(x => x.Nights);
        });
        b.Entity<ReservationUnit>(e =>
        {
            e.ToTable("reservation_units");
            // "stay" is a generated column used only by the database's EXCLUDE constraints; not mapped.
            e.Ignore(x => x.IsCurrent);
        });
        b.Entity<ReservationGuest>().ToTable("reservation_guests");
        b.Entity<ReservationRateBreakdown>(e =>
        {
            e.ToTable("reservation_rate_breakdowns");
            // Declared so EF inserts the reservation and unit rows before the nights that point to them.
            e.HasOne<Reservation>().WithMany().HasForeignKey(x => x.ReservationId);
            e.HasOne<ReservationUnit>().WithMany().HasForeignKey(x => x.ReservationUnitId);
            foreach (var m in new[] { nameof(ReservationRateBreakdown.UnitPrice), nameof(ReservationRateBreakdown.BaseAmount),
                         nameof(ReservationRateBreakdown.ExtraAdultRate), nameof(ReservationRateBreakdown.ExtraAdultAmount),
                         nameof(ReservationRateBreakdown.ExtraChildRate), nameof(ReservationRateBreakdown.ExtraChildAmount),
                         nameof(ReservationRateBreakdown.Total) })
                e.Property(m).HasColumnType("numeric(12,2)");
        });
        b.Entity<ReservationNote>(e =>
        {
            e.ToTable("reservation_notes");
            e.HasOne<Reservation>().WithMany().HasForeignKey(x => x.ReservationId);
        });
        b.Entity<ReservationStatusChange>(e =>
        {
            e.ToTable("reservation_status_history");
            e.HasOne<Reservation>().WithMany().HasForeignKey(x => x.ReservationId);
        });
        b.Entity<IdempotencyKey>(e =>
        {
            e.ToTable("idempotency_keys");
            e.Property(x => x.Response).HasColumnType("jsonb");
        });
        b.Entity<UnitHousekeepingStatus>().ToTable("unit_housekeeping_status");
        b.Entity<UnitHousekeepingChange>().ToTable("unit_housekeeping_history");

        // ---- Sprint 8: folios (database/migrations/V010__folio.sql)
        b.Entity<Folio>(e =>
        {
            e.ToTable("folios");
            e.Property(x => x.Currency).HasColumnType("char(3)");
        });
        b.Entity<FolioItem>(e =>
        {
            e.ToTable("folio_items");
            // Declared so EF inserts a new folio before the lines that point to it, and a charge before its reversal.
            e.HasOne<Folio>().WithMany().HasForeignKey(x => x.FolioId);
            e.HasOne<FolioItem>().WithMany().HasForeignKey(x => x.ReversesItemId);
            e.HasOne<FolioItem>().WithMany().HasForeignKey(x => x.TransferredFromItemId);
            // A credit note before the CREDIT lines it puts on the folio (Sprint 12).
            e.HasOne<FolioItem>().WithMany().HasForeignKey(x => x.AdjustsItemId);
            e.HasOne<CreditNote>().WithMany().HasForeignKey(x => x.CreditNoteId);
            e.Property(x => x.Quantity).HasColumnType("numeric(12,3)");
            foreach (var m in new[] { nameof(FolioItem.UnitPrice), nameof(FolioItem.Amount), nameof(FolioItem.TaxAmount), nameof(FolioItem.Total) })
                e.Property(m).HasColumnType("numeric(12,2)");
        });
        b.Entity<FolioItemTax>(e =>
        {
            e.ToTable("folio_item_taxes");
            e.HasOne<FolioItem>().WithMany().HasForeignKey(x => x.FolioItemId);
            e.Property(x => x.Rate).HasColumnType("numeric(5,2)");
            e.Property(x => x.TaxableValue).HasColumnType("numeric(12,2)");
            e.Property(x => x.Amount).HasColumnType("numeric(12,2)");
        });

        // ---- Sprint 9: GST and the tax engine (database/migrations/V011__tax.sql)
        b.Entity<GstRegistration>(e =>
        {
            e.ToTable("gst_registrations");
            e.Property(x => x.Gstin).HasColumnType("char(15)");
            e.Property(x => x.StateCode).HasColumnType("char(2)");
        });
        b.Entity<TaxCategory>().ToTable("tax_categories");
        b.Entity<TaxRule>(e =>
        {
            e.ToTable("tax_rules");
            e.HasMany(x => x.Slabs).WithOne().HasForeignKey(x => x.TaxRuleId);
        });
        b.Entity<TaxRuleSlab>(e =>
        {
            e.ToTable("tax_rule_slabs");
            e.Property(x => x.UpTo).HasColumnType("numeric(12,2)");
            e.Property(x => x.Rate).HasColumnType("numeric(5,2)");
        });

        // ---- Sprint 10: payments, refunds and the cash drawer (database/migrations/V012__payments.sql)
        b.Entity<CashDrawer>().ToTable("cash_drawers");
        b.Entity<CashDrawerSession>(e =>
        {
            e.ToTable("cash_drawer_sessions");
            e.HasOne<CashDrawer>().WithMany().HasForeignKey(x => x.CashDrawerId);
            foreach (var m in new[] { nameof(CashDrawerSession.OpeningFloat), nameof(CashDrawerSession.ExpectedAmount),
                         nameof(CashDrawerSession.CountedAmount), nameof(CashDrawerSession.Variance) })
                e.Property(m).HasColumnType("numeric(12,2)");
        });
        b.Entity<Payment>(e =>
        {
            e.ToTable("payments");
            e.Property(x => x.Currency).HasColumnType("char(3)");
            e.Property(x => x.Amount).HasColumnType("numeric(12,2)");
        });
        b.Entity<PaymentAllocation>(e =>
        {
            e.ToTable("payment_allocations");
            // Declared so EF inserts a new payment before its allocations, refunds and drawer movements.
            e.HasOne<Payment>().WithMany().HasForeignKey(x => x.PaymentId);
            e.Property(x => x.Amount).HasColumnType("numeric(12,2)");
        });
        b.Entity<Refund>(e =>
        {
            e.ToTable("refunds");
            e.HasOne<Payment>().WithMany().HasForeignKey(x => x.PaymentId);
            e.Property(x => x.Amount).HasColumnType("numeric(12,2)");
        });
        b.Entity<CashDrawerMovement>(e =>
        {
            e.ToTable("cash_drawer_movements");
            e.HasOne<CashDrawerSession>().WithMany().HasForeignKey(x => x.SessionId);
            e.HasOne<Payment>().WithMany().HasForeignKey(x => x.PaymentId);
            e.HasOne<Refund>().WithMany().HasForeignKey(x => x.RefundId);
            e.Property(x => x.Amount).HasColumnType("numeric(12,2)");
        });

        // ---- Sprint 11: Razorpay payment links and webhooks (database/migrations/V013__razorpay.sql)
        b.Entity<PaymentGatewayOrder>(e =>
        {
            e.ToTable("payment_gateway_orders");
            // Declared so EF inserts the webhook's new payment before it marks the link paid.
            e.HasOne<Payment>().WithMany().HasForeignKey(x => x.PaymentId);
            e.Property(x => x.Currency).HasColumnType("char(3)");
            e.Property(x => x.Amount).HasColumnType("numeric(12,2)");
        });
        b.Entity<WebhookEvent>(e =>
        {
            e.ToTable("webhook_events");
            e.Property(x => x.Payload).HasColumnType("jsonb");
        });

        // ---- Sprint 12: invoices, credit and debit notes, receipts (database/migrations/V014__invoicing.sql)
        b.Entity<InvoiceSeries>().ToTable("invoice_series");
        b.Entity<Invoice>(e =>
        {
            e.ToTable("invoices");
            e.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.InvoiceId);
            Document(e);
            foreach (var m in new[] { nameof(Invoice.SupplierGstin), nameof(Invoice.CustomerGstin) }) e.Property(m).HasColumnType("char(15)");
            foreach (var m in new[] { nameof(Invoice.SupplierStateCode), nameof(Invoice.CustomerStateCode), nameof(Invoice.PlaceOfSupply) })
                e.Property(m).HasColumnType("char(2)");
        });
        b.Entity<InvoiceItem>(e =>
        {
            e.ToTable("invoice_items");
            e.HasMany(x => x.Taxes).WithOne().HasForeignKey(x => x.InvoiceItemId);
            Line(e);
        });
        b.Entity<InvoiceItemTax>(e => { e.ToTable("invoice_item_taxes"); Tax(e); });
        b.Entity<DebitNote>(e =>
        {
            e.ToTable("debit_notes");
            e.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.DebitNoteId);
            Document(e);
        });
        b.Entity<DebitNoteItem>(e =>
        {
            e.ToTable("debit_note_items");
            e.HasMany(x => x.Taxes).WithOne().HasForeignKey(x => x.DebitNoteItemId);
            Line(e);
        });
        b.Entity<DebitNoteItemTax>(e => { e.ToTable("debit_note_item_taxes"); Tax(e); });
        b.Entity<CreditNote>(e =>
        {
            e.ToTable("credit_notes");
            e.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.CreditNoteId);
            Document(e);
        });
        b.Entity<CreditNoteItem>(e =>
        {
            e.ToTable("credit_note_items");
            e.HasMany(x => x.Taxes).WithOne().HasForeignKey(x => x.CreditNoteItemId);
            // Declared so EF inserts the folio's CREDIT line before the note line that points to it.
            e.HasOne<FolioItem>().WithMany().HasForeignKey(x => x.FolioCreditItemId);
            foreach (var m in new[] { nameof(CreditNoteItem.TaxableValue), nameof(CreditNoteItem.TaxAmount), nameof(CreditNoteItem.Total) })
                e.Property(m).HasColumnType("numeric(12,2)");
        });
        b.Entity<CreditNoteItemTax>(e => { e.ToTable("credit_note_item_taxes"); Tax(e); });
        b.Entity<Receipt>(e =>
        {
            e.ToTable("receipts");
            e.Property(x => x.Amount).HasColumnType("numeric(12,2)");
            e.Property(x => x.Currency).HasColumnType("char(3)");
        });
        b.Entity<EInvoiceRecord>(e =>
        {
            e.ToTable("einvoice_records");
            e.Property(x => x.Irn).HasColumnType("char(64)");
        });

        // ---- Sprint 13: housekeeping and maintenance (database/migrations/V015__housekeeping.sql)
        b.Entity<HousekeepingChecklistTemplate>(e =>
        {
            e.ToTable("housekeeping_checklist_templates");
            e.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.TemplateId);
        });
        b.Entity<HousekeepingChecklistItem>().ToTable("housekeeping_checklist_items");
        b.Entity<HousekeepingTask>(e =>
        {
            e.ToTable("housekeeping_tasks");
            e.Property(x => x.Checklist).HasColumnType("jsonb");
            // Declared so EF inserts the DIRTY change (and a new ticket) before the task that points to it.
            e.HasOne<UnitHousekeepingChange>().WithMany().HasForeignKey(x => x.SourceChangeId);
            e.HasOne<MaintenanceTicket>().WithMany().HasForeignKey(x => x.MaintenanceTicketId);
        });
        b.Entity<MaintenanceAsset>().ToTable("maintenance_assets");
        b.Entity<MaintenanceTicket>(e =>
        {
            e.ToTable("maintenance_tickets");
            // A new out-of-order block before the ticket that points to it.
            e.HasOne<UnitBlock>().WithMany().HasForeignKey(x => x.BlockId);
        });
        b.Entity<MaintenanceComment>(e =>
        {
            e.ToTable("maintenance_comments");
            e.HasOne<MaintenanceTicket>().WithMany().HasForeignKey(x => x.TicketId);
        });
        b.Entity<MaintenanceAttachment>(e =>
        {
            e.ToTable("maintenance_attachments");
            e.HasOne<MaintenanceTicket>().WithMany().HasForeignKey(x => x.TicketId);
        });

        // Restaurant (Sprint 14). The relationships only order the INSERTs of one save (an order before its lines and tickets).
        b.Entity<MenuCategory>().ToTable("menu_categories");
        b.Entity<MenuItem>(e =>
        {
            e.ToTable("menu_items");
            e.HasOne<MenuCategory>().WithMany().HasForeignKey(x => x.CategoryId);
            e.HasMany(x => x.Variants).WithOne().HasForeignKey(x => x.MenuItemId);
            e.HasMany(x => x.ModifierGroups).WithOne().HasForeignKey(x => x.MenuItemId);
        });
        b.Entity<MenuItemVariant>().ToTable("menu_item_variants");
        b.Entity<ModifierGroup>(e =>
        {
            e.ToTable("modifier_groups");
            e.HasMany(x => x.Modifiers).WithOne().HasForeignKey(x => x.GroupId);
        });
        b.Entity<Modifier>().ToTable("modifiers");
        b.Entity<RestaurantTable>().ToTable("restaurant_tables");
        b.Entity<Order>(e =>
        {
            e.ToTable("orders");
            // A room charge's folio line is saved before the order that points to it.
            e.HasOne<FolioItem>().WithMany().HasForeignKey(x => x.FolioItemId);
        });
        b.Entity<OrderItem>(e =>
        {
            e.ToTable("order_items");
            e.Property(x => x.Modifiers).HasColumnType("jsonb");
            e.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId);
            e.HasOne<Kot>().WithMany().HasForeignKey(x => x.KotId);
        });
        b.Entity<Kot>(e =>
        {
            e.ToTable("kots");
            e.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId);
        });
        b.Entity<KotItem>(e =>
        {
            e.ToTable("kot_items");
            e.HasOne<Kot>().WithMany().HasForeignKey(x => x.KotId);
            e.HasOne<OrderItem>().WithMany().HasForeignKey(x => x.OrderItemId);
        });

        foreach (var entity in b.Model.GetEntityTypes())
        {
            var clr = entity.ClrType;

            // Uuid PKs are generated by the app (UUIDv7), never by the database.
            if (typeof(Entity).IsAssignableFrom(clr))
                b.Entity(clr).Property(nameof(Entity.Id)).ValueGeneratedNever();

            // Optimistic concurrency on PostgreSQL's xmin system column.
            if (typeof(IVersioned).IsAssignableFrom(clr))
                b.Entity(clr).Property(nameof(IVersioned.Version))
                    .HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

            if (typeof(IOrganizationScoped).IsAssignableFrom(clr))
                TenantFilterMethod.MakeGenericMethod(clr).Invoke(this, [b]);
            else if (typeof(ISoftDelete).IsAssignableFrom(clr))
                SoftDeleteFilterMethod.MakeGenericMethod(clr).Invoke(this, [b]);
        }

        ApplySnakeCase(b);
    }

    private static void Document<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> e) where T : class, IDocumentTotals
    {
        foreach (var m in new[] { nameof(IDocumentTotals.TaxableValue), nameof(IDocumentTotals.Cgst), nameof(IDocumentTotals.Sgst),
                     nameof(IDocumentTotals.Igst), nameof(IDocumentTotals.Cess), nameof(IDocumentTotals.TaxTotal), nameof(IDocumentTotals.Total) })
            e.Property(m).HasColumnType("numeric(12,2)");
        e.Property("Currency").HasColumnType("char(3)");
    }

    private static void Line<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> e) where T : BilledLine
    {
        e.Property(x => x.Quantity).HasColumnType("numeric(12,3)");
        foreach (var m in new[] { nameof(BilledLine.UnitPrice), nameof(BilledLine.GrossAmount), nameof(BilledLine.Discount),
                     nameof(BilledLine.TaxableValue), nameof(BilledLine.TaxAmount), nameof(BilledLine.Total) })
            e.Property(m).HasColumnType("numeric(12,2)");
    }

    private static void Tax<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> e) where T : DocumentTax
    {
        e.Property(x => x.Rate).HasColumnType("numeric(5,2)");
        e.Property(x => x.TaxableValue).HasColumnType("numeric(12,2)");
        e.Property(x => x.Amount).HasColumnType("numeric(12,2)");
    }

    private static readonly MethodInfo TenantFilterMethod =
        typeof(AppDbContext).GetMethod(nameof(SetTenantFilter), BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo SoftDeleteFilterMethod =
        typeof(AppDbContext).GetMethod(nameof(SetSoftDeleteFilter), BindingFlags.NonPublic | BindingFlags.Static)!;

    private void SetTenantFilter<T>(ModelBuilder b) where T : class, IOrganizationScoped
    {
        Expression<Func<T, bool>> filter = e => e.OrganizationId == CurrentOrganizationId;
        if (typeof(ISoftDelete).IsAssignableFrom(typeof(T)))
            filter = e => e.OrganizationId == CurrentOrganizationId && ((ISoftDelete)e).DeletedAt == null;
        b.Entity<T>().HasQueryFilter(filter);
    }

    private static void SetSoftDeleteFilter<T>(ModelBuilder b) where T : class, ISoftDelete =>
        b.Entity<T>().HasQueryFilter(e => e.DeletedAt == null);

    private static void ApplySnakeCase(ModelBuilder b)
    {
        foreach (var entity in b.Model.GetEntityTypes())
        {
            foreach (var p in entity.GetProperties())
                if (p.GetColumnName() != "xmin")
                    p.SetColumnName(ToSnake(p.Name));
            foreach (var k in entity.GetKeys()) k.SetName(ToSnake(k.GetName() ?? ""));
            foreach (var fk in entity.GetForeignKeys()) fk.SetConstraintName(ToSnake(fk.GetConstraintName() ?? ""));
        }
    }

    internal static string ToSnake(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1]))))
                    sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
