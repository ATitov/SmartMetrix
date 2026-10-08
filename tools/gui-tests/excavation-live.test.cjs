// Optional smoke test against the stand started by start-excavation-web.ps1.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require(process.env.SMARTMETRIX_PLAYWRIGHT || 'playwright');

test('Live excavation stand: sign-in, replay report, phases and CSV', async () => {
  const workspace = path.resolve(__dirname, '../..');
  const credentials = JSON.parse((await fs.readFile(path.join(workspace, 'artifacts/excavation-web/credentials.local.json'), 'utf8')).replace(/^\uFEFF/, ''));
  const browser = await chromium.launch({ headless: true, ...(process.env.SMARTMETRIX_BROWSER ? { executablePath: process.env.SMARTMETRIX_BROWSER } : {}) });
  try {
    const page = await browser.newPage({ viewport: { width: 1440, height: 1100 }, acceptDownloads: true });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(`http://127.0.0.1:${credentials.port}/excavation/`);
    await page.getByLabel('Логин', { exact: true }).fill(credentials.username);
    await page.getByLabel('Пароль', { exact: true }).fill(credentials.password);
    await page.getByRole('button', { name: 'Войти', exact: true }).click();
    await page.locator('#report').waitFor({ state: 'visible' });
    assert.equal(await page.locator('#completed').innerText(), '2');
    assert.equal(await page.locator('#coverage').innerText(), '92,5%');
    assert.match(await page.locator('#dataMode').innerText(), /СИНТЕТИЧЕСКИЕ/);
    assert.equal(await page.locator('#cycles [data-cycle]').count(), 3);
    await page.locator('#cycles [data-cycle]').first().click();
    assert.match(await page.locator('#cycleDetails').innerText(), /Копание/);
    await page.locator('#closeCycle').click();
    const downloadPromise = page.waitForEvent('download');
    await page.locator('#exportCsv').click();
    const download = await downloadPromise;
    const csv = await fs.readFile(await download.path(), 'utf8');
    assert.match(csv, /completedCycles/i);
    assert.match(csv, /demo-excavator/);
    const output = path.join(workspace, 'artifacts/gui');
    await fs.mkdir(output, { recursive: true });
    await page.screenshot({ path: path.join(output, 'excavation-live-desktop.png'), fullPage: true });
    await page.setViewportSize({ width: 390, height: 844 });
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
    await page.screenshot({ path: path.join(output, 'excavation-live-mobile.png'), fullPage: true });
    assert.deepEqual(errors, []);
  } finally { await browser.close(); }
});
