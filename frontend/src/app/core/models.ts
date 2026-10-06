// Mirrors Resort.Contracts (backend). Keep in sync when contracts change.

export interface AccessGrant { propertyId: string | null; roles: string[]; permissions: string[]; }
export interface OrganizationSummary { id: string; code: string; name: string; }
export interface Me {
  userId: string; email: string; fullName: string; mustChangePassword: boolean;
  organization: OrganizationSummary; grants: AccessGrant[];
}
export interface AuthResponse { accessToken: string; accessTokenExpiresAt: string; me: Me; }

export interface Property {
  id: string; code: string; name: string; legalName: string | null; propertyType: string; timezone: string; currency: string;
  addressLine1: string | null; addressLine2: string | null; city: string | null; district: string | null; stateName: string | null;
  gstStateCode: string | null; postalCode: string | null; countryCode: string; phone: string | null; email: string | null;
  checkInTime: string; checkOutTime: string; latitude: number | null; longitude: number | null;
  isActive: boolean; localDate: string; version: number;
}

export interface RoleGrant { roleCode: string; propertyId: string | null; }
export interface User {
  id: string; email: string; fullName: string; phone: string | null; isActive: boolean; mustChangePassword: boolean;
  lastLoginAt: string | null; roles: RoleGrant[]; version: number;
}
export interface Role { id: string; code: string; name: string; description: string | null; isSystem: boolean; permissions: string[]; }
export interface Permission { code: string; module: string; description: string; isSensitive: boolean; }

export interface AuditLog {
  id: number; occurredAt: string; userId: string | null; userName: string | null; propertyId: string | null;
  action: string; entityType: string; entityId: string | null; changes: Record<string, { old?: unknown; new?: unknown }> | null;
  reason: string | null; device: string | null; ipAddress: string | null; correlationId: string | null; requestId: string | null;
}
export interface Paged<T> { items: T[]; page: number; pageSize: number; total: number; }

/** RFC 7807 problem returned by the API. `code` is stable; `detail` is human-readable. */
export interface Problem { status: number; code: string; detail: string; errors?: Record<string, string[]>; correlationId?: string; }

export const PROPERTY_TYPES = ['RESORT', 'HOTEL', 'HOMESTAY', 'HOSTEL', 'VILLA', 'OTHER'];

/** GST state codes (for place-of-supply later). */
export const GST_STATES: { code: string; name: string }[] = [
  { code: '01', name: 'Jammu and Kashmir' }, { code: '02', name: 'Himachal Pradesh' }, { code: '03', name: 'Punjab' },
  { code: '04', name: 'Chandigarh' }, { code: '05', name: 'Uttarakhand' }, { code: '06', name: 'Haryana' },
  { code: '07', name: 'Delhi' }, { code: '08', name: 'Rajasthan' }, { code: '09', name: 'Uttar Pradesh' },
  { code: '10', name: 'Bihar' }, { code: '11', name: 'Sikkim' }, { code: '12', name: 'Arunachal Pradesh' },
  { code: '13', name: 'Nagaland' }, { code: '14', name: 'Manipur' }, { code: '15', name: 'Mizoram' },
  { code: '16', name: 'Tripura' }, { code: '17', name: 'Meghalaya' }, { code: '18', name: 'Assam' },
  { code: '19', name: 'West Bengal' }, { code: '20', name: 'Jharkhand' }, { code: '21', name: 'Odisha' },
  { code: '22', name: 'Chhattisgarh' }, { code: '23', name: 'Madhya Pradesh' }, { code: '24', name: 'Gujarat' },
  { code: '26', name: 'Dadra and Nagar Haveli and Daman and Diu' }, { code: '27', name: 'Maharashtra' },
  { code: '29', name: 'Karnataka' }, { code: '30', name: 'Goa' }, { code: '31', name: 'Lakshadweep' },
  { code: '32', name: 'Kerala' }, { code: '33', name: 'Tamil Nadu' }, { code: '34', name: 'Puducherry' },
  { code: '35', name: 'Andaman and Nicobar Islands' }, { code: '36', name: 'Telangana' }, { code: '37', name: 'Andhra Pradesh' },
  { code: '38', name: 'Ladakh' },
];
