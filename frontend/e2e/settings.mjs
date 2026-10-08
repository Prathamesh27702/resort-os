// Shared settings for the browser (end-to-end) tests. Plain JS so both Playwright and the API launcher can use it.
//
// The tests never touch your normal database. They start their own API on port 5181 against a throwaway
// database `resort_e2e` (recreated on every run) and their own web app on port 4301.
import { readFileSync, existsSync } from 'node:fs';
import { join, resolve } from 'node:path';

// Playwright and the API launcher both run from the frontend/ folder (npm scripts, webServer cwd).
export const repoRoot = resolve(process.cwd(), '..');

/** Values from the repo-root .env (written by scripts/setup-dev). Real environment variables win, as in the API. */
export function envValue(key) {
  if (process.env[key]) return process.env[key];
  const file = join(repoRoot, '.env');
  if (!existsSync(file)) return undefined;
  for (const line of readFileSync(file, 'utf8').split(/\r?\n/)) {
    const m = /^\s*([A-Za-z0-9_]+)\s*=\s*(.*)\s*$/.exec(line);
    if (m && m[1] === key && m[2] !== '') return m[2].replace(/^"(.*)"$/, '$1');
  }
  return undefined;
}

export const API_PORT = 5181;
export const WEB_PORT = 4301;
export const E2E_DB = 'resort_e2e';
export const OWNER_EMAIL = 'owner@e2e.local';

/** Npgsql connection string for the throwaway e2e database, built from RESORT_TEST_DB (same server, other name). */
export function e2eConnectionString() {
  const base = envValue('RESORT_TEST_DB');
  if (!base) throw new Error('RESORT_TEST_DB is not set. Run scripts/setup-dev first (see docs/GETTING_STARTED.md Step 5).');
  const parts = base.split(';').filter(Boolean).filter(p => !/^\s*database\s*=/i.test(p));
  return [...parts, `Database=${E2E_DB}`].join(';');
}

/** The same server as a node-postgres config, connected to the `postgres` maintenance database. */
export function adminPgConfig() {
  const kv = Object.fromEntries(e2eConnectionString().split(';').filter(Boolean).map(p => {
    const i = p.indexOf('=');
    return [p.slice(0, i).trim().toLowerCase(), p.slice(i + 1).trim()];
  }));
  return {
    host: kv.host ?? 'localhost', port: Number(kv.port ?? 5432),
    user: kv.username ?? kv['user id'] ?? kv.user, password: kv.password, database: 'postgres',
  };
}
