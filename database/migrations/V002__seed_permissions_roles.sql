-- =====================================================================
-- V002  Permission catalogue + 17 system roles + default permission matrix
-- Sprint 1. The catalogue covers every module in the sprint plan so the
-- matrix can be reviewed once, up front. Later sprints may ADD codes in
-- new migrations; codes are never renamed.
-- =====================================================================

INSERT INTO permissions (code, module, description, is_sensitive) VALUES
-- Identity & administration
('user.read',                    'identity',      'View users',                                   false),
('user.create',                  'identity',      'Create users',                                 false),
('user.update',                  'identity',      'Edit users and their role grants',             false),
('user.deactivate',              'identity',      'Deactivate users / revoke sessions',           false),
('role.read',                    'identity',      'View roles and permissions',                   false),
('role.manage',                  'identity',      'Create/edit custom roles',                     true),
('organization.read',            'property',      'View organisation details',                    false),
('organization.update',          'property',      'Edit organisation details',                    true),
('property.read',                'property',      'View properties',                              false),
('property.create',              'property',      'Create properties',                            false),
('property.update',              'property',      'Edit properties',                              false),
('property_settings.update',     'property',      'Change property settings',                     false),
('system.settings',              'system',        'Change platform settings',                     true),
('audit.read',                   'audit',         'View audit logs',                              true),
-- Accommodation, rates, tax
('accommodation.read',           'accommodation', 'View rooms, dormitories and beds',             false),
('accommodation.manage',         'accommodation', 'Configure rooms, dormitories and beds',        false),
('unit_block.manage',            'accommodation', 'Block / unblock rooms and beds',               false),
('rate.read',                    'rates',         'View rate plans and packages',                 false),
('rate.manage',                  'rates',         'Edit rate plans and packages',                 false),
('tax.read',                     'gst',           'View GST registrations and tax rules',         false),
('tax.manage',                   'gst',           'Edit GST registrations and tax rules',         true),
-- Guests & reservations
('guest.read',                   'guests',        'View guest profiles',                          false),
('guest.create',                 'guests',        'Create guest profiles',                        false),
('guest.update',                 'guests',        'Edit guest profiles',                          false),
('guest.delete',                 'guests',        'Delete / anonymise guest profiles',            true),
('guest_document.read',          'guests',        'View guest ID documents',                      true),
('reservation.read',             'reservations',  'View reservations',                            false),
('reservation.create',           'reservations',  'Create reservations',                          false),
('reservation.update',           'reservations',  'Modify reservations / reassign rooms',         false),
('reservation.cancel',           'reservations',  'Cancel reservations / mark no-show',           false),
('reservation.delete',           'reservations',  'Delete enquiry-stage reservations',            true),
('reservation.override_rate',    'reservations',  'Override the calculated rate',                 true),
-- Front desk, folio, payments, invoicing
('frontdesk.checkin',            'frontdesk',     'Check guests in',                              false),
('frontdesk.checkout',           'frontdesk',     'Check guests out',                             false),
('folio.read',                   'folio',         'View folios',                                  false),
('folio.post_charge',            'folio',         'Post charges to a folio',                      false),
('folio.adjust',                 'folio',         'Adjust / reverse folio charges',               true),
('payment.read',                 'payments',      'View payments',                                false),
('payment.create',               'payments',      'Record payments',                              false),
('payment.refund',               'payments',      'Issue refunds',                                true),
('payment.void',                 'payments',      'Void payments',                                true),
('cash_drawer.operate',          'payments',      'Open/close the cash drawer',                   false),
('cash_drawer.approve_variance', 'payments',      'Approve cash drawer variances',                true),
('invoice.read',                 'gst',           'View invoices and receipts',                   false),
('invoice.create',               'gst',           'Issue invoices',                               false),
('invoice.cancel',               'gst',           'Cancel invoices (via credit note)',            true),
('credit_note.create',           'gst',           'Issue credit / debit notes',                   true),
-- Housekeeping & maintenance
('housekeeping.read',            'housekeeping',  'View housekeeping board',                      false),
('housekeeping.update',          'housekeeping',  'Update cleaning status',                       false),
('housekeeping.assign',          'housekeeping',  'Assign housekeeping tasks',                    false),
('housekeeping.inspect',         'housekeeping',  'Inspect and release rooms as READY',           false),
('maintenance.read',             'maintenance',   'View maintenance tickets',                     false),
('maintenance.create',           'maintenance',   'Report maintenance issues',                    false),
('maintenance.update',           'maintenance',   'Work and resolve maintenance tickets',         false),
('maintenance.assign',           'maintenance',   'Assign maintenance tickets',                   false),
-- Restaurant
('menu.read',                    'menu',          'View menu',                                    false),
('menu.create',                  'menu',          'Create menu items',                            false),
('menu.update',                  'menu',          'Edit menu items / availability',               false),
('pos_order.read',               'pos',           'View restaurant orders',                       false),
('pos_order.create',             'pos',           'Create restaurant orders',                     false),
('pos_order.update',             'pos',           'Modify restaurant orders',                     false),
('pos_order.void',               'pos',           'Void restaurant orders / items',               true),
('pos.discount',                 'pos',           'Apply discounts',                              false),
('kot.read',                     'pos',           'View kitchen order tickets',                   false),
('kot.update',                   'pos',           'Update KOT status',                            false),
-- Inventory & purchasing
('inventory.read',               'inventory',     'View stock',                                   false),
('inventory.adjust',             'inventory',     'Record wastage / adjustments / counts',        false),
('inventory.manage',             'inventory',     'Configure stock items, units, locations',      false),
('recipe.read',                  'inventory',     'View recipes and costing',                     false),
('recipe.manage',                'inventory',     'Edit recipes',                                 false),
('supplier.read',                'purchasing',    'View suppliers',                               false),
('supplier.manage',              'purchasing',    'Create/edit suppliers',                        false),
('purchase.read',                'purchasing',    'View purchase requests/orders',                false),
('purchase.create',              'purchasing',    'Raise purchase requests/orders',               false),
('purchase.approve',             'purchasing',    'Approve purchase orders',                      true),
('grn.create',                   'purchasing',    'Record goods received',                        false),
('supplier_invoice.manage',      'purchasing',    'Record supplier invoices and payments',        false),
-- People
('employee.read',                'employees',     'View employee profiles',                       false),
('employee.create',              'employees',     'Create employees',                             false),
('employee.update',              'employees',     'Edit employees',                               false),
('employee_salary.read',         'employees',     'View salary / compensation',                   true),
('employee_document.read',       'employees',     'View employee documents',                      true),
('attendance.self',              'attendance',    'Clock in/out for self',                        false),
('attendance.read',              'attendance',    'View staff attendance',                        false),
('attendance.update',            'attendance',    'Correct attendance / manager override',        false),
('timesheet.self',               'attendance',    'Submit own timesheet',                         false),
('timesheet.approve',            'attendance',    'Approve / reject timesheets',                  false),
('roster.self',                  'roster',        'View own roster',                              false),
('roster.read',                  'roster',        'View full roster',                             false),
('roster.manage',                'roster',        'Create/edit roster shifts',                    false),
('leave.create',                 'leave',         'Request leave for self',                       false),
('leave.read',                   'leave',         'View staff leave',                             false),
('leave.approve',                'leave',         'Approve leave',                                false),
('leave.reject',                 'leave',         'Reject leave',                                 false),
('leave.manage_balances',        'leave',         'Adjust leave balances / types / holidays',     false),
-- Guest experiences
('activity.read',                'activities',    'View activities and schedules',                false),
('activity.manage',              'activities',    'Configure activities and schedules',           false),
('activity_booking.create',      'activities',    'Book guests on activities',                    false),
('transport.read',               'transport',     'View vehicles, trips, manifests',              false),
('transport.manage',             'transport',     'Configure vehicles, routes, trips',            false),
('transport_booking.create',     'transport',     'Book guests on transfers',                     false),
('transport.trip_operate',       'transport',     'Start/complete trips, mark passengers',        false),
('equipment.read',               'equipment',     'View equipment',                               false),
('equipment.manage',             'equipment',     'Configure equipment',                          false),
('equipment.issue',              'equipment',     'Issue / receive equipment',                    false),
-- Finance control
('expense.read',                 'expenses',      'View expenses',                                false),
('expense.create',               'expenses',      'Record expenses',                              false),
('expense.approve',              'expenses',      'Approve expenses',                             true),
('budget.read',                  'budgeting',     'View budgets',                                 false),
('budget.manage',                'budgeting',     'Create/edit budgets',                          true),
-- Reports, integrations, communication
('report.operational.read',      'reports',       'View operational reports',                     false),
('financial_report.read',        'reports',       'View financial reports',                       true),
('integration.manage',           'integrations',  'Configure Razorpay/Zoho/OTA/WhatsApp',          true),
('ota.read',                     'integrations',  'View OTA mappings and sync status',            false),
('ota.manage',                   'integrations',  'Edit OTA mappings, push rates/inventory',      false),
('message.read',                 'communication', 'View guest messages',                          false),
('message.send',                 'communication', 'Send guest messages',                          false);

