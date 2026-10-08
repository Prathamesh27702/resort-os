// Starts a separate API for the browser tests:
//   1. drops and recreates the throwaway database `resort_e2e` (your normal database is not touched),
//   2. builds the API in Release (so it doesn't fight over files with your `dotnet watch` Debug build),
//   3. runs it on http://localhost:5181 with a one-off owner password.
// Playwright starts and stops this script itself (see playwright.config.ts → webServer).
import { spawn, spawnSync } from 'node:child_process';
import { join } from 'node:path';
import pg from 'pg';
import { API_PORT, E2E_DB, OWNER_EMAIL, adminPgConfig, e2eConnectionString, repoRoot } from './settings.mjs';

const password = process.env.E2E_OWNER_PASSWORD;
if (!password) throw new Error('E2E_OWNER_PASSWORD missing — start the tests with `npm run e2e`, not this script.');

const admin = new pg.Client(adminPgConfig());
await admin.connect();
await admin.query(`DROP DATABASE IF EXISTS ${E2E_DB} WITH (FORCE)`);
await admin.query(`CREATE DATABASE ${E2E_DB}`);
await admin.end();
console.log(`[e2e] fresh database ${E2E_DB} created`);

const apiDir = join(repoRoot, 'backend', 'Resort.Api');
const build = spawnSync('dotnet', ['build', '-c', 'Release', '--nologo', '-v', 'q'], { cwd: apiDir, stdio: 'inherit', shell: process.platform === 'win32' });
if (build.status !== 0) process.exit(build.status ?? 1);

const api = spawn('dotnet', [join('bin', 'Release', 'net10.0', 'Resort.Api.dll'), '--urls', `http://localhost:${API_PORT}`], {
  cwd: apiDir,
  stdio: 'inherit',
  env: {
    ...process.env,
    ASPNETCORE_ENVIRONMENT: 'Development',     // enables the first-run owner account and the demo rooms
    ConnectionStrings__Resort: e2eConnectionString(),
    Bootstrap__OwnerEmail: OWNER_EMAIL,
    Bootstrap__OwnerPassword: password,
    DevSeed__Enabled: 'true',
  },
});
const stop = () => { if (!api.killed) api.kill(); };
process.on('SIGINT', stop);
process.on('SIGTERM', stop);
process.on('exit', stop);
api.on('exit', code => process.exit(code ?? 0));
