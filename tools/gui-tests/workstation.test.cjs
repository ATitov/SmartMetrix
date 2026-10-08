const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require(process.env.SMARTMETRIX_PLAYWRIGHT || 'playwright');
const root = path.resolve(__dirname, '../../src/Services/SmartMetrix.ApiGateway/wwwroot');
const id = '10000000-0000-4000-8000-000000000001';

async function fixture(mode, accessible = true) {
  const browser = await chromium.launch({ headless:true, ...(process.env.SMARTMETRIX_BROWSER ? { executablePath:process.env.SMARTMETRIX_BROWSER } : {}) });
  const page = await browser.newPage();
  const requests = [], errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.route('http://127.0.0.1:4189/**', async route => {
    const request = route.request(), url = new URL(request.url());
    const pathname = url.pathname;
    if (!pathname.startsWith('/api/')) {
      const file = pathname.endsWith('/') ? pathname + 'index.html' : pathname;
      await route.fulfill({ status:200, contentType:file.endsWith('.js') ? 'text/javascript' : file.endsWith('.css') ? 'text/css' : 'text/html; charset=utf-8', body:await fs.readFile(path.join(root, file)) });
      return;
    }
    const body = request.postData() ? JSON.parse(request.postData()) : null;
    requests.push({ pathname, method:request.method(), body, csrf:request.headers()['x-csrf-token'] });
    let result;
    if (pathname.startsWith('/api/operator')) { await route.fulfill({ status:410, body:'UseScopedWorkstationApi' }); return; }
    if (pathname === '/api/auth/me') result = { displayName:'Тест', role:mode, roles:[mode] };
    else if (pathname === '/api/auth/csrf') result = { token:'csrf-test' };
    else if (pathname === '/api/v1/workstations') result = { scoped:true, scopes:accessible ? [
      { id:'north', siteId:'Север', excavatorId:'EX-N', coordinateSystemId:'quarry-n' },
      { id:'south', siteId:'Юг', excavatorId:'EX-S', coordinateSystemId:'quarry-s' }] : [] };
    else if (pathname.endsWith('/operator/status')) result = { state:'Ready', checks:[{ name:'camera', state:'Ready' }] };
    else if (pathname.endsWith('/measurements') && request.method() === 'GET') result = { items:[{ id, excavatorId:'EX-N', status:mode === 'engineer' ? 'Failed' : 'Completed', version:2 }] };
    else if (pathname.endsWith('/measurements') && request.method() === 'POST') result = { id:body.measurementId, status:'Capturing' };
    else if (pathname.endsWith('/retry')) result = { id, status:'Capturing' };
    else if (pathname.endsWith('/audit') || pathname.includes('/logs')) result = [];
    else if (pathname.endsWith('/config')) result = { quality:{ revision:'initial', values:{ 'Metrics:ShadowThreshold':'38' } } };
    else if (pathname.endsWith('/config/quality')) result = { revision:'applied', values:body.values };
    else if (pathname.endsWith('/system')) result = { drives:[], processMemoryBytes:0, processors:1, runtime:'test' };
    else { await route.fulfill({ status:404, body:pathname }); return; }
    await route.fulfill({ status:200, contentType:'application/json', body:JSON.stringify(result) });
  });
  await page.goto(`http://127.0.0.1:4189/${mode}/`);
  if (accessible) await page.waitForFunction(() => document.querySelector('#resultCount').textContent.includes('Показано'));
  else await page.waitForFunction(() => document.querySelector('#toast').textContent.includes('нет доступных областей'));
  return { browser, page, requests, errors };
}

test('Scoped operator starts an idempotent measurement and switches scope', async () => {
  const f = await fixture('operator');
  try {
    await f.page.locator('#start').click();
    assert.equal(await f.page.locator('[name=excavatorId]').inputValue(), 'EX-N');
    await f.page.locator('#startForm button[type=submit]').click();
    await f.page.waitForFunction(() => document.querySelector('#toast').textContent.includes('Измерение запущено'));
    const start = f.requests.find(r => r.pathname === '/api/v1/scopes/north/measurements' && r.method === 'POST');
    assert.match(start.body.measurementId, /^[0-9a-f-]{36}$/);
    assert.match(start.body.commandId, /^[0-9a-f-]{36}$/);
    assert.equal(start.csrf, 'csrf-test');
    await f.page.locator('#scopeSelector').selectOption('south');
    await f.page.waitForFunction(() => !document.querySelector('#scopeSelector').disabled);
    assert.ok(f.requests.some(r => r.pathname === '/api/v1/scopes/south/measurements'));
    assert.ok(!f.requests.some(r => r.pathname.startsWith('/api/operator')));
    assert.deepEqual(f.errors, []);
  } finally { await f.browser.close(); }
});

