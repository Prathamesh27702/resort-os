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

// ---------------------------------------------------------------- Sprint 2 — accommodation
export type InventoryMode = 'UNIT' | 'BED';
export const BED_TYPES = ['SINGLE', 'BUNK_LOWER', 'BUNK_UPPER', 'DOUBLE'] as const;
export const BLOCK_TYPES = ['MAINTENANCE', 'OUT_OF_ORDER', 'OWNER_USE', 'HOLD', 'OTHER'] as const;

export interface Amenity { id: string; code: string; name: string; category: string; }

export interface AccommodationType {
  id: string; code: string; name: string; description: string | null; inventoryMode: InventoryMode;
  baseOccupancy: number; maxOccupancy: number; sizeSqm: number | null; sortOrder: number; isActive: boolean;
  amenityIds: string[]; unitCount: number; sellableItems: number; maxGuests: number; version: number;
}

export interface Bed { id: string; unitId: string; code: string; name: string; bedType: string; sortOrder: number; isActive: boolean; version: number; }

export interface Room {
  id: string; code: string; name: string; accommodationTypeId: string; accommodationTypeName: string; inventoryMode: InventoryMode;
  floor: string | null; notes: string | null; sortOrder: number; isActive: boolean; beds: Bed[]; version: number;
}

/** Dates are the property's local dates (YYYY-MM-DD); endDate is exclusive, like a check-out date. */
export interface UnitBlock {
  id: string; unitId: string; unitCode: string; unitName: string; bedId: string | null; bedCode: string | null;
  blockType: string; startDate: string; endDate: string; nights: number; reason: string; status: 'ACTIVE' | 'RELEASED';
  createdAt: string; createdByName: string | null; releasedAt: string | null; releaseReason: string | null;
}

export type RoomState = 'AVAILABLE' | 'BLOCKED' | 'PARTLY_BLOCKED' | 'INACTIVE';
export interface BedStatus { bedId: string; code: string; name: string; isActive: boolean; status: RoomState; blockId: string | null; }
export interface RoomStatus {
  unitId: string; code: string; name: string; typeName: string; inventoryMode: InventoryMode; isActive: boolean; status: RoomState;
  block: UnitBlock | null; activeBeds: number; blockedBeds: number; beds: BedStatus[];
}
export interface RoomStatusBoard { date: string; rooms: RoomStatus[]; }

// ---------------------------------------------------------------- guests (Sprint 3)
export const DOCUMENT_TYPES = ['PASSPORT', 'AADHAAR', 'DRIVING_LICENCE', 'VOTER_ID', 'PAN', 'VISA', 'OTHER'] as const;
export const PREFERENCE_CATEGORIES = ['ROOM', 'FOOD', 'DIETARY', 'ACTIVITY', 'OTHER'] as const;
export const CONSENT_PURPOSES = ['MARKETING_WHATSAPP', 'MARKETING_EMAIL', 'DATA_PROCESSING'] as const;
export const CONSENT_SOURCES = ['FRONT_DESK', 'PHONE', 'WEBSITE', 'OTA', 'EMAIL'] as const;
export type GuestStatus = 'ACTIVE' | 'MERGED' | 'ANONYMISED';

export interface GuestAddress {
  line1: string | null; line2: string | null; city: string | null; state: string | null; stateCode: string | null;
  postalCode: string | null; countryCode: string;
}
export interface GuestPreference { category: string; value: string; }
export interface GuestConsent { purpose: string; granted: boolean; source: string; recordedAt: string; recordedByName: string | null; }
export interface GuestListItem {
  id: string; fullName: string; phone: string | null; email: string | null; nationality: string | null; city: string | null;
  companyName: string | null; isVip: boolean; status: GuestStatus; createdAt: string;
}
export interface Guest {
  id: string; firstName: string; lastName: string | null; fullName: string; phone: string | null; email: string | null;
  dateOfBirth: string | null; nationality: string | null; companyName: string | null; gstin: string | null; notes: string | null;
  isVip: boolean; status: GuestStatus; mergedIntoId: string | null; anonymisedAt: string | null;
  address: GuestAddress | null; preferences: GuestPreference[]; consents: GuestConsent[];
  /** null when the signed-in user can't see ID documents. */
  documentCount: number | null; createdAt: string; updatedAt: string; version: number;
}
export interface PossibleDuplicate { id: string; fullName: string; phone: string | null; email: string | null; nationality: string | null; reasons: string[]; }
export interface GuestStay { reservationId: string; propertyId: string; propertyName: string; checkIn: string; checkOut: string; status: string; reference: string; }
export interface GuestDocument {
  id: string; documentType: string; maskedNumber: string | null; issuingCountry: string | null; expiresOn: string | null;
  hasFile: boolean; fileName: string | null; contentType: string | null; sizeBytes: number | null; status: 'ACTIVE' | 'PURGED';
  createdAt: string; createdByName: string | null;
}
export interface GuestDocumentDetail extends GuestDocument { number: string | null; details: Record<string, string> | null; }

