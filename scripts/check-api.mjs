#!/usr/bin/env node
// API checks for Sprints 01–17: calls the running API like a client would and prints PASS/FAIL per check.
//
//   node scripts/check-api.mjs                 (API must be running: backend/Resort.Api → dotnet watch run)
//
// Signs in as the owner using Bootstrap__OwnerEmail / Bootstrap__OwnerPassword from your .env.
// If you changed the owner's password in the app, run with OWNER_PASSWORD=<new one> set.
// What it changes in your local database (safe to run again and again):
//   - a staff account apicheck.receptionist@demo.local (created once; its password is reset on each run)
//   - one block on Room 103, about 60 nights from today, which it then releases (released blocks stay as history)
//   - a guest "API Check Guest" (+91 90000 00999, created once) with one more passport document per run, and one
//     throwaway guest per run that it anonymises straight away
//   - the BAR price of the Standard room for one night about 90 days ahead: raised by ₹1, then put back (both audited)
//   - bookings about 120, 130 and 140 nights ahead, each cancelled straight away (Sprints 05–07; cancelled bookings stay as history)
//   - on the bill of one guest in house: a ₹1 "API check" charge and its reversal (Sprint 08; bill lines are never deleted,
//     so both stay, and they add up to ₹0)
//   - Sprint 09 changes nothing: it reads the GST settings and asks the tax engine for previews
//   - Sprint 10: a ₹1 UPI payment on account for the demo booking R00002, voided straight away (payments are never
//     deleted, so it stays as a voided payment that counts for nothing); every other check is a read or a refusal
//   - Sprint 11: a ₹1 Razorpay payment link for the demo booking R00003, cancelled straight away (links are never deleted,
//     so it stays as a cancelled link). With Razorpay keys in .env this also creates a ₹1 order in your Razorpay test
//     account (orders that are never paid expire there); without keys the built-in fake Razorpay is used
//   - Sprint 12: the receipt of the demo payment P00001 (issued the first time, the same one after that; receipts are never
//     deleted). Every other check is a read, a preview or a refusal: no invoice or note is issued, so no number is used up
//   - Sprint 13 changes nothing: it reads the housekeeping board, checklists and tickets, and checks refusals (a photo that
//     isn't a picture, a report without a title, a step someone may not take); no task or ticket is changed
//   - Sprint 14 changes nothing: it reads the menu, tables, floor, orders and kitchen display, and checks refusals (a table
//     that already has an order, a stale version, a bill with food still in the kitchen, a void without a reason, a card
//     number as a reference); no order is opened, so no order number is used up
//   - Sprint 15 changes nothing: it reads stock, the ledger, recipes and the food-cost report, and checks refusals (a stock
//     change without a reason, more written off than the store has, a count that differs without a reason, a recipe
//     quantity in a unit of another kind); no stock moves, so no SA or SC number is used up
//   - Sprint 16 changes nothing: it reads suppliers, purchase requests, orders and supplier bills, and checks refusals (a
//     GSTIN with a wrong check digit, a request without items, a UPI payment without its UTR, a receptionist approving an
//     order); no supplier, request or order is saved, so no PR, PO, GRN, SI or SP number is used up
//   - Sprint 17 changes nothing: it reads employees, departments, the roster and one person's pay, and checks refusals (a
//     shift overlapping one already on the roster, a PAN in the wrong format, a receptionist reading pay or the roster);
//     no employee or shift is saved, so no E number is used up
//   - Sprint 18 changes nothing: it reads the attendance board, geofences and a week's timesheets, and checks refusals (a
//     geofence radius under 20 m, a manager entry without a reason, a receptionist reading the board or approving a week);
//     no clock event, geofence or timesheet is saved
// Docs: docs/testing/VERIFYING.md → "API checks".
import { readFileSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { randomBytes } from 'node:crypto';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const env = key => {
  if (process.env[key]) return process.env[key];
  const f = join(root, '.env');
  if (!existsSync(f)) return undefined;
  const line = readFileSync(f, 'utf8').split(/\r?\n/).find(l => l.startsWith(`${key}=`));
  return line ? line.slice(key.length + 1).trim() || undefined : undefined;
};

const BASE = (process.env.API_URL ?? 'http://localhost:5180').replace(/\/$/, '');
const OWNER = { email: env('Bootstrap__OwnerEmail') ?? 'owner@demo.local', password: process.env.OWNER_PASSWORD ?? env('Bootstrap__OwnerPassword') };
if (!OWNER.password) { console.error('No owner password: set Bootstrap__OwnerPassword in .env or OWNER_PASSWORD.'); process.exit(2); }

const results = [];
let section = '';
const sleep = ms => new Promise(r => setTimeout(r, ms));

async function call(method, path, { token, body, headers } = {}) {
  for (let attempt = 0; ; attempt++) {
    const res = await fetch(BASE + path, {
      method,
      headers: { 'content-type': 'application/json', ...(token ? { authorization: `Bearer ${token}` } : {}), ...headers },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    const text = await res.text();
    let json; try { json = text ? JSON.parse(text) : undefined; } catch { json = text; }
    // Sign-in is limited to 10 a minute per computer. Waiting is the limit working, not a failure.
    if (res.status === 429 && attempt < 2) { console.log('   (sign-in rate limit reached — waiting 61 s)'); await sleep(61_000); continue; }
    return { status: res.status, json, headers: res.headers };
  }
}

/** Runs one request and records PASS/FAIL. `check` gets the parsed body and returns true or a failure message. */
async function expectCall(name, method, path, expected, opts = {}, check = () => true) {
  let r, ok = false, note = '';
  try {
    r = await call(method, path, opts);
    if (r.status !== expected) note = `got ${r.status}${r.json?.code ? ' ' + r.json.code : ''}${r.json?.message ? ': ' + r.json.message : ''}`;
    else { const c = check(r.json); ok = c === true; if (!ok) note = c || 'unexpected body'; }
  } catch (e) { note = e.cause?.code === 'ECONNREFUSED' ? `API not reachable at ${BASE} — is it running?` : String(e); }
  results.push({ section, name, ok });
  console.log(`${ok ? ' PASS' : ' FAIL'}  ${name}\n       ${method} ${short(path)} → expected ${expected}${note ? ' · ' + note : ''}`);
  return r;
}
/** Records a check that isn't a single expectCall (several requests, or headers). */
function record(name, ok, note) {
  results.push({ section, name, ok });
  console.log(`${ok ? ' PASS' : ' FAIL'}  ${name}${ok ? '' : `\n       ${note}`}`);
}
/** Shows ids as {propertyId} / {id} so the printed URLs read like the API docs. */
const short = path => path.replace(/\/pay\/[A-Za-z0-9_-]{20,}/, '/pay/{token}').replace(/\/properties\/[0-9a-f-]{36}/, '/properties/{propertyId}').replace(/[0-9a-f]{8}-[0-9a-f-]{27}/g, '{id}');
const title = t => { section = t; console.log(`\n=== ${t} ===`); };

// ---------------------------------------------------------------- Sprint 01
title('Sprint 01 — sign-in, organisation, properties, staff, roles, audit');
const health = await expectCall('API and database are up', 'GET', '/health/ready', 200, {}, b => b === 'Healthy' || `body: ${b}`);
if (!health) process.exit(1);

await expectCall('Wrong password is refused with the generic message', 'POST', '/api/v1/auth/login', 401,
  { body: { email: OWNER.email, password: 'not-the-password-1' } }, b => b.code === 'INVALID_CREDENTIALS' || b.code);
const login = await expectCall('Owner signs in', 'POST', '/api/v1/auth/login', 200,
  { body: OWNER }, b => !!b.accessToken || 'no accessToken');
const T = login?.json?.accessToken;
if (!T) { console.log('\nCannot continue without the owner sign-in. Check the password in .env (Bootstrap__OwnerPassword).'); process.exit(1); }

await expectCall('No token → 401', 'GET', '/api/v1/auth/me', 401);
await expectCall('Who am I', 'GET', '/api/v1/auth/me', 200, { token: T },
  b => b.email === OWNER.email.toLowerCase() || `email ${b.email}`);
await expectCall('Organisation', 'GET', '/api/v1/organization', 200, { token: T }, b => !!b.code || 'no code');
const props = await expectCall('Properties list', 'GET', '/api/v1/properties', 200, { token: T },
  b => (Array.isArray(b) && b.length > 0) || 'no properties');
const prop = props.json[0];
const P = `/api/v1/properties/${prop.id}`;
await expectCall(`Property has its own local date (${prop.timezone})`, 'GET', P, 200, { token: T },
  b => /^\d{4}-\d{2}-\d{2}$/.test(b.localDate) || 'no localDate');
await expectCall('Time zone "IST" is refused (must be Asia/Kolkata)', 'PUT', P, 400,
  { token: T, body: { ...prop, timezone: 'IST' } }, b => !!b.details?.Timezone || 'no Timezone error');
await expectCall('Staff list', 'GET', '/api/v1/users', 200, { token: T }, b => Array.isArray(b) || 'not a list');
await expectCall('Empty staff form → 400 with a message per field', 'POST', '/api/v1/users', 400,
  { token: T, body: { email: '', fullName: '', temporaryPassword: '', roles: [] } },
  b => (b.details?.Email && b.details?.FullName && b.details?.TemporaryPassword) ? true : 'missing field errors');

// A receptionist for permission checks: create once, reset its password every run.
const recEmail = 'apicheck.receptionist@demo.local';
const temp = `Temp-${randomBytes(4).toString('hex')}-1`;
const own = `Own-${randomBytes(6).toString('hex')}-1`;
let rec = await call('POST', '/api/v1/users', { token: T, body: { email: recEmail, fullName: 'API Check Receptionist', temporaryPassword: temp, roles: [{ roleCode: 'RECEPTIONIST', propertyId: prop.id }] } });
if (rec.status === 409) {
  const u = (await call('GET', '/api/v1/users', { token: T })).json.find(x => x.email === recEmail);
  if (!u.isActive) await call('POST', `/api/v1/users/${u.id}/activate`, { token: T, headers: { 'X-Change-Reason': 'API check' } });
  rec = await call('POST', `/api/v1/users/${u.id}/reset-password`, { token: T, body: { temporaryPassword: temp }, headers: { 'X-Change-Reason': 'API check' } });
}
results.push({ section, name: 'Receptionist account ready', ok: rec.status === 201 || rec.status === 204 });
console.log(`${rec.status === 201 || rec.status === 204 ? ' PASS' : ' FAIL'}  Receptionist account ready (${recEmail})\n       POST /api/v1/users or …/reset-password → ${rec.status}`);
const recLogin = await expectCall('Temporary password signs in but must be changed', 'POST', '/api/v1/auth/login', 200,
  { body: { email: recEmail, password: temp } }, b => b.me?.mustChangePassword === true || 'mustChangePassword not set');
await expectCall('Receptionist sets her own password', 'POST', '/api/v1/auth/change-password', 204,
  { token: recLogin?.json?.accessToken, body: { currentPassword: temp, newPassword: own } });
const recLogin2 = await expectCall('Receptionist signs in with her own password', 'POST', '/api/v1/auth/login', 200,
  { body: { email: recEmail, password: own } });
const R = recLogin2?.json?.accessToken;
await expectCall('Receptionist can\'t list staff (403)', 'GET', '/api/v1/users', 403, { token: R });

await expectCall('17 roles', 'GET', '/api/v1/roles', 200, { token: T }, b => b.length === 17 || `${b.length} roles`);
await expectCall('117 permissions', 'GET', '/api/v1/permissions', 200, { token: T }, b => b.length === 117 || `${b.length} permissions`);
await expectCall('Audit trail (latest 5)', 'GET', '/api/v1/audit-logs?pageSize=5', 200, { token: T },
  b => (Array.isArray(b.items) && b.items.length > 0) || 'no audit entries');

// ---------------------------------------------------------------- Sprint 02
title('Sprint 02 — rooms, dormitory beds, blocks');
await expectCall('22 built-in amenities', 'GET', `${P}/amenities`, 200, { token: T }, b => b.length >= 22 || `${b.length}`);
await expectCall('Room types', 'GET', `${P}/accommodation-types`, 200, { token: T },
  b => b.length > 0 || 'no room types — reset the database and restart the API to get the demo rooms');
const rooms = await expectCall('Rooms (demo: 101–106 and DORM)', 'GET', `${P}/rooms`, 200, { token: T },
  b => (b.some(r => r.code === '103') && b.some(r => r.code === '101') && b.some(r => r.code === 'DORM')) || 'demo rooms 101/103/DORM not found — reset the database');
const byCode = c => rooms.json.find(r => r.code === c);
await expectCall('DORM is sold per bed and has beds', 'GET', `${P}/rooms/${byCode('DORM')?.id}/beds`, 200, { token: T },
  b => b.length > 0 || 'no beds');
await expectCall('Room status board for today', 'GET', `${P}/rooms/status`, 200, { token: T },
  b => b.rooms.length === rooms.json.length || `${b.rooms.length} vs ${rooms.json.length} rooms`);

const today = prop.localDate;
const plus = n => { const [y, m, d] = today.split('-').map(Number); return new Date(Date.UTC(y, m - 1, d + n)).toISOString().slice(0, 10); };
// Free nights about 60 days out, so it never clashes with blocks you made by hand.
let start = 60;
const existing = (await call('GET', `${P}/blocks?unitId=${byCode('103').id}`, { token: T })).json ?? [];
while (existing.some(b => b.startDate < plus(start + 2) && b.endDate > plus(start))) start += 3;
const blockBody = { unitId: byCode('103').id, bedId: null, blockType: 'MAINTENANCE', startDate: plus(start), endDate: plus(start + 2), reason: 'API check' };
const block = await expectCall(`Block Room 103 for 2 nights (${plus(start)} → ${plus(start + 2)})`, 'POST', `${P}/blocks`, 201,
  { token: T, body: blockBody }, b => b.nights === 2 || `nights ${b.nights}`);
await expectCall('Same nights again → 409 BLOCK_OVERLAP', 'POST', `${P}/blocks`, 409, { token: T, body: blockBody },
  b => b.code === 'BLOCK_OVERLAP' || b.code);
await expectCall('Block without a reason → 400', 'POST', `${P}/blocks`, 400, { token: T, body: { ...blockBody, reason: '' } });
await expectCall('Block that ended yesterday → 422 BLOCK_IN_PAST', 'POST', `${P}/blocks`, 422,
  { token: T, body: { ...blockBody, startDate: plus(-3), endDate: plus(-1) } }, b => b.code === 'BLOCK_IN_PAST' || b.code);
await expectCall('Receptionist can\'t block (403)', 'POST', `${P}/blocks`, 403, { token: R, body: blockBody });
if (block?.json?.id) {
  await expectCall('Release the block with a reason', 'POST', `${P}/blocks/${block.json.id}/release`, 200,
    { token: T, body: { reason: 'API check done' } }, b => (b.status === 'RELEASED' && b.releaseReason === 'API check done') || b.status);
}
await expectCall('Beds can\'t be added to per-room Room 101 → 422', 'POST', `${P}/rooms/${byCode('101').id}/beds`, 422,
  { token: T, body: { code: 'B99', name: 'Bed 99', bedType: 'SINGLE', sortOrder: 0 } }, b => b.code === 'BEDS_NOT_ALLOWED' || b.code);
await expectCall('Receptionist can view rooms', 'GET', `${P}/rooms`, 200, { token: R });
await expectCall('Receptionist can\'t add rooms (403)', 'POST', `${P}/rooms`, 403,
  { token: R, body: { code: 'X1', name: 'X', accommodationTypeId: rooms.json[0].accommodationTypeId, sortOrder: 0 } });
await expectCall('Audit trail has the block reasons', 'GET', '/api/v1/audit-logs?entityType=UnitBlock&pageSize=20', 200, { token: T },
  b => (b.items.some(a => a.reason === 'API check') && b.items.some(a => a.reason === 'API check done')) || 'reasons not found');

// ---------------------------------------------------------------- Sprint 03 — guests
title('Sprint 03 — guests, ID documents, privacy');
const CHECK_PHONE = '+919000000999';
let checkGuest = (await call('GET', `/api/v1/guests?q=${encodeURIComponent('90000 00999')}`, { token: T })).json?.items?.[0];
if (!checkGuest) {
  checkGuest = (await expectCall('Create the check guest (first run only)', 'POST', '/api/v1/guests', 201,
    { token: T, body: { firstName: 'API Check', lastName: 'Guest', phone: CHECK_PHONE, nationality: 'GB', isVip: false, ignoreDuplicates: true } }))?.json;
}
await expectCall('Find the guest by phone, typed the local way', 'GET', `/api/v1/guests?q=${encodeURIComponent('090000 00999')}`, 200,
  { token: R }, b => (b.items.length === 1 && b.items[0].phone === CHECK_PHONE) || `found ${b.items.length}`);
await expectCall('Same phone again → 409 POSSIBLE_DUPLICATE (lists the existing profile)', 'POST', '/api/v1/guests', 409,
  { token: R, body: { firstName: 'Api', lastName: 'Check', phone: '90000 00999', isVip: false } },
  b => (b.code === 'POSSIBLE_DUPLICATE' && b.details.some(d => d.reasons.includes('Same phone'))) || b.code);
await expectCall('Invalid phone → 400 with a field message', 'POST', '/api/v1/guests', 400,
  { token: R, body: { firstName: 'X', phone: '12345', isVip: false } }, b => !!b.details?.Phone || 'no Phone message');
await expectCall('Not signed in → 401', 'GET', '/api/v1/guests', 401);

async function upload(token, guestId, fields, file) {
  const fd = new FormData();
  for (const [k, v] of Object.entries(fields)) fd.set(k, v);
  if (file) fd.set('file', new Blob([file], { type: 'image/png' }), 'passport.png');
  const res = await fetch(`${BASE}/api/v1/guests/${guestId}/documents`, { method: 'POST', headers: { authorization: `Bearer ${token}` }, body: fd });
  return { status: res.status, json: await res.json().catch(() => undefined) };
}
const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==', 'base64');
const passportNo = 'P' + randomBytes(4).toString('hex').toUpperCase().slice(0, 7);
let doc;
{
  const r = await upload(R, checkGuest.id, { documentType: 'PASSPORT', number: passportNo, issuingCountry: 'GB' }, png);
  const ok = r.status === 201 && r.json?.maskedNumber === '•••• ' + passportNo.slice(-4);
  results.push({ section, name: 'Receptionist adds a passport with a scan (multipart)', ok });
  console.log(`${ok ? ' PASS' : ' FAIL'}  Receptionist adds a passport with a scan (multipart)\n       POST /guests/{id}/documents → expected 201, masked number${ok ? '' : ` · got ${r.status} ${JSON.stringify(r.json)}`}`);
  doc = r.json;
}
if (doc?.id) {
  await expectCall('Open the document: number is decrypted for this view', 'GET', `/api/v1/guests/${checkGuest.id}/documents/${doc.id}`, 200,
    { token: R }, b => b.number === passportNo || `number ${b.number}`);
  await expectCall('The opening is in the audit trail (SENSITIVE_READ)', 'GET', `/api/v1/audit-logs?entityType=GuestDocument&entityId=${doc.id}`, 200,
    { token: T }, b => b.items.some(a => a.action === 'SENSITIVE_READ') || 'no SENSITIVE_READ row');
}
const forgetMe = await expectCall('Create a throwaway guest to anonymise', 'POST', '/api/v1/guests', 201,
  { token: T, body: { firstName: 'Forget', lastName: 'Me ' + randomBytes(2).toString('hex'), isVip: false, ignoreDuplicates: true } });
if (forgetMe?.json?.id) {
  await expectCall('Receptionist can\'t anonymise (403)', 'POST', `/api/v1/guests/${forgetMe.json.id}/anonymise`, 403, { token: R, body: { reason: 'test' } });
  await expectCall('Owner anonymises with a reason', 'POST', `/api/v1/guests/${forgetMe.json.id}/anonymise`, 200,
    { token: T, body: { reason: 'API check: guest asked to be forgotten' } },
    b => (b.status === 'ANONYMISED' && b.lastName === null && b.fullName === 'Anonymised guest') || b.status);
}
await expectCall('Retention dry run lists what would be purged, deletes nothing', 'GET', '/api/v1/guests/retention/dry-run', 200,
  { token: T }, b => (typeof b.retentionDays === 'number' && Array.isArray(b.items)) || 'unexpected body');

// ---------------------------------------------------------------- Sprint 04 — rates and packages
title('Sprint 04 — rate plans, rate grid, quotes, packages');
const plans = await expectCall('Rate plans (demo: BAR is the default)', 'GET', `${P}/rate-plans`, 200, { token: R },
  b => b.some(x => x.code === 'BAR' && x.isDefault) || `plans: ${b.map(x => x.code).join(', ')}`);
const bar = plans?.json?.find(x => x.isDefault);
const types = (await call('GET', `${P}/accommodation-types`, { token: T })).json ?? [];
const standard = types.find(t => t.code === 'STANDARD_ROOM');
const dow = new Date(today + 'T00:00:00Z').getUTCDay();
const thu = (4 - dow + 7) % 7 + 7;                  // next week's Thursday: the stay crosses into the weekend
if (bar && standard) {
  const grid = await expectCall('Grid for Thu–Sat next week', 'GET', `${P}/rate-plans/${bar.id}/prices?from=${plus(thu)}&to=${plus(thu + 3)}`, 200,
    { token: R }, b => b.filter(x => x.accommodationTypeId === standard.id).length === 3 || 'Standard room has no prices for those nights');
  const nights = (grid?.json ?? []).filter(x => x.accommodationTypeId === standard.id);
  await expectCall('Quote Room 103, 2 adults, 3 nights = the grid (acceptance)', 'GET',
    `${P}/rates/quote?room=103&from=${plus(thu)}&to=${plus(thu + 3)}&adults=2`, 200, { token: R },
    b => (b.breakdown.map(n => n.total).join() === nights.map(n => n.price).join() && b.total === nights.reduce((s, n) => s + n.price, 0))
      || `quote ${b.breakdown.map(n => n.total).join(' + ')} vs grid ${nights.map(n => n.price).join(' + ')}`);
  await expectCall('3 adults in a Standard room → 422 TOO_MANY_GUESTS with the reason', 'GET',
    `${P}/rates/quote?room=103&from=${plus(thu)}&to=${plus(thu + 1)}&adults=3`, 422, { token: R },
    b => (b.code === 'TOO_MANY_GUESTS' && /sleeps at most 2/.test(b.message)) || b.message);
  await expectCall('Receptionist can\'t change prices (403)', 'PUT', `${P}/rate-plans/${bar.id}/prices`, 403,
    { token: R, body: { accommodationTypeIds: [standard.id], from: plus(90), to: plus(91), daysOfWeek: [], price: 1, extraAdult: 0, extraChild: 0, minStay: 1 } });
  const cell = ((await call('GET', `${P}/rate-plans/${bar.id}/prices?from=${plus(90)}&to=${plus(91)}`, { token: T })).json ?? [])
    .find(x => x.accommodationTypeId === standard.id);
  if (cell) {
    const set = price => ({ accommodationTypeIds: [standard.id], from: plus(90), to: plus(91), daysOfWeek: [], price,
      extraAdult: cell.extraAdult, extraChild: cell.extraChild, minStay: cell.minStay });
    await expectCall('Owner raises one night by ₹1', 'PUT', `${P}/rate-plans/${bar.id}/prices`, 200,
      { token: T, body: set(cell.price + 1) }, b => b.changed === 1 || JSON.stringify(b));
    await expectCall('The change is audited with old and new price', 'GET', '/api/v1/audit-logs?entityType=RatePlanPrice', 200,
      { token: T }, b => b.items.some(a => a.changes?.Price?.old === cell.price && a.changes?.Price?.new === cell.price + 1) || 'no audit row with old/new price');
    await expectCall('Put the price back', 'PUT', `${P}/rate-plans/${bar.id}/prices`, 200, { token: T, body: set(cell.price) });
  }
  await expectCall('Negative price → 400 with the reason under Price', 'PUT', `${P}/rate-plans/${bar.id}/prices`, 400,
    { token: T, body: { accommodationTypeIds: [standard.id], from: plus(90), to: plus(91), daysOfWeek: [], price: -5, extraAdult: 0, extraChild: 0, minStay: 1 } },
    b => b.details?.Price?.[0] === "Price can't be negative." || JSON.stringify(b.details));
}
await expectCall('Packages: every split adds up to the price', 'GET', `${P}/packages`, 200, { token: R },
  b => (b.length > 0 && b.every(k => Math.round(k.components.reduce((s, c) => s + c.amount * 100, 0)) === Math.round(k.price * 100))) || 'a split does not add up');
await expectCall('Shares that don\'t add up to 100 → 400 with the total', 'POST', `${P}/packages`, 400,
  { token: T, body: { code: 'API_CHECK', name: 'API check', accommodationTypeId: standard?.id, nights: 1, adults: 2, children: 0, price: 1000, sortOrder: 0,
    components: [{ category: 'ROOM', description: 'Room', sharePercent: 80 }, { category: 'FOOD', description: 'Breakfast', sharePercent: 15.5 }] } },
  b => b.details?.Components?.[0] === 'The shares add up to 95.5%; they must add up to exactly 100%.' || JSON.stringify(b.details));

// ---------------------------------------------------------------- Sprint 05 — availability and reservations
title('Sprint 05 — availability, reservations, idempotency');
// Books one Standard room 120 nights from now for the check guest, then cancels it, so re-running leaves nothing held.
const far = plus(120), farEnd = plus(121);
const avail = await expectCall('Availability 120 nights ahead lists every room type', 'GET',
  `${P}/availability?from=${far}&to=${farEnd}&adults=2`, 200, { token: R },
  b => (b.types.some(t => t.code === 'STANDARD_ROOM') && b.nights === 1) || JSON.stringify(b.types?.map(t => t.code)));
const std = avail?.json?.types?.find(t => t.code === 'STANDARD_ROOM');
await expectCall('Check-in in the past → 400 under From', 'GET', `${P}/availability?from=${plus(-1)}&to=${plus(1)}&adults=2`, 400,
  { token: R }, b => /can't be before today/.test(b.details?.From?.[0] ?? '') || JSON.stringify(b.details));
const walkIn = ((await call('GET', `${P}/reservation-sources`, { token: R })).json ?? []).find(x => x.code === 'WALK_IN');
if (std && std.freeRooms.length > 0 && walkIn && checkGuest) {
  const room = std.freeRooms[0];
  const body = { checkIn: far, checkOut: farEnd, rooms: [{ accommodationTypeId: std.accommodationTypeId, unitId: room.id, adults: 2, children: 0 }],
    guestIds: [checkGuest.id], sourceId: walkIn.id, ratePlanId: null, status: 'CONFIRMED', specialRequests: null, note: 'API check' };
  const key = `api-check-${Date.now()}`;
  await expectCall('Booking without an Idempotency-Key → 400 under Idempotency-Key', 'POST', `${P}/reservations`, 400,
    { token: R, body }, b => !!b.details?.['Idempotency-Key'] || JSON.stringify(b.details));
  const made = await expectCall(`Receptionist books ${room.name} (201, reference R…)`, 'POST', `${P}/reservations`, 201,
    { token: R, body, headers: { 'Idempotency-Key': key } }, b => /^R\d{5,}$/.test(b.reference) || b.reference);
  const again = await call('POST', `${P}/reservations`, { token: R, body, headers: { 'Idempotency-Key': key } });
  record('Same key and body again → the same booking, marked as replayed',
    again.status === 201 && again.json?.id === made?.json?.id && again.headers.get('idempotent-replayed') === 'true',
    `status ${again.status}, id ${again.json?.id}, replayed ${again.headers.get('idempotent-replayed')}`);
  await expectCall('Same key, different body → 422 IDEMPOTENCY_KEY_REUSED', 'POST', `${P}/reservations`, 422,
    { token: R, body: { ...body, specialRequests: 'different' }, headers: { 'Idempotency-Key': key } }, b => b.code === 'IDEMPOTENCY_KEY_REUSED' || b.code);
  await expectCall(`${room.name} again for the same night → 409 ROOM_UNAVAILABLE naming the booking`, 'POST', `${P}/reservations`, 409,
    { token: R, body, headers: { 'Idempotency-Key': `${key}-2` } },
    b => (b.code === 'ROOM_UNAVAILABLE' && b.message.includes(made?.json?.reference ?? '?')) || b.message);
  if (made?.json) {
    await expectCall('Cancel without a reason → 400 under Reason', 'POST', `${P}/reservations/${made.json.id}/cancel`, 400,
      { token: R, body: {} }, b => !!b.details?.Reason || JSON.stringify(b.details));
    await expectCall('Cancel with a reason → CANCELLED, the room is free again', 'POST', `${P}/reservations/${made.json.id}/cancel`, 200,
      { token: R, body: { reason: 'API check' } }, b => (b.status === 'CANCELLED' && b.units.every(u => !u.holdsInventory)) || b.status);
    await expectCall('Confirm a cancelled booking → 422 INVALID_STATUS_TRANSITION with the reason', 'POST', `${P}/reservations/${made.json.id}/confirm`, 422,
      { token: R, body: {} }, b => (b.code === 'INVALID_STATUS_TRANSITION' && /cancelled/.test(b.message)) || b.message);
    await expectCall('History: created CONFIRMED, then CANCELLED with the reason', 'GET', `${P}/reservations/${made.json.id}`, 200,
      { token: R }, b => (b.history.length === 2 && b.history[1].reason === 'API check') || JSON.stringify(b.history));
  }
} else record('Book a Standard room 120 nights ahead', false, 'no free Standard room, walk-in source or check guest');
await expectCall('Reservations list (default: stays not ended yet)', 'GET', `${P}/reservations`, 200, { token: R },
  b => Array.isArray(b.items) || 'no items');

// ---------------------------------------------------------------- Sprint 06 — reservation calendar
title('Sprint 06 — calendar and moving a guest to another room');
// Books one Standard room 130 nights from now, moves it to another Standard room, then cancels it: nothing stays held.
const c0 = plus(130), c1 = plus(132);
await expectCall('Calendar for 7 days: rooms, bookings, blocks', 'GET', `${P}/calendar?from=${today}&to=${plus(7)}`, 200, { token: R },
  b => (Array.isArray(b.rooms) && b.rooms.length > 0 && Array.isArray(b.bookings) && Array.isArray(b.blocks)) || 'missing rooms/bookings/blocks');
await expectCall('Calendar longer than 62 days → 400 under To with the reason', 'GET', `${P}/calendar?from=${today}&to=${plus(63)}`, 400,
  { token: R }, b => /at most 62 days/.test(b.details?.To?.[0] ?? '') || JSON.stringify(b.details));
const freeStd = ((await call('GET', `${P}/availability?from=${c0}&to=${c1}&adults=2`, { token: R })).json?.types ?? [])
  .find(t => t.code === 'STANDARD_ROOM')?.freeRooms ?? [];
if (freeStd.length >= 2 && walkIn && checkGuest && std) {
  const [a, b2] = freeStd;
  const made = await expectCall(`Receptionist books ${a.name} for 2 nights`, 'POST', `${P}/reservations`, 201,
    { token: R, headers: { 'Idempotency-Key': `api-check-move-${Date.now()}` },
      body: { checkIn: c0, checkOut: c1, rooms: [{ accommodationTypeId: std.accommodationTypeId, unitId: a.id, adults: 2, children: 0 }],
              guestIds: [checkGuest.id], sourceId: walkIn.id, ratePlanId: null, status: 'CONFIRMED', specialRequests: null, note: null } });
  if (made?.json) {
    const r = made.json;
    const unit = r.units.find(u => u.current);
    const url = `${P}/reservations/${r.id}/reassign`;
    await expectCall('Move without a reason → 400 under Reason', 'POST', url, 400,
      { token: R, body: { reservationUnitId: unit.id, unitId: b2.id, bedId: null, reason: '', version: r.version } },
      b => /Say why the guest is moving/.test(b.details?.Reason?.[0] ?? '') || JSON.stringify(b.details));
    await expectCall('Move with an old version → 409 CONCURRENCY_CONFLICT', 'POST', url, 409,
      { token: R, body: { reservationUnitId: unit.id, unitId: b2.id, bedId: null, reason: 'API check', version: r.version + 1 } },
      b => b.code === 'CONCURRENCY_CONFLICT' || b.code);
    const moved = await expectCall(`Move ${a.name} → ${b2.name}: same total, a note with the reason`, 'POST', url, 200,
      { token: R, body: { reservationUnitId: unit.id, unitId: b2.id, bedId: null, reason: 'API check', version: r.version } },
      b => (b.units.find(u => u.current)?.unitId === b2.id && b.totalAmount === r.totalAmount && b.notes.some(n => n.note.includes('Reason: API check')))
        || JSON.stringify(b.units));
    await expectCall(`Calendar shows the booking in ${b2.name}`, 'GET', `${P}/calendar?from=${c0}&to=${c1}`, 200, { token: R },
      b => b.bookings.some(x => x.reservationId === r.id && x.unitId === b2.id) || 'not in the new room');
    if (moved?.json)
      await expectCall('Cancel the check booking (frees the room)', 'POST', `${P}/reservations/${r.id}/cancel`, 200,
        { token: R, body: { reason: 'API check' } }, b => b.status === 'CANCELLED' || b.status);
  }
} else record('Move a booking between two free Standard rooms 130 nights ahead', false, 'fewer than 2 free Standard rooms, or no walk-in source / check guest');

// ---------------------------------------------------------------- Sprint 07 — front desk
title('Sprint 07 — front desk: arrivals, check-in and check-out, walk-ins, room readiness');
// Read-only lists, then the refusals (nothing is checked in or out). One booking 140 nights ahead is made and cancelled.
await expectCall('Front desk dashboard: counts and the rooms board', 'GET', `${P}/front-desk/dashboard`, 200, { token: R },
  b => (b.today === today && Array.isArray(b.rooms) && b.rooms.length > 0 && typeof b.arrivalsExpected === 'number') || JSON.stringify(b).slice(0, 200));
await expectCall("Today's arrivals use the property's date", 'GET', `${P}/front-desk/arrivals`, 200, { token: R },
  b => (b.today === today && b.date === today && Array.isArray(b.items)) || `today ${b.today}, date ${b.date}`);
await expectCall('Departures for tomorrow', 'GET', `${P}/front-desk/departures?date=${plus(1)}`, 200, { token: R },
  b => b.date === plus(1) || b.date);
await expectCall('In house', 'GET', `${P}/front-desk/in-house`, 200, { token: R }, b => Array.isArray(b.items) || 'no items');
const room103 = byCode('103');
await expectCall('Receptionist can’t change a room’s cleaning state → 403', 'PUT', `${P}/rooms/${room103.id}/housekeeping`, 403,
  { token: R, body: { bedId: null, status: 'READY', reason: null } });
await expectCall('Cleaning state must be one of the six → 400 under Status', 'PUT', `${P}/rooms/${room103.id}/housekeeping`, 400,
  { token: T, body: { bedId: null, status: 'SPARKLING', reason: null } }, b => !!b.details?.Status?.[0] || JSON.stringify(b.details));
await expectCall('Walk-in with neither a guest nor new guest details → 400 under GuestId', 'POST', `${P}/walk-ins`, 400,
  { token: R, headers: { 'Idempotency-Key': `api-check-walkin-${Date.now()}` },
    body: { guestId: null, newGuest: null, checkOut: plus(1), rooms: [], ratePlanId: null, specialRequests: null, note: null } },
  b => /one of the two/.test(b.details?.GuestId?.[0] ?? '') || JSON.stringify(b.details));
const f0 = plus(140), f1 = plus(141);
const freeLater = ((await call('GET', `${P}/availability?from=${f0}&to=${f1}&adults=1`, { token: R })).json?.types ?? [])
  .find(t => t.code === 'STANDARD_ROOM')?.freeRooms ?? [];
if (freeLater.length && walkIn && checkGuest && std) {
  const made = await expectCall(`Receptionist books ${freeLater[0].name} 140 nights ahead`, 'POST', `${P}/reservations`, 201,
    { token: R, headers: { 'Idempotency-Key': `api-check-desk-${Date.now()}` },
      body: { checkIn: f0, checkOut: f1, rooms: [{ accommodationTypeId: std.accommodationTypeId, unitId: freeLater[0].id, adults: 1, children: 0 }],
              guestIds: [checkGuest.id], sourceId: walkIn.id, ratePlanId: null, status: 'CONFIRMED', specialRequests: null, note: null } });
  if (made?.json) {
    const r = made.json;
    await expectCall('Check in before the arrival day → 422 CHECK_IN_DATE with the dates', 'POST', `${P}/reservations/${r.id}/check-in`, 422,
      { token: R, body: { version: null } }, b => (b.code === 'CHECK_IN_DATE' && b.message.includes('Change the arrival date')) || `${b.code}: ${b.message}`);
    await expectCall('Check out a guest who never checked in → 422 INVALID_STATUS_TRANSITION', 'POST', `${P}/reservations/${r.id}/check-out`, 422,
      { token: R, body: { version: null } }, b => b.code === 'INVALID_STATUS_TRANSITION' || `${b.code}: ${b.message}`);
    await expectCall('Room options for the booking (with readiness)', 'GET', `${P}/reservations/${r.id}/room-options?reservationUnitId=${r.units[0].id}`, 200,
      { token: R }, b => (Array.isArray(b) && b.length > 0 && 'housekeeping' in b[0]) || JSON.stringify(b).slice(0, 200));
    await expectCall('Cancel the check booking (frees the room)', 'POST', `${P}/reservations/${r.id}/cancel`, 200,
      { token: R, body: { reason: 'API check' } }, b => b.status === 'CANCELLED' || b.status);
  }
} else record('Check-in refusals on a booking 140 nights ahead', false, 'no free Standard room, or no walk-in source / check guest');

// ---------------------------------------------------------------- Sprint 08 — folios
title('Sprint 08 — folios: charges, reversals, room nights');
// Read-only lists, the refusals, then one ₹1 charge on an in-house guest's bill that the owner reverses (net ₹0).
await expectCall('Folios of the guests in house', 'GET', `${P}/folios?show=in-house`, 200, { token: R },
  b => Array.isArray(b.items) || 'no items');
await expectCall('An unknown list filter → 400 under Show with the choices', 'GET', `${P}/folios?show=everything`, 400, { token: R },
  b => !!b.details?.Show?.[0] || JSON.stringify(b.details));
const inHouse = (await call('GET', `${P}/folios?show=in-house`, { token: R })).json?.items?.find(f => f.isPrimary);
if (inHouse) {
  const F = `${P}/folios/${inHouse.id}`;
  const before = (await call('GET', F, { token: R })).json;
  await expectCall(`Bill ${inHouse.number}: every line, the sums by category, the balance`, 'GET', F, 200, { token: R },
    b => (Array.isArray(b.items) && Array.isArray(b.categories) && typeof b.balance === 'number' && b.canPost === true) || JSON.stringify(b).slice(0, 200));
  await expectCall('Charge without a description → 400 under Description', 'POST', `${F}/charges`, 400,
    { token: R, headers: { 'Idempotency-Key': `api-check-charge-bad-${Date.now()}` },
      body: { category: 'OTHER', description: '', quantity: 1, unitPrice: 1 } },
    b => /Say what the charge is for/.test(b.details?.Description?.[0] ?? '') || JSON.stringify(b.details));
  await expectCall('Charge without an Idempotency-Key → 400', 'POST', `${F}/charges`, 400,
    { token: R, body: { category: 'OTHER', description: 'API check', quantity: 1, unitPrice: 1 } });
  const key = `api-check-charge-${Date.now()}`;
  const charge = { category: 'OTHER', description: 'API check', quantity: 1, unitPrice: 1 };
  const posted = await expectCall('Receptionist posts a ₹1 charge → the balance goes up by ₹1', 'POST', `${F}/charges`, 201,
    { token: R, headers: { 'Idempotency-Key': key }, body: charge }, b => b.balance === before.balance + 1 || `balance ${b.balance}, was ${before.balance}`);
  const again = await call('POST', `${F}/charges`, { token: R, headers: { 'Idempotency-Key': key }, body: charge });
  record('Same key again → charged once, marked as replayed',
    again.status === 201 && again.json?.balance === before.balance + 1 && again.headers.get('idempotent-replayed') === 'true',
    `status ${again.status}, balance ${again.json?.balance}, replayed ${again.headers.get('idempotent-replayed')}`);
  const line = posted?.json?.items?.filter(i => i.description === 'API check' && i.live).at(-1);
  if (line) {
    const url = `${F}/charges/${line.id}/reverse`;
    await expectCall('Receptionist can’t reverse a charge → 403', 'POST', url, 403, { token: R, body: { reason: 'API check' } });
    await expectCall('Reverse without a reason → 400 under Reason', 'POST', url, 400, { token: T, body: { reason: '' } },
      b => /Say why the charge is reversed/.test(b.details?.Reason?.[0] ?? '') || JSON.stringify(b.details));
    await expectCall('Owner reverses it with a reason → the balance is back, both lines stay', 'POST', url, 200,
      { token: T, body: { reason: 'API check' } },
      b => (b.balance === before.balance && b.items.some(i => i.id === line.id && !i.live)) || `balance ${b.balance}, was ${before.balance}`);
    await expectCall('Reverse it again → 409 ALREADY_REVERSED with who and when', 'POST', url, 409,
      { token: T, body: { reason: 'API check' } }, b => (b.code === 'ALREADY_REVERSED' && b.message.includes('already reversed')) || `${b.code}: ${b.message}`);
  }
} else record('Charge and reverse on an in-house guest’s bill', false, 'nobody is in house (the demo data has R00008 in Room 104)');

// ---------------------------------------------------------------- Sprint 09 — GST and the tax engine
title('Sprint 09 — GST registrations, tax categories and rules, the tax preview');
// Read-only: the settings, previews and refusals. Adding a rule would change your GST from its date, so it is left to the
// review script (REVIEWING.md → Sprint 09) and the browser tests.
await expectCall('The property\u2019s GST registration', 'GET', `${P}/gst-registrations`, 200, { token: T },
  b => (Array.isArray(b) && b.some(r => r.current && /^[0-9]{2}[A-Z0-9]{13}$/.test(r.gstin))) || `no current registration: ${JSON.stringify(b).slice(0, 200)}`);
const cats = await expectCall('Tax categories with the rule that applies today', 'GET', '/api/v1/tax/categories', 200, { token: T },
  b => (Array.isArray(b) && ['ACCOMMODATION', 'RESTAURANT'].every(c => b.some(x => x.code === c && x.currentRule))) || JSON.stringify(b).slice(0, 200));
await expectCall('Every rule with its dates, rates and source', 'GET', '/api/v1/tax/rules', 200, { token: T },
  b => (Array.isArray(b) && b.length >= 3 && b.every(r => r.source && r.slabs.length > 0)) || JSON.stringify(b).slice(0, 200));
const preview = (date, amount, extra = {}) => ({ propertyId: prop.id, chargeCategory: 'ROOM', date, amount, ...extra });
await expectCall('Preview: ₹3,500 room night on 21 Sep 2025 → 12% (CGST 6% + SGST 6% = ₹420)', 'POST', '/api/v1/tax/calculate', 200,
  { token: T, body: preview('2025-09-21', 3500) }, b => (b.taxed && b.tax === 420 && b.components.length === 2) || `${b.tax}: ${b.noTaxReason ?? ''}`);
await expectCall('Preview: the same night on 22 Sep 2025 → 5% (₹175)', 'POST', '/api/v1/tax/calculate', 200,
  { token: T, body: preview('2025-09-22', 3500) }, b => (b.taxed && b.tax === 175) || `${b.tax}: ${b.noTaxReason ?? ''}`);
await expectCall('Preview: exactly ₹7,500 a night is still in the 5% slab', 'POST', '/api/v1/tax/calculate', 200,
  { token: T, body: preview('2026-01-10', 7500) }, b => (b.slab?.rate === 5 && b.tax === 375) || JSON.stringify(b.slab));
await expectCall('Preview: ₹7,500.01 a night → 18%', 'POST', '/api/v1/tax/calculate', 200,
  { token: T, body: preview('2026-01-10', 7500.01) }, b => b.slab?.rate === 18 || JSON.stringify(b.slab));
await expectCall('Preview: food with the place of supply in another state → IGST', 'POST', '/api/v1/tax/calculate', 200,
  { token: T, body: preview('2026-01-10', 1000, { chargeCategory: 'FOOD', placeOfSupply: '27' }) },
  b => (!b.intraState && b.components.length === 1 && b.components[0].code === 'IGST' && b.tax === 50) || JSON.stringify(b.components));
await expectCall('Preview without saying what is charged → 400 under ChargeCategory', 'POST', '/api/v1/tax/calculate', 400,
  { token: T, body: { propertyId: prop.id, amount: 100 } }, b => !!b.details?.ChargeCategory?.[0] || JSON.stringify(b.details));
await expectCall('A registration with a wrong check digit → 400 saying what the digit should be', 'POST', `${P}/gst-registrations`, 400,
  { token: T, body: { gstin: '30AABCT1234C1Z8', legalName: 'API check', effectiveFrom: '2026-04-01' } },
  b => /check digit\) should be 9/.test(b.details?.Gstin?.[0] ?? '') || JSON.stringify(b.details));
const food = cats?.json?.find?.(c => c.code === 'RESTAURANT');
if (food) {
  await expectCall('A rule starting yesterday → 400: a rule can only start today or later', 'POST', '/api/v1/tax/rules', 400,
    { token: T, body: { taxCategoryId: food.id, effectiveFrom: '2020-01-01', ruleType: 'FLAT', slabs: [{ upTo: null, rate: 5, inputTaxCredit: false }], source: 'API check' } },
    b => /can start today/.test(b.details?.EffectiveFrom?.[0] ?? '') || JSON.stringify(b.details));
  await expectCall('A slab rule with one rate → 400 under Slabs', 'POST', '/api/v1/tax/rules', 400,
    { token: T, body: { taxCategoryId: food.id, effectiveFrom: '2099-01-01', ruleType: 'SLAB', slabs: [{ upTo: null, rate: 5, inputTaxCredit: false }], source: 'API check' } },
    b => /at least two rates/.test(b.details?.Slabs?.[0] ?? '') || JSON.stringify(b.details));
  await expectCall('Receptionist can\u2019t add a rule → 403', 'POST', '/api/v1/tax/rules', 403,
    { token: R, body: { taxCategoryId: food.id, effectiveFrom: '2099-01-01', ruleType: 'FLAT', slabs: [{ upTo: null, rate: 5, inputTaxCredit: false }], source: 'API check' } });
} else record('Rule refusals', false, 'no RESTAURANT tax category (migration V011 seeds it)');
await expectCall('Receptionist can\u2019t read the GST settings → 403', 'GET', '/api/v1/tax/categories', 403, { token: R });

// ---------------------------------------------------------------- Sprint 10 — payments, refunds, cash drawer
title('Sprint 10 — payments, refunds and the cash drawer');
const key = () => ({ 'Idempotency-Key': `api-check-${randomBytes(6).toString('hex')}` });
const drawer = await expectCall('The cash drawer (Front desk), its shifts and what waits for approval', 'GET', `${P}/cash-drawer`, 200, { token: T },
  b => (b?.name && Array.isArray(b.recent) && Array.isArray(b.pendingApproval)) || JSON.stringify(b).slice(0, 200));
const pays = await expectCall('Payments with totals by method (demo: P00001 cash, P00002 UPI advance)', 'GET', `${P}/payments?pageSize=100`, 200, { token: T },
  b => (Array.isArray(b?.items) && Array.isArray(b.byMethod) && ['P00001', 'P00002'].every(n => b.items.some(i => i.number === n))) || JSON.stringify(b).slice(0, 200));
const advance = pays?.json?.items?.find?.(i => i.number === 'P00002');
if (advance) {
  await expectCall('One payment: P00002 is ₹2,000 on account for R00002', 'GET', `${P}/payments/${advance.id}`, 200, { token: T },
    b => (b.amount === 2000 && b.onAccount + b.allocated === 2000 - b.refunded && b.reference === 'R00002') || JSON.stringify(b).slice(0, 200));
  await expectCall('A refund of more than the payment → 400 under Amount, saying the most that can be refunded', 'POST', `${P}/payments/${advance.id}/refund`, 400,
    { token: T, headers: key(), body: { amount: 999999, reason: 'API check', method: 'UPI' } },
    b => /at most/.test(b.details?.Amount?.[0] ?? '') || JSON.stringify(b.details));
  await expectCall('A refund without a reason → 400 under Reason', 'POST', `${P}/payments/${advance.id}/refund`, 400,
    { token: T, headers: key(), body: { amount: 1, method: 'UPI' } }, b => /Say why/.test(b.details?.Reason?.[0] ?? '') || JSON.stringify(b.details));
  await expectCall('Receptionist can’t refund → 403', 'POST', `${P}/payments/${advance.id}/refund`, 403,
    { token: R, headers: key(), body: { amount: 1, reason: 'API check', method: 'UPI' } });

  const body = { method: 'UPI', amount: 1, reservationId: advance.reservationId, note: 'API check' };
  await expectCall('Taking money without an Idempotency-Key → 400', 'POST', `${P}/payments`, 400, { token: T, body });
  await expectCall('A card number in the reference → 400 under PaymentReference (card details are never stored)', 'POST', `${P}/payments`, 400,
    { token: T, headers: key(), body: { ...body, method: 'CARD', paymentReference: '4111 1111 1111 1111' } },
    b => /card number/.test(b.details?.PaymentReference?.[0] ?? '') || JSON.stringify(b.details));
  const one = await expectCall('₹1 by UPI on account for R00002', 'POST', `${P}/payments`, 201, { token: T, headers: key(), body },
    b => (b.status === 'CAPTURED' && b.onAccount === 1 && b.canVoid) || JSON.stringify(b).slice(0, 200));
  if (one?.json?.id) {
    await expectCall('Void without a reason → 400 under Reason', 'POST', `${P}/payments/${one.json.id}/void`, 400, { token: T, body: {} },
      b => !!b.details?.Reason?.[0] || JSON.stringify(b.details));
    await expectCall('Void it with a reason → CANCELLED, counts for nothing', 'POST', `${P}/payments/${one.json.id}/void`, 200,
      { token: T, body: { reason: 'API check: entered by the check script' } }, b => (b.status === 'CANCELLED' && !!b.voidReason) || JSON.stringify(b).slice(0, 200));
    await expectCall('Voiding it twice → 409 ALREADY_VOIDED', 'POST', `${P}/payments/${one.json.id}/void`, 409,
      { token: T, body: { reason: 'API check again' } }, b => b.code === 'ALREADY_VOIDED' || b.code);
  }
} else record('Payment checks', false, 'no P00002 (dev seed S009 adds it; reset the database)');

if (drawer?.json && !drawer.json.current) {
  await expectCall('Cash with the drawer closed → 400 under Method, saying to open it', 'POST', `${P}/payments`, 400,
    { token: T, headers: key(), body: { method: 'CASH', amount: 1, reservationId: advance?.reservationId } },
    b => /drawer isn’t open|drawer isn't open/.test(b.details?.Method?.[0] ?? '') || JSON.stringify(b.details));
  await expectCall('Closing a drawer that isn’t open → 422 DRAWER_NOT_OPEN', 'POST', `${P}/cash-drawer/close`, 422,
    { token: T, body: { countedAmount: 0 } }, b => b.code === 'DRAWER_NOT_OPEN' || b.code);
} else console.log(' SKIP  Closed-drawer refusals (a shift is open; close it to run them)');
const closed = drawer?.json?.recent?.find?.(s => s.status === 'CLOSED');
if (closed) {
  await expectCall('Approving a shift that balanced → 409 NO_VARIANCE_PENDING', 'POST', `${P}/cash-drawer/sessions/${closed.id}/approve-variance`, 409,
    { token: T, body: { note: 'API check' } }, b => b.code === 'NO_VARIANCE_PENDING' || b.code);
}
await expectCall('Receptionist can read the drawer', 'GET', `${P}/cash-drawer`, 200, { token: R });
await expectCall('Receptionist can’t approve a variance → 403', 'POST', `${P}/cash-drawer/sessions/${closed?.id ?? '00000000-0000-0000-0000-000000000000'}/approve-variance`, 403,
  { token: R, body: { note: 'API check' } });

// ---------------------------------------------------------------- Sprint 11 — Razorpay
title('Sprint 11 — Razorpay payment links, the guest page and the webhook');
const r3 = (await call('GET', `${P}/reservations?q=R00003`, { token: T }))?.json?.items?.find?.(x => x.reference === 'R00003');
if (r3) {
  const linksUrl = `${P}/payments/razorpay/orders`;
  await expectCall('R00003’s payment links and what it owes', 'GET', `${linksUrl}?reservationId=${r3.id}`, 200, { token: T },
    b => (b?.reference === 'R00003' && typeof b.owed === 'number' && Array.isArray(b.links)) || JSON.stringify(b).slice(0, 200));
  await expectCall('Listing links without a booking → 400 under ReservationId', 'GET', linksUrl, 400, { token: T },
    b => !!b.details?.ReservationId?.[0] || JSON.stringify(b.details));
  const body = { reservationId: r3.id, amount: 1, note: 'API check' };
  await expectCall('A link without an Idempotency-Key → 400', 'POST', linksUrl, 400, { token: T, body });
  await expectCall('A link for less than ₹1 → 400 under Amount (Razorpay’s smallest payment)', 'POST', linksUrl, 400,
    { token: T, headers: key(), body: { ...body, amount: 0.5 } }, b => /at least ₹1/.test(b.details?.Amount?.[0] ?? '') || JSON.stringify(b.details));
  await expectCall('A link for more than the booking owes → 400 under Amount, saying the most', 'POST', linksUrl, 400,
    { token: T, headers: key(), body: { ...body, amount: 9999999 } }, b => /at most|nothing to ask/.test(b.details?.Amount?.[0] ?? '') || JSON.stringify(b.details));
  const link = await expectCall('₹1 link for R00003 (a Razorpay order; status CREATED, a /pay/… page)', 'POST', linksUrl, 201, { token: T, headers: key(), body },
    b => (b.status === 'CREATED' && /^order_/.test(b.gatewayOrderId) && /^\/pay\//.test(b.payPath)) || JSON.stringify(b).slice(0, 200));
  if (link?.json?.id) {
    const token = link.json.payPath.split('/').pop();
    await expectCall('The guest’s page (no sign-in) asks for ₹1 and has what Checkout needs', 'GET', `/api/v1/pay/${token}`, 200, {},
      b => (b.status === 'AWAITING_PAYMENT' && b.amount === 1 && b.gatewayOrderId === link.json.gatewayOrderId && !!b.keyId) || JSON.stringify(b).slice(0, 200));
    await expectCall('A made-up browser callback is refused (signature checked) and records nothing', 'POST', `/api/v1/pay/${token}/callback`, 400,
      { body: { razorpayPaymentId: 'pay_apicheck', razorpayOrderId: link.json.gatewayOrderId, razorpaySignature: 'bad' } });
    await expectCall('Cancel the link without a reason → 400 under Reason', 'POST', `${linksUrl}/${link.json.id}/cancel`, 400, { token: T, body: {} },
      b => !!b.details?.Reason?.[0] || JSON.stringify(b.details));
    await expectCall('Cancel it with a reason → CANCELLED', 'POST', `${linksUrl}/${link.json.id}/cancel`, 200,
      { token: T, body: { reason: 'API check: made by the check script' } }, b => b.status === 'CANCELLED' || JSON.stringify(b).slice(0, 200));
    await expectCall('Cancelling it twice → 409 LINK_ALREADY_CANCELLED', 'POST', `${linksUrl}/${link.json.id}/cancel`, 409,
      { token: T, body: { reason: 'API check again' } }, b => b.code === 'LINK_ALREADY_CANCELLED' || b.code);
    await expectCall('The guest’s page now says the link was cancelled', 'GET', `/api/v1/pay/${token}`, 200, {},
      b => (b.status === 'CANCELLED' && !b.gatewayOrderId) || JSON.stringify(b).slice(0, 200));
  }
  await expectCall('Receptionist can see the links', 'GET', `${linksUrl}?reservationId=${r3.id}`, 200, { token: R });
} else record('Payment link checks', false, 'no R00003 among current bookings (dev seed S004 adds it; reset the database)');
await expectCall('A pay page with a made-up token → 404 saying the link isn’t valid', 'GET', '/api/v1/pay/not-a-real-token', 404, {},
  b => /isn't valid|isn’t valid/.test(b.message ?? '') || b.message);
const hook = JSON.stringify({ event: 'payment.captured', payload: {} });
await expectCall('Webhook with a wrong signature → 401 BAD_SIGNATURE, nothing stored', 'POST', '/api/v1/webhooks/razorpay', 401,
  { body: JSON.parse(hook), headers: { 'X-Razorpay-Signature': '0'.repeat(64), 'X-Razorpay-Event-Id': `evt_apicheck_${randomBytes(4).toString('hex')}` } },
  b => b.code === 'BAD_SIGNATURE' || b.code);
await expectCall('Webhook with no signature → 401', 'POST', '/api/v1/webhooks/razorpay', 401, { body: JSON.parse(hook) });

// ---------------------------------------------------------------- Sprint 12 — GST invoices, notes, receipts
title('Sprint 12 — GST invoices, credit and debit notes, receipts');
const NONE = '00000000-0000-0000-0000-000000000000';
await expectCall('Invoices, newest first, with their status (demo: one per checked-out stay)', 'GET', `${P}/invoices?pageSize=20`, 200, { token: T },
  b => (Array.isArray(b?.items) && b.items.every(i => /^[A-Z0-9]{1,5}\/B?\d{4}\/\d{5}$/.test(i.number) && i.status)) || JSON.stringify(b).slice(0, 200));
const r7 = (await call('GET', `${P}/reservations?q=R00007`, { token: T }))?.json?.items?.find?.(x => x.reference === 'R00007');
const f7 = r7 ? (await call('GET', `${P}/reservations/${r7.id}/folios`, { token: T }))?.json?.find?.(f => f.isPrimary) : undefined;
if (f7) {
  await expectCall('Invoice preview of R00007’s bill: tax invoice, the room night with SAC 996311, totals; or already invoiced', 'GET', `${P}/folios/${f7.id}/invoice-preview`, 200, { token: T },
    b => (b.documentType === 'INVOICE' && (b.invoiceId ? /already|is on invoice|invoice/.test(b.cantIssueReason ?? '') : b.lines.some(l => l.category === 'ROOM' && l.hsnSac === '996311')
      && Math.abs(b.taxableValue + b.taxTotal - b.total) < 0.005)) || JSON.stringify(b).slice(0, 300));
  await expectCall('Issuing without an Idempotency-Key → 400', 'POST', `${P}/folios/${f7.id}/invoice`, 400, { token: T, body: {} });
  await expectCall('A GSTIN with a typing mistake → 400 under CustomerGstin, saying the right check digit', 'POST', `${P}/folios/${f7.id}/invoice`, 400,
    { token: T, headers: key(), body: { customerName: 'API Check Pvt Ltd', customerGstin: '27AAPFU0939F1ZX' } },
    b => /check digit\) should be V/.test(b.details?.CustomerGstin?.[0] ?? '') || JSON.stringify(b.details));
  await expectCall('Receptionist can preview the invoice', 'GET', `${P}/folios/${f7.id}/invoice-preview`, 200, { token: R });
} else record('Invoice preview checks', false, 'no R00007 bill (dev seed S006/S007 add it; reset the database)');
await expectCall('A credit note without a reason → 400 under Reason', 'POST', `${P}/invoices/${NONE}/credit-notes`, 400,
  { token: T, headers: key(), body: { type: 'REDUCE', lines: [] } }, b => /Say why/.test(b.details?.Reason?.[0] ?? '') || JSON.stringify(b.details));
await expectCall('A credit note of an unknown type → 400 under Type, naming CANCEL and REDUCE', 'POST', `${P}/invoices/${NONE}/credit-notes`, 400,
  { token: T, headers: key(), body: { type: 'EDIT', reason: 'API check' } }, b => /CANCEL.*REDUCE/.test(b.details?.Type?.[0] ?? '') || JSON.stringify(b.details));
await expectCall('A debit note on an invoice that doesn’t exist → 404', 'POST', `${P}/invoices/${NONE}/debit-notes`, 404,
  { token: T, headers: key(), body: { reason: 'API check' } });
await expectCall('Receptionist can’t issue a credit note → 403', 'POST', `${P}/invoices/${NONE}/credit-notes`, 403,
  { token: R, headers: key(), body: { type: 'REDUCE', reason: 'API check' } });
const p1 = (await call('GET', `${P}/payments?q=P00001`, { token: T }))?.json?.items?.find?.(x => x.number === 'P00001');
if (p1) {
  const receipt = await expectCall('Receipt of P00001 (issued once; asking again gives the same one)', 'POST', `${P}/payments/${p1.id}/receipt`, 200, { token: T, body: {} },
    b => (/^[A-Z0-9]{1,5}\/R\d{4}\/\d{5}$/.test(b.number) && b.paymentNumber === 'P00001' && b.amount === p1.amount) || JSON.stringify(b).slice(0, 200));
  if (receipt?.json?.id) {
    const again = await call('POST', `${P}/payments/${p1.id}/receipt`, { token: T, body: {} });
    record('Asking again returns the same receipt number', again.json?.number === receipt.json.number, `got ${again.json?.number}`);
    const pdf = await call('GET', `${P}/receipts/${receipt.json.id}/pdf`, { token: R });
    record('Receipt PDF (receptionist): application/pdf named after the number',
      pdf.status === 200 && /application\/pdf/.test(pdf.headers.get('content-type') ?? '') && String(pdf.json).startsWith('%PDF')
      && (pdf.headers.get('content-disposition') ?? '').includes(receipt.json.number.replace(/\//g, '-')),
      `got ${pdf.status} ${pdf.headers.get('content-type')} ${pdf.headers.get('content-disposition')}`);
  }
} else record('Receipt checks', false, 'no P00001 (dev seed S009 adds it; reset the database)');
const voided = (await call('GET', `${P}/payments?pageSize=100`, { token: T }))?.json?.items?.find?.(x => x.status === 'CANCELLED');
if (voided) {
  await expectCall(`A voided payment (${voided.number}) gets no receipt → 422 NO_RECEIPT_FOR_PAYMENT, saying why`, 'POST', `${P}/payments/${voided.id}/receipt`, 422,
    { token: T, body: {} }, b => (b.code === 'NO_RECEIPT_FOR_PAYMENT' && /was voided/.test(b.message)) || b.code);
}

// ---------------------------------------------------------------- Sprint 13 — housekeeping and maintenance
title('Sprint 13 — housekeeping tasks, inspection, maintenance tickets');
const HK = `${P}/housekeeping`, MT = `${P}/maintenance`;
await expectCall('Housekeeping board: every room or dorm bed with its cleaning state, no guest names', 'GET', `${HK}/board`, 200, { token: T },
  b => (Array.isArray(b?.rooms) && b.rooms.length > 0 && typeof b.summary?.rush === 'number'
    && b.rooms.every(r => ['DIRTY', 'CLEANING', 'CLEAN', 'INSPECTION', 'READY', 'OUT_OF_ORDER'].includes(r.status))
    && !JSON.stringify(b).includes('guestName')) || JSON.stringify(b).slice(0, 200));
await expectCall('Receptionist can see the board', 'GET', `${HK}/board`, 200, { token: R });
await expectCall('Receptionist can’t start a cleaning task → 403', 'POST', `${HK}/tasks/${NONE}/start`, 403, { token: R, body: {} });
await expectCall('Starting a task that doesn’t exist → 404', 'POST', `${HK}/tasks/${NONE}/start`, 404, { token: T, body: {} });
await expectCall('Inspection with an unknown result → 400 under Result, naming PASS, FAIL and START', 'POST', `${HK}/tasks/${NONE}/inspect`, 400,
  { token: T, body: { result: 'MAYBE' } }, b => /PASS.*FAIL.*START/.test(b.details?.Result?.[0] ?? '') || JSON.stringify(b.details));
await expectCall('Sending a room back without saying what to redo → 400 under Note', 'POST', `${HK}/tasks/${NONE}/inspect`, 400,
  { token: T, body: { result: 'FAIL' } }, b => /redo/.test(b.details?.Note?.[0] ?? '') || JSON.stringify(b.details));
await expectCall('A cleaning task of an unknown kind → 400 under TaskType', 'POST', `${HK}/tasks`, 400,
  { token: T, body: { unitId: NONE, taskType: 'POLISH' } }, b => !!b.details?.TaskType?.[0] || JSON.stringify(b.details));
await expectCall('Checklists: the departure clean has its lines (demo: 7)', 'GET', `${HK}/checklists`, 200, { token: T },
  b => (Array.isArray(b) && b.some(t => t.taskType === 'DEPARTURE' && t.items.length > 0)) || JSON.stringify(b).slice(0, 200));
await expectCall('A checklist with no lines → 400 under Items', 'POST', `${HK}/checklists`, 400,
  { token: T, body: { name: 'API check', taskType: 'DEPARTURE', isActive: false, items: [] } }, b => !!b.details?.Items?.[0] || JSON.stringify(b.details));
const open = await expectCall('Open tickets, most urgent first (demo: M00001 pool pump, M00002 tap)', 'GET', `${MT}/tickets`, 200, { token: T },
  b => (Array.isArray(b) && b.every(t => /^M\d{5,}$/.test(t.number) && ['OPEN', 'ASSIGNED', 'IN_PROGRESS', 'ON_HOLD'].includes(t.status))) || JSON.stringify(b).slice(0, 200));
await expectCall('A report without a title → 400 under Title', 'POST', `${MT}/tickets`, 400,
  { token: R, body: { title: '', category: 'AC', priority: 'HIGH', location: 'Lobby', outOfOrder: false } },
  b => /3–120/.test(b.details?.Title?.[0] ?? '') || JSON.stringify(b.details));
await expectCall('A report that doesn’t say where → 400 under Location', 'POST', `${MT}/tickets`, 400,
  { token: R, body: { title: 'API check', category: 'AC', priority: 'HIGH', outOfOrder: false } },
  b => /where/.test(b.details?.Location?.[0] ?? '') || JSON.stringify(b.details));
await expectCall('Out of order without a ready date → 400 under ExpectedReadyOn', 'POST', `${MT}/tickets`, 400,
  { token: R, body: { title: 'API check', category: 'AC', priority: 'HIGH', unitId: NONE, outOfOrder: true } },
  b => !!b.details?.ExpectedReadyOn?.[0] || JSON.stringify(b.details));
await expectCall('Receptionist can’t resolve a ticket → 403', 'POST', `${MT}/tickets/${NONE}/resolve`, 403,
  { token: R, body: { resolution: 'API check' } });
const t1 = open?.json?.find?.(t => t.number === 'M00001');
if (t1) {
  // A text file posted as a photo: refused by its content, not its name, and nothing is stored.
  const form = new FormData();
  form.set('file', new Blob(['not a picture'], { type: 'image/jpeg' }), 'fake.jpg');
  const res = await fetch(`${BASE}${MT}/tickets/${t1.id}/photos`, { method: 'POST', headers: { authorization: `Bearer ${T}` }, body: form });
  const b = await res.json().catch(() => ({}));
  record('A “photo” that isn’t a picture → 400 under File, nothing stored', res.status === 400 && /isn't a photo/.test(b.details?.File?.[0] ?? ''),
    `got ${res.status} ${JSON.stringify(b.details ?? b).slice(0, 200)}`);
  await expectCall('Closing a ticket that isn’t resolved → 422, saying to resolve it first', 'POST', `${MT}/tickets/${t1.id}/close`, 422,
    { token: T, body: {} }, b => /resolve it before closing/.test(b.message ?? '') || b.message);
} else record('Photo and close checks', false, 'no open M00001 (dev seed S010 adds it; reset the database)');
await expectCall('Room status shows today’s cleaning state and open repairs', 'GET', `${P}/rooms/status`, 200, { token: T },
  b => (b.rooms.every(r => 'housekeeping' in r && typeof r.openRepairs === 'number')) || JSON.stringify(b.rooms?.[0]).slice(0, 200));

// ---------------------------------------------------------------- Sprint 14 — menu, restaurant POS and KOT
title('Sprint 14 — menu, restaurant orders, kitchen tickets');
const OR = `${P}/orders`;
const menu = await expectCall('Menu by section (demo: Paneer Tikka with Half / Full, Masala Chai sold out)', 'GET', `${P}/menu`, 200, { token: T },
  b => {
    const items = (b?.categories ?? []).flatMap(c => c.items);
    const tikka = items.find(i => i.code === 'PANEER-TIKKA'), chai = items.find(i => i.code === 'MASALA-CHAI');
    return (items.length > 0 && (!tikka || tikka.variants.map(v => v.name).join('/') === 'Half/Full') && (!chai || chai.isAvailable === false))
      || JSON.stringify(b).slice(0, 200);
  });
await expectCall('Tables (demo: T1–T4 and B1, B2)', 'GET', `${P}/menu/tables`, 200, { token: T },
  b => (Array.isArray(b) && b.every(t => /^[A-Z0-9][A-Z0-9_-]*$/.test(t.code) && t.seats >= 1)) || JSON.stringify(b).slice(0, 200));
const floor = await expectCall('Floor: every table free or with its open order (demo: O00001 at T2)', 'GET', `${OR}/floor`, 200, { token: T },
  b => (Array.isArray(b?.tables) && b.tables.every(t => t.order === null || /^O\d{5,}$/.test(t.order.number))) || JSON.stringify(b).slice(0, 200));
await expectCall('Kitchen display: tickets still to cook, oldest first (demo: K00001 New)', 'GET', `${P}/kitchen/kots`, 200, { token: T },
  b => (Array.isArray(b?.tickets) && b.tickets.every(k => /^K\d{5,}$/.test(k.number) && ['NEW', 'PREPARING', 'READY'].includes(k.status))) || JSON.stringify(b).slice(0, 200));
await expectCall('Today’s open orders with the day’s totals', 'GET', `${OR}?status=OPEN`, 200, { token: T },
  b => (Array.isArray(b?.items) && typeof b.openTotal === 'number' && b.items.every(o => o.status === 'OPEN')) || JSON.stringify(b).slice(0, 200));
await expectCall('Orders with an unknown status → 400 under Status', 'GET', `${OR}?status=MAYBE`, 400, { token: T },
  b => /ALL/.test(b.details?.Status?.[0] ?? '') || JSON.stringify(b.details));
await expectCall('Receptionist can’t see restaurant orders → 403', 'GET', `${OR}/floor`, 403, { token: R });
await expectCall('A table order without a table → 400 under TableId', 'POST', OR, 400,
  { token: T, body: { orderType: 'DINE_IN', items: [] } }, b => b.details?.TableId?.[0] === 'Choose the table.' || JSON.stringify(b.details));
await expectCall('Room service without a room → 400 under UnitId', 'POST', OR, 400,
  { token: T, body: { orderType: 'ROOM_SERVICE' } }, b => !!b.details?.UnitId?.[0] || JSON.stringify(b.details));
await expectCall('A dish with quantity 0 → 400 under Items[0].Quantity', 'POST', OR, 400,
  { token: T, body: { orderType: 'TAKEAWAY', items: [{ menuItemId: NONE, quantity: 0 }] } },
  b => b.details?.['Items[0].Quantity']?.[0] === 'The quantity must be 1–99.' || JSON.stringify(b.details));
await expectCall('Voiding without a reason → 400 under Reason', 'POST', `${OR}/${NONE}/void`, 400,
  { token: T, body: { reason: '' } }, b => /Say why it is voided/.test(b.details?.Reason?.[0] ?? '') || JSON.stringify(b.details));
await expectCall('A discount of 150% → 400 under Value', 'POST', `${OR}/${NONE}/discount`, 400,
  { token: T, body: { kind: 'PERCENT', value: 150, reason: 'API check' } }, b => /at most 100%/.test(b.details?.Value?.[0] ?? '') || JSON.stringify(b.details));
await expectCall('Paying a restaurant bill ONLINE → 400 under Method', 'POST', `${OR}/${NONE}/pay`, 400,
  { token: T, body: { method: 'ONLINE' } }, b => /paid at the restaurant/.test(b.details?.Method?.[0] ?? '') || JSON.stringify(b.details));
await expectCall('A card number as the reference → 400 under Reference (no card data is ever stored)', 'POST', `${OR}/${NONE}/pay`, 400,
  { token: T, body: { method: 'CARD', reference: '4111111111111111' } }, b => !!b.details?.Reference?.[0] || JSON.stringify(b.details));
await expectCall('Receptionist can’t void an order → 403', 'POST', `${OR}/${NONE}/void`, 403, { token: R, body: { reason: 'API check' } });
await expectCall('A kitchen step on a ticket that doesn’t exist → 404', 'POST', `${P}/kitchen/kots/${NONE}/start`, 404, { token: T, body: {} });
const busy = floor?.json?.tables?.find?.(t => t.order);
if (busy) {
  await expectCall(`A second order on table ${busy.code} → 409, naming ${busy.order.number}`, 'POST', OR, 409,
    { token: T, body: { orderType: 'DINE_IN', tableId: busy.id } },
    b => (b.code === 'TABLE_HAS_ORDER' && b.message.includes(busy.order.number)) || b.message);
  const o = (await call('GET', `${OR}/${busy.order.id}`, { token: T })).json;
  await expectCall(`A change on ${o.number} with an old version → 409 (another waiter changed it first)`, 'POST', `${OR}/${o.id}/items`, 409,
    { token: T, body: { items: [{ menuItemId: o.items[0].menuItemId, quantity: 1 }], version: (o.version + 7) % 4294967296 } },
    b => (b.code === 'CONCURRENCY_CONFLICT' && b.message.includes(o.number)) || b.message);
  if (o.kots.some(k => ['NEW', 'PREPARING', 'READY'].includes(k.status)))
    await expectCall(`Billing ${o.number} while food is in the kitchen → 422, naming the ticket`, 'POST', `${OR}/${o.id}/pay`, 422,
      { token: T, body: { method: 'UPI', reference: 'API check', version: o.version } },
      b => (b.code === 'ORDER_IN_KITCHEN' && /K\d{5}/.test(b.message)) || b.message);
} else record('Table, version and kitchen refusals', false, 'no open table order (dev seed S011 adds O00001 at T2; reset the database)');
const starters = menu?.json?.categories?.find?.(c => c.items.some(i => i.code === 'PANEER-TIKKA'));
if (starters)
  await expectCall('A new dish with a code already on the menu → 400 under Code', 'POST', `${P}/menu/items`, 400,
    { token: T, body: { categoryId: starters.id, code: 'PANEER-TIKKA', name: 'API check', foodType: 'VEG', price: 1, isActive: false, sortOrder: 0, variants: [], modifierGroups: [] } },
    b => /already has the code PANEER-TIKKA/.test(b.details?.Code?.[0] ?? '') || JSON.stringify(b.details));

// ---------------------------------------------------------------- Sprint 15 — inventory and recipe costing
title('Sprint 15 — stock, ledger, recipes, food cost');
const IV = `${P}/inventory`;
const inv = await expectCall('Stock by store (demo: Main store and Kitchen, 21 items, fresh cream to reorder)', 'GET', IV, 200, { token: T },
  b => (Array.isArray(b?.items) && b.locations.filter(l => l.isKitchen).length <= 1 && typeof b.value === 'number'
        && b.items.every(i => i.onHand === i.levels.reduce((s, l) => s + l.onHand, 0) || Math.abs(i.onHand - i.levels.reduce((s, l) => s + l.onHand, 0)) < 0.0005))
        || JSON.stringify(b).slice(0, 200));
await expectCall('The ledger, newest first (demo: SA00001 wastage of onion and capsicum)', 'GET', `${IV}/movements?type=WASTAGE`, 200, { token: T },
  b => (Array.isArray(b?.items) && b.items.every(m => m.type === 'WASTAGE' && m.quantity < 0)) || JSON.stringify(b).slice(0, 200));
await expectCall('Recipes with their cost (demo: Paneer Tikka (Full) ₹105 of ₹280 = 37.5 %)', 'GET', `${IV}/recipes`, 200, { token: T },
  b => {
    const full = b?.dishes?.find?.(d => d.name === 'Paneer Tikka (Full)');
    return (Array.isArray(b?.dishes) && (!full || !full.recipe || Math.abs(full.foodCostPercent - full.cost / full.price * 100) < 0.01)) || JSON.stringify(b).slice(0, 200);
  });
await expectCall('Food-cost report: highest and lowest margins, dishes without a recipe', 'GET', `${IV}/food-cost`, 200, { token: T },
  b => (Array.isArray(b?.highest) && Array.isArray(b.lowest) && Array.isArray(b.noRecipe) && b.highest.length <= 3
        && b.highest.every((l, n, a) => n === 0 || a[n - 1].marginPercent >= l.marginPercent)) || JSON.stringify(b).slice(0, 200));
await expectCall('A food-cost period that ends before it starts → 400 under from', 'GET', `${IV}/food-cost?from=2026-10-10&to=2026-10-01`, 400, { token: T },
  b => !!b.details?.from?.[0] || JSON.stringify(b.details));
await expectCall('Receptionist can’t see stock → 403', 'GET', IV, 403, { token: R });
const paneerItem = inv?.json?.items?.find?.(i => i.code === 'PANEER');
const kitchenLoc = inv?.json?.locations?.find?.(l => l.isKitchen);
await expectCall('A stock change without a reason → 400 under Reason', 'POST', `${IV}/adjustments`, 400,
  { token: T, body: { kind: 'WASTAGE', itemId: paneerItem?.id ?? NONE, locationId: kitchenLoc?.id ?? NONE, quantity: 1, reason: '' } },
  b => /Every stock change needs a reason/.test(b.details?.Reason?.[0] ?? '') || JSON.stringify(b.details));
if (paneerItem && kitchenLoc) {
  await expectCall('Writing off more paneer than the kitchen has → 400 under Quantity, saying how much is there', 'POST', `${IV}/adjustments`, 400,
    { token: T, body: { kind: 'WASTAGE', itemId: paneerItem.id, locationId: kitchenLoc.id, quantity: 999999, reason: 'API check' } },
    b => /^Kitchen has (only|no) /.test(b.details?.Quantity?.[0] ?? '') || JSON.stringify(b.details));
  const have = paneerItem.levels.find(l => l.locationId === kitchenLoc.id)?.onHand ?? 0;
  await expectCall('A count that differs without a reason → 400 under Lines[0].Reason, naming the item', 'POST', `${IV}/counts`, 400,
    { token: T, body: { locationId: kitchenLoc.id, lines: [{ itemId: paneerItem.id, countedQuantity: have + 1, reason: null }] } },
    b => /^Paneer: the system has/.test(b.details?.['Lines[0].Reason']?.[0] ?? '') || JSON.stringify(b.details));
  const units = inv.json.units;
  const tikkaDish = menu?.json?.categories?.flatMap?.(c => c.items)?.find?.(i => i.code === 'PANEER-TIKKA');
  if (tikkaDish)
    await expectCall('A recipe quantity of paneer in ml → 400 under Ingredients[0].UnitId (kg or g only)', 'PUT', `${IV}/recipes`, 400,
      { token: T, body: { menuItemId: tikkaDish.id, variantId: null, note: null, ingredients: [{ itemId: paneerItem.id, quantity: 200, unitId: units.find(u => u.code === 'ml').id }], version: null } },
      b => b.details?.['Ingredients[0].UnitId']?.[0] === 'Paneer is kept in kg, so its quantity must be in kg or g.' || JSON.stringify(b.details));
} else record('Stock refusals', false, 'no PANEER item or kitchen store (dev seed S012 adds them; reset the database)');

// ---------------------------------------------------------------- Sprint 16 — suppliers and purchasing
title('Sprint 16 — suppliers, purchase orders, goods received, supplier bills');
const PU = `${P}/purchasing`;
await expectCall('Suppliers with GSTIN, state and what is owed (demo: Mapusa Fresh Vegetables, Anjuna Fishermen unregistered)', 'GET', `${PU}/suppliers`, 200, { token: T },
  b => (Array.isArray(b) && b.every(s => typeof s.outstanding === 'number' && /^\d{2}$/.test(s.stateCode) && (s.gstin === null || s.gstin.slice(0, 2) === s.stateCode))
        && (!b.some(s => s.code === 'FISH') || b.find(s => s.code === 'FISH').gstin === null)) || JSON.stringify(b).slice(0, 200));
await expectCall('Purchase requests (demo: PR00001 vegetables for the main store)', 'GET', `${PU}/requests`, 200, { token: T },
  b => (Array.isArray(b) && b.every(r => /^PR\d{5}$/.test(r.number) && r.items.length > 0)) || JSON.stringify(b).slice(0, 200));
await expectCall('Purchase orders with the approval limit', 'GET', `${PU}/orders`, 200, { token: T },
  b => (Array.isArray(b?.items) && b.approvalLimit > 0 && b.waiting >= b.items.filter(o => o.status === 'PENDING_APPROVAL').length) || JSON.stringify(b).slice(0, 200));
await expectCall('Supplier bills with what is still to pay and the input GST', 'GET', `${PU}/invoices`, 200, { token: T },
  b => (Array.isArray(b?.items) && typeof b.outstanding === 'number' && b.overdue <= b.outstanding + 0.005) || JSON.stringify(b).slice(0, 200));
await expectCall('Receptionist can’t see purchasing → 403', 'GET', `${PU}/orders`, 403, { token: R });
await expectCall('A supplier GSTIN with a wrong check digit → 400 under Gstin, saying the right one', 'POST', `${PU}/suppliers`, 400,
  { token: T, body: { code: 'API-CHECK', name: 'API check supplier', gstin: '30AAACF1234A1ZT', stateCode: null, paymentTermsDays: 7 } },
  b => /check digit\) should be S/.test(b.details?.Gstin?.[0] ?? '') || JSON.stringify(b.details));
const storeLoc = inv?.json?.locations?.find?.(l => l.code === 'STORE') ?? inv?.json?.locations?.[0];
await expectCall('A purchase request without items → 400 under Items', 'POST', `${PU}/requests`, 400,
  { token: T, body: { locationId: storeLoc?.id ?? NONE, neededBy: null, note: null, items: [] } },
  b => b.details?.Items?.[0] === 'Add at least one item.' || JSON.stringify(b.details));
await expectCall('A UPI supplier payment without its UTR → 400 under Reference', 'POST', `${PU}/invoices/${NONE}/payments`, 400,
  { token: T, body: { amount: 1, paidOn: null, method: 'UPI', reference: '', note: null, version: 0 }, headers: { 'Idempotency-Key': `api-check-${randomBytes(6).toString('hex')}` } },
  b => /Enter the UTR/.test(b.details?.Reference?.[0] ?? '') || JSON.stringify(b.details));
await expectCall('Receptionist can’t approve an order → 403', 'POST', `${PU}/orders/${NONE}/approve`, 403,
  { token: R, body: { note: null, version: 0 } });

// ---------------------------------------------------------------- Sprint 17 — employees and roster
title('Sprint 17 — employees, pay behind its own permission, the roster with clash detection');
const emps = await expectCall('Employees by department, numbered E00001 … and without pay (demo: 10)', 'GET', `${P}/employees`, 200, { token: T },
  b => (Array.isArray(b) && b.length > 0 && b.every(e => /^E\d{5}$/.test(e.code) && e.departmentName && !('basic' in e) && !('gross' in e))) || JSON.stringify(b).slice(0, 200));
await expectCall('Departments with their job titles', 'GET', `${P}/departments`, 200, { token: T },
  b => (Array.isArray(b) && b.every(d => Array.isArray(d.designations))) || JSON.stringify(b).slice(0, 200));
const firstEmp = emps?.json?.[0];
await expectCall('Pay is its own endpoint (the owner has employee_salary.read)', 'GET', `${P}/employees/${firstEmp?.id ?? NONE}/compensation`, 200, { token: T },
  b => (Array.isArray(b?.history) && b.history.every(c => Math.abs(c.gross - c.basic - c.allowances) < 0.005)) || JSON.stringify(b).slice(0, 200));
await expectCall('Receptionist can’t read anyone’s pay → 403', 'GET', `${P}/employees/${firstEmp?.id ?? NONE}/compensation`, 403, { token: R });
await expectCall('Receptionist can’t see the whole roster → 403', 'GET', `${P}/roster`, 403, { token: R });
const week = await expectCall('This week’s roster with shortages and warnings', 'GET', `${P}/roster`, 200, { token: T },
  b => (Array.isArray(b?.shifts) && Array.isArray(b.shortages) && b.shortages.every(x => x.rostered < x.needed && x.missing === x.needed - x.rostered)) || JSON.stringify(b).slice(0, 200));
const onShift = week?.json?.shifts?.find?.(s => !s.overlapAllowed && s.workDate >= week.json.today);
if (onShift) {
  await expectCall('A shift overlapping one already on the roster → 409 SHIFT_CLASH naming it (nothing saved)', 'POST', `${P}/roster/shifts`, 409,
    { token: T, body: { employeeId: onShift.employeeId, workDate: onShift.workDate, templateId: null, departmentId: null,
      startTime: onShift.startTime, endTime: onShift.endTime, breakMinutes: 0, allowOverlap: false } },
    b => (b.code === 'SHIFT_CLASH' && b.message.includes(onShift.employeeName) && b.message.includes('One person can')) || JSON.stringify(b));
} else {
  console.log('  (skipped the clash check: nobody is on the roster from today to Sunday)');
}
const pan = new FormData();
pan.append('documentType', 'PAN');
pan.append('number', 'ABCPK1234');
const panRes = await fetch(`${BASE}${P}/employees/${firstEmp?.id ?? NONE}/documents`, { method: 'POST', headers: { Authorization: `Bearer ${T}` }, body: pan });
const panBody = await panRes.json().catch(() => ({}));
record('A PAN in the wrong format → 400 under Number, saying the format', panRes.status === 400 && /A PAN is 10 characters/.test(panBody.details?.Number?.[0] ?? ''),
  `${panRes.status} ${JSON.stringify(panBody.details ?? panBody).slice(0, 200)}`);

// ---------------------------------------------------------------- Sprint 18 — attendance and timesheets
title('Sprint 18 — attendance with a geofence checked by the server, timesheets with approval');
await expectCall('Geofences (demo: Main gate, 300 m)', 'GET', `${P}/attendance/geofences`, 200, { token: T },
  b => (Array.isArray(b) && b.length > 0 && b.every(g => g.radiusM >= 20 && g.radiusM <= 2000)) || JSON.stringify(b).slice(0, 200));
const board = await expectCall('Today’s attendance board with counts', 'GET', `${P}/attendance`, 200, { token: T },
  b => (Array.isArray(b?.rows) && b.rows.every(r => /^E\d{5}$/.test(r.employeeCode) && r.state)) || JSON.stringify(b).slice(0, 200));
await expectCall('Receptionist can’t see the attendance board → 403', 'GET', `${P}/attendance`, 403, { token: R });
await expectCall('A geofence radius of 5 m → 400 under RadiusM (nothing saved)', 'POST', `${P}/attendance/geofences`, 400,
  { token: T, body: { name: 'API check', latitude: 15.5, longitude: 73.7, radiusM: 5, isActive: false } },
  b => /20–2000 metres/.test(b.details?.RadiusM?.[0] ?? '') || JSON.stringify(b.details));
await expectCall('A manager entry without a reason → 400 under Reason (nothing saved)', 'POST', `${P}/attendance/events`, 400,
  { token: T, body: { employeeId: board?.json?.rows?.[0]?.employeeId ?? NONE, action: 'CLOCK_IN', date: null, time: null, reason: '' } },
  b => (b.details?.Reason?.[0] ?? '').length > 0 || JSON.stringify(b.details));
const mondayOf = d => { const x = new Date(d + 'T00:00:00Z'); x.setUTCDate(x.getUTCDate() - (x.getUTCDay() + 6) % 7); return x.toISOString().slice(0, 10); };
const lastWeek = (() => { const x = new Date(mondayOf(today) + 'T00:00:00Z'); x.setUTCDate(x.getUTCDate() - 7); return x.toISOString().slice(0, 10); })();
await expectCall(`Last week’s timesheets (${lastWeek}) with totals and what the owner may do`, 'GET', `${P}/timesheets?week=${lastWeek}`, 200, { token: T },
  b => (b?.weekStart === lastWeek && Array.isArray(b.timesheets) && b.timesheets.every(t => Array.isArray(t.actions) && t.workedMinutes >= 0)) || JSON.stringify(b).slice(0, 200));
await expectCall('Receptionist can’t approve a week → 403', 'POST', `${P}/timesheets/approve-week`, 403,
  { token: R, body: { weekStart: lastWeek } });

// ---------------------------------------------------------------- summary
const failed = results.filter(r => !r.ok);
console.log(`\n${results.length - failed.length} passed, ${failed.length} failed, ${results.length} checks`);
for (const f of failed) console.log(`  FAILED: [${f.section.split(' —')[0]}] ${f.name}`);
process.exit(failed.length ? 1 : 0);
