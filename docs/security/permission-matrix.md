# Permission matrix

Generated from the seeded database (`V002__seed_permissions_roles.sql`; `payroll.export` added by `V021__leave.sql` in Sprint 19, 118 permissions). ✓ = granted by default. 🛡 = sensitive.

Roles are templates: a grant can be organisation-wide or limited to one property. Code only ever checks permission codes, never role names.

Legend: **OWN** OWNER · **GM** GENERAL_MANAGER · **MGR** MANAGER · **ACC** ACCOUNTANT · **HR** HR · **RCP** RECEPTIONIST · **HKS** HOUSEKEEPING_SUPERVISOR · **HK** HOUSEKEEPER · **MNT** MAINTENANCE · **RM** RESTAURANT_MANAGER · **WTR** WAITER · **KIT** KITCHEN · **INV** INVENTORY_MANAGER · **PUR** PURCHASING · **ACT** ACTIVITY_MANAGER · **DRV** DRIVER · **EMP** EMPLOYEE


### accommodation

| Permission | OWN | GM | MGR | ACC | HR | RCP | HKS | HK | MNT | RM | WTR | KIT | INV | PUR | ACT | DRV | EMP |
|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `accommodation.manage`<br>Configure rooms, dormitories and beds | ✓ | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |  |  |  |
| `accommodation.read`<br>View rooms, dormitories and beds | ✓ | ✓ | ✓ |  |  | ✓ | ✓ | ✓ | ✓ |  |  |  |  |  |  |  |  |
| `unit_block.manage`<br>Block / unblock rooms and beds | ✓ | ✓ | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |  |  |

### activities

| Permission | OWN | GM | MGR | ACC | HR | RCP | HKS | HK | MNT | RM | WTR | KIT | INV | PUR | ACT | DRV | EMP |
|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `activity.manage`<br>Configure activities and schedules | ✓ | ✓ | ✓ |  |  |  |  |  |  |  |  |  |  |  | ✓ |  |  |
| `activity.read`<br>View activities and schedules | ✓ | ✓ | ✓ |  |  | ✓ |  |  |  |  |  |  |  |  | ✓ |  |  |
| `activity_booking.create`<br>Book guests on activities | ✓ | ✓ | ✓ |  |  | ✓ |  |  |  |  |  |  |  |  | ✓ |  |  |

### attendance

| Permission | OWN | GM | MGR | ACC | HR | RCP | HKS | HK | MNT | RM | WTR | KIT | INV | PUR | ACT | DRV | EMP |
|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `attendance.read`<br>View staff attendance | ✓ | ✓ | ✓ |  | ✓ |  | ✓ |  |  | ✓ |  |  |  |  |  |  |  |
| `attendance.self`<br>Clock in/out for self | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| `attendance.update`<br>Correct attendance / manager override | ✓ | ✓ | ✓ |  | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |
| `timesheet.approve`<br>Approve / reject timesheets | ✓ | ✓ | ✓ |  | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |
| `timesheet.self`<br>Submit own timesheet | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |

### audit

| Permission | OWN | GM | MGR | ACC | HR | RCP | HKS | HK | MNT | RM | WTR | KIT | INV | PUR | ACT | DRV | EMP |
|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `audit.read` 🛡<br>View audit logs | ✓ | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |  |  |  |

### budgeting

| Permission | OWN | GM | MGR | ACC | HR | RCP | HKS | HK | MNT | RM | WTR | KIT | INV | PUR | ACT | DRV | EMP |
|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `budget.manage` 🛡<br>Create/edit budgets | ✓ | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |  |  |  |
| `budget.read`<br>View budgets | ✓ | ✓ | ✓ | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |  |

### communication

| Permission | OWN | GM | MGR | ACC | HR | RCP | HKS | HK | MNT | RM | WTR | KIT | INV | PUR | ACT | DRV | EMP |
|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `message.read`<br>View guest messages | ✓ | ✓ | ✓ |  |  | ✓ |  |  |  |  |  |  |  |  |  |  |  |
| `message.send`<br>Send guest messages | ✓ | ✓ | ✓ |  |  | ✓ |  |  |  |  |  |  |  |  |  |  |  |

