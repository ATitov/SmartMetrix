const { test, before, after } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const http = require('node:http');
const { chromium } = require(process.env.SMARTMETRIX_PLAYWRIGHT || 'playwright');
const root = path.resolve(__dirname, '../../docs/gui-prototype');
const output = path.resolve(__dirname, '../../artifacts/gui');
const names = {operator:'Пульт измерения',geologist:'Анализ горного массива',surveyor:'Пространственная привязка',engineer:'Диагностика комплекса',administrator:'Управление системой'};
let server, browser, base;
async function newPage(options) {
  const page = await browser.newPage(options);
  // Serve exact repository bytes as a browser fixture, independent of HTTP content injection.
  await page.route(`${base}/**`, async route => {
    const pathname = new URL(route.request().url()).pathname;
    const file = {'/':'index.html','/index.html':'index.html','/style.css':'style.css','/app.js':'app.js'}[pathname];
    if (!file) { await route.fulfill({status:404,body:''}); return; }
    await route.fulfill({status:200,contentType:file.endsWith('.css')?'text/css':file.endsWith('.js')?'text/javascript':'text/html; charset=utf-8',body:await fs.readFile(path.join(root,file))});
  });
  return page;
}
before(async () => {
  await fs.mkdir(output,{recursive:true});
  server = http.createServer(async (req,res) => {
    const name = new URL(req.url,'http://localhost').pathname;
    const file = {'/':'index.html','/index.html':'index.html','/style.css':'style.css','/app.js':'app.js'}[name];
    if(!file){res.writeHead(404).end();return;}
    try { const data=await fs.readFile(path.join(root,file));res.setHeader('Content-Type',file.endsWith('.css')?'text/css':file.endsWith('.js')?'text/javascript':'text/html; charset=utf-8');res.end(data); }
    catch {res.writeHead(500).end();}
  });
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  base=`http://127.0.0.1:${server.address().port}`;
  browser=await chromium.launch({headless:true,...(process.env.SMARTMETRIX_BROWSER ? {executablePath:process.env.SMARTMETRIX_BROWSER}: {})});
});
after(async()=>{await browser?.close();if(server)await new Promise(resolve=>server.close(resolve));});

