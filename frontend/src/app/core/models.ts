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
/** housekeeping: the cleaning state now (Sprint 13), on today's board only (null on other dates). */
export interface BedStatus { bedId: string; code: string; name: string; isActive: boolean; status: RoomState; blockId: string | null; housekeeping: Housekeeping | null; }
export interface RoomStatus {
  unitId: string; code: string; name: string; typeName: string; inventoryMode: InventoryMode; isActive: boolean; status: RoomState;
  block: UnitBlock | null; activeBeds: number; blockedBeds: number; beds: BedStatus[];
  /** Today only: the room's cleaning state (a dormitory: its least ready bed). openRepairs: unresolved tickets on it. */
  housekeeping: Housekeeping | null; openRepairs: number;
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
/** One room (or dorm bed) of a booking for [startDate, endDate). paymentStatus: NONE, PARTIAL or PAID from the payments taken (Sprint 10). */
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

// ---------------------------------------------------------------- folios (Sprint 8)
export type ChargeCategory = 'ROOM' | 'FOOD' | 'BEVERAGE' | 'ACTIVITY' | 'TRANSPORT' | 'EQUIPMENT' | 'LAUNDRY' | 'OTHER';
/** One bill line. Lines are never edited or deleted: a correction is a REVERSAL (negative) line, or a CREDIT line from a
 *  credit note (Sprint 12). live = a charge that still counts; reversedByItemId / reversalReason = what cancelled it;
 *  movedFrom / movedTo = folio numbers of a split. invoiceNumber = the invoice or debit note the charge is billed on;
 *  adjustable = it can still be reversed or moved (live, not billed, not reduced by a credit note). */
export interface FolioItem {
  id: string; itemType: 'CHARGE' | 'REVERSAL' | 'CREDIT'; category: ChargeCategory; description: string; serviceDate: string; quantity: number;
  unitPrice: number; amount: number; taxAmount: number; total: number; source: 'MANUAL' | 'ROOM_NIGHT' | 'PACKAGE';
  packageId: string | null; packageName: string | null; groupId: string | null; reversesItemId: string | null;
  reversedByItemId: string | null; reason: string | null; reversalReason: string | null; movedFrom: string | null; movedTo: string | null;
  postedAt: string; postedByName: string | null; live: boolean; taxes: FolioItemTax[]; invoiceNumber: string | null; adjustable: boolean;
}
/** One tax on a bill line, as calculated when it was posted (Sprint 9): CGST 2.5% on ₹1,450 = ₹36.25. */
export interface FolioItemTax { code: TaxCode; rate: number; taxableValue: number; amount: number; taxRuleId: string | null; }
export interface FolioSummary { id: string; number: string; name: string; isPrimary: boolean; total: number; balance: number; }
/** A bill: total = sum of lines, GST included (Sprint 9); payments = what it has been paid, net of refunds (Sprint 10);
 *  balance = total − payments (negative: in credit). */
export interface Folio {
  id: string; number: string; name: string; isPrimary: boolean; reservationId: string; reference: string; reservationStatus: ReservationStatus;
  guestId: string; guestName: string; billToGuestId: string | null; billToName: string | null; checkIn: string; checkOut: string; rooms: string;
  currency: string; items: FolioItem[]; categories: { category: ChargeCategory; amount: number }[]; charges: number; reversals: number;
  tax: number; total: number; payments: number; balance: number; folios: FolioSummary[]; canPost: boolean; cantPostReason: string | null;
  paymentLines: FolioPaymentLine[];
}
/** A payment (+) or refund (−) on a bill. Voided: the payment was voided and counts for nothing. */
export interface FolioPaymentLine {
  paymentId: string; paymentNumber: string; refundId: string | null; kind: 'PAYMENT' | 'REFUND'; method: PaymentMethod; amount: number; at: string;
  byName: string | null; detail: string | null; voided: boolean;
}
export interface FolioListItem {
  id: string; number: string; name: string; isPrimary: boolean; reservationId: string; reference: string; reservationStatus: ReservationStatus;
  guestName: string; checkIn: string; checkOut: string; rooms: string; lines: number; total: number; balance: number; currency: string;
}

// ---------------------------------------------------------------- GST and the tax engine (Sprint 9)
export type TaxCode = 'CGST' | 'SGST' | 'IGST' | 'CESS';
export type RegistrationType = 'REGULAR' | 'COMPOSITION';
export type TaxRuleType = 'FLAT' | 'SLAB';
/** A GSTIN a property bills under; effectiveTo null = until further notice; current = it covers today. */
export interface GstRegistration {
  id: string; propertyId: string; gstin: string; legalName: string; tradeName: string | null; stateCode: string; stateName: string;
  registrationType: RegistrationType; effectiveFrom: string; effectiveTo: string | null; current: boolean;
}
/** A rate of a rule: values up to upTo (included; null = no limit) pay rate percent. */
export interface TaxSlab { upTo: number | null; rate: number; inputTaxCredit: boolean; }
/** A category's rule from effectiveFrom to effectiveTo (both included). state: by today's date in India. */
export interface TaxRule {
  id: string; taxCategoryId: string; categoryName: string; effectiveFrom: string; effectiveTo: string | null; ruleType: TaxRuleType;
  slabs: TaxSlab[]; source: string; notes: string | null; state: 'PAST' | 'CURRENT' | 'FUTURE';
  verifiedAt: string | null; verifiedByName: string | null; createdAt: string; createdByName: string | null;
}
/** What is taxed; chargeCategory = the bill charges it covers. */
export interface TaxCategory {
  id: string; code: string; name: string; hsnSac: string; chargeCategory: ChargeCategory | null; description: string | null; rules: number;
  currentRule: TaxRule | null;
}
export interface TaxComponent { code: TaxCode; rate: number; taxableValue: number; amount: number; }
/** A tax preview. taxed false → noTaxReason says why. */
export interface TaxCalculation {
  date: string; amount: number; unitValue: number; taxed: boolean; noTaxReason: string | null; gstin: string | null; supplierState: string | null;
  placeOfSupply: string | null; intraState: boolean; category: TaxCategory | null; rule: TaxRule | null; slab: TaxSlab | null;
  components: TaxComponent[]; tax: number; total: number;
}

// ---------------------------------------------------------------- payments and the cash drawer (Sprint 10)

/** ONLINE: paid through Razorpay (Sprint 11); recorded only by Razorpay's webhook, never typed in. */
export type PaymentMethod = 'CASH' | 'UPI' | 'CARD' | 'BANK_TRANSFER' | 'ONLINE';
export type PaymentStatus = 'PENDING' | 'AUTHORIZED' | 'CAPTURED' | 'FAILED' | 'REFUNDED' | 'PARTIALLY_REFUNDED' | 'CANCELLED';

/** A payment with what became of it: on folios (allocated), on account (an advance), refunded, or voided (CANCELLED). */
export interface Payment {
  id: string; number: string; reservationId: string; reference: string; guestName: string; method: PaymentMethod; status: PaymentStatus;
  amount: number; allocated: number; refunded: number; onAccount: number; refundable: number; currency: string;
  paymentReference: string | null; note: string | null; cashDrawerSessionId: string | null; receivedAt: string; receivedByName: string | null;
  voidedAt: string | null; voidedByName: string | null; voidReason: string | null; canVoid: boolean; cantVoidReason: string | null;
  allocations: PaymentAllocation[]; refunds: Refund[]; folios: PaymentFolio[];
  /** ONLINE only: how the guest paid in Razorpay (upi, card, netbanking, wallet) and the payment link it came through. */
  gatewayMethod: string | null; paymentLinkId: string | null;
}
export interface PaymentAllocation {
  id: string; folioId: string; folioNumber: string; folioName: string; amount: number; refunded: number; allocatedAt: string; allocatedByName: string | null;
}
export interface Refund {
  id: string; paymentId: string; paymentNumber: string; folioId: string | null; folioNumber: string | null; amount: number; method: PaymentMethod;
  status: 'PENDING' | 'PROCESSED' | 'FAILED'; reason: string; reference: string | null; refundedAt: string; refundedByName: string | null;
}
export interface PaymentFolio { id: string; number: string; name: string; isPrimary: boolean; balance: number; }
export interface PaymentListItem {
  id: string; number: string; reservationId: string; reference: string; guestName: string; method: PaymentMethod; status: PaymentStatus;
  amount: number; refunded: number; onAccount: number; paymentReference: string | null; receivedAt: string; receivedByName: string | null;
  folios: string | null;
}
export interface PaymentMethodTotal { method: PaymentMethod; count: number; received: number; refunded: number; net: number; }
export interface PaymentList extends Paged<PaymentListItem> { byMethod: PaymentMethodTotal[]; received: number; refundedTotal: number; net: number; }

export type DrawerStatus = 'OPEN' | 'VARIANCE_PENDING' | 'CLOSED';
export interface CashDrawerSession {
  id: string; cashDrawerId: string; drawerName: string; status: DrawerStatus; openedAt: string; openedByName: string | null; openingFloat: number;
  openNote: string | null; cashIn: number; cashOut: number; expected: number; payments: number; closedAt: string | null; closedByName: string | null;
  counted: number | null; variance: number | null; closeNote: string | null; approvedAt: string | null; approvedByName: string | null;
  approvalNote: string | null; movements: CashDrawerMovement[] | null;
}
export interface CashDrawerMovement {
  id: string; movementType: 'OPENING_FLOAT' | 'PAYMENT' | 'REFUND' | 'VOID' | 'PAY_IN' | 'PAY_OUT'; amount: number; description: string;
  paymentId: string | null; paymentNumber: string | null; reference: string | null; movedAt: string; byName: string | null;
}
// ---------------------------------------------------------------- Razorpay payment links (Sprint 11)

export type PaymentLinkStatus = 'CREATED' | 'PAID' | 'CANCELLED';
/** A Razorpay webhook as received: PROCESSED, IGNORED (nothing to do) or FAILED (Razorpay retries it). */
export interface WebhookEvent { id: string; eventId: string; eventType: string; status: 'PROCESSED' | 'IGNORED' | 'FAILED'; outcome: string | null; attempts: number; receivedAt: string; }
/** A payment link: a Razorpay order the guest pays on their own page (payPath, relative to the app). */
export interface PaymentLink {
  id: string; reservationId: string; reference: string; guestName: string; gatewayOrderId: string; amount: number; currency: string;
  status: PaymentLinkStatus; payPath: string; note: string | null; attempts: number; lastError: string | null; callbackAt: string | null;
  paymentId: string | null; paymentNumber: string | null; paymentStatus: PaymentStatus | null; paidAt: string | null; createdAt: string;
  createdByName: string | null; cancelledAt: string | null; cancelledByName: string | null; cancelReason: string | null; events: WebhookEvent[] | null;
}
/** A booking's links and what it owes. fake: the server uses the built-in fake Razorpay (no keys). */
export interface BookingPaymentLinks {
  reservationId: string; reference: string; due: number; paid: number; owed: number; open: number; canCreate: boolean;
  cantCreateReason: string | null; fake: boolean; links: PaymentLink[];
}
export type PayPageStatus = 'AWAITING_PAYMENT' | 'CONFIRMING' | 'PAID' | 'CANCELLED' | 'CLOSED';
/** The guest's pay page (no sign-in). */
export interface PayPage {
  propertyName: string; bookingReference: string; guestName: string; checkIn: string; checkOut: string; amount: number; currency: string;
  status: PayPageStatus; message: string; keyId: string | null; gatewayOrderId: string | null; fake: boolean; lastError: string | null;
  paymentNumber: string | null; paidAt: string | null;
}

export interface CashDrawer { id: string; name: string; current: CashDrawerSession | null; pendingApproval: CashDrawerSession[]; recent: CashDrawerSession[]; }

// ---------------------------------------------------------------- GST invoices, credit and debit notes, receipts (Sprint 12)

export type DocumentType = 'INVOICE' | 'BILL_OF_SUPPLY' | 'CREDIT_NOTE' | 'DEBIT_NOTE' | 'RECEIPT';
/** Worked out from the credit notes, never stored: CANCELLED = a CANCEL credit note (its charges can be invoiced again). */
export type InvoiceStatus = 'ISSUED' | 'PARTLY_CREDITED' | 'CREDITED' | 'CANCELLED';
export type CreditNoteType = 'CANCEL' | 'REDUCE';
/** A supplier or customer as printed. stateName: "Goa (30)". */
export interface DocumentParty { name: string; tradeName: string | null; gstin: string | null; address: string | null; stateCode: string | null; stateName: string | null; }
export interface DocumentTax { code: TaxCode; rate: number; taxableValue: number; amount: number; }
/** A billed line: one folio charge. discount = what credit notes took off the charge before it was billed; credited /
 *  creditable = taxable value credited since, and what is left to credit. id is null on a preview. */
export interface DocumentLine {
  id: string | null; lineNo: number; folioItemId: string; category: ChargeCategory; description: string; hsnSac: string | null; serviceDate: string;
  quantity: number; unitPrice: number; grossAmount: number; discount: number; taxableValue: number; taxAmount: number; total: number;
  taxes: DocumentTax[]; credited: number; creditable: number; debitNoteNumber: string | null;
}
export interface TaxSummary { hsnSac: string | null; code: TaxCode; rate: number; taxableValue: number; amount: number; }
export interface NoteLine { lineNo: number; description: string; hsnSac: string | null; taxableValue: number; taxAmount: number; total: number; taxes: DocumentTax[]; }
/** A credit note (kind CREDIT_NOTE, noteType CANCEL or REDUCE) or a debit note (kind DEBIT_NOTE). */
export interface InvoiceNote {
  id: string; kind: 'CREDIT_NOTE' | 'DEBIT_NOTE'; number: string; noteType: CreditNoteType | null; noteDate: string; reason: string;
  taxableValue: number; taxTotal: number; total: number; issuedAt: string; issuedByName: string | null; lines: NoteLine[];
}
/** A payment receipt. voided: the payment was voided after the receipt was issued. */
export interface Receipt {
  id: string; number: string; receiptDate: string; paymentId: string; paymentNumber: string; reservationId: string; reference: string;
  receivedFrom: string; amount: number; method: PaymentMethod; paymentReference: string | null; receivedAt: string; paymentStatus: PaymentStatus;
  voided: boolean; issuedAt: string; issuedByName: string | null;
}
export interface EInvoice { status: string; irn: string | null; ackNo: string | null; ackDate: string | null; lastError: string | null; }
/** An invoice with its notes and receipts. billed = invoice + debit notes; credited = credit notes; outstanding = the difference.
 *  unbilledCharges / unbilledAmount: charges posted after it, for a debit note. */
export interface Invoice {
  id: string; number: string; documentType: DocumentType; title: string; financialYear: string; invoiceDate: string; folioId: string;
  folioNumber: string; reservationId: string; reference: string; supplier: DocumentParty; customer: DocumentParty; isB2B: boolean;
  placeOfSupply: string; placeOfSupplyName: string; items: DocumentLine[]; taxSummary: TaxSummary[]; taxableValue: number; cgst: number;
  sgst: number; igst: number; cess: number; taxTotal: number; total: number; amountInWords: string; currency: string; notes: string | null;
  issuedAt: string; issuedByName: string | null; status: InvoiceStatus; billed: number; credited: number; outstanding: number;
  creditNotes: InvoiceNote[]; debitNotes: InvoiceNote[]; receipts: Receipt[]; eInvoice: EInvoice | null; unbilledCharges: number; unbilledAmount: number;
}
export interface InvoiceListItem {
  id: string; number: string; documentType: DocumentType; invoiceDate: string; folioId: string; folioNumber: string; reservationId: string;
  reference: string; customerName: string; customerGstin: string | null; taxableValue: number; taxTotal: number; total: number; credited: number;
  status: InvoiceStatus; creditNotes: number; debitNotes: number; currency: string;
}
/** What issuing a folio's invoice would produce. cantIssueReason: why it can't be issued now; gstinProblem: the GSTIN on file is wrong. */
export interface InvoicePreview {
  folioId: string; folioNumber: string; reservationId: string; reference: string; documentType: DocumentType; supplier: DocumentParty | null;
  billToGuestId: string | null; customerName: string; customerGstin: string | null; customerAddress: string | null; customerStateCode: string | null;
  gstinProblem: string | null; lines: DocumentLine[]; taxableValue: number; taxTotal: number; total: number; invoiceId: string | null;
  invoiceNumber: string | null; cantIssueReason: string | null;
}

// ---------------------------------------------------------------- housekeeping and maintenance (Sprint 13)

export type TaskType = 'DEPARTURE' | 'STAYOVER' | 'DEEP_CLEAN' | 'AFTER_REPAIR';
export type TaskStatus = 'OPEN' | 'IN_PROGRESS' | 'CLEANED' | 'DONE' | 'CANCELLED';
export type TaskAction = 'start' | 'finish' | 'inspect' | 'assign';
export interface ChecklistLine { id: string; label: string; required: boolean; done: boolean; }
/** A cleaning task; nextActions = what the signed-in user may do now. */
export interface HousekeepingTask {
  id: string; unitId: string; bedId: string | null; place: string; taskType: TaskType; status: TaskStatus; source: string;
  reservationReference: string | null; assignedTo: string | null; assignedToName: string | null; startedAt: string | null;
  startedByName: string | null; finishedAt: string | null; finishedByName: string | null; inspectedAt: string | null;
  inspectedByName: string | null; inspectionNote: string | null; failedInspections: number; note: string | null;
  checklist: ChecklistLine[]; businessDate: string; createdAt: string; nextActions: TaskAction[]; version: number;
}
export interface RepairBrief { ticketId: string; number: string; title: string; outOfOrder: boolean; expectedReadyOn: string | null; }
/** A room (bedId null) or dorm bed on the board. rush = not ready and a guest arrives today. No guest names. */
export interface HousekeepingRoom {
  unitId: string; unitCode: string; unitName: string; bedId: string | null; bedCode: string | null; typeName: string; place: string;
  status: Housekeeping; occupied: boolean; departureToday: boolean; arrivalToday: boolean; rush: boolean;
  task: HousekeepingTask | null; repair: RepairBrief | null;
}
export interface HousekeepingSummary { dirty: number; cleaning: number; toInspect: number; ready: number; outOfOrder: number; mine: number; rush: number; }
export interface HousekeepingBoard { today: string; inspectionRequired: boolean; summary: HousekeepingSummary; rooms: HousekeepingRoom[]; }
export interface StaffOption { userId: string; name: string; openWork: number; }
export interface ChecklistItem { id: string; label: string; isRequired: boolean; sortOrder: number; }
export interface ChecklistTemplate {
  id: string; name: string; taskType: TaskType; accommodationTypeId: string | null; accommodationTypeName: string | null; isActive: boolean;
  items: ChecklistItem[];
}

export type TicketStatus = 'OPEN' | 'ASSIGNED' | 'IN_PROGRESS' | 'ON_HOLD' | 'RESOLVED' | 'CLOSED';
export type TicketPriority = 'LOW' | 'NORMAL' | 'HIGH' | 'URGENT';
export type TicketCategory = 'AC' | 'PLUMBING' | 'ELECTRICAL' | 'FURNITURE' | 'APPLIANCE' | 'POOL' | 'CIVIL' | 'OTHER';
export type TicketAction = 'assign' | 'progress' | 'resolve' | 'close' | 'comment' | 'photo';
export interface TicketSummary {
  id: string; number: string; title: string; category: TicketCategory; priority: TicketPriority; status: TicketStatus; place: string;
  unitId: string | null; outOfOrder: boolean; expectedReadyOn: string | null; assignedToName: string | null; reportedByName: string | null;
  photos: number; createdAt: string;
}
export interface TicketComment { id: string; kind: 'NOTE' | 'STATUS'; body: string; fromStatus: TicketStatus | null; toStatus: TicketStatus | null; createdAt: string; createdByName: string | null; }
export interface TicketPhoto { id: string; fileName: string; contentType: string; sizeBytes: number; createdAt: string; createdByName: string | null; }
export interface Ticket {
  id: string; number: string; title: string; description: string | null; category: TicketCategory; priority: TicketPriority; status: TicketStatus;
  place: string; unitId: string | null; unitName: string | null; bedId: string | null; bedCode: string | null; assetId: string | null;
  assetName: string | null; location: string | null; outOfOrder: boolean; expectedReadyOn: string | null; blockId: string | null;
  blockActive: boolean; reportedBy: string | null; reportedByName: string | null; assignedTo: string | null; assignedToName: string | null;
  assignedAt: string | null; startedAt: string | null; resolvedAt: string | null; resolvedByName: string | null; resolution: string | null;
  closedAt: string | null; closedByName: string | null; createdAt: string; comments: TicketComment[]; photos: TicketPhoto[];
  nextActions: TicketAction[]; version: number;
}
export interface MaintenanceAsset {
  id: string; code: string; name: string; category: TicketCategory; unitId: string | null; unitName: string | null; location: string | null;
  isActive: boolean; openTickets: number;
}

// ---------------------------------------------------------------- restaurant POS and KOT (Sprint 14)

export type FoodType = 'VEG' | 'NON_VEG' | 'EGG';
export type OrderType = 'DINE_IN' | 'ROOM_SERVICE' | 'TAKEAWAY';
export type OrderStatus = 'OPEN' | 'SETTLED' | 'VOIDED';
export type OrderItemStatus = 'NEW' | 'SENT' | 'VOIDED';
export type KotStatus = 'NEW' | 'PREPARING' | 'READY' | 'SERVED' | 'CANCELLED';
export type OrderAction = 'add' | 'edit' | 'send' | 'serve' | 'discount' | 'pay' | 'charge' | 'void';
export interface MenuVariant { id: string; name: string; price: number; }
export interface MenuModifier { id: string; name: string; priceDelta: number; }
export interface ModifierGroup { id: string; name: string; minSelect: number; maxSelect: number; modifiers: MenuModifier[]; }
/** A dish or drink; price before GST. isAvailable false = sold out for now. Ordered by variant when it has any. */
export interface MenuItem {
  id: string; categoryId: string; code: string; name: string; description: string | null; foodType: FoodType; price: number;
  isAvailable: boolean; isActive: boolean; sortOrder: number; variants: MenuVariant[]; modifierGroups: ModifierGroup[]; version: number;
}
export interface MenuCategory { id: string; name: string; sortOrder: number; isActive: boolean; items: MenuItem[]; }
export interface Menu { categories: MenuCategory[]; }
export interface RestaurantTable { id: string; code: string; seats: number; area: string | null; sortOrder: number; isActive: boolean; }
/** An order in a list. inKitchen / ready: kitchen tickets still cooking / ready to serve. unsent: lines not sent yet. */
export interface OrderSummary {
  id: string; number: string; orderType: OrderType; status: OrderStatus; where: string; guestName: string | null; covers: number | null;
  lines: number; unsent: number; inKitchen: number; ready: number; total: number; settlement: 'PAID' | 'ROOM_CHARGE' | null;
  openedByName: string | null; createdAt: string; settledAt: string | null;
}
export interface FloorTable { id: string; code: string; seats: number; area: string | null; order: OrderSummary | null; }
export interface Floor { today: string; tables: FloorTable[]; otherOrders: OrderSummary[]; }
export interface OrderList { items: OrderSummary[]; settledTotal: number; openTotal: number; }
export interface InHouseRoom { unitId: string; unitCode: string; unitName: string; reservationId: string; reference: string; guestName: string; checkOut: string; }
export interface OrderItem {
  id: string; menuItemId: string; name: string; variantName: string | null; foodType: FoodType; modifiers: string[]; quantity: number;
  unitPrice: number; amount: number; note: string | null; status: OrderItemStatus; kotId: string | null; kotNumber: string | null;
  kitchenStatus: KotStatus | null; voidReason: string | null; voidedByName: string | null;
}
export interface OrderKot { id: string; number: string; status: KotStatus; sentAt: string; readyAt: string | null; servedAt: string | null; lines: number; }
/** One order. subtotal − discountAmount = net; taxes on net; total = net + taxAmount. Send version back with every change. */
export interface Order {
  id: string; number: string; orderType: OrderType; status: OrderStatus; where: string; tableId: string | null; tableCode: string | null;
  unitId: string | null; unitName: string | null; reservationId: string | null; reservationReference: string | null; guestName: string | null;
  covers: number | null; note: string | null; businessDate: string; items: OrderItem[]; kots: OrderKot[];
  subtotal: number; discountKind: 'PERCENT' | 'AMOUNT' | null; discountValue: number | null; discountAmount: number; discountReason: string | null;
  discountedByName: string | null; net: number; taxes: DocumentTax[]; taxAmount: number; total: number; taxNote: string | null;
  settlement: 'PAID' | 'ROOM_CHARGE' | null; paymentMethod: PaymentMethod | null; paymentReference: string | null; folioId: string | null;
  folioNumber: string | null; settledAt: string | null; settledByName: string | null; voidedAt: string | null; voidedByName: string | null;
  voidReason: string | null; openedByName: string | null; createdAt: string; nextActions: OrderAction[]; version: number;
}
export interface OrderLineRequest { menuItemId: string; variantId: string | null; modifierIds: string[] | null; quantity: number; note: string | null; }
export interface KotItem { id: string; name: string; quantity: number; modifiers: string | null; note: string | null; foodType: FoodType; voided: boolean; voidReason: string | null; }
export interface Kot {
  id: string; number: string; orderId: string; orderNumber: string; where: string; status: KotStatus; sentAt: string; sentByName: string | null;
  startedAt: string | null; readyAt: string | null; note: string | null; items: KotItem[]; version: number;
}
export interface Kitchen { now: string; tickets: Kot[]; }

// ---------------------------------------------------------------- inventory and recipe costing (Sprint 15)
export type UnitDimension = 'MASS' | 'VOLUME' | 'COUNT';
export type AdjustmentKind = 'RECEIVED' | 'WASTAGE' | 'EXPIRED' | 'DAMAGED' | 'CORRECTION' | 'TRANSFER';
export type MovementType = 'OPENING' | 'RECEIVED' | 'CONSUMPTION' | 'WASTAGE' | 'ADJUSTMENT' | 'COUNT' | 'TRANSFER_OUT' | 'TRANSFER_IN';
/** factor: base units (g, ml, pcs) in one; kg = 1000. */
export interface StockUnit { id: string; code: string; name: string; dimension: UnitDimension; factor: number; }
export interface InventoryCategory { id: string; name: string; sortOrder: number; isActive: boolean; }
export interface StockLocation { id: string; code: string; name: string; isKitchen: boolean; sortOrder: number; isActive: boolean; }
export interface StockAt { locationId: string; locationCode: string; onHand: number; }
/** unitCost: one unit before GST (moving average). low: at or below the reorder level. negative: used more than recorded. */
export interface InventoryItem {
  id: string; categoryId: string; categoryName: string; code: string; name: string; unitId: string; unit: string; dimension: UnitDimension;
  unitCost: number; reorderLevel: number | null; isActive: boolean; onHand: number; value: number; low: boolean; negative: boolean;
  levels: StockAt[]; version: number;
}
export interface Inventory { units: StockUnit[]; categories: InventoryCategory[]; locations: StockLocation[]; items: InventoryItem[]; value: number; lowCount: number; }
export interface StockAdjustment {
  id: string; number: string; kind: AdjustmentKind; itemId: string; itemName: string; unit: string; locationId: string; locationName: string;
  toLocationId: string | null; toLocationName: string | null; quantity: number; unitCost: number; value: number; reason: string; onHandAfter: number;
  byName: string | null; createdAt: string;
}
export interface StockCountLine { id: string; number: string; itemId: string; itemName: string; unit: string; systemQuantity: number; countedQuantity: number; difference: number; value: number; reason: string | null; }
export interface StockCountResult { locationName: string; lines: StockCountLine[]; value: number; }
export interface StockMovement {
  id: string; at: string; itemId: string; itemName: string; unit: string; locationCode: string; type: MovementType; quantity: number;
  unitCost: number; value: number; onHandAfter: number; source: string | null; note: string | null; byName: string | null;
}
export interface StockMovementList { items: StockMovement[]; more: boolean; }
export interface RecipeIngredient {
  id: string; itemId: string; itemCode: string; itemName: string; quantity: number; unitId: string; unit: string; itemQuantity: number; itemUnit: string; cost: number;
}
export interface Recipe { id: string; menuItemId: string; variantId: string | null; note: string | null; ingredients: RecipeIngredient[]; cost: number; version: number; }
/** A dish or portion as costed. recipe null: uses nothing from stock. ownRecipe false: a portion made by its dish's recipe. */
export interface CostedDish {
  menuItemId: string; variantId: string | null; name: string; category: string; foodType: FoodType; price: number; ownRecipe: boolean; recipe: Recipe | null;
  cost: number | null; foodCostPercent: number | null; contribution: number | null; marginPercent: number | null;
}
export interface RecipeList { dishes: CostedDish[]; units: StockUnit[]; }
export interface FoodCostLine {
  menuItemId: string; variantId: string | null; name: string; category: string; price: number; cost: number | null; foodCostPercent: number | null;
  contribution: number | null; marginPercent: number | null; sold: number; revenue: number; costOfSales: number; actualFoodCostPercent: number | null;
}
export interface FoodCostReport {
  from: string; to: string; lines: FoodCostLine[]; highest: FoodCostLine[]; lowest: FoodCostLine[]; noRecipe: string[]; sold: number; revenue: number;
  costOfSales: number; foodCostPercent: number | null; wastage: number;
}