### employees

| Permission | OWN | GM | MGR | ACC | HR | RCP | HKS | HK | MNT | RM | WTR | KIT | INV | PUR | ACT | DRV | EMP |
|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `employee.create`<br>Create employees | ✓ | ✓ |  |  | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |
| `employee.read`<br>View employee profiles | ✓ | ✓ | ✓ |  | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |
| `employee.update`<br>Edit employees | ✓ | ✓ |  |  | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |
| `employee_document.read` 🛡<br>View employee documents | ✓ | ✓ |  |  | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |
| `employee_salary.read` 🛡<br>View salary / compensation | ✓ |  |  |  | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |

### equipment

| Permission | OWN | GM | MGR | ACC | HR | RCP | HKS | HK | MNT | RM | WTR | KIT | INV | PUR | ACT | DRV | EMP |
|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `equipment.issue`<br>Issue / receive equipment | ✓ | ✓ | ✓ |  |  | ✓ |  |  |  |  |  |  |  |  | ✓ |  |  |
| `equipment.manage`<br>Configure equipment | ✓ | ✓ | ✓ |  |  |  |  |  |  |  |  |  |  |  | ✓ |  |  |
| `equipment.read`<br>View equipment | ✓ | ✓ | ✓ |  |  | ✓ |  |  | ✓ |  |  |  |  |  | ✓ |  |  |

### expenses

| Permission | OWN | GM | MGR | ACC | HR | RCP | HKS | HK | MNT | RM | WTR | KIT | INV | PUR | ACT | DRV | EMP |
|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `expense.approve` 🛡<br>Approve expenses | ✓ | ✓ |  | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |  |
| `expense.create`<br>Record expenses | ✓ | ✓ | ✓ | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |  |
| `expense.read`<br>View expenses | ✓ | ✓ | ✓ | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |  |

### folio

| Permission | OWN | GM | MGR | ACC | HR | RCP | HKS | HK | MNT | RM | WTR | KIT | INV | PUR | ACT | DRV | EMP |
|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `folio.adjust` 🛡<br>Adjust / reverse folio charges | ✓ | ✓ | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |  |  |
| `folio.post_charge`<br>Post charges to a folio | ✓ | ✓ | ✓ |  |  | ✓ |  |  |  | ✓ | ✓ |  |  |  | ✓ |  |  |
| `folio.read`<br>View folios | ✓ | ✓ | ✓ | ✓ |  | ✓ |  |  |  |  |  |  |  |  |  |  |  |

### frontdesk

| Permission | OWN | GM | MGR | ACC | HR | RCP | HKS | HK | MNT | RM | WTR | KIT | INV | PUR | ACT | DRV | EMP |
|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `frontdesk.checkin`<br>Check guests in | ✓ | ✓ | ✓ |  |  | ✓ |  |  |  |  |  |  |  |  |  |  |  |
| `frontdesk.checkout`<br>Check guests out | ✓ | ✓ | ✓ |  |  | ✓ |  |  |  |  |  |  |  |  |  |  |  |

### gst

| Permission | OWN | GM | MGR | ACC | HR | RCP | HKS | HK | MNT | RM | WTR | KIT | INV | PUR | ACT | DRV | EMP |
|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `credit_note.create` 🛡<br>Issue credit / debit notes | ✓ | ✓ |  | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |  |
| `invoice.cancel` 🛡<br>Cancel invoices (via credit note) | ✓ | ✓ |  | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |  |
| `invoice.create`<br>Issue invoices | ✓ | ✓ | ✓ | ✓ |  | ✓ |  |  |  |  |  |  |  |  |  |  |  |
| `invoice.read`<br>View invoices and receipts | ✓ | ✓ | ✓ | ✓ |  | ✓ |  |  |  |  |  |  |  |  |  |  |  |
| `tax.manage` 🛡<br>Edit GST registrations and tax rules | ✓ | ✓ |  | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |  |
| `tax.read`<br>View GST registrations and tax rules | ✓ | ✓ | ✓ | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |  |