-- ---------------------------------------------------------------------
-- System roles
-- ---------------------------------------------------------------------
INSERT INTO roles (code, name, description, is_system) VALUES
('OWNER',                   'Owner',                   'Full access to the organisation',                      true),
('GENERAL_MANAGER',         'General Manager',         'Runs the property; no platform/integration settings',   true),
('MANAGER',                 'Manager',                 'Duty manager across front office and operations',      true),
('RECEPTIONIST',            'Receptionist',            'Front desk: bookings, check-in/out, payments',         true),
('HOUSEKEEPING_SUPERVISOR', 'Housekeeping Supervisor', 'Assigns and inspects housekeeping',                    true),
('HOUSEKEEPER',             'Housekeeper',             'Cleans rooms, reports issues',                         true),
('RESTAURANT_MANAGER',      'Restaurant Manager',      'Menu, POS, kitchen, F&B stock',                        true),
('WAITER',                  'Waiter',                  'Takes orders, posts room charges',                     true),
('KITCHEN',                 'Kitchen',                 'Kitchen display / KOT',                                true),
('INVENTORY_MANAGER',       'Inventory Manager',       'Stock control',                                        true),
('PURCHASING',              'Purchasing',              'Suppliers and purchase orders',                        true),
('ACCOUNTANT',              'Accountant',              'Invoices, payments, expenses, financial reports',      true),
('HR',                      'HR',                      'Employees, attendance, leave, roster',                 true),
('MAINTENANCE',             'Maintenance',             'Works maintenance tickets',                            true),
('DRIVER',                  'Driver',                  'Operates beach transfers',                             true),
('ACTIVITY_MANAGER',        'Activity Manager',        'Activities and equipment',                             true),
('EMPLOYEE',                'Employee',                'Self-service only',                                    true);

