#!/usr/bin/env node
// API checks for Sprints 01–03: calls the running API like a client would and prints PASS/FAIL per check.
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
    return { status: res.status, json };
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

// ---------------------------------------------------------------- summary
const failed = results.filter(r => !r.ok);
console.log(`\n${results.length - failed.length} passed, ${failed.length} failed, ${results.length} checks`);
for (const f of failed) console.log(`  FAILED: [${f.section.split(' —')[0]}] ${f.name}`);
process.exit(failed.length ? 1 : 0);