### guests

| Permission | OWN | GM | MGR | ACC | HR | RCP | HKS | HK | MNT | RM | WTR | KIT | INV | PUR | ACT | DRV | EMP |
|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `guest.create`<br>Create guest profiles | ✓ | ✓ | ✓ |  |  | ✓ |  |  |  |  |  |  |  |  |  |  |  |
| `guest.delete` 🛡<br>Delete / anonymise guest profiles | ✓ | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |  |  |  |
| `guest.read`<br>View guest profiles | ✓ | ✓ | ✓ |  |  | ✓ |  |  |  | ✓ | ✓ |  |  |  | ✓ |  |  |
| `guest.update`<br>Edit guest profiles | ✓ | ✓ | ✓ |  |  | ✓ |  |  |  |  |  |  |  |  |  |  |  |
| `guest_document.read` 🛡<br>View guest ID documents | ✓ | ✓ | ✓ |  |  | ✓ |  |  |  |  |  |  |  |  |  |  |  |

### housekeeping

| Permission | OWN | GM | MGR | ACC | HR | RCP | HKS | HK | MNT | RM | WTR | KIT | INV | PUR | ACT | DRV | EMP |
|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `housekeeping.assign`<br>Assign housekeeping tasks | ✓ | ✓ | ✓ |  |  |  | ✓ |  |  |  |  |  |  |  |  |  |  |
| `housekeeping.inspect`<br>Inspect and release rooms as READY | ✓ | ✓ | ✓ |  |  |  | ✓ |  |  |  |  |  |  |  |  |  |  |
| `housekeeping.read`<br>View housekeeping board | ✓ | ✓ | ✓ |  |  | ✓ | ✓ | ✓ |  |  |  |  |  |  |  |  |  |
| `housekeeping.update`<br>Update cleaning status | ✓ | ✓ | ✓ |  |  |  | ✓ | ✓ |  |  |  |  |  |  |  |  |  |

### identity

| Permission | OWN | GM | MGR | ACC | HR | RCP | HKS | HK | MNT | RM | WTR | KIT | INV | PUR | ACT | DRV | EMP |
|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `role.manage` 🛡<br>Create/edit custom roles | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |  |  |  |  |
| `role.read`<br>View roles and permissions | ✓ | ✓ | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |  |  |
| `user.create`<br>Create users | ✓ | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |  |  |  |
| `user.deactivate`<br>Deactivate users / revoke sessions | ✓ | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |  |  |  |
| `user.read`<br>View users | ✓ | ✓ | ✓ |  | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |
| `user.update`<br>Edit users and their role grants | ✓ | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |  |  |  |

### integrations

| Permission | OWN | GM | MGR | ACC | HR | RCP | HKS | HK | MNT | RM | WTR | KIT | INV | PUR | ACT | DRV | EMP |
|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `integration.manage` 🛡<br>Configure Razorpay/Zoho/OTA/WhatsApp | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |  |  |  |  |
| `ota.manage`<br>Edit OTA mappings, push rates/inventory | ✓ | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |  |  |  |
| `ota.read`<br>View OTA mappings and sync status | ✓ | ✓ | ✓ | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |  |

### inventory

| Permission | OWN | GM | MGR | ACC | HR | RCP | HKS | HK | MNT | RM | WTR | KIT | INV | PUR | ACT | DRV | EMP |
|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `inventory.adjust`<br>Record wastage / adjustments / counts | ✓ | ✓ |  |  |  |  |  |  |  | ✓ |  |  | ✓ |  |  |  |  |
| `inventory.manage`<br>Configure stock items, units, locations | ✓ | ✓ |  |  |  |  |  |  |  |  |  |  | ✓ |  |  |  |  |
| `inventory.read`<br>View stock | ✓ | ✓ | ✓ |  |  |  | ✓ |  |  | ✓ |  | ✓ | ✓ | ✓ |  |  |  |
| `recipe.manage`<br>Edit recipes | ✓ | ✓ |  |  |  |  |  |  |  | ✓ |  |  |  |  |  |  |  |
| `recipe.read`<br>View recipes and costing | ✓ | ✓ |  |  |  |  |  |  |  | ✓ |  | ✓ | ✓ |  |  |  |  |

