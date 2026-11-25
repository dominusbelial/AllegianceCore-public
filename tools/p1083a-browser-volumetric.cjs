// NPM_DIRECTORY HOST_STATE OUTPUT GENERATION [--hardware-gl EXECUTABLE]
// Fresh P1051 proof host: --bots 0. Default timing pins presentation scale;
// --adaptive records normal player pacing instead. Installed Playwright only. Software by
// default; --hardware-gl uses a visible Chromium with no SwiftShader flags.
const fs = require('node:fs'), path = require('node:path'), assert = require('node:assert/strict');
const [npm, stateDir, out, generation] = process.argv.slice(2, 6).map(x => path.resolve(x));
const fixedScale = !process.argv.includes('--adaptive');
const fallbacks = process.argv.includes('--fallbacks-only');
const hardwareIndex = process.argv.indexOf('--hardware-gl');
const hardware = hardwareIndex >= 0 ? process.argv[hardwareIndex + 1] : null;
if (hardwareIndex >= 0) assert.ok(hardware && !hardware.startsWith('--'), '--hardware-gl requires a Chromium executable');
const origin = JSON.parse(fs.readFileSync(path.join(stateDir, 'state.json'))).origin || 'https://localhost:8443';
const {chromium: playwright} = require(path.join(npm, 'node_modules/playwright'));
const wait = ms => new Promise(resolve => setTimeout(resolve, ms));
async function until(test, label, timeout = 120000) {
  const end = Date.now() + timeout;
  while (Date.now() < end) { if (await test()) return; await wait(50); }
  throw Error('Timed out: ' + label);
}
async function fixture(ship, count) {
  const response = path.join(stateDir, 'combat-response.json'); fs.rmSync(response, {force:true});
  fs.writeFileSync(path.join(stateDir, 'combat-request.tmp'), JSON.stringify({ship, action:'volumetric', count}));
  fs.renameSync(path.join(stateDir, 'combat-request.tmp'), path.join(stateDir, 'combat-request.json'));
  await until(() => fs.existsSync(response), 'controlled explosion burst', 10000);
  const receipt = JSON.parse(fs.readFileSync(response)); assert.equal(receipt.details.count, count); return receipt;
}
const percentile = (values, p) => [...values].sort((a,b) => a-b)[Math.min(values.length-1, Math.floor(values.length*p))];
async function main() {
  fs.mkdirSync(out, {recursive:true});
  const manifest = JSON.parse(fs.readFileSync(path.join(generation, 'battle-manifest.json')));
  const effect = manifest.assets.find(a => a.source === 'content/shaders/volumetric.xnb');
  assert.ok(effect, 'verified effect declared');
  assert.equal(JSON.parse(fs.readFileSync(path.join(stateDir, 'state.json'))).battle.size, 0, 'isolated zero-bot cost fixture');
  const launch = hardware ? {executablePath:path.resolve(hardware), headless:false, args:[]} : await (async () => {
    const chromium = (await import(require('node:url').pathToFileURL(path.join(npm, 'node_modules/@sparticuz/chromium/build/index.js')).href)).default;
    return {executablePath:await chromium.executablePath(), headless:true,
      args:chromium.args.filter(a => !['--disable-web-security','--allow-running-insecure-content','--single-process'].includes(a))};
  })();
  launch.args.push('--ignore-certificate-errors');
  const evidence = {presentationPolicy:fixedScale ? 'fixed scale 1 and full effect load (proof only); shared volume budget remains adaptive' : 'normal adaptive browser presentation',rendererMode: hardware ? 'hardware requested; verify WebGL renderer below' : 'headless SwiftShader SOFTWARE; not representative of a GPU',
    controlled:{type:3,distance:150,fovDegrees:50,counts:[0,1,4,10],costBurstMs:1500,repeats:3,settleAfterWarmupMs:45000,captureCadenceMs:250,captureMechanism:'canvas export after game RAF'},effect,runs:[]};
  const save = () => fs.writeFileSync(path.join(out, fallbacks ? 'fallbacks.json' : fixedScale ? 'volumetric-fixed.json' : 'volumetric.json'), JSON.stringify(evidence,null,2));
  // Fresh browser processes keep one style/mode's heap and shaders out of another's timing.
  for (const style of ['Classic','Modern']) for (const mode of (fallbacks ? ['missing','unusable'] : ['native','volumetric'])) {
    const enabled = mode !== 'native';
    const browser = await playwright.launch(launch);
    evidence.browser = browser.version();
    const context = await browser.newContext({viewport:{width:1280,height:800},ignoreHTTPSErrors:true});
    await context.addInitScript(({fixedScale}) => {
      if (!fixedScale) return;
      // Benchmark only: hold the pixel count and ambient effect load equal on
      // the native and volumetric paths. Keep the real pacing sampler/slow
      // signal and the shared VolumetricRenderer's own adaptive LOD budget.
      let pacingClass;
      Object.defineProperty(window,'FramePacing',{
        configurable:true,get:()=>pacingClass,set:value=>{
          const sample=value.prototype.sample;
          value.prototype.sample=function(...args){sample.apply(this,args);this.scale=1;this.effectLoad=true;};
          pacingClass=value;
        }
      });
    }, {fixedScale});
    await context.addInitScript(require('./p1075-frame-probe.cjs'));
    const page = await context.newPage(); page.setDefaultTimeout(120000);
    const run = {style,mode,enabled,errors:[],console:[],requests:[],cost:[],captures:[]}; evidence.runs.push(run);
    page.on('pageerror', error => run.errors.push(error.message));
    page.on('console', message => {run.console.push(message.text());});
    page.on('request', request => {if (request.url().includes('/packs/' + effect.sha256)) run.requests.push(request.url().split('/packs/')[1]);});
    const badBytes = Buffer.from('P1083 deliberately invalid effect'), badHash = require('node:crypto').createHash('sha256').update(badBytes).digest('hex');
    await context.route('**/*', async route => {
      if (fallbacks && route.request().url().endsWith('/battle-manifest.json')) {
        const response = await route.fetch(); const replacement = await response.json();
        if (mode === 'missing') {
          replacement.assets = replacement.assets.filter(a => a.source !== effect.source);
          replacement.packs = replacement.packs.filter(p => p.key !== 'shader-volumetric');
        } else {
          const asset = replacement.assets.find(a => a.source === effect.source);
          Object.assign(asset, {path:'packs/'+badHash,sha256:badHash,byteSize:badBytes.length});
        }
        await route.fulfill({response,json:replacement}); return;
      }
      if (mode === 'unusable' && route.request().url().endsWith('/packs/'+badHash)) {
        await route.fulfill({status:200,contentType:'application/octet-stream',body:badBytes}); return;
      }
      assert.ok(['localhost','127.0.0.1'].includes(new URL(route.request().url()).hostname), 'proof stays local'); await route.continue();
    });
    try {
      const cdp = await context.newCDPSession(page); await cdp.send('WebAuthn.enable');
      await cdp.send('WebAuthn.addVirtualAuthenticator',{options:{protocol:'ctap2',transport:'internal',hasResidentKey:true,hasUserVerification:true,isUserVerified:true,automaticPresenceSimulation:true}});
      await page.goto(origin + '/identity.html');
      await page.locator('#callsign').fill('Shader1083' + Date.now().toString().slice(-8));
      await page.locator('#register').click(); await page.locator('#account').waitFor({state:'visible'});
      await page.goto(origin + '/play/menu.html?dev=1' + (enabled ? '&volumetric=1' : ''));
      await page.waitForFunction(() => window.preview?.measurements.firstFrameMs != null);
      await page.getByRole('button',{name:style,exact:true}).click();
      await page.locator('#online-open').click(); await page.locator('#join-password').waitFor();
      await page.locator('#join-password').fill(fs.readFileSync(path.join(stateDir,'game-password'),'utf8').trim());
      await page.locator('.online-join').first().click(); await page.locator('.online-side').first().click();
      await page.locator('#online-panel[data-state="Hangar"]').waitFor();
      await page.waitForFunction(() => gameMenu.hangar().launchArt);
      await page.waitForFunction(() => {gameMenu.launch();return document.getElementById('online-panel')?.dataset.state === 'Flight';}, null, {polling:100});
      await page.locator('#theCanvas').focus(); await page.keyboard.press('Backspace',{delay:100});
      await page.waitForFunction(() => gameMenu.metrics().flight.clockReady && gameMenu.metrics().flight.combat.ready);
      await page.evaluate(() => gameMenu.proofView('volumetric',0));
      const metrics = () => page.evaluate(() => gameMenu.metrics());
      if (fixedScale && !hardware) {
        // Page chrome can change between briefs. Match P1083A's actual drawing
        // buffer, not its assumed 104px toolbar; keep the cost assertion below.
        const buffer = (await metrics()).flight.viewport;
        if (buffer.width !== 1280 || buffer.height !== 696) {
          const window = page.viewportSize();
          await page.setViewportSize({width: window.width + 1280 - buffer.width,
            height: window.height + 696 - buffer.height});
          await until(async () => {const v=(await metrics()).flight.viewport;
            return v.width===1280 && v.height===696;}, 'P1083A fixed drawing buffer');
        }
      }
      let initial = await metrics(); const id = initial.online.shipId;
      assert.equal(initial.gameStyle, style); assert.equal(initial.style, style);
      if (fallbacks) {
        await until(async () => !!(await metrics()).flight.combat.volumetric.failure, 'native fallback');
      } else if (enabled) {
        await until(async () => {const m = (await metrics()).flight.combat.volumetric; if(m.failure) throw Error('Volumetric fallback: '+m.failure);return m.loaded && m.usable;}, 'effect loaded and usable');
      } else assert.equal(initial.flight.combat.volumetric.requested,false);
      run.webgl = (await metrics()).webgl; assert.ok(run.webgl?.version && run.webgl.maxTextureUnits > 0);
      await wait(5000); // Flight grace and artwork promotions are outside timed bursts.
      // Exercise an actual first explosion before timing: zero-vertex shader
      // preload alone cannot warm all WASM and driver draw/composite paths.
      run.warmup = await fixture(id,1);
      await until(async () => (await metrics()).flight.combat.explosions > 0, 'warm-up event received');
      await until(async () => (await metrics()).flight.combat.explosions === 0, 'warm-up event expired',30000);
      // Priority-2 scene art has a 30-second not-before boundary. Keep that
      // deferred promotion and presentation hysteresis outside the cost cells.
      await wait(fallbacks ? 1000 : 45000);
      await page.evaluate(() => {p1075Frames.enabled = true;});
      for (const count of (fallbacks ? [] : [0,1,4,10])) {
        const cell = {count,bursts:[],samples:[]}; run.cost.push(cell);
        for (let repeat=0;repeat<3;repeat++) {
          await until(async () => (await metrics()).flight.combat.explosions === 0,'previous burst expired',30000);
          await page.evaluate(() => {
            window.p1083Timing = {active:true,samples:[]};let previous;
            function tick(now) {
              const flight = gameMenu.metrics().flight;
              if(previous != null) p1083Timing.samples.push({gap:now-previous,count:flight.combat.explosions,volumes:flight.combat.volumetric.volumes,
                submissions:flight.combat.volumetric.submissions,viewport:flight.viewport});
              previous=now;if(p1083Timing.active)requestAnimationFrame(tick);
            }
            requestAnimationFrame(tick);
          });
          cell.bursts.push(await fixture(id,count));
          await wait(1500);
          const samples = await page.evaluate(() => {p1083Timing.active=false;return p1083Timing.samples;});
          // Count the actual concurrent events on each presented frame; exclude
          // both the waiting frame before reception and the first burst frame.
          cell.samples.push(...samples.filter((s,i) => i>0 && s.count===count && samples[i-1].count===count));
        }
        assert.ok(cell.samples.length >= 12,'enough actual '+count+'-explosion RAF samples');
        // Headless is 1280x696; a visible window's chrome changes the height by a few pixels,
        // so --hardware-gl only requires one constant full-scale buffer for the whole cell.
        if(fixedScale) assert.ok(cell.samples.every(s=>s.viewport.width===1280 && (hardware
          ? s.viewport.height===cell.samples[0].viewport.height && s.viewport.height>=680
          : s.viewport.height===696)),'fixed 1280x696 cost buffer');
        if(enabled && count>0) assert.ok(cell.samples.some(s => s.volumes>0),'real shared renderer submissions');
        const intervals = cell.samples.map(s=>s.gap);
        cell.frames=intervals.length;cell.p50Ms=percentile(intervals,.5);cell.p95Ms=percentile(intervals,.95);
        console.log(style,enabled?'Volumetric':'Native',count,cell.frames,cell.p50Ms,cell.p95Ms);save();
      }
      await until(async () => (await metrics()).flight.combat.explosions===0,'cost bursts expired');
      run.captureReceipt = await fixture(id,1);
      await page.evaluate(() => {
        window.p1083Burst = {frames:[],start:null};
        function capture(now) {
          p1083Burst.start ??= now;
          if (now >= p1083Burst.start + p1083Burst.frames.length*250) {
            // The game's already queued RAF runs first. Export its presented
            // canvas here, before the compositor discards the drawing buffer;
            // CDP screenshots can otherwise wait for extra compositor frames.
            const m = gameMenu.metrics().flight;
            p1083Burst.frames.push({jpeg:document.getElementById('theCanvas').toDataURL('image/jpeg',.9),
              elapsedMs:now-p1083Burst.start,combat:m.combat,viewport:m.viewport,
              ageMs:m.combat.localMs-m.combat.explosionDraws.at(-1)?.receivedMs});
          }
          if(p1083Burst.frames.length<8)requestAnimationFrame(capture);
        }
        requestAnimationFrame(capture);
      });
      await page.waitForFunction(() => p1083Burst.frames.length===8, null, {timeout:30000});
      const burst = await page.evaluate(() => p1083Burst.frames);
      for(const [frame,capture] of burst.entries()) {
        const file=`${style.toLowerCase()}-${mode}-${frame}.jpg`;
        fs.writeFileSync(path.join(out,file),Buffer.from(capture.jpeg.split(',')[1],'base64'));
        delete capture.jpeg;run.captures.push({file,...capture});
      }
      const final=await metrics();run.final=final.flight.combat.volumetric;run.frames=await page.evaluate(()=>p1075Frames);
      assert.ok(run.frames.sampled>30);assert.deepEqual(run.frames.discarded,[]);assert.deepEqual(run.frames.errors,[]);assert.deepEqual(run.errors,[]);
      assert.equal(run.console.filter(line=>line.includes('[browser] WebGL capabilities ')).length,1,'capabilities log once');
      if(fallbacks) {
        assert.ok(run.final.requested && !run.final.loaded && !run.final.usable && run.final.failure);
        assert.equal(run.final.submissions,0);
        assert.equal(run.console.filter(line=>line.includes('using P1079 native explosions:')).length,1);
        assert.ok(run.captures.some(c=>c.combat.explosions>0),'fallback draws real native event');
      } else if(enabled) {assert.ok(run.final.loaded && run.final.usable && run.final.submissions>0);assert.ok(run.requests.length>0);}
      else {assert.equal(run.final.submissions,0);assert.equal(run.requests.length,0,'default never downloads effect');}
      await page.locator('#online-close').click();await page.locator('#online-panel').waitFor({state:'detached'});save();
    } catch(error) {
      run.failure=String(error);run.metrics=await metricsSafely(page);save();throw error;
    } finally {await context.close();await browser.close();save();}
  }
  evidence.pass=true;save();console.log(fallbacks ? 'P1083_FALLBACKS_PASS 4 runs' : 'P1083_VOLUMETRIC_PASS 4 runs');
}
async function metricsSafely(page){return page.evaluate(()=>gameMenu.metrics()).catch(String);}
main().catch(error=>{console.error(error);process.exitCode=1;});