// ---- Sprint 4: rates and packages
export const MEAL_PLANS = ['EP', 'CP', 'MAP', 'AP'] as const;
export const MEAL_PLAN_LABELS: Record<string, string> = {
  EP: 'EP — room only', CP: 'CP — with breakfast', MAP: 'MAP — breakfast + one meal', AP: 'AP — all meals',
};
export const REVENUE_CATEGORIES = ['ROOM', 'FOOD', 'TRANSPORT', 'ACTIVITY'] as const;

export interface RatePlan {
  id: string; code: string; name: string; description: string | null; mealPlan: string; isDefault: boolean;
  sortOrder: number; isActive: boolean; version: number;
}
/** One cell of the rate grid; price is per room (or per bed for a dormitory) per night. */
export interface RatePrice {
  accommodationTypeId: string; date: string; price: number; extraAdult: number; extraChild: number; minStay: number; updatedAt: string;
}
export interface SetRatePricesResult { created: number; changed: number; unchanged: number; }
export interface RateQuoteNight {
  date: string; units: number; unitPrice: number; baseAmount: number;
  extraAdults: number; extraAdultRate: number; extraAdultAmount: number;
  extraChildren: number; extraChildRate: number; extraChildAmount: number; total: number;
}
export interface RateQuote {
  ratePlanId: string; ratePlanCode: string; ratePlanName: string; mealPlan: string;
  accommodationTypeId: string; accommodationTypeName: string; inventoryMode: InventoryMode; roomCode: string | null;
  from: string; to: string; nights: number; adults: number; children: number; units: number;
  breakdown: RateQuoteNight[]; total: number; currency: string;
}
export interface PackageComponent { id: string; category: string; description: string; sharePercent: number; amount: number; sortOrder: number; }
export interface Package {
  id: string; code: string; name: string; description: string | null; accommodationTypeId: string; accommodationTypeName: string;
  nights: number; adults: number; children: number; price: number; sortOrder: number; isActive: boolean;
  components: PackageComponent[]; revenueByCategory: Record<string, number>; version: number;
}

// ---------------------------------------------------------------- reservations (Sprint 5)
export type ReservationStatus = 'INQUIRY' | 'PENDING' | 'CONFIRMED' | 'CHECKED_IN' | 'CHECKED_OUT' | 'CANCELLED' | 'NO_SHOW' | 'REFUNDED';
export const RESERVATION_STATUSES: ReservationStatus[] = ['INQUIRY', 'PENDING', 'CONFIRMED', 'CHECKED_IN', 'CHECKED_OUT', 'CANCELLED', 'NO_SHOW', 'REFUNDED'];

export interface AvailableRoom { id: string; code: string; name: string; floor: string | null; }
/** free = rooms (or dorm beds) free for every night; fits = the guests can be booked here; price = default plan, or priceProblem. */
export interface AvailabilityType {
  accommodationTypeId: string; code: string; name: string; inventoryMode: InventoryMode; baseOccupancy: number; maxOccupancy: number;
  total: number; free: number; fits: boolean; fitProblem: string | null; ratePlanId: string | null; ratePlanName: string | null;
  price: number | null; priceProblem: string | null; freeRooms: AvailableRoom[];
}
export interface Availability { from: string; to: string; nights: number; adults: number; children: number; currency: string; types: AvailabilityType[]; }

export interface ReservationListItem {
  id: string; reference: string; status: ReservationStatus; primaryGuestId: string; guestName: string; guestPhone: string | null;
  checkIn: string; checkOut: string; nights: number; adults: number; children: number; rooms: string; sourceName: string;
  totalAmount: number; currency: string; createdAt: string;
}
export interface ReservationUnit {
  id: string; accommodationTypeId: string; accommodationTypeName: string; inventoryMode: InventoryMode; unitId: string; unitCode: string;
  unitName: string; bedId: string | null; bedCode: string | null; startDate: string; endDate: string; adults: number; children: number;
  holdsInventory: boolean; current: boolean; total: number;
}
export interface ReservationNight {
  reservationUnitId: string; date: string; units: number; unitPrice: number; baseAmount: number; extraAdults: number; extraAdultRate: number;
  extraAdultAmount: number; extraChildren: number; extraChildRate: number; extraChildAmount: number; total: number;
}
export interface ReservationGuest { guestId: string; fullName: string; phone: string | null; email: string | null; isPrimary: boolean; }
export interface ReservationNote { id: string; note: string; createdAt: string; createdByName: string | null; }
export interface ReservationStatusChange { fromStatus: ReservationStatus | null; toStatus: ReservationStatus; reason: string | null; changedAt: string; changedByName: string | null; }
export interface Reservation {
  id: string; reference: string; status: ReservationStatus; checkIn: string; checkOut: string; nights: number; adults: number; children: number;
  sourceId: string; sourceName: string; ratePlanId: string; ratePlanName: string; mealPlan: string; totalAmount: number; currency: string;
  specialRequests: string | null; guests: ReservationGuest[]; units: ReservationUnit[]; breakdown: ReservationNight[];
  notes: ReservationNote[]; history: ReservationStatusChange[]; nextStatuses: ReservationStatus[];
  createdAt: string; createdByName: string | null; version: number;
  /** When the guest actually arrived and left (Sprint 7). */
  checkedInAt: string | null; checkedOutAt: string | null;
}
export interface ReservationRoomRequest { accommodationTypeId: string; unitId: string | null; adults: number; children: number; }
export interface ReservationSource { id: string; code: string; name: string; channel: string; commissionPercent: number | null; }