### leave

| Permission | OWN | GM | MGR | ACC | HR | RCP | HKS | HK | MNT | RM | WTR | KIT | INV | PUR | ACT | DRV | EMP |
|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `leave.approve`<br>Approve leave | ✓ | ✓ | ✓ |  | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |
| `leave.create`<br>Request leave for self | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| `leave.manage_balances`<br>Adjust leave balances / types / holidays | ✓ | ✓ |  |  | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |
| `leave.read`<br>View staff leave | ✓ | ✓ | ✓ |  | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |
| `leave.reject`<br>Reject leave | ✓ | ✓ | ✓ |  | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |

### maintenance

| Permission | OWN | GM | MGR | ACC | HR | RCP | HKS | HK | MNT | RM | WTR | KIT | INV | PUR | ACT | DRV | EMP |
|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `maintenance.assign`<br>Assign maintenance tickets | ✓ | ✓ | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |  |  |
| `maintenance.create`<br>Report maintenance issues | ✓ | ✓ | ✓ |  |  | ✓ | ✓ | ✓ | ✓ |  |  |  |  |  |  |  |  |
| `maintenance.read`<br>View maintenance tickets | ✓ | ✓ | ✓ |  |  | ✓ | ✓ |  | ✓ |  |  |  |  |  |  |  |  |
| `maintenance.update`<br>Work and resolve maintenance tickets | ✓ | ✓ | ✓ |  |  |  |  |  | ✓ |  |  |  |  |  |  |  |  |

### menu

| Permission | OWN | GM | MGR | ACC | HR | RCP | HKS | HK | MNT | RM | WTR | KIT | INV | PUR | ACT | DRV | EMP |
|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `menu.create`<br>Create menu items | ✓ | ✓ |  |  |  |  |  |  |  | ✓ |  |  |  |  |  |  |  |
| `menu.read`<br>View menu | ✓ | ✓ | ✓ |  |  |  |  |  |  | ✓ | ✓ | ✓ |  |  |  |  |  |
| `menu.update`<br>Edit menu items / availability | ✓ | ✓ |  |  |  |  |  |  |  | ✓ |  |  |  |  |  |  |  |

### payments

| Permission | OWN | GM | MGR | ACC | HR | RCP | HKS | HK | MNT | RM | WTR | KIT | INV | PUR | ACT | DRV | EMP |
|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `cash_drawer.approve_variance` 🛡<br>Approve cash drawer variances | ✓ | ✓ | ✓ | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |  |
| `cash_drawer.operate`<br>Open/close the cash drawer | ✓ | ✓ | ✓ |  |  | ✓ |  |  |  | ✓ |  |  |  |  |  |  |  |
| `payment.create`<br>Record payments | ✓ | ✓ | ✓ |  |  | ✓ |  |  |  | ✓ | ✓ |  |  |  |  |  |  |
| `payment.read`<br>View payments | ✓ | ✓ | ✓ | ✓ |  | ✓ |  |  |  | ✓ |  |  |  |  |  |  |  |
| `payment.refund` 🛡<br>Issue refunds | ✓ | ✓ | ✓ | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |  |
| `payment.void` 🛡<br>Void payments | ✓ | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |  |  |  |

### payroll

| Permission | OWN | GM | MGR | ACC | HR | RCP | HKS | HK | MNT | RM | WTR | KIT | INV | PUR | ACT | DRV | EMP |
|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `payroll.export` 🛡<br>Make and download payroll files (Sprint 19, V021) | ✓ |  |  |  | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |

