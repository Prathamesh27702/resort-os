# India readiness: FSSAI, UPI, printed bills and Form C

What an Indian property is expected to print and file, built in Sprint 30 after the market comparison of 13 Nov 2026
(Hotelogix, eZee, Djubo, mycloud, IDS, Petpooja, POSist and others). What comes later is in the sprint plan:
[Sprints 31 to 33](sprints/README.md) and additions to Sprints 25 to 29.

| Expected in India | In Resort OS | Since |
|---|---|---|
| GST invoice with GSTIN, SAC, CGST/SGST/IGST, amount in words | Invoices | Sprint 12 |
| Aadhaar never shown or stored in full | Only the last 4 digits are shown; the number is encrypted | Sprint 03 |
| FSSAI licence number on food bills | Restaurant bill and invoice PDF | Sprint 30 |
| UPI QR on the bill | Restaurant bill, Paid dialog, Take payment dialog, invoice PDF | Sprint 30 |
| Bills and kitchen tickets on a thermal printer | 80 mm and 58 mm | Sprint 30 |
| Form C for foreign guests within 24 hours | A sheet per guest and stay, reminder, register | Sprint 30 |
| GSTR-1 export, e-invoice | Planned | Sprint 25 |
| Groups, company credit, night audit | Planned | Sprint 31 |
| DPDP Act: notice, retention, breach log | Planned | Sprint 32 |

## 1. FSSAI licence number

**Settings → Properties → the property → Food licence and UPI → FSSAI licence number.** 14 digits, as on the licence or
registration certificate (spaces are dropped). FSSAI's order of 8 June 2021 asks food businesses to print it on cash
receipts, bills and invoices from 1 Oct 2021. Once set it prints as *FSSAI Lic. No. …* on every restaurant bill and under
the seller's GSTIN on invoice PDFs. Leave it empty if the property sells no food; the bill preview says when it is missing.

## 2. UPI QR

**UPI ID for payments** on the same screen: the property's UPI ID (VPA), for example `palmgrove@okhdfcbank`; use the
ID of the property's current account, not a personal one. **Name shown to the payer** is what the guest's UPI app shows
(the property's name when empty). After saving, the screen shows the QR; scan it with your own phone and check the name.

The app only draws a QR of a `upi://pay` link (NPCI's linking specification) with the UPI ID, the name, the exact amount
and the bill number as the note. **No money moves through Resort OS**: the guest's bank pays the property's bank, and
staff record the payment with its UPI reference as before (the reference is what the bank statement shows). The QR is on:

- the printed restaurant bill, for its total, while the bill is open;
- **Paid → UPI** on a restaurant order and **Take payment → UPI** on a guest's bill, for the amount typed;
- the invoice PDF, for the balance the booking still owes (none when it is paid).

The online pay page (payment links) stays with Razorpay, which matches the payment to the booking by itself; a plain QR
there would need someone to match it by hand. A QR can ask for ₹0.01 to ₹10,00,000.

The demo data's UPI ID (`resort.demo@exampleupi`) and FSSAI number are made up. Replace both before a real guest sees a bill.

## 3. Printing bills and kitchen tickets

**Print bill** on a restaurant order shows the bill as it prints: the property with address, GSTIN and FSSAI number, the
bill number, table and waiter, each dish (the same dish on two lines is one line), discount, each tax with its rate, the
total, the SAC, and the UPI QR or how it was paid. Choose **80 mm** or **58 mm** (remembered on that device), then
**Print**. The printer symbol on a kitchen ticket (on the order and on the kitchen display) prints the ticket in large type.

Setting up a thermal printer once, on the computer or tablet at the counter:

1. Install the printer with its own driver so it appears in the system's list of printers (USB, network or Bluetooth).
2. In the browser's print window choose that printer, paper **80 mm** (or 58 mm) roll, **Margins: None**, and turn off
   *Headers and footers*. Chrome and Edge remember this.
3. To skip the print window at a busy counter, start Chrome with `--kiosk-printing` (it prints straight to the default printer).

The page is sized to the roll (`@page { size: 80mm auto }`), so nothing is cut off and no blank paper follows. Direct
printing to a kitchen printer without a browser window (ESC/POS over the network) is not built; say if the kitchen needs it.

## 4. Form C (foreign guests)

Every hotel, guest house or resort must report each foreign guest to the Foreigners Regional Registration Office within
**24 hours of arrival** (Foreigners Act 1946, s. 7; Registration of Foreigners Rules 1992), on the FRRO's Form C portal:
<https://indianfrro.gov.in/frro/FormC>. The portal has no way in for software, so the property files there by hand; Resort
OS makes that quick and keeps the proof.

1. **At check-in** a sheet is opened for every guest on the booking whose nationality isn't Indian. Reception gets a line
   in the bell (and an email). Guests already in house when Sprint 30 is installed get theirs the first time the Form C
   page is opened. A guest with no nationality on the profile gets no sheet, so set it when taking the booking.
2. **Front desk** shows *N Form C to file*; **Form C** in the menu lists them with the time left: blue, amber in the last
   6 hours, red when past the 24.
3. **The sheet** has the portal's 26 fields in the portal's order. What is known is filled in: name, date of birth,
   nationality and address abroad from the profile; passport number and expiry from a passport saved under the guest's
   documents, visa number and expiry from a visa; address in India, arrival date and time and the length of stay from the
   booking. Type the rest and **Save**. A value that can't be right is refused under its field.
4. **Copy** next to each field puts it on the clipboard, dates as DD/MM/YYYY the way the portal takes them. Paste into the
   portal, submit there.
5. **Mark as filed** with the acknowledgement number the portal shows. Refused while a required field is empty, naming
   the fields. The sheet is then locked; **Reopen to correct** unlocks it.
6. **No Form C needed**, with a reason, is for a guest entered as a foreign national by mistake.
7. Every morning at 09:00 (Messages → Daily jobs → *Form C reminder*) the people who check guests in get the list still
   to file, overdue ones marked.
8. **Download register** gives the foreign guest register for a period as a spreadsheet (passport as its last 4
   characters only), for an inspection.

Who may: reading a sheet or the register needs *guest documents: read* (owner, GM, manager, receptionist) and is written
to the audit trail; changing one needs *front desk: check in*. Sheets are stored encrypted like ID documents, are never
deleted, and are emptied when the guest is anonymised.

Not covered: Form C for a foreign guest's Indian-origin family with OCI cards is still required by most FRROs (file it;
use *No Form C needed* only when the FRRO says so); Form B (the paper register some states' police ask for) is the
register download; C-Form by SMS or bulk upload is not offered by the portal.

## Questions for the owner

1. Is the FSSAI document a licence or a registration? The number has 14 digits either way; the bill says *Lic. No.*
2. Which UPI ID should collect restaurant money: the same account as room payments?
3. Which thermal printers (make, 80 mm or 58 mm, USB or network) are at the counter and in the kitchen?
4. Does your FRRO ask for anything beyond the portal's Form C (a weekly print-out, Form B)?
