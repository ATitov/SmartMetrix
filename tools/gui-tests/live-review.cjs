// Live smoke test: no route interception, fixtures or synthetic measurements.
const { chromium } = require(process.env.SMARTMETRIX_PLAYWRIGHT || 'playwright');
const fs = require('node:fs/promises');
const path = require('node:path');
const assert = require('node:assert/strict');
(async () => {
  const root=path.resolve(__dirname,'../..');
  const credentials=JSON.parse(await fs.readFile(path.join(root,'data/workspace-run/credentials.json'),'utf8'));
  const output=path.join(root,'artifacts/live-review');await fs.mkdir(output,{recursive:true});
  const browser=await chromium.launch({headless:true,...(process.env.SMARTMETRIX_BROWSER?{executablePath:process.env.SMARTMETRIX_BROWSER}:{})});
  try {
    const context=await browser.newContext();const page=await context.newPage();const errors=[];
    page.on('pageerror',error=>errors.push(error.message));
    await page.goto('http://127.0.0.1:5190/login/');
    await page.locator('[name=username]').fill('engineer');
    await page.locator('[name=password]').fill(credentials.engineerPassword);
    await page.getByRole('button',{name:'Войти',exact:true}).click();
    await page.waitForURL('**/engineer/');
    const status=await (await context.request.get('http://127.0.0.1:5190/api/v1/scopes/local/operator/status')).json();
    const measurements=await (await context.request.get('http://127.0.0.1:5190/api/v1/scopes/local/measurements')).json();
    assert.ok(Array.isArray(measurements.items));
    assert.equal(measurements.items.length,0,'Fresh workspace launch must not seed measurements');
    const segmentation=await (await context.request.get('http://127.0.0.1:5190/api/v1/scopes/local/engineer/segmentation/capabilities')).json();
    assert.equal(segmentation.backend,'StoneVision');assert.equal(segmentation.isTestData,false);
    const layouts=[];
    for(const mode of ['operator','engineer']) for(const width of [390,768,1440]) {
      await page.setViewportSize({width,height:900});await page.goto(`http://127.0.0.1:5190/${mode}/`);
      await page.waitForFunction(()=>document.querySelector('#resultCount').textContent.includes('Показано'));
      const disabled=mode==='operator'?await page.locator('#start').isDisabled():null;
      if(mode==='operator') assert.ok(disabled,'Capture must stay disabled with unavailable hardware');
      const overflow=await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth);
      layouts.push({mode,width,overflow,captureDisabled:disabled});
      await page.screenshot({path:path.join(output,`${mode}-${width}.png`),fullPage:true});
    }
    const report={checkedAt:new Date().toISOString(),url:'http://127.0.0.1:5190/',measurementCount:measurements.items.length,status,segmentation,layouts,errors};
    await fs.writeFile(path.join(output,'summary.json'),JSON.stringify(report,null,2));
    assert.deepEqual(errors,[]);assert.ok(layouts.every(x=>!x.overflow),'Responsive page overflows');
    console.log(JSON.stringify({measurementCount:0,state:status.state,checks:status.checks.map(x=>({name:x.name,state:x.state})),layouts,errors},null,2));
  } finally {await browser.close();}
})().catch(error=>{console.error(error.message);process.exitCode=1;});