### pos

| Permission | OWN | GM | MGR | ACC | HR | RCP | HKS | HK | MNT | RM | WTR | KIT | INV | PUR | ACT | DRV | EMP |
|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `kot.read`<br>View kitchen order tickets | ✓ | ✓ | ✓ |  |  |  |  |  |  | ✓ | ✓ | ✓ |  |  |  |  |  |
| `kot.update`<br>Update KOT status | ✓ | ✓ | ✓ |  |  |  |  |  |  | ✓ |  | ✓ |  |  |  |  |  |
| `pos.discount`<br>Apply discounts | ✓ | ✓ | ✓ |  |  |  |  |  |  | ✓ |  |  |  |  |  |  |  |
| `pos_order.create`<br>Create restaurant orders | ✓ | ✓ | ✓ |  |  |  |  |  |  | ✓ | ✓ |  |  |  |  |  |  |
| `pos_order.read`<br>View restaurant orders | ✓ | ✓ | ✓ |  |  |  |  |  |  | ✓ | ✓ |  |  |  |  |  |  |
| `pos_order.update`<br>Modify restaurant orders | ✓ | ✓ | ✓ |  |  |  |  |  |  | ✓ | ✓ |  |  |  |  |  |  |
| `pos_order.void` 🛡<br>Void restaurant orders / items | ✓ | ✓ | ✓ |  |  |  |  |  |  | ✓ |  |  |  |  |  |  |  |

### property

| Permission | OWN | GM | MGR | ACC | HR | RCP | HKS | HK | MNT | RM | WTR | KIT | INV | PUR | ACT | DRV | EMP |
|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `organization.read`<br>View organisation details | ✓ | ✓ |  | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |  |
| `organization.update` 🛡<br>Edit organisation details | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |  |  |  |  |
| `property.create`<br>Create properties | ✓ | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |  |  |  |
| `property.read`<br>View properties | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| `property.update`<br>Edit properties | ✓ | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |  |  |  |
| `property_settings.update`<br>Change property settings | ✓ | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |  |  |  |

### purchasing

| Permission | OWN | GM | MGR | ACC | HR | RCP | HKS | HK | MNT | RM | WTR | KIT | INV | PUR | ACT | DRV | EMP |
|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `grn.create`<br>Record goods received | ✓ | ✓ |  |  |  |  |  |  |  |  |  |  | ✓ | ✓ |  |  |  |
| `purchase.approve` 🛡<br>Approve purchase orders | ✓ | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |  |  |  |
| `purchase.create`<br>Raise purchase requests/orders | ✓ | ✓ | ✓ |  |  |  |  |  |  | ✓ |  |  | ✓ | ✓ |  |  |  |
| `purchase.read`<br>View purchase requests/orders | ✓ | ✓ | ✓ | ✓ |  |  |  |  |  | ✓ |  |  | ✓ | ✓ |  |  |  |
| `supplier.manage`<br>Create/edit suppliers | ✓ | ✓ |  |  |  |  |  |  |  |  |  |  |  | ✓ |  |  |  |
| `supplier.read`<br>View suppliers | ✓ | ✓ | ✓ | ✓ |  |  |  |  |  | ✓ |  |  | ✓ | ✓ |  |  |  |
| `supplier_invoice.manage`<br>Record supplier invoices and payments | ✓ | ✓ |  | ✓ |  |  |  |  |  |  |  |  |  | ✓ |  |  |  |

### rates

| Permission | OWN | GM | MGR | ACC | HR | RCP | HKS | HK | MNT | RM | WTR | KIT | INV | PUR | ACT | DRV | EMP |
|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `rate.manage`<br>Edit rate plans and packages | ✓ | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |  |  |  |
| `rate.read`<br>View rate plans and packages | ✓ | ✓ | ✓ |  |  | ✓ |  |  |  |  |  |  |  |  |  |  |  |

### reports

