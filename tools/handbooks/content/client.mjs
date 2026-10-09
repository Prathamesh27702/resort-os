// Client overview: what Resort OS is and does, written for resort owners, managers and partners.
// Plain language. No internal links, keys or personal data. Sprint-specific text comes from docs/sprints/sprints.yml
// (`client:` and `handbook:`), so a new edition only needs the plan updated and `npm run build`.
import { Body } from '../lib/doc.mjs';
import { C, TEXT_W, p, small, bullets, table, kv, callout, stats, image, spacer, STATUS } from '../lib/kit.mjs';
import { two, PHASES } from '../lib/repo.mjs';

const AREAS = [
  { title: 'Foundation', sprints: [1, 28, 29],
    intro: 'Everything else is built on this: who can sign in, what each person may do, which properties exist, and a permanent record of every change.' },
  { title: 'Rooms and reservations', sprints: [2, 4, 5, 6],
    intro: 'The resort\'s inventory (rooms and individual dormitory beds), its prices, and bookings that can never double-sell a room or a bed.' },
  { title: 'Front desk and guests', sprints: [3, 7],
    intro: 'One record per guest, fast look-up by phone, and check-in and check-out in a few clicks.' },
  { title: 'Billing, GST and payments', sprints: [8, 9, 10, 11, 12],
    intro: 'One running bill per stay, GST worked out correctly for every line, payments by any method, and legally numbered invoices.' },
  { title: 'Housekeeping and maintenance', sprints: [13],
    intro: 'Room readiness and repairs managed from staff phones.' },
  { title: 'Restaurant, stock and purchasing', sprints: [14, 15, 16],
    intro: 'Food and beverage from order to kitchen to the guest\'s bill, with stock and food cost under control.' },
  { title: 'Staff and attendance', sprints: [17, 18, 19],
    intro: 'Employee records, rosters, phone clock-in at the resort, leave and payroll input.' },
  { title: 'Guest experiences', sprints: [20, 21],
    intro: 'Activities, equipment and the beach shuttle, booked within real capacity.' },
  { title: 'Control and reports', sprints: [22, 23],
    intro: 'Spending under approval and budget, and the numbers owners and managers need every day.' },
  { title: 'Connections', sprints: [24, 25, 26, 27],
    intro: 'The outside world: WhatsApp and email, accounting, online travel agents and the resort\'s own booking website.' },
];

const statusCell = (s) => STATUS[s.status].text;
const statusFill = (s) => STATUS[s.status].fill;