test('Scoped engineer retries with confirmation and applies service settings', async () => {
  const f = await fixture('engineer');
  try {
    await f.page.locator('[data-retry]').click();
    await f.page.locator('#commandForm [name=reason]').fill('Повтор после проверки');
    await f.page.locator('#commandForm button[type=submit]').click();
    await f.page.waitForFunction(() => document.querySelector('#toast').textContent.includes('Команда выполнена'));
    const retry = f.requests.find(r => r.pathname.endsWith(`/engineer/measurements/${id}/retry`));
    assert.equal(retry.body.confirmed, true); assert.equal(retry.body.expectedVersion, 2);
    await f.page.locator('[data-config]').fill('40');
    await f.page.locator('#saveConfig').click();
    await f.page.waitForFunction(() => document.querySelector('#toast').textContent.includes('Настройки применены'));
    const update = f.requests.find(r => r.pathname.endsWith('/engineer/config/quality') && r.method === 'PUT');
    assert.equal(update.body.expectedRevision, 'initial');
    assert.equal(update.body.values['Metrics:ShadowThreshold'], '40');
    assert.ok(!f.requests.some(r => r.pathname.startsWith('/api/operator')));
    assert.deepEqual(f.errors, []);
  } finally { await f.browser.close(); }
});


test('Account without scopes shows denial and sends no legacy measurement requests', async () => {
  const f = await fixture('operator', false);
  try {
    await f.page.locator('#refresh').click();
    await f.page.waitForFunction(() => !document.querySelector('#refresh').disabled);
    assert.ok(!f.requests.some(r => r.pathname.startsWith('/api/operator') || r.pathname.includes('/scopes/')));
    assert.deepEqual(f.errors, []);
  } finally { await f.browser.close(); }
});

test('Engineer cannot switch scope while settings are being applied', async () => {
  const f = await fixture('engineer');
  let release;
  const pending = new Promise(resolve => { release = resolve; });
  try {
    await f.page.route('**/engineer/config/quality', async route => {
      await pending;
      await route.fulfill({ status:409, contentType:'application/json', body:JSON.stringify({ error:'RevisionConflict' }) });
    });
    await f.page.locator('[data-config]').fill('40');
    await f.page.locator('#saveConfig').click();
    await f.page.waitForFunction(() => document.querySelector('#saveConfig').disabled);
    assert.equal(await f.page.locator('#scopeSelector').isDisabled(), true);
    release();
    await f.page.waitForFunction(() => !document.querySelector('#saveConfig').disabled);
    assert.equal(await f.page.locator('#scopeSelector').isDisabled(), false);
    assert.equal(await f.page.locator('#scopeSelector').inputValue(), 'north');
    assert.ok(!(await f.page.locator('#toast').textContent()).includes('Настройки применены'));
    assert.deepEqual(f.errors, []);
  } finally { release(); await f.browser.close(); }
});

test('Missing configuration never renders healthy diagnostics or enables capture', async () => {
  const f = await fixture('operator');
  try {
    await f.page.route('**/operator/status', route => route.fulfill({status:200,contentType:'application/json',body:JSON.stringify({state:'Degraded',triggerConditionsChecked:false,checks:[{name:'camera',state:'NotConfigured',checkedAt:'2026-09-30T12:00:00Z'}]})}));
    await f.page.locator('#refresh').click();
    await f.page.waitForFunction(()=>!document.querySelector('#refresh').disabled);
    assert.ok((await f.page.locator('#systemState').textContent()).includes('Требуется настройка'));
    assert.ok(!(await f.page.locator('#components').textContent()).includes('Диагностика без замечаний'));
    assert.ok(await f.page.locator('#start').isDisabled());
    assert.equal(await f.page.locator('#activeMeasurement').textContent(),'Не предоставлено API');
    assert.deepEqual(f.errors,[]);
  } finally { await f.browser.close(); }
});

test('Test measurements are visibly marked and stale data cannot start a capture', async () => {
  const f = await fixture('operator');
  try {
    await f.page.route('**/measurements', route=>route.fulfill({status:200,contentType:'application/json',body:JSON.stringify({items:[{id,excavatorId:'EX-N',status:'Completed',version:3,isTestData:true}]})}));
    await f.page.locator('#refresh').click();
    await f.page.waitForFunction(()=>!document.querySelector('#refresh').disabled);
    assert.ok((await f.page.locator('#measurements').textContent()).includes('Тестовые данные'));
    await f.page.route('**/operator/status', route=>route.fulfill({status:503,body:'Service unavailable'}));
    await f.page.locator('#refresh').click();
    await f.page.waitForFunction(()=>!document.querySelector('#refresh').disabled);
    assert.ok(await f.page.locator('#start').isDisabled());
    assert.ok((await f.page.locator('#freshness').textContent()).includes('Нет актуальных данных'));
    assert.deepEqual(f.errors,[]);
  } finally { await f.browser.close(); }
});