-- ---------------------------------------------------------------------
-- Default matrix. Patterns use SQL LIKE ('reservation.%' = all reservation
-- permissions). '!' prefix = exclusion applied after inclusions.
-- SELF = the self-service set every staff role receives.
-- ---------------------------------------------------------------------
CREATE TEMP TABLE _matrix (role_code text, pattern text);

INSERT INTO _matrix (role_code, pattern)
SELECT r, p FROM (VALUES
 ('OWNER','%'),

 ('GENERAL_MANAGER','%'),
 ('GENERAL_MANAGER','!system.settings'),
 ('GENERAL_MANAGER','!organization.update'),
 ('GENERAL_MANAGER','!integration.manage'),
 ('GENERAL_MANAGER','!role.manage'),
 ('GENERAL_MANAGER','!employee_salary.read'),

 ('MANAGER','property.read'),('MANAGER','user.read'),('MANAGER','role.read'),
 ('MANAGER','accommodation.read'),('MANAGER','unit_block.manage'),('MANAGER','rate.read'),('MANAGER','tax.read'),
 ('MANAGER','guest.read'),('MANAGER','guest.create'),('MANAGER','guest.update'),('MANAGER','guest_document.read'),
 ('MANAGER','reservation.%'),('MANAGER','!reservation.delete'),
 ('MANAGER','frontdesk.%'),('MANAGER','folio.%'),
 ('MANAGER','payment.read'),('MANAGER','payment.create'),('MANAGER','payment.refund'),
 ('MANAGER','cash_drawer.%'),
 ('MANAGER','invoice.read'),('MANAGER','invoice.create'),
 ('MANAGER','housekeeping.%'),('MANAGER','maintenance.%'),
 ('MANAGER','menu.read'),('MANAGER','pos_order.%'),('MANAGER','pos.discount'),('MANAGER','kot.%'),
 ('MANAGER','inventory.read'),('MANAGER','supplier.read'),('MANAGER','purchase.read'),('MANAGER','purchase.create'),
 ('MANAGER','employee.read'),('MANAGER','attendance.read'),('MANAGER','attendance.update'),('MANAGER','timesheet.approve'),
 ('MANAGER','roster.%'),('MANAGER','leave.read'),('MANAGER','leave.approve'),('MANAGER','leave.reject'),
 ('MANAGER','activity.%'),('MANAGER','activity_booking.create'),
 ('MANAGER','transport.%'),('MANAGER','transport_booking.create'),
 ('MANAGER','equipment.%'),
 ('MANAGER','expense.read'),('MANAGER','expense.create'),('MANAGER','budget.read'),
 ('MANAGER','report.operational.read'),('MANAGER','ota.read'),('MANAGER','message.%'),

 ('RECEPTIONIST','property.read'),('RECEPTIONIST','accommodation.read'),('RECEPTIONIST','rate.read'),
 ('RECEPTIONIST','guest.read'),('RECEPTIONIST','guest.create'),('RECEPTIONIST','guest.update'),('RECEPTIONIST','guest_document.read'),
 ('RECEPTIONIST','reservation.read'),('RECEPTIONIST','reservation.create'),('RECEPTIONIST','reservation.update'),('RECEPTIONIST','reservation.cancel'),
 ('RECEPTIONIST','frontdesk.%'),('RECEPTIONIST','folio.read'),('RECEPTIONIST','folio.post_charge'),
 ('RECEPTIONIST','payment.read'),('RECEPTIONIST','payment.create'),('RECEPTIONIST','cash_drawer.operate'),
 ('RECEPTIONIST','invoice.read'),('RECEPTIONIST','invoice.create'),
 ('RECEPTIONIST','housekeeping.read'),('RECEPTIONIST','maintenance.read'),('RECEPTIONIST','maintenance.create'),
 ('RECEPTIONIST','activity.read'),('RECEPTIONIST','activity_booking.create'),
 ('RECEPTIONIST','transport.read'),('RECEPTIONIST','transport_booking.create'),
 ('RECEPTIONIST','equipment.read'),('RECEPTIONIST','equipment.issue'),
 ('RECEPTIONIST','message.%'),

 ('HOUSEKEEPING_SUPERVISOR','property.read'),('HOUSEKEEPING_SUPERVISOR','accommodation.read'),
 ('HOUSEKEEPING_SUPERVISOR','reservation.read'),('HOUSEKEEPING_SUPERVISOR','housekeeping.%'),
 ('HOUSEKEEPING_SUPERVISOR','maintenance.read'),('HOUSEKEEPING_SUPERVISOR','maintenance.create'),
 ('HOUSEKEEPING_SUPERVISOR','inventory.read'),('HOUSEKEEPING_SUPERVISOR','roster.read'),('HOUSEKEEPING_SUPERVISOR','attendance.read'),

 ('HOUSEKEEPER','property.read'),('HOUSEKEEPER','accommodation.read'),('HOUSEKEEPER','housekeeping.read'),
 ('HOUSEKEEPER','housekeeping.update'),('HOUSEKEEPER','maintenance.create'),

 ('RESTAURANT_MANAGER','property.read'),('RESTAURANT_MANAGER','menu.%'),('RESTAURANT_MANAGER','pos_order.%'),
 ('RESTAURANT_MANAGER','pos.discount'),('RESTAURANT_MANAGER','kot.%'),('RESTAURANT_MANAGER','recipe.%'),
 ('RESTAURANT_MANAGER','inventory.read'),('RESTAURANT_MANAGER','inventory.adjust'),
 ('RESTAURANT_MANAGER','supplier.read'),('RESTAURANT_MANAGER','purchase.read'),('RESTAURANT_MANAGER','purchase.create'),
 ('RESTAURANT_MANAGER','guest.read'),('RESTAURANT_MANAGER','reservation.read'),('RESTAURANT_MANAGER','folio.post_charge'),
 ('RESTAURANT_MANAGER','payment.read'),('RESTAURANT_MANAGER','payment.create'),('RESTAURANT_MANAGER','cash_drawer.operate'),
 ('RESTAURANT_MANAGER','report.operational.read'),('RESTAURANT_MANAGER','roster.read'),('RESTAURANT_MANAGER','attendance.read'),

 ('WAITER','property.read'),('WAITER','menu.read'),('WAITER','pos_order.read'),('WAITER','pos_order.create'),('WAITER','pos_order.update'),
 ('WAITER','kot.read'),('WAITER','guest.read'),('WAITER','folio.post_charge'),('WAITER','payment.create'),

 ('KITCHEN','property.read'),('KITCHEN','menu.read'),('KITCHEN','kot.read'),('KITCHEN','kot.update'),
 ('KITCHEN','recipe.read'),('KITCHEN','inventory.read'),

 ('INVENTORY_MANAGER','property.read'),('INVENTORY_MANAGER','inventory.%'),('INVENTORY_MANAGER','recipe.read'),
 ('INVENTORY_MANAGER','supplier.read'),('INVENTORY_MANAGER','purchase.read'),('INVENTORY_MANAGER','purchase.create'),
 ('INVENTORY_MANAGER','grn.create'),('INVENTORY_MANAGER','report.operational.read'),

 ('PURCHASING','property.read'),('PURCHASING','supplier.%'),('PURCHASING','purchase.read'),('PURCHASING','purchase.create'),
 ('PURCHASING','grn.create'),('PURCHASING','supplier_invoice.manage'),('PURCHASING','inventory.read'),

 ('ACCOUNTANT','property.read'),('ACCOUNTANT','organization.read'),('ACCOUNTANT','tax.%'),
 ('ACCOUNTANT','folio.read'),('ACCOUNTANT','payment.read'),('ACCOUNTANT','payment.refund'),
 ('ACCOUNTANT','invoice.%'),('ACCOUNTANT','credit_note.create'),('ACCOUNTANT','cash_drawer.approve_variance'),
 ('ACCOUNTANT','supplier.read'),('ACCOUNTANT','purchase.read'),('ACCOUNTANT','supplier_invoice.manage'),
 ('ACCOUNTANT','expense.%'),('ACCOUNTANT','budget.read'),
 ('ACCOUNTANT','report.operational.read'),('ACCOUNTANT','financial_report.read'),('ACCOUNTANT','ota.read'),

 ('HR','property.read'),('HR','user.read'),('HR','employee.%'),('HR','employee_salary.read'),('HR','employee_document.read'),
 ('HR','attendance.read'),('HR','attendance.update'),('HR','timesheet.approve'),('HR','roster.%'),
 ('HR','leave.read'),('HR','leave.approve'),('HR','leave.reject'),('HR','leave.manage_balances'),

 ('MAINTENANCE','property.read'),('MAINTENANCE','accommodation.read'),('MAINTENANCE','maintenance.read'),
 ('MAINTENANCE','maintenance.create'),('MAINTENANCE','maintenance.update'),('MAINTENANCE','equipment.read'),

 ('DRIVER','property.read'),('DRIVER','transport.read'),('DRIVER','transport.trip_operate'),

 ('ACTIVITY_MANAGER','property.read'),('ACTIVITY_MANAGER','activity.%'),('ACTIVITY_MANAGER','activity_booking.create'),
 ('ACTIVITY_MANAGER','equipment.%'),('ACTIVITY_MANAGER','transport.read'),('ACTIVITY_MANAGER','guest.read'),
 ('ACTIVITY_MANAGER','reservation.read'),('ACTIVITY_MANAGER','folio.post_charge'),

 ('EMPLOYEE','property.read')
) AS v(r, p);

-- self-service set for every role
INSERT INTO _matrix (role_code, pattern)
SELECT code, s FROM roles, unnest(ARRAY['attendance.self','timesheet.self','roster.self','leave.create']) AS s
WHERE is_system;

INSERT INTO role_permissions (role_id, permission_code)
SELECT DISTINCT r.id, p.code
FROM _matrix m
JOIN roles r       ON r.code = m.role_code AND r.organization_id IS NULL
JOIN permissions p ON p.code LIKE m.pattern
WHERE m.pattern NOT LIKE '!%'
  AND NOT EXISTS (
        SELECT 1 FROM _matrix x
        WHERE x.role_code = m.role_code
          AND x.pattern LIKE '!%'
          AND p.code LIKE substr(x.pattern, 2));

DROP TABLE _matrix;
