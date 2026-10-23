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
/** One bill line. Lines are never edited or deleted: a correction is a REVERSAL (negative) line. live = a charge that still
 *  counts; reversedByItemId / reversalReason = what cancelled it; movedFrom / movedTo = folio numbers of a split. */
export interface FolioItem {
  id: string; itemType: 'CHARGE' | 'REVERSAL'; category: ChargeCategory; description: string; serviceDate: string; quantity: number;
  unitPrice: number; amount: number; taxAmount: number; total: number; source: 'MANUAL' | 'ROOM_NIGHT' | 'PACKAGE';
  packageId: string | null; packageName: string | null; groupId: string | null; reversesItemId: string | null;
  reversedByItemId: string | null; reason: string | null; reversalReason: string | null; movedFrom: string | null; movedTo: string | null;
  postedAt: string; postedByName: string | null; live: boolean; taxes: FolioItemTax[];
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
