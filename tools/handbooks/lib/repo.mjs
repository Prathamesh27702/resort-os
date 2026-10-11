// Reads facts from the repository so the handbooks never drift from the code:
// the sprint plan, roles, permissions, tables, endpoints, code size, tests and git state.
import { readFileSync, readdirSync, statSync, existsSync } from 'node:fs';
import { join, relative, extname } from 'node:path';
import { execSync } from 'node:child_process';
import YAML from 'yaml';

export function loadRepo(root) {
  const read = (p) => readFileSync(join(root, p), 'utf8');
  const plan = YAML.parse(read('docs/sprints/sprints.yml'));
  const history = YAML.parse(read('tools/handbooks/history.yml'));

  // Roles and permissions from the seed migration
  const v002 = read('database/migrations/V002__seed_permissions_roles.sql');
  const permRows = [...v002.matchAll(/^\('([a-z_]+\.[a-z_.]+)',\s*'([a-z_]+)',\s*'([^']*)',\s*(true|false)\)/gm)]
    .map(m => ({ code: m[1], module: m[2], description: m[3], sensitive: m[4] === 'true' }));
  const roleRows = [...v002.matchAll(/^\('([A-Z_]+)',\s*'([^']+)',\s*'([^']*)',\s*true\)/gm)]
    .map(m => ({ code: m[1], name: m[2], description: m[3] }));

  // Migrations and tables
  const migDir = join(root, 'database/migrations');
  const migrations = readdirSync(migDir).filter(f => f.endsWith('.sql')).sort().map(f => {
    const sql = readFileSync(join(migDir, f), 'utf8');
    const tables = [...sql.matchAll(/CREATE TABLE (?:IF NOT EXISTS )?([a-z_]+)/gi)].map(m => m[1]);
    return { file: f, tables, lines: sql.split('\n').length };
  });

  // API operations from the committed OpenAPI snapshot
  const openapi = JSON.parse(read('docs/api/openapi-v1.json'));
  const operations = Object.entries(openapi.paths).flatMap(([path, ops]) =>
    Object.entries(ops).map(([method, op]) => ({ method: method.toUpperCase(), path, tag: op.tags?.[0] ?? '', summary: op.summary ?? '' })));

  // Code size (lines) by area
  const walk = (dir, exts, skip = ['node_modules', 'bin', 'obj', 'dist', '.angular', 'e2e-report', 'e2e-results']) => {
    const out = [];
    if (!existsSync(dir)) return out;
    for (const name of readdirSync(dir)) {
      if (skip.includes(name)) continue;
      const full = join(dir, name);
      const st = statSync(full);
      if (st.isDirectory()) out.push(...walk(full, exts, skip));
      else if (exts.includes(extname(name))) out.push(full);
    }
    return out;
  };
  const measure = (files) => ({ files: files.length, lines: files.reduce((n, f) => n + readFileSync(f, 'utf8').split('\n').length, 0) });
  const backendSrc = walk(join(root, 'backend'), ['.cs']).filter(f => !f.includes('Resort.Tests'));
  const backendTests = walk(join(root, 'backend', 'Resort.Tests'), ['.cs']);
  const feAll = walk(join(root, 'frontend', 'src'), ['.ts', '.html', '.scss', '.css']);
  const feSpecs = feAll.filter(f => f.endsWith('.spec.ts'));
  const feSrc = feAll.filter(f => !f.endsWith('.spec.ts'));
  const e2e = walk(join(root, 'frontend', 'e2e'), ['.ts', '.mjs']);
  const sql = walk(join(root, 'database'), ['.sql']);
  const docs = walk(join(root, 'docs'), ['.md']);
  const code = {
    backend: measure(backendSrc), backendTests: measure(backendTests), frontend: measure(feSrc), frontendSpecs: measure(feSpecs),
    e2e: measure(e2e), sql: measure(sql), docs: measure(docs),
  };
  const count = (files, re) => files.reduce((n, f) => n + (readFileSync(f, 'utf8').match(re) ?? []).length, 0);
  const testMethods = {
    backend: count(backendTests, /\[(Fact|Theory)\b/g),
    frontend: count(feSpecs, /^\s*it\(/gm),
    e2e: count(e2e.filter(f => f.endsWith('.spec.ts')), /^test\(/gm),
  };

  // Hand-run checks: API check script and read-only SQL check files
  const apiScript = read('scripts/check-api.mjs');
  const apiChecks = (apiScript.match(/await expectCall\(/g) ?? []).length + (apiScript.match(/results\.push\(\{ section, name: '/g) ?? []).length;
  const sqlFiles = readdirSync(join(root, 'database', 'queries')).filter(f => f.endsWith('.sql')).sort();
  const sqlChecks = sqlFiles.map(f => ({ file: `database/queries/${f}`, count: (read(`database/queries/${f}`).match(/^-- \d+\./gm) ?? []).length }));

  // Backend projects
  const projects = readdirSync(join(root, 'backend')).filter(d => d.startsWith('Resort.'));

  // Package versions
  const pkgs = Object.fromEntries([...read('Directory.Packages.props').matchAll(/Include="([^"]+)"\s+Version="([^"]+)"/g)].map(m => [m[1], m[2]]));
  const fe = JSON.parse(read('frontend/package.json'));
  const feVer = (n) => (fe.dependencies?.[n] ?? fe.devDependencies?.[n] ?? '').replace(/^[\^~]/, '');
  const sdk = JSON.parse(read('global.json')).sdk.version;

  // CI jobs
  const ci = read('.github/workflows/ci.yml');
  const ciJobs = [...ci.matchAll(/^\s{4}name:\s*(.+)$/gm)].map(m => m[1].replace(/^["']|["']$/g, ''));

  let git = { sha: 'unknown', date: '', branch: '' };
  try {
    git = {
      sha: execSync('git rev-parse --short HEAD', { cwd: root }).toString().trim(),
      date: execSync('git log -1 --format=%cs', { cwd: root }).toString().trim(),
      branch: execSync('git rev-parse --abbrev-ref HEAD', { cwd: root }).toString().trim(),
    };
  } catch { /* not a git checkout */ }

  const sprints = plan.sprints;
  const done = sprints.filter(s => s.status === 'done');
  const lastDone = done.at(-1);
  const current = sprints.find(s => s.id === plan.current);

  return {
    root, plan, sprints, done, lastDone, current, history, permissions: permRows, roles: roleRows, migrations, operations,
    code, testMethods, apiChecks, sqlChecks, projects, pkgs, feVer, sdk, ciJobs, git,
    rel: (p) => relative(root, p),
    abs: (p) => join(root, p),
  };
}

export const two = (n) => String(n).padStart(2, '0');
export const PHASES = {
  0: 'Requirements and architecture',
  1: 'Foundation, property, rooms, beds',
  2: 'Guests, reservations, availability, calendar, front desk',
  3: 'Folio, payments, Razorpay, GST, invoices',
  4: 'Housekeeping and maintenance',
  5: 'Restaurant, kitchen, menu, inventory, purchasing',
  6: 'Employees, attendance, timesheets, leave, roster',
  7: 'Activities, transport, equipment',
  8: 'Expenses, budgets, reports',
  9: 'Notifications, accounting, OTA, booking website',
  10: 'Offline use, security hardening, go-live',
};