| Permission | OWN | GM | MGR | ACC | HR | RCP | HKS | HK | MNT | RM | WTR | KIT | INV | PUR | ACT | DRV | EMP |
|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `financial_report.read` 🛡<br>View financial reports | ✓ | ✓ |  | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |  |
| `report.operational.read`<br>View operational reports | ✓ | ✓ | ✓ | ✓ |  |  |  |  |  | ✓ |  |  | ✓ |  |  |  |  |

### reservations

| Permission | OWN | GM | MGR | ACC | HR | RCP | HKS | HK | MNT | RM | WTR | KIT | INV | PUR | ACT | DRV | EMP |
|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `reservation.cancel`<br>Cancel reservations / mark no-show | ✓ | ✓ | ✓ |  |  | ✓ |  |  |  |  |  |  |  |  |  |  |  |
| `reservation.create`<br>Create reservations | ✓ | ✓ | ✓ |  |  | ✓ |  |  |  |  |  |  |  |  |  |  |  |
| `reservation.delete` 🛡<br>Delete enquiry-stage reservations | ✓ | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |  |  |  |
| `reservation.override_rate` 🛡<br>Override the calculated rate | ✓ | ✓ | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |  |  |
| `reservation.read`<br>View reservations | ✓ | ✓ | ✓ |  |  | ✓ | ✓ |  |  | ✓ |  |  |  |  | ✓ |  |  |
| `reservation.update`<br>Modify reservations / reassign rooms | ✓ | ✓ | ✓ |  |  | ✓ |  |  |  |  |  |  |  |  |  |  |  |

### roster

| Permission | OWN | GM | MGR | ACC | HR | RCP | HKS | HK | MNT | RM | WTR | KIT | INV | PUR | ACT | DRV | EMP |
|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `roster.manage`<br>Create/edit roster shifts | ✓ | ✓ | ✓ |  | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |
| `roster.read`<br>View full roster | ✓ | ✓ | ✓ |  | ✓ |  | ✓ |  |  | ✓ |  |  |  |  |  |  |  |
| `roster.self`<br>View own roster | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |

### system

| Permission | OWN | GM | MGR | ACC | HR | RCP | HKS | HK | MNT | RM | WTR | KIT | INV | PUR | ACT | DRV | EMP |
|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `system.settings` 🛡<br>Change platform settings | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |  |  |  |  |

### transport

| Permission | OWN | GM | MGR | ACC | HR | RCP | HKS | HK | MNT | RM | WTR | KIT | INV | PUR | ACT | DRV | EMP |
|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `transport.manage`<br>Configure vehicles, routes, trips | ✓ | ✓ | ✓ |  |  |  |  |  |  |  |  |  |  |  |  |  |  |
| `transport.read`<br>View vehicles, trips, manifests | ✓ | ✓ | ✓ |  |  | ✓ |  |  |  |  |  |  |  |  | ✓ | ✓ |  |
| `transport.trip_operate`<br>Start/complete trips, mark passengers | ✓ | ✓ | ✓ |  |  |  |  |  |  |  |  |  |  |  |  | ✓ |  |
| `transport_booking.create`<br>Book guests on transfers | ✓ | ✓ | ✓ |  |  | ✓ |  |  |  |  |  |  |  |  |  |  |  |

## Counts

| Role | Permissions |
|---|--:|
| OWNER | 118 |
| GENERAL_MANAGER | 112 |
| MANAGER | 78 |
| ACCOUNTANT | 26 |
| HR | 21 |
| RECEPTIONIST | 35 |
| HOUSEKEEPING_SUPERVISOR | 16 |
| HOUSEKEEPER | 9 |
| MAINTENANCE | 10 |
| RESTAURANT_MANAGER | 31 |
| WAITER | 13 |
| KITCHEN | 10 |
| INVENTORY_MANAGER | 14 |
| PURCHASING | 12 |
| ACTIVITY_MANAGER | 15 |
| DRIVER | 7 |
| EMPLOYEE | 5 |
