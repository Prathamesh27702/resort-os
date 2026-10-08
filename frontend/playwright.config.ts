import { defineConfig, devices } from '@playwright/test';
import { randomBytes } from 'node:crypto';
import { API_PORT, WEB_PORT } from './e2e/settings.mjs';

// Browser tests for the staff app: they click through the screens like a person would.
// How to run and watch them: docs/testing/VERIFYING.md → "Browser tests".
//
// A one-off owner password for the throwaway e2e database. Set here (in the runner) so the API launcher
// and the test workers all see the same value. It is never written to disk.
process.env['E2E_OWNER_PASSWORD'] ??= `E2e-${randomBytes(9).toString('base64url')}1`;

export default defineConfig({
  testDir: './e2e',
  // Steps inside a file depend on each other (sign in → create → check), so run in order, one browser at a time.
  fullyParallel: false,
  workers: 1,
  timeout: 4 * 60_000,              // sign-in is rate limited to 10 a minute; helpers wait out the limit if hit
  expect: { timeout: 10_000 },
  retries: 0,
  forbidOnly: !!process.env['CI'],
  reporter: [['list'], ['html', { open: 'never', outputFolder: 'e2e-report' }]],
  outputDir: 'e2e-results',
  use: {
    baseURL: `http://localhost:${WEB_PORT}`,
    trace: 'retain-on-failure',      // open with `npm run e2e:report` to replay a failure step by step
    screenshot: 'on',
    video: 'retain-on-failure',
    ...devices['Desktop Chrome'],
    viewport: { width: 1280, height: 860 },
    launchOptions: process.env['PW_CHROMIUM_PATH'] ? { executablePath: process.env['PW_CHROMIUM_PATH'] } : {},
  },
  webServer: [
    {
      command: 'node e2e/start-api.mjs',
      url: `http://localhost:${API_PORT}/health/ready`,
      timeout: 5 * 60_000,           // first run builds the API in Release
      reuseExistingServer: false,
      stdout: 'ignore',
      stderr: 'pipe',
    },
    {
      command: `npx ng serve --port ${WEB_PORT} --proxy-config proxy.e2e.conf.json`,
      url: `http://localhost:${WEB_PORT}`,
      timeout: 3 * 60_000,
      reuseExistingServer: false,
      stdout: 'ignore',
      stderr: 'pipe',
    },
  ],
});
