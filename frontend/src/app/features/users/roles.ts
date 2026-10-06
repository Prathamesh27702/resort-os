export function roleName(code: string): string {
  const s = code.toLowerCase().replace(/_/g, ' ');
  return s.charAt(0).toUpperCase() + s.slice(1);
}

/** Display order for the role picker: management first, then by department. */
export const ROLE_ORDER = [
  'OWNER', 'GENERAL_MANAGER', 'MANAGER', 'ACCOUNTANT', 'HR',
  'RECEPTIONIST', 'HOUSEKEEPING_SUPERVISOR', 'HOUSEKEEPER', 'MAINTENANCE',
  'RESTAURANT_MANAGER', 'WAITER', 'KITCHEN', 'INVENTORY_MANAGER', 'PURCHASING',
  'ACTIVITY_MANAGER', 'DRIVER', 'EMPLOYEE',
];
