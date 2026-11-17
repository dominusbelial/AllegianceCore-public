// Real Chromium → shared ClientWebSocket/ConnectionSession → P1048 listener.
const fs = require('node:fs'), path = require('node:path'), assert = require('node:assert/strict');
const modules = path.resolve(process.argv[2] || '.scratch/p1048b/npm', 'node_modules');
const { chromium: playwright } = require(path.join(modules,'playwright'));
let chromium;
const origin = 'http://localhost:8048', output = path.resolve('docs/reports/BROWSER_WS_JOIN_P1048B_assets');
const stateFile = path.resolve(process.argv[3] || '.scratch/p1048b/host/state.json');
const results = [];
async function main() {
  chromium = (await import(require('node:url').pathToFileURL(path.join(modules,'@sparticuz/chromium/build/index.js')).href)).default;
  fs.mkdirSync(output,{recursive:true});
  const launch = async () => playwright.launch({ executablePath:await chromium.executablePath(), headless:true,
    args:[...chromium.args.filter(a=>!['--disable-web-security','--allow-running-insecure-content'].includes(a)),
      '--proxy-server=http://127.0.0.1:9','--proxy-bypass-list=127.0.0.1;localhost'] });
  let browser, browserVersion;
  try {
    for (const style of ['Modern','Classic']) for (const [width,height] of [[1920,1080],[800,480]]) {
      if (results.length === 2) { console.log('Waiting for the unchanged identity rate-limit window'); await new Promise(r=>setTimeout(r,60000)); }
      browser = await launch(); browserVersion = browser.version();
      const context = await browser.newContext({viewport:{width,height},deviceScaleFactor:1});
      const page = await context.newPage(), errors=[], requests=[];
      page.setDefaultTimeout(15000);
      console.log(`Starting ${style} ${width}x${height}`);
      page.on('pageerror',e=>{ errors.push(e.message); console.error(e.message); });
      page.on('console',m=>{ if(m.type()==='error' && !m.text().includes('401')) errors.push(m.text()); });
      page.on('request',r=>{
        const url=new URL(r.url()); assert.ok(['127.0.0.1','localhost'].includes(url.hostname), 'Network must remain loopback');
        requests.push({path:url.pathname,at:Date.now()});
      });
      const cdp=await context.newCDPSession(page); await cdp.send('WebAuthn.enable');
      await cdp.send('WebAuthn.addVirtualAuthenticator',{options:{protocol:'ctap2',transport:'internal',hasResidentKey:true,
        hasUserVerification:true,isUserVerified:true,automaticPresenceSimulation:true}});
      // Register through the existing production passkey UI, then actually sign in.
      const name=`Browser${style[0]}${width}`;
      await page.goto(origin+'/identity.html'); await page.locator('#callsign').fill(name); await page.locator('#register').click();
      await page.locator('#account').waitFor({state:'visible'}); await page.locator('#signout').click();
      await page.locator('#login').waitFor({state:'visible'}); await page.locator('#signin').click();
      await page.locator('#account').waitFor({state:'visible'}); assert.equal(await page.locator('#pilot').textContent(),name);
      await page.goto(origin+'/play/preview/menu.html');
      await page.waitForFunction(()=>window.preview?.measurements.firstFrameMs != null,null,{timeout:60000});
      await page.getByRole('button',{name:style,exact:true}).click();
      await page.locator('#online-open').click();
      await page.locator('#online-panel[data-state="Servers"]').waitFor();
      await page.screenshot({path:path.join(output,`list-${style.toLowerCase()}-${width}x${height}.png`)});
      const joinStarted = Date.now();
      await page.locator('.online-join').first().click();
      await page.locator('#online-panel[data-state="Sides"]').waitFor({timeout:20000});
      const joinMilliseconds = Date.now() - joinStarted;
      await page.screenshot({path:path.join(output,`sides-${style.toLowerCase()}-${width}x${height}.png`)});
      await page.locator('.online-side').first().click();
      try { await page.locator('#online-panel[data-state="Hangar"]').waitFor(); } catch(e) { console.error(await page.locator('#online-panel').textContent(),errors); throw e; }
      const launchStarted = Date.now();
      await page.locator('#online-launch').click();
      await page.locator('#online-panel[data-state="Flight"]').waitFor({timeout:20000});
      const launchMilliseconds = Date.now() - launchStarted;
      await page.locator('#online-pose').filter({hasText:'bytes received'}).waitFor();
      const pose=await page.locator('#online-pose').textContent();
      const ownShip=Number(/^Ship (\d+)/.exec(pose)[1]);
      const state=JSON.parse(fs.readFileSync(stateFile,'utf8'));
      assert.ok(state.players.includes(name),'Server must confirm canonical player');
      assert.ok(state.ships.some(s=>s.Id===ownShip && s.SideId===0 && s.StationId==null),'Server must confirm launched ship');
      await page.screenshot({path:path.join(output,`flight-${style.toLowerCase()}-${width}x${height}.png`)});
      // Ticket request follows complete resident staging and the user join action.
      const ticket=requests.findIndex(r=>r.path==='/api/identity/join-token');
      const lastAsset=requests.findLastIndex(r=>r.path.includes('/packs/'));
      assert.ok(ticket>lastAsset && lastAsset>=0,'Ticket must follow asset staging');
      await page.locator('#online-close').click();
      await page.locator('#online-panel').waitFor({state:'detached'});
      assert.deepEqual(errors,[]);
      results.push({style,width,height,passed:true,passkeyLogin:true,canonicalPlayer:name,serverState:state,pose,joinMilliseconds,launchMilliseconds,residentBytes:await page.evaluate(()=>gameMenu.metrics().residentBytes),ticketAfterStaging:true,errors});
      await context.close(); await browser.close(); browser = null;
    }
    browser = await launch();
    const preview = await browser.newPage({viewport:{width:800,height:480}});
    await preview.goto(origin+'/play/preview/');
    await preview.waitForFunction(()=>window.preview?.measurements.firstFrameMs != null,null,{timeout:30000});
    assert.equal(await preview.getByRole('heading',{level:1}).textContent(),'Allegiance browser preview — not the game yet');
    await preview.getByRole('button',{name:'Rotate right'}).click();
    await preview.getByRole('button',{name:'Zoom in'}).click();
    await browser.close(); browser = null;
    const pngs=fs.readdirSync(output).filter(f=>f.endsWith('.png'));
    const pngBytes=pngs.reduce((n,f)=>n+fs.statSync(path.join(output,f)).size,0); assert.ok(pngBytes<=2*1024*1024);
    fs.writeFileSync(path.join(output,'browser-proof.json'),JSON.stringify({browserVersion,pngBytes,previewSmoke:true,results},null,2));
    console.log(JSON.stringify({passed:true,scenarios:results.length,pngBytes}));
  } finally { if (browser) await browser.close(); }
}
main().catch(e=>{console.error(e);process.exitCode=1;});
