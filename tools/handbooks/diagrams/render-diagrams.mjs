// Renders every <svg id="…"> in diagrams.html to ../assets/<id>.png (2× resolution).
// Uses the Playwright that the frontend already installs:  cd frontend && npm ci && npm run e2e:install
// Run from tools/handbooks:  node diagrams/render-diagrams.mjs
import { createRequire } from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { dirname, join, resolve } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const repo = resolve(here, '..', '..', '..');
const require = createRequire(join(repo, 'frontend', 'package.json'));
const { chromium } = require('@playwright/test');

const browser = await chromium.launch(process.env.PW_CHROMIUM_PATH ? { executablePath: process.env.PW_CHROMIUM_PATH } : {});
const page = await browser.newPage({ deviceScaleFactor: 2, viewport: { width: 1280, height: 900 } });
await page.goto(pathToFileURL(join(here, 'diagrams.html')).href);
await page.evaluate(() => document.fonts.ready);
const ids = await page.$$eval('svg[id]', els => els.map(e => e.id));
for (const id of ids) {
  await page.locator(`#${id}`).screenshot({ path: join(here, '..', 'assets', `${id}.png`) });
  console.log(`assets/${id}.png`);
}
await browser.close();
