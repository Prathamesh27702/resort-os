/** One link in the staff menu. `perm` hides it from people whose role can't use it (the API checks again). */
export interface NavItem { label: string; icon: string; link: string; perm?: string; exact?: boolean; keywords?: string; }
export interface NavSection { id: string; title: string; items: NavItem[]; }

/**
 * The staff menu, in sections (docs/design.md 7). Sections a person can't use at all are hidden; each section can be
 * folded and remembers that per device. The labels are the screens' names, so keep them in step with the e2e tests.
 */
export const NAV: NavSection[] = [
  {
    id: 'today', title: 'Today', items: [
      { label: 'Today', icon: 'wb_sunny', link: '/dashboard', keywords: 'dashboard home overview' },
      { label: 'Reports', icon: 'monitoring', link: '/reports', perm: 'report.operational.read', keywords: 'occupancy revenue adr revpar gst profit' },
      { label: 'Front desk', icon: 'room_service', link: '/front-desk', perm: 'reservation.read', keywords: 'arrivals departures check in check out walk-in' },
      { label: 'Room status', icon: 'grid_view', link: '/rooms/status', perm: 'accommodation.read', keywords: 'room board block' },
    ],
  },
  {
    id: 'guests', title: 'Guests & money', items: [
      { label: 'Reservations', icon: 'event_available', link: '/reservations', perm: 'reservation.read', exact: true, keywords: 'bookings enquiries' },
      { label: 'Calendar', icon: 'calendar_month', link: '/reservations/calendar', perm: 'reservation.read', keywords: 'tape chart' },
      { label: 'Folios', icon: 'receipt_long', link: '/folios', perm: 'folio.read', keywords: 'bills charges' },
      { label: 'Payments', icon: 'payments', link: '/payments', perm: 'payment.read', keywords: 'upi cash card razorpay refund drawer' },
      { label: 'Invoices', icon: 'request_quote', link: '/invoices', perm: 'invoice.read', keywords: 'gst tax invoice credit note' },
      { label: 'Guests', icon: 'group', link: '/guests', perm: 'guest.read', keywords: 'customers kyc' },
      { label: 'Form C', icon: 'badge', link: '/form-c', perm: 'guest_document.read', keywords: 'frro foreign guests passport visa c-form register' },
      { label: 'Messages', icon: 'forum', link: '/messages', perm: 'message.read', keywords: 'whatsapp email sms templates' },
    ],
  },
  {
    id: 'rooms', title: 'Rooms', items: [
      { label: 'Housekeeping', icon: 'cleaning_services', link: '/housekeeping', perm: 'housekeeping.read', keywords: 'cleaning inspection' },
      { label: 'Maintenance', icon: 'build', link: '/maintenance', perm: 'maintenance.create', keywords: 'repair ticket out of order' },
      { label: 'Rooms', icon: 'bed', link: '/rooms', perm: 'accommodation.read', exact: true, keywords: 'units beds dorm' },
    ],
  },
  {
    id: 'resort', title: 'Resort', items: [
      { label: 'Activities', icon: 'kayaking', link: '/activities', perm: 'activity.read', keywords: 'kayak tours sessions' },
      { label: 'Equipment', icon: 'scuba_diving', link: '/equipment', perm: 'equipment.read', keywords: 'gear hire' },
      { label: 'Beach transport', icon: 'airport_shuttle', link: '/transport', perm: 'transport.read', keywords: 'pickup drop cab station airport' },
      { label: 'Restaurant', icon: 'restaurant', link: '/restaurant', perm: 'pos_order.read', exact: true, keywords: 'tables orders pos kot menu' },
      { label: 'Kitchen', icon: 'soup_kitchen', link: '/restaurant/kitchen', perm: 'kot.read', keywords: 'kot kds' },
    ],
  },
  {
    id: 'stock', title: 'Stock & buying', items: [
      { label: 'Inventory', icon: 'inventory_2', link: '/inventory', perm: 'inventory.read', exact: true, keywords: 'stock store items' },
      { label: 'Purchasing', icon: 'shopping_cart', link: '/inventory/purchases', perm: 'purchase.read', keywords: 'purchase order grn bill' },
      { label: 'Suppliers', icon: 'local_shipping', link: '/inventory/suppliers', perm: 'supplier.read', keywords: 'vendors' },
      { label: 'Expenses', icon: 'account_balance_wallet', link: '/expenses', perm: 'expense.read', keywords: 'spend receipts' },
      { label: 'Budget', icon: 'insights', link: '/budgets', perm: 'budget.read', keywords: 'budgets forecast' },
    ],
  },
  {
    id: 'team', title: 'Team', items: [
      { label: 'Employees', icon: 'groups', link: '/employees', perm: 'employee.read', keywords: 'staff hr' },
      { label: 'Roster', icon: 'calendar_view_week', link: '/roster', perm: 'roster.read', keywords: 'shifts schedule' },
      { label: 'Attendance', icon: 'pin_drop', link: '/attendance', perm: 'attendance.self', keywords: 'clock in out' },
      { label: 'Timesheets', icon: 'fact_check', link: '/timesheets', perm: 'timesheet.self', keywords: 'hours overtime' },
      { label: 'Leave', icon: 'beach_access', link: '/leave', perm: 'leave.create', keywords: 'holiday time off' },
      { label: 'Payroll export', icon: 'summarize', link: '/payroll', perm: 'payroll.export', keywords: 'salary csv' },
    ],
  },
  {
    id: 'settings', title: 'Settings', items: [
      { label: 'Room types', icon: 'category', link: '/settings/room-types', perm: 'accommodation.read' },
      { label: 'Rates', icon: 'sell', link: '/settings/rates', perm: 'rate.read', keywords: 'tariff prices' },
      { label: 'Packages', icon: 'redeem', link: '/settings/packages', perm: 'rate.read', keywords: 'meal plan cp map ap' },
      { label: 'GST', icon: 'percent', link: '/settings/gst', perm: 'tax.read', keywords: 'tax hsn sac' },
      { label: 'Properties', icon: 'holiday_village', link: '/settings/properties', perm: 'property.read' },
      { label: 'Staff accounts', icon: 'badge', link: '/users', perm: 'user.read', keywords: 'users logins' },
      { label: 'Roles & permissions', icon: 'shield_person', link: '/roles', perm: 'role.read' },
      { label: 'Audit trail', icon: 'history', link: '/audit', perm: 'audit.read', keywords: 'log history' },
    ],
  },
];

/** The phone tab bar: the first four of these a person may use, then More (docs/design.md 7, Phone). */
export const TAB_PREFERENCE = ['/dashboard', '/front-desk', '/rooms/status', '/housekeeping', '/restaurant', '/restaurant/kitchen',
  '/folios', '/attendance', '/reservations', '/leave'];

/** Short labels for the phone tab bar (the menu uses the full ones). */
export const TAB_LABELS: Record<string, string> = {
  '/dashboard': 'Today', '/front-desk': 'Desk', '/rooms/status': 'Rooms', '/housekeeping': 'Cleaning', '/restaurant': 'Tables',
  '/restaurant/kitchen': 'Kitchen', '/folios': 'Bills', '/attendance': 'Clock in', '/reservations': 'Bookings', '/leave': 'Leave',
};
