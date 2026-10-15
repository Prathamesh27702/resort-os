#!/usr/bin/env node
// API checks for Sprints 01–04: calls the running API like a client would and prints PASS/FAIL per check.
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
const short = path => path.replace(/\/properties\/[0-9a-f-]{36}/, '/properties/{propertyId}').replace(/[0-9a-f]{8}-[0-9a-f-]{27}/g, '{id}');
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

// ---------------------------------------------------------------- summary
const failed = results.filter(r => !r.ok);
console.log(`\n${results.length - failed.length} passed, ${failed.length} failed, ${results.length} checks`);
for (const f of failed) console.log(`  FAILED: [${f.section.split(' —')[0]}] ${f.name}`);
process.exit(failed.length ? 1 : 0);