export function clientBody(r) {
  const b = new Body();
  const doneCount = r.done.length;
  const buildSprints = r.sprints.filter(s => s.id > 0);
  const byId = Object.fromEntries(r.sprints.map(s => [s.id, s]));
  const lastDone = r.lastDone;

  // 1 ─────────────────────────────────────────────────────────────
  b.h1('Executive summary');
  b.add(p('**Resort OS** is a complete operating system for a resort in India. It brings reservations, rooms and dormitory beds, the front desk, guest billing with GST, payments, housekeeping, the restaurant and kitchen, stock and purchasing, staff and attendance, activities, the beach shuttle, expenses and reports into **one application with one database**, used by every member of staff on a computer, tablet or phone.'));
  b.add(p('It is designed around how an Indian resort actually works: GST with CGST/SGST and IGST, UPI and Razorpay payments, WhatsApp messages to guests, guests who book a whole room or a single dormitory bed, ID rules for foreign nationals, and accounts that end up in Zoho Books or Tally.'));
  b.add(spacer(4), stats([
    { value: '32', label: 'modules in one system' },
    { value: String(r.roles.length), label: 'ready-made staff roles' },
    { value: `${doneCount} of ${buildSprints.length}`, label: 'build sprints delivered', color: C.palm },
    { value: '₹0', label: 'running cost while in development', color: C.kokum },
  ]), spacer(8));
  b.add(kv([
    ['Who it is for', 'Resort owners, general managers and every staff role: reception, housekeeping, kitchen and restaurant, stores, accounts, HR, drivers and activity staff.'],
    ['How staff use it', 'In a web browser on any computer, tablet or phone. No installation; it can be added to a phone\'s home screen like an app.'],
    ['What makes it different', 'One guest record, one bill and one audit trail across every department, and the rules (GST, no double-booking, who may see ID documents) enforced by the system rather than by memory.'],
    ['Where it stands today', `${doneCount === 1 ? 'Sprint' : 'Sprints'} ${r.done.map(s => two(s.id)).join(' and ')} ${doneCount === 1 ? 'is' : 'are'} delivered and accepted. Sprint ${two(r.current.id)} (${r.current.title}) is ${r.current.status === 'in-review' ? 'built and waiting for review' : 'in progress'}.`],
  ]));
  b.add(spacer(6), callout('In one sentence', 'A resort team should be able to run the whole property, from the first booking enquiry to the GST invoice and the month-end report, without re-typing anything into a second system.'));

  // 2 ─────────────────────────────────────────────────────────────
  b.h1('Why Resort OS');
  b.add(p('Most small and mid-sized resorts run on a patchwork: a booking register or spreadsheet, the OTA extranets, a separate restaurant billing machine, invoices typed by hand, staff attendance on paper and guest communication on personal WhatsApp. Each tool works on its own; the trouble is in the gaps between them.'));
  b.add(table(['Today, typically', 'With Resort OS'], [
    ['The same booking is typed into the register, the OTA extranet and the invoice', 'A booking is entered once; the bill, the room calendar, housekeeping and the invoice all use it'],
    ['Two people sell the last room at the same moment', 'The system refuses the second booking; a room or a dorm bed can never be double-sold'],
    ['Restaurant bills for in-house guests are carried to reception on paper', 'Orders are charged straight to the guest\'s room bill'],
    ['GST rates and invoice numbers are worked out by hand', 'GST is calculated per line from the configured rules; invoice numbers follow the legal series'],
    ['Guest ID copies sit in a drawer or on a phone', 'ID documents are stored encrypted and only permitted roles can open them'],
    ['Nobody can say who changed a price or cancelled a booking', 'Every change is recorded: who, when, from which device, what changed and why'],
    ['Month-end numbers take days to put together', 'Occupancy, revenue, GST, food cost and staff hours are reports, not projects'],
  ], [4657, TEXT_W - 4657], { zebra: true }));

  // 3 ─────────────────────────────────────────────────────────────
  b.h1('What Resort OS does');
  b.add(p('Resort OS is organised in ten areas that share one foundation. The picture shows them all, with when each arrives; the pages that follow describe what each area gives the resort.'));
  b.add(image(r.abs('tools/handbooks/assets/capabilities.png'), { caption: 'The ten areas of Resort OS and when each is delivered' }));
  for (const area of AREAS) {
    b.h2(area.title);
    b.add(p(area.intro));
    b.add(table(['Sprint', 'What it gives the resort', 'Status'],
      area.sprints.map(id => byId[id]).map(s => [two(s.id), s.client, statusCell(s)]),
      [900, TEXT_W - 900 - 1500, 1500],
      { fills: (ri, ci) => (ci === 2 ? statusFill(byId[area.sprints[ri]]) : undefined) }));
  }

  // 4 ─────────────────────────────────────────────────────────────
  b.h1('A guest\'s journey through Resort OS');
  b.add(p('The best way to see how the modules fit together is to follow one guest from booking to invoice. Every step below reads from and writes to the same guest record and the same bill.'));
  b.add(image(r.abs('tools/handbooks/assets/guest-journey.png'), { caption: 'One stay, end to end' }));
  b.add(table(['Moment', 'What the staff do', 'What Resort OS does for them'], [
    ['Booking', 'Take a phone booking, or receive one from the website or an OTA', 'Shows live availability, prices the stay, holds the room or bed, takes an advance, sends confirmation'],
    ['Before arrival', 'Prepare the room, arrange the shuttle', 'Gives housekeeping the room list, books transport seats, sends a reminder'],
    ['Check-in', 'Greet the guest, record ID where required', 'Finds the guest by phone, stores ID encrypted, assigns the room or bed, opens the bill'],
    ['During the stay', 'Serve meals, run activities, fix problems', 'Charges orders and activities to the room, tracks housekeeping and repairs'],
    ['Check-out', 'Settle the bill', 'Totals every charge, applies GST correctly, takes payment, issues the invoice and sends it on WhatsApp'],
    ['After the stay', 'Close the day and the month', 'Balances the cash drawer, syncs accounts to Zoho Books or Tally, updates reports'],
  ], [1700, 3100, TEXT_W - 4800], { zebra: true }));

  // 5 ─────────────────────────────────────────────────────────────
  b.h1('Who uses it');
  b.add(p(`Resort OS ships with **${r.roles.length} staff roles**. Each role is a bundle of permissions (there are **${r.permissions.length}**), and a person can hold a role for every property or for one property only. Screens and buttons a person is not allowed to use are simply not shown, and the server checks every request again, so a hidden button can never be a back door.`));
  b.add(table(['Role', 'What they do in Resort OS'],
    r.roles.map(x => [x.name, x.description]), [2800, TEXT_W - 2800], { zebra: true, boldFirst: true }));
  b.add(spacer(4), callout('Sensitive information stays with the right people', [
    'Guest ID documents, staff salaries, refunds, tax rules and the audit trail each need a dedicated permission. A waiter or a housekeeper cannot open a guest\'s ID scan; only the owner and HR see salaries.',
  ], { fill: C.greenBg, accent: C.palm }));

  // 6 ─────────────────────────────────────────────────────────────
  b.h1('Built for India');
  b.add(p('This is how the finished system handles each need. The roadmap section shows which sprint delivers each one.'));
  b.add(table(['Need', 'How Resort OS handles it'], [
    ['GST', 'CGST + SGST within the state and IGST across states, chosen per line from the property\'s GST state. Rates are configuration with effective dates, so a rate change does not need new software. Invoice numbers follow a legal series per financial year; cancellations go through credit notes.'],
    ['Payments', 'Cash, UPI, card and bank transfer at the desk; online UPI and cards through Razorpay, where the payment confirmation from Razorpay\'s server, not the browser, is what counts.'],
    ['Guest messages', 'WhatsApp Business and email from approved templates: confirmations, reminders, invoices, with delivery tracking.'],
    ['Rooms and dormitory beds', 'A room type is sold either as a whole room or bed by bed, so a hostel-style dormitory and private rooms live side by side.'],
    ['Guest identification', 'ID details are captured only where the law requires it (for example Form-C for foreign nationals), stored encrypted and deleted after the retention period the owner sets.'],
    ['Local time and currency', 'All business dates follow the property\'s time zone (IST) and amounts are in rupees, stored exactly (no rounding drift).'],
    ['Accounting', 'Invoices, payments and expenses sync to Zoho Books; a Tally export is available for accountants who use Tally.'],
    ['Online travel agents', 'Booking.com, MakeMyTrip, Goibibo, Airbnb and others through one Indian channel manager.'],
    ['Connectivity', 'The staff app is designed to keep working through a poor connection and catch up when it returns; money actions wait until it is back online.'],
  ], [2300, TEXT_W - 2300], { zebra: true, boldFirst: true }));

  // 7 ─────────────────────────────────────────────────────────────
  b.h1('Security and data protection');
  b.add(p('Security is built into every layer rather than added at the end. Nobody can honestly promise a system that cannot be attacked; what Resort OS does is make each layer hard to get past, keep any attempt visible, and test the controls on every change.'));
  b.add(table(['Protection', 'In practice'], [
    ['Sign-in', 'Strong password hashing, lock-out after 5 wrong attempts, temporary passwords that must be changed, and immediate sign-out when an account is deactivated or a password changes. Two-factor sign-in for owners, managers and accountants follows in Sprint 28.'],
    ['Least access', 'Every action needs a specific permission; access can be limited to one property; nobody can give themselves more access than they hold.'],
    ['Separation of resorts', 'Each organisation\'s data is invisible to every other, enforced on every database query.'],
    ['Encryption', 'HTTPS everywhere; guest ID numbers and other sensitive fields are additionally encrypted inside the database (from Sprint 03).'],
    ['Audit trail', 'Every change records who, when, from which device and IP address, the old and new values, and the reason. The trail cannot be edited or deleted, even directly in the database.'],
    ['Money and bookings', 'Invoices, payments and stock movements are never edited or deleted; corrections are new entries. Repeated clicks or network retries never create duplicate bookings or payments.'],
    ['Secrets', 'Passwords and service keys are never stored in the source code; they are held in the hosting provider\'s secret store and shared only with the people who run the system.'],
    ['Backups', 'A managed database with point-in-time recovery, and a tested restore procedure, before the resort goes live.'],
  ], [2100, TEXT_W - 2100], { zebra: true, boldFirst: true }));

  // 8 ─────────────────────────────────────────────────────────────
  b.h1('Technology in brief');
  b.add(p('Resort OS uses mainstream, long-term-supported technology that many developers in India know, so the resort is never dependent on one person or one vendor.'));
  b.add(table(['Part', 'Technology', 'Why it matters to the resort'], [
    ['Server', 'Microsoft .NET 10 (long-term support)', 'Fast, secure, supported by Microsoft for years; runs on low-cost Linux servers'],
    ['Database', 'PostgreSQL 16', 'Open-source and free; the database itself enforces the rules that protect money and bookings'],
    ['Staff app', 'Angular 20 with Material Design', 'One app for desktop, tablet and phone; installable like an app; clear, consistent screens'],
    ['Payments', 'Razorpay', 'India\'s widely used gateway for UPI, cards and netbanking'],
    ['Accounting', 'Zoho Books (Tally export)', 'Fits how Indian accountants already work'],
    ['Messages', 'WhatsApp Business Platform, email', 'Reaches guests where they already are'],
    ['Hosting', 'A small cloud server in an Indian region, managed database, Cloudflare in front', 'Data can stay in India; low monthly cost; protection against attacks'],
  ], [1500, 3000, TEXT_W - 4500], { zebra: true, boldFirst: true }));
  b.add(image(r.abs('tools/handbooks/assets/architecture.png'), { caption: 'How the parts connect' }));

  // 9 ─────────────────────────────────────────────────────────────
  b.h1('Available today');
  b.add(p(`Work is delivered in short sprints, and each one ends with the owner trying the new screens and accepting them. These are the parts that are built, tested and accepted as of ${lastDone ? `Sprint ${two(lastDone.id)}` : 'today'}.`));
  for (const s of r.done) {
    b.h2(`Sprint ${two(s.id)} — ${s.title.split(' — ')[0]}`);
    b.add(p(s.client));
    if (s.handbook?.highlights) b.add(bullets(s.handbook.highlights));
    const shots = (s.handbook?.screenshots ?? []).slice(0, 3);
    for (const sh of shots) b.add(image(r.abs(sh.file), { widthIn: sh.width ?? 5.6, caption: sh.caption, border: true }));
  }
  const cur = r.current;
  if (cur.status === 'in-review' && cur.handbook) {
    b.h2(`Ready for review: Sprint ${two(cur.id)} — ${cur.title.split(' — ')[0]}`);
    b.add(p(`${cur.client} Built and tested; it becomes part of the system once the owner accepts it.`));
    if (cur.handbook.highlights) b.add(bullets(cur.handbook.highlights));
    for (const sh of (cur.handbook.screenshots ?? []).slice(0, 3)) b.add(image(r.abs(sh.file), { widthIn: sh.width ?? 5.6, caption: sh.caption, border: true }));
  } else {
    b.h2(`In progress: Sprint ${two(cur.id)} — ${cur.title}`);
    b.add(p(cur.client));
    if (cur.acceptance) b.add(small(`It will be accepted when: ${cur.acceptance.join('; ')}.`));
  }

  // 10 ────────────────────────────────────────────────────────────
  b.h1('Delivery roadmap');
  b.add(p(`The plan has ${r.sprints.length} sprints in ${Object.keys(PHASES).length} phases. Each sprint is about two weeks of work and ends with a review. The order follows what the resort needs first: rooms and bookings, then money, then operations, then connections and go-live.`));
  const phases = Object.keys(PHASES).map(Number).map(ph => {
    const ss = r.sprints.filter(s => s.phase === ph);
    const st = ss.every(s => s.status === 'done') ? 'done' : ss.some(s => s.status !== 'planned') ? 'in-progress' : 'planned';
    return { ph, ss, st };
  });
  b.add(table(['Phase', 'Sprints', 'What it brings', 'Status'],
    phases.map(x => [String(x.ph), x.ss.map(s => two(s.id)).join(', '), PHASES[x.ph], STATUS[x.st].text]),
    [800, 1500, TEXT_W - 800 - 1500 - 1500, 1500], { fills: (ri, ci) => (ci === 3 ? STATUS[phases[ri].st].fill : undefined) }));
  b.h2('Every sprint');
  b.add(table(['#', 'Sprint', 'What the resort gets', 'Status'],
    r.sprints.map(s => [two(s.id), s.title.split(' — ')[0], s.client, statusCell(s)]),
    [600, 2000, TEXT_W - 600 - 2000 - 1250, 1250],
    { size: 17, fills: (ri, ci) => (ci === 3 ? statusFill(r.sprints[ri]) : undefined) }));
  b.add(small('Dates depend on team size and on decisions the owner still has to make (for example the channel manager and the payroll provider). The order and scope are agreed; each sprint is confirmed at its planning meeting.'));

  // 11 ────────────────────────────────────────────────────────────
  b.h1('How we deliver');
  b.add(image(r.abs('tools/handbooks/assets/sprint-process.png'), { caption: 'One sprint, from plan to published' }));
  b.add(bullets([
    '**Small, reviewable steps.** Every sprint has a written goal, a list of tasks and the tests that prove it works.',
    '**You see it before it counts.** At the end of each sprint the owner follows a step-by-step review script on the real screens. Nothing is released until it is accepted.',
    '**Automatic checks on every change.** Each change runs through automatic checks before it can be merged: it builds, its tests pass on a real database on both Linux and Windows, the screens are clicked through in a real browser, and it contains no passwords or known-vulnerable components.',
    `**Tests that grow with the system.** As of Sprint ${lastDone ? two(lastDone.id) : '—'}: ${r.testMethods.backend} server test methods, ${r.testMethods.frontend} screen tests and ${r.testMethods.e2e} end-to-end browser steps, all passing.`,
    '**Everything is recorded.** Each sprint has a written guide, a changelog entry and a tagged release, so anyone can see what changed and when.',
  ]));

  // 12 ────────────────────────────────────────────────────────────
  b.h1('Running costs');
  b.add(p('Resort OS is built on free tools and free tiers while it is being developed. Paid services start only when the resort goes live, and most of them are charged per use.'));
  b.add(table(['Stage', 'When', 'Monthly cost'], [
    ['Development', 'Now, until about Sprint 28', '₹0'],
    ['Online demo / test environment (optional)', 'From about Sprint 11, to test payments and messages', '₹0 on free tiers'],
    ['Live operation', 'From go-live (Sprint 29)', 'About ₹2,500–₹6,000 per month (estimate) plus per-transaction fees'],
  ], [3300, 3400, TEXT_W - 6700], { zebra: true, boldFirst: true }));
  b.add(spacer(4), table(['Per-use charge', 'Typical rate (verify before go-live)'], [
    ['Razorpay online payments', 'About 2% per domestic payment (3% international), plus 18% GST on the fee; nothing for cash or UPI taken at the desk'],
    ['WhatsApp messages', 'Roughly ₹0.12 per utility message (confirmations, invoices) in India, plus GST'],
    ['Zoho Books', 'A paid plan is needed for the automatic sync (from about ₹749 per month)'],
    ['Channel manager for OTAs', 'Quoted by the provider; typically under ₹5,000 per month for a small property'],
  ], [3300, TEXT_W - 3300], { zebra: true }));
  b.add(small('Prices were checked in October 2026 and change over time; they are planning figures, not quotes.'));

  // 13 ────────────────────────────────────────────────────────────
  b.h1('Glossary');
  b.add(table(['Term', 'Meaning'], [
    ['Audit trail', 'A permanent record of every change: who, when, what and why'],
    ['Block', 'Taking a room or bed out of sale for some nights (repairs, owner use, a hold)'],
    ['Channel manager', 'A service that keeps availability and bookings in step across OTAs'],
    ['CGST / SGST / IGST', 'Central and State GST for sales within a state; Integrated GST for sales across states'],
    ['Credit note', 'The legal way to reduce or cancel an invoice that has been issued'],
    ['Dormitory (bed-based) room', 'A room sold bed by bed rather than as a whole'],
    ['Folio', 'The running bill for a stay, collecting every charge and payment'],
    ['Form-C', 'The report of a foreign national\'s stay that Indian accommodation providers must file'],
    ['KOT', 'Kitchen order ticket: the restaurant order as the kitchen sees it'],
    ['OTA', 'Online travel agent, for example Booking.com or MakeMyTrip'],
    ['PWA', 'Progressive web app: a website that can be installed and used like a phone app'],
    ['Role / permission', 'A role (Receptionist) is a named bundle of permissions (view rooms, take payments…)'],
    ['Sprint', 'A short, fixed period of work (about two weeks) that ends with a review and acceptance'],
  ], [2600, TEXT_W - 2600], { zebra: true, boldFirst: true }));
  return b;
}
