// P1066 B1: PNG captures, default black sky and effect-load metrics.
// For a workstation with the already-installed P1051 Chromium modules. No npm install.
// Args: NPM_DIRECTORY HOST_STATE OUTPUT GENERATION [SAMPLE_SECONDS=60].
// Run once per fresh host --bots 1/5/10. Every browser process is closed here;
// the calling shell owns the proof-host PID and its stop file.
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const modules=path.resolve(process.argv[2],'node_modules'),stateDir=path.resolve(process.argv[3]),out=path.resolve(process.argv[4]),generation=path.resolve(process.argv[5]);
const sampleSeconds=Number(process.argv[6]||60);
assert.ok(Number.isFinite(sampleSeconds)&&sampleSeconds>=1&&sampleSeconds<=300);
const {chromium:playwright}=require(path.join(modules,'playwright'));
const origin='https://localhost:8443',menu=origin+'/play/menu.html?dev=1';
const state=()=>JSON.parse(fs.readFileSync(path.join(stateDir,'state.json'))),delay=ms=>new Promise(r=>setTimeout(r,ms));
const percentile=(a,p)=>[...a].sort((x,y)=>x-y)[Math.min(a.length-1,Math.floor(a.length*p))];
async function until(test,label,timeout=180000){const end=Date.now()+timeout;while(Date.now()<end){if(await test())return;await delay(100)}throw Error('Timed out: '+label)}
async function type(page,selector,value){await page.locator(selector).evaluate((input,text)=>{input.value=text;input.dispatchEvent(new Event('input',{bubbles:true}));input.dispatchEvent(new Event('change',{bubbles:true}))},String(value))}
async function main(){
 fs.mkdirSync(out,{recursive:true});
 // The host writes state.json only after it boots; a missing or mid-swap file is not ready yet.
 await until(()=>{try{return state().battle.ready}catch(e){if(e.code==='ENOENT'||e instanceof SyntaxError)return false;throw e}},'capped bots authenticated and pilot slot reserved',600000);
 const size=state().battle.size;assert.ok([1,5,10].includes(size));
 const target=state().chart.find(w=>w.ClusterId===1053&&w.Name===state().battle.targetSector)?.destinationSectorId;
 // Native authored chart: the target is an actual adjacent sector, never home.
 assert.ok(target!=null&&target!==1053);
 const chromium=(await import(require('node:url').pathToFileURL(path.join(modules,'@sparticuz/chromium/build/index.js')).href)).default;
 const browser=await playwright.launch({executablePath:await chromium.executablePath(),headless:true,args:[...chromium.args.filter(a=>!['--disable-web-security','--allow-running-insecure-content'].includes(a)),'--proxy-server=http://127.0.0.1:9','--proxy-bypass-list=127.0.0.1;localhost','--ignore-certificate-errors']});
 let releasePacks;const packsReleased=new Promise(resolve=>releasePacks=resolve);
 const join=JSON.parse(fs.readFileSync(path.join(generation,'join-manifest.json'))),battle=JSON.parse(fs.readFileSync(path.join(generation,'battle-manifest.json')));
 const residentPaths=new Set(join.assets.map(a=>'/play/'+a.path)),deferredPaths=new Set(battle.assets.map(a=>'/play/'+a.path).filter(p=>!residentPaths.has(p)));
 let failedOnce=false,page;const consoleLines=[],errors=[],requests=[],samples=[],captures=[];
 try{
  const context=await browser.newContext({viewport:{width:1920,height:1080},ignoreHTTPSErrors:true});page=await context.newPage();page.setDefaultTimeout(120000);
  await context.addInitScript(()=>{
   window.p1065Memories=new Set();const Original=WebAssembly.Memory;
   WebAssembly.Memory=new Proxy(Original,{construct(target,args,newTarget){const memory=Reflect.construct(target,args,newTarget);p1065Memories.add(memory);return memory}});
  });
  await context.route('**/*',async route=>{const u=new URL(route.request().url());if(!['localhost','127.0.0.1'].includes(u.hostname))throw Error('Non-loopback request: '+u.origin);if(deferredPaths.has(u.pathname)){await packsReleased;if(!failedOnce){failedOnce=true;await route.fulfill({status:503,body:'P1066 one failed visual fetch'});return}}await route.continue()});
  page.on('pageerror',error=>errors.push(error.message));page.on('console',message=>{const t=message.text();if(/\[browser\]/.test(t))consoleLines.push(t.slice(0,400))});page.on('request',request=>{const u=new URL(request.url());if(u.pathname.includes('/packs/'))requests.push(u.pathname)});
  const cdp=await context.newCDPSession(page);await cdp.send('WebAuthn.enable');await cdp.send('WebAuthn.addVirtualAuthenticator',{options:{protocol:'ctap2',transport:'internal',hasResidentKey:true,hasUserVerification:true,isUserVerified:true,automaticPresenceSimulation:true}});
  await page.goto(origin+'/identity.html');await type(page,'#callsign','Browser1066'+Date.now().toString().slice(-7));await page.locator('#register').click();await page.locator('#account').waitFor({state:'visible'});
  await page.goto(menu);await page.waitForFunction(()=>window.preview?.measurements.firstFrameMs!=null);
  await page.locator('#online-open').click();await page.locator('#join-password').waitFor();await type(page,'#join-password',fs.readFileSync(path.join(stateDir,'game-password'),'utf8').trim());await page.locator('.online-join').first().click();await page.locator('.online-side').first().click();await page.locator('#online-panel[data-state="Hangar"]').waitFor();await page.locator('#online-launch').click();await page.locator('#online-panel[data-state="Flight"]').waitFor();
  await page.locator('#theCanvas').focus();await page.keyboard.press('Backspace',{delay:100});
  const start=await page.evaluate(()=>gameMenu.metrics());const shipId=start.online.shipId;
  const chart=state().chart.find(w=>w.ClusterId===start.flight.sectorId&&w.destinationSectorId===target);assert.ok(chart);
  const option=`#flight-warp option[value="${chart.WarpId}"]`;
  if(!await page.locator(option).count()){
   await page.locator('#flight-waypoint').click();for(const [i,axis]of ['x','y','z'].entries())await type(page,'#waypoint-'+axis,chart.Position[i]*.85);await page.locator('#waypoint-go').click();await page.locator(option).waitFor({state:'attached',timeout:240000});
  }
  await page.locator('#flight-warp').selectOption(String(chart.WarpId));await page.waitForFunction(id=>gameMenu.metrics().flight.sectorId===id,target,{timeout:240000});
  await page.locator('#theCanvas').focus();await page.keyboard.press('Backspace',{delay:100});
  // Fly into the rendezvous with ordinary waypoint input, then park the pilot.
  await page.locator('#flight-waypoint').click();for(const axis of ['x','y','z'])await type(page,'#waypoint-'+axis,axis==='y'?550:0);await page.locator('#waypoint-go').click();
  await until(async()=>{const m=await page.evaluate(()=>gameMenu.metrics());return m.flight.battle.contacts>=Math.min(3,size*2-1)},'several real visible contacts in battle',240000);
  await page.locator('#theCanvas').focus();await page.keyboard.press('Backspace',{delay:100});
  await until(async()=>{const m=await page.evaluate(()=>gameMenu.metrics());return m.flight.battle.standIns>0},'visible contacts survive held mesh fetches');
  const held=await page.evaluate(()=>gameMenu.metrics());assert.equal(held.flight.battle.contacts,held.flight.battle.meshes+held.flight.battle.standIns);
  releasePacks();
  await until(async()=>{const m=await page.evaluate(()=>gameMenu.metrics());return m.battlePacks.retries>=1&&m.flight.battle.meshes>1},'failed fetch retries then swaps stand-ins for original meshes');
  await page.waitForFunction(()=>gameMenu.metrics().flight.scene.ready&&gameMenu.metrics().flight.scene.paintingsReady,null,{timeout:240000});
  const swapped=await page.evaluate(()=>gameMenu.metrics());
  const timing=[];
  for(const [style,width,height]of [['Classic',1920,1080],['Modern',1920,1080],['Classic',800,480],['Modern',800,480]]){
   await page.setViewportSize({width,height});await page.getByRole('button',{name:style,exact:true}).click();await delay(1000);
   await page.evaluate(()=>{window.battleSample={intervals:[],active:true};let previous;const tick=now=>{if(previous!==undefined)battleSample.intervals.push(now-previous);previous=now;if(battleSample.active)requestAnimationFrame(tick)};requestAnimationFrame(tick)});
   const end=Date.now()+sampleSeconds*1000;
   while(Date.now()<end){
    const m=await page.evaluate(()=>{const m=gameMenu.metrics(),wasmMemoryBuffers=[...p1065Memories].map(memory=>memory.buffer.byteLength);return {metrics:m,pacing:window.preview.pacing(),wasmMemoryBuffers,wasmLinearBytes:wasmMemoryBuffers.reduce((n,b)=>n+b,0),jsHeapBytes:performance.memory?.usedJSHeapSize??null}});
    const s=state(),own=s.ships.find(x=>x.Id===shipId),inSector=s.ships.filter(x=>x.StationId==null&&x.sectorId===m.metrics.flight.sectorId);
    const b=m.metrics.flight.battle;
    if(m.metrics.flight.flying){assert.equal(b.contacts,b.meshes+b.standIns);assert.ok(b.sectorShips>=b.contacts)}
    samples.push({style,width,height,sampledAt:s.sampledAt,serverShipsInSector:inSector.length,serverHullIds:[...new Set(inSector.map(x=>x.hullId))],ownSector:own?.sectorId,...m});
    await delay(500);
   }
   const intervals=await page.evaluate(()=>{battleSample.active=false;return battleSample.intervals});timing.push({style,width,height,frames:intervals.length,p50Ms:percentile(intervals,.5),p95Ms:percentile(intervals,.95),headlessGpuRepresentative:false});
   const latest=samples.at(-1).metrics.flight;
   // Do not silently save empty/home-sector frames as combat evidence.
   assert.equal(latest.sectorId,target);assert.ok(latest.flying);assert.ok(latest.battle.contacts>=Math.min(3,size*2-1));
   if(size>=5)assert.ok(latest.battle.hullIds.length>=2,'multiple hull types actually visible');
   await page.locator('#flight-contact').click();await delay(200);
   assert.equal((await page.evaluate(()=>gameMenu.metrics())).flight.scene.skyTier,'black');
   const file=`scene-${size}-${style.toLowerCase()}-${width}x${height}.png`;await page.screenshot({path:path.join(out,file),type:'png'});
   assert.ok(fs.statSync(path.join(out,file)).size<=2*1024*1024,'each PNG <=2 MiB');captures.push({file,style,width,height,battle:latest.battle});await page.locator('#flight-contact').click();
  }
  assert.ok(samples.every(s=>s.wasmLinearBytes>0),'WASM linear memory was measured');
  assert.ok(samples.some(s=>s.metrics.flight.battle.meshes>1),'real remote meshes drew');
  assert.deepEqual(errors,[]);
  const imageBytes=captures.reduce((n,c)=>n+fs.statSync(path.join(out,c.file)).size,0);assert.ok(imageBytes<=2*1024*1024,'PNG capture set <=2 MiB');
  const report={size,browserVersion:browser.version(),headlessGpuRepresentative:false,held,swapped,forcedFetchFailure:failedOnce,wasmHeapNote:'Memory constructor instrumentation runs before the runtime; wasmMemoryBuffers reports each actual WebAssembly.Memory buffer, summed by wasmLinearBytes. managedHeapBytes is the .NET managed portion.',samples,timing,captures,imageBytes,packRequests:requests,manifest:JSON.parse(fs.readFileSync(path.join(generation,'battle-receipt.json')))};
  fs.writeFileSync(path.join(out,'browser-scene.json'),JSON.stringify(report,null,2));
  await page.locator('#online-close').click();await page.locator('#online-panel').waitFor({state:'detached'});console.log('BROWSER_SCENE_PASS',size,imageBytes);
 }catch(error){if(page){fs.writeFileSync(path.join(out,'failure.json'),JSON.stringify({error:String(error),errors,consoleLines:consoleLines.slice(-80),packRequests:requests.slice(-80),samples,lastMetrics:await page.evaluate(()=>gameMenu.metrics()).catch(e=>String(e))},null,2))}throw error}
 finally{releasePacks();await browser.close()}
}
main().catch(error=>{console.error(error);process.exitCode=1});
