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

    public const string AuditRead = "audit.read";
    public const string SystemSettings = "system.settings";

    public static readonly string[] All =
    [
        UserRead, UserCreate, UserUpdate, UserDeactivate, RoleRead, RoleManage,
        OrganizationRead, OrganizationUpdate, PropertyRead, PropertyCreate, PropertyUpdate, PropertySettingsUpdate,
        AccommodationRead, AccommodationManage, UnitBlockManage,
        AuditRead, SystemSettings,
    ];
}
