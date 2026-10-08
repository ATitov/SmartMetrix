const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require(process.env.SMARTMETRIX_PLAYWRIGHT || 'playwright');
const root = path.resolve(__dirname, '../../src/Services/SmartMetrix.ApiGateway/wwwroot');
const runId = 'a'.repeat(64);
const report = {
  excavatorId: 'EX-N', sourceId: 'fixture', clockId: 'clock', algorithmVersion: 'signal-rules-v1', isSynthetic: true,
  start: '2026-10-04T08:00:00+06:00', end: '2026-10-04T08:02:00+06:00',
  completedCycles: 2, interruptedCycles: 1, incompleteCycles: 0, coverageFraction: .925,
  phaseSeconds: { Unknown: 9, Waiting: 15, Digging: 36, LoadedSwing: 21, Unloading: 14, Returning: 25 },
  timeline: [
    { start: '2026-10-04T02:00:00Z', end: '2026-10-04T02:00:36Z', phase: 'Digging', reason: 'tool-engaged' },
    { start: '2026-10-04T02:00:36Z', end: '2026-10-04T02:00:45Z', phase: 'Unknown', reason: 'telemetry-gap' },
    { start: '2026-10-04T02:00:45Z', end: '2026-10-04T02:02:00Z', phase: 'Waiting', reason: 'idle-reason-unknown' }
  ],
  cycles: [
    { cycleId: 'cycle-complete', start: '2026-10-04T02:00:04Z', end: '2026-10-04T02:00:45Z', status: 'Completed', reason: 'ordered-phases', phases: [{ start: '2026-10-04T02:00:04Z', end: '2026-10-04T02:00:20Z', phase: 'Digging', reason: 'tool-engaged' }] },
    { cycleId: 'cycle-interrupted', start: '2026-10-04T02:00:50Z', end: '2026-10-04T02:01:01Z', status: 'Interrupted', reason: 'telemetry-gap', phases: [] }
  ]
};
async function fixture(options = {}) {
  const browser = await chromium.launch({ headless: true, ...(process.env.SMARTMETRIX_BROWSER ? { executablePath: process.env.SMARTMETRIX_BROWSER } : {}) });
  const page = await browser.newPage({ viewport: { width: options.width || 1440, height: 1000 }, timezoneId: 'America/New_York', acceptDownloads: true });
  const errors = [], requests = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.route('http://127.0.0.1:4189/**', async route => {
    const pathname = new URL(route.request().url()).pathname;
    requests.push(pathname);
    if (!pathname.startsWith('/api/')) {
      const file = pathname.endsWith('/') ? pathname + 'index.html' : pathname;
      await route.fulfill({ status: 200, contentType: file.endsWith('.js') ? 'text/javascript' : file.endsWith('.css') ? 'text/css' : 'text/html; charset=utf-8', body: await fs.readFile(path.join(root, file)) });
      return;
    }
    let result;
    if (pathname === '/api/auth/me') result = { username: 'tester', displayName: '<img src=x onerror=alert(1)>', role: 'operator' };
    else if (pathname === '/api/v1/workstations') result = { scopes: options.noScopes ? [] : [{ id: 'north', siteId: 'Север', excavatorId: 'EX-N' }, { id: 'south', siteId: 'Юг', excavatorId: 'EX-S' }] };
    else if (pathname.endsWith('/excavation/runs')) result = pathname.includes('/south/') ? { state: 'NotConfigured', items: [] } : { state: options.empty ? 'NotConfigured' : 'Ready', invalidCount: options.corrupt ? 1 : 0, items: options.empty ? [] : [{ runId, ...report }] };
    else if (pathname.endsWith('/report')) {
      if (options.failReport) { await route.fulfill({ status: 409, body: 'ReplayArtifactsInvalid' }); return; }
      result = { runId, inputSha256: 'b'.repeat(64), report };
    } else if (pathname.endsWith('/csv')) {
      await route.fulfill({ status: 200, contentType: 'text/csv', headers: { 'Content-Disposition': 'attachment; filename="shift.csv"' }, body: 'isSynthetic,completedCycles\ntrue,2\n' }); return;
    } else { await route.fulfill({ status: 404, body: pathname }); return; }
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(result) });
  });
  await page.goto('http://127.0.0.1:4189/excavation/');
  await page.waitForFunction(() => document.getElementById('loading').hidden);
  return { browser, page, errors, requests };
}
test('Excavation screen shows synthetic mode, fixed shift timezone, cycle filter and phases', async () => {
  const f = await fixture();
  try {
    assert.match(await f.page.locator('#dataMode').innerText(), /СИНТЕТИЧЕСКИЕ/);
    assert.equal(await f.page.locator('#completed').innerText(), '2');
    assert.equal(await f.page.locator('#coverage').innerText(), '92,5%');
    assert.match(await f.page.locator('#shiftBounds').innerText(), /08:00:00/);
    assert.match(await f.page.locator('#shiftBounds').innerText(), /UTC\+06:00/);
    assert.equal(await f.page.locator('#currentUser img').count(), 0);
    await f.page.locator('#cycleFilter').selectOption('Interrupted');
    assert.equal(await f.page.locator('#cycles [data-cycle]').count(), 1);
    assert.match(await f.page.locator('#cycles').innerText(), /Обрыв телеметрии/);
    await f.page.locator('#cycleFilter').selectOption('Completed');
    await f.page.locator('#cycles [data-cycle]').click();
    assert.equal(await f.page.locator('#cycleDialog').isVisible(), true);
    assert.match(await f.page.locator('#cycleDetails').innerText(), /Копание/);
    await f.page.locator('#closeCycle').click();
    const downloadPromise = f.page.waitForEvent('download');
    await f.page.locator('#exportCsv').click();
    const download = await downloadPromise;
    assert.match(download.suggestedFilename(), /^shift-/);
    await fs.mkdir(path.resolve(__dirname, '../../artifacts/gui'), { recursive: true });
    await f.page.screenshot({ path: path.resolve(__dirname, '../../artifacts/gui/excavation-desktop.png'), fullPage: true });
    assert.deepEqual(f.errors, []);
  } finally { await f.browser.close(); }
});
test('Changing scope clears old report and disables export', async () => {
  const f = await fixture();
  try {
    await f.page.locator('#scopeSelector').selectOption('south');
    await f.page.waitForFunction(() => document.getElementById('empty').hidden === false);
    assert.equal(await f.page.locator('#report').isVisible(), false);
    assert.equal(await f.page.locator('#exportCsv').isDisabled(), true);
    assert.ok(f.requests.some(x => x.includes('/south/excavation/runs')));
    assert.deepEqual(f.errors, []);
  } finally { await f.browser.close(); }
});
for (const options of [{ empty: true }, { noScopes: true }, { failReport: true }]) {
  test(`Missing or invalid report has no stale metrics: ${JSON.stringify(options)}`, async () => {
    const f = await fixture(options);
    try {
      assert.equal(await f.page.locator('#report').isVisible(), false);
      assert.equal(await f.page.locator('#exportCsv').isDisabled(), true);
      if (options.noScopes) assert.ok(!f.requests.some(x => x.includes('/excavation/runs')));
      assert.deepEqual(f.errors, []);
    } finally { await f.browser.close(); }
  });
}
test('Mobile excavation report fits viewport and flags corrupt runs', async () => {
  const f = await fixture({ width: 390, corrupt: true });
  try {
    assert.match(await f.page.locator('#storageWarning').innerText(), /Повреждённых отчётов: 1/);
    const overflow = await f.page.evaluate(() => document.documentElement.scrollWidth > innerWidth);
    assert.equal(overflow, false);
    assert.equal(await f.page.locator('#cycles td:nth-child(3)').first().isVisible(), true);
    await fs.mkdir(path.resolve(__dirname, '../../artifacts/gui'), { recursive: true });
    await f.page.screenshot({ path: path.resolve(__dirname, '../../artifacts/gui/excavation-mobile.png'), fullPage: true });
    assert.deepEqual(f.errors, []);
  } finally { await f.browser.close(); }
});