// ---------------------------------------------------------------- reservation calendar (Sprint 6)
export interface CalendarBed { id: string; code: string; name: string; isActive: boolean; }
export interface CalendarRoom {
  id: string; code: string; name: string; accommodationTypeId: string; accommodationTypeName: string; inventoryMode: InventoryMode;
  maxOccupancy: number; floor: string | null; isActive: boolean; beds: CalendarBed[];
}
/** One room (or dorm bed) of a booking for [startDate, endDate). paymentStatus: NONE until payments arrive (Sprint 10). */
export interface CalendarBooking {
  reservationUnitId: string; reservationId: string; reference: string; status: ReservationStatus; unitId: string; bedId: string | null;
  startDate: string; endDate: string; guestName: string; adults: number; children: number; sourceName: string;
  paymentStatus: 'NONE' | 'PARTIAL' | 'PAID'; totalAmount: number; version: number;
}
export interface CalendarBlock { id: string; unitId: string; bedId: string | null; blockType: string; startDate: string; endDate: string; reason: string; }
export interface Calendar { from: string; to: string; today: string; rooms: CalendarRoom[]; bookings: CalendarBooking[]; blocks: CalendarBlock[]; }

// ---------------------------------------------------------------- front desk (Sprint 7)
export type Housekeeping = 'DIRTY' | 'CLEANING' | 'CLEAN' | 'INSPECTION' | 'READY' | 'OUT_OF_ORDER';
/** A room (or dorm bed) of a booking with its cleaning state; occupiedBy = another booking still checked in there. */
export interface FrontDeskRoom {
  reservationUnitId: string; unitId: string; unitCode: string; unitName: string; bedId: string | null; bedCode: string | null;
  typeName: string; startDate: string; endDate: string; housekeeping: Housekeeping; occupiedBy: string | null;
}
/** One booking on a front desk list. late = due on an earlier day, not arrived; overdue = still in after departure day;
 *  checkInProblem = why check-in won't go straight through (null = it will). */
export interface FrontDeskStay {
  reservationId: string; reference: string; status: ReservationStatus; guestId: string; guestName: string; guestPhone: string | null;
  isVip: boolean; checkIn: string; checkOut: string; nights: number; adults: number; children: number; rooms: FrontDeskRoom[];
  sourceName: string; totalAmount: number; balance: number; currency: string; specialRequests: string | null;
  checkedInAt: string | null; checkedOutAt: string | null; late: boolean; overdue: boolean; checkInProblem: string | null; version: number;
}
export interface FrontDeskList { date: string; today: string; items: FrontDeskStay[]; }
export type Occupancy = 'VACANT' | 'OCCUPIED' | 'ARRIVING' | 'BLOCKED' | 'INACTIVE';
export interface FrontDeskBoardRoom {
  unitId: string; unitCode: string; unitName: string; bedId: string | null; bedCode: string | null; typeName: string;
  occupancy: Occupancy; housekeeping: Housekeeping; reservationId: string | null; reference: string | null; guestName: string | null;
  until: string | null;
}
export interface FrontDeskDashboard {
  today: string; arrivalsExpected: number; arrivalsCheckedIn: number; departuresExpected: number; departuresCheckedOut: number;
  overdue: number; inHouseStays: number; inHouseGuests: number; roomsTotal: number; roomsOccupied: number; roomsReady: number;
  roomsNotReady: number; roomsBlocked: number; rooms: FrontDeskBoardRoom[];
}
export interface RoomOption {
  unitId: string; unitCode: string; unitName: string; bedId: string | null; bedCode: string | null; accommodationTypeId: string;
  typeName: string; housekeeping: Housekeeping; free: boolean; fits: boolean; problem: string | null;
}