for(const [role,title] of Object.entries(names)) {
  test(`GUI-RWD-04 / ${role}: touch dialog and enlarged text`,async()=>{
    const page=await newPage({viewport:{width:390,height:844},isMobile:true,hasTouch:true});
    try {
      await page.goto(`${base}/#${role}`);
      await page.locator('#primary').tap();
      assert.ok(await page.locator('#command-dialog').isVisible());
      const box=await page.locator('#command-dialog').boundingBox();
      assert.ok(box.x>=0 && box.x+box.width<=390);
      await page.locator('#cancel').tap();
      await page.evaluate(()=>document.documentElement.style.fontSize='32px');
      assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'Enlarged text overflows');
      await page.screenshot({path:path.join(output,`${role}-text-200.png`),fullPage:true});
    } finally {await page.close();}
  });
  for(const width of [320,390,768,1024,1440]) {
    test(`GUI-RWD / ${role} at ${width}px: layout, navigation, labelled controls`,async()=>{
      const page=await newPage({viewport:{width,height:900}});
      try {
        const errors=[];const external=[];
        page.on('pageerror',error=>errors.push(error.message));
        page.on('request',request=>{if(!request.url().startsWith(base))external.push(request.url());});
        await page.goto(`${base}/#${role}`);
        assert.equal(await page.locator('h1').textContent(),title);
        assert.equal(await page.locator('.metrics .metric').count(),4);
        assert.equal(await page.locator('[aria-current=page]').getAttribute('href'),`#${role}`);
        assert.match(await page.locator('#source').textContent(),/Источники целевого АРМ/);
        assert.ok(await page.getByText('Демонстрационные данные',{exact:true}).isVisible());
        assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'Page overflows horizontally');
        const badTargets=await page.locator('button,select,input').evaluateAll(nodes=>nodes.filter(n=>n.getClientRects().length && (n.getBoundingClientRect().height<44 || n.getBoundingClientRect().width<44)).map(n=>n.outerHTML));
        assert.deepEqual(badTargets,[]);
        if(width<768){await page.locator('#menu').click();assert.equal(await page.locator('#menu').getAttribute('aria-expanded'),'true');assert.ok(await page.locator('nav').isVisible());await page.locator(`nav a[href="#${role==='operator'?'engineer':'operator'}"]`).click();assert.equal(await page.locator('#menu').getAttribute('aria-expanded'),'false');await page.goto(`${base}/#${role}`);}
        await page.screenshot({path:path.join(output,`${role}-${width}.png`),fullPage:true});
        assert.deepEqual(errors,[]);assert.deepEqual(external,[]);
      } finally {await page.close();}
    });
  }
  test(`GUI-STATE / ${role}: loading, empty, error, offline, partial and recovery`,async()=>{
    const page=await newPage();
    try {
      await page.goto(`${base}/#${role}`);
      for(const state of ['loading','empty','error','offline','partial']){
        await page.locator('#scenario').selectOption(state);
        assert.ok(await page.locator('#primary').isDisabled());
        assert.ok((await page.locator('#notice').textContent()).length>10);
        assert.equal(await page.locator('.metrics').count(),['loading','empty','error'].includes(state)?0:1);
        if(state==='partial')assert.ok(await page.locator('#content').getByText('Нет данных',{exact:true}).isVisible());
      }
      await page.locator('#scenario').selectOption('error');await page.getByRole('button',{name:'Повторить чтение'}).click();
      assert.ok(await page.locator('#primary').isEnabled());
    } finally {await page.close();}
  });
  test(`GUI-DS-06 / ${role}: dialog, reason validation, no production commands or XSS`,async()=>{
    const page=await newPage();
    try {
      const writes=[];page.on('request',request=>{if(request.method()!=='GET')writes.push(request.url());});
      await page.goto(`${base}/#${role}`);await page.locator('#primary').click();
      assert.ok(await page.locator('#command-dialog').isVisible());
      assert.equal(await page.evaluate(()=>document.activeElement.id),'reason');
      await page.keyboard.press('Escape');assert.ok(!(await page.locator('#command-dialog').isVisible()));
      assert.equal(await page.evaluate(()=>document.activeElement.id),'primary');
      await page.locator('#primary').click();await page.locator('#reason').fill('   ');
      await page.getByRole('button',{name:'Подтвердить в макете'}).click();assert.match(await page.locator('#validation').textContent(),/минимум 3/);
      const comment='<img src=x onerror=alert(1)> Проверено';
      await page.locator('#reason').fill(comment);await page.getByRole('button',{name:'Подтвердить в макете'}).click();
      assert.ok(!(await page.locator('#command-dialog').isVisible()));
      assert.match(await page.locator('#activity').textContent(),/Выполнено только в макете/);
      assert.ok((await page.locator('#activity').textContent()).includes(comment));assert.equal(await page.locator('#activity img').count(),0);
      assert.deepEqual(writes,[]);
    } finally {await page.close();}
  });
}
test('GUI-GEO-02: confirmation applies to v3; search and shared measurement card',async()=>{
  const page=await newPage();
  try {
    await page.goto(`${base}/#geologist`);await page.locator('#search').fill('not-found');assert.equal(await page.locator('.record').count(),0);
    await page.locator('#search').fill('M-0248');assert.equal(await page.locator('.record').count(),1);
    await page.locator('.open-detail').click();assert.match(await page.locator('#review-status').textContent(),/Ожидает/);await page.locator('#close-detail').click();
    await page.locator('#primary').click();await page.locator('#reason').fill('Проверены контуры и качество');await page.getByRole('button',{name:'Подтвердить в макете'}).click();
    await page.locator('.open-detail').click();assert.equal(await page.locator('#review-status').textContent(),'Подтверждено · v3');assert.match(await page.locator('#detail-dialog').textContent(),/В локальной очереди/);
  } finally {await page.close();}
});
test('GUI-DS-02: text tokens meet 4.5:1 on panel background',async()=>{
  const page=await newPage();
  try {
    await page.goto(base);
    const tokens=await page.evaluate(()=>{const s=getComputedStyle(document.documentElement);return Object.fromEntries(['panel','text','muted','accent','warn','danger','blue'].map(k=>[k,s.getPropertyValue(`--${k}`).trim()]));});
    const luminance=hex=>{const c=hex.slice(1).match(/../g).map(v=>parseInt(v,16)/255).map(v=>v<=.04045?v/12.92:((v+.055)/1.055)**2.4);return c[0]*.2126+c[1]*.7152+c[2]*.0722;};
    for(const [name,value] of Object.entries(tokens)){if(name==='panel')continue;const a=luminance(value),b=luminance(tokens.panel);assert.ok((Math.max(a,b)+.05)/(Math.min(a,b)+.05)>=4.5,`${name} contrast`);}
  } finally {await page.close();}
});
