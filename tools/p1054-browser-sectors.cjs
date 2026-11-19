// Real Chromium, production identity/session, ordinary browser aleph autopilot.
// Args: preinstalled npm directory, proof host state, output, generation
// [--smoke|--entry-views|--visual-swap|--visual-swap-modern].
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const modules=path.resolve(process.argv[2],'node_modules'),stateDir=path.resolve(process.argv[3]),output=path.resolve(process.argv[4]),generation=path.resolve(process.argv[5]);
const {chromium:playwright}=require(path.join(modules,'playwright'));
const origin='http://localhost:8048',menu=origin+'/play/preview/menu.html';
const index=JSON.parse(fs.readFileSync(path.join(generation,'sector-manifest.json'))),base=JSON.parse(fs.readFileSync(path.join(generation,'join-manifest.json')));
const basePaths=new Set(base.assets.map(a=>'/play/preview/'+a.path)),entry=index.sectors.find(s=>s.sectorId===index.entrySectorId);
const entryPaths=new Set(entry.assets.map(a=>'/play/preview/'+a.path));
const readState=()=>JSON.parse(fs.readFileSync(path.join(stateDir,'state.json'))),delay=ms=>new Promise(r=>setTimeout(r,ms));
const percentile=(values,p)=>[...values].sort((a,b)=>a-b)[Math.min(values.length-1,Math.floor(values.length*p))];
const watchTimers=new Set();
const gate=()=>{let release;const promise=new Promise(r=>release=r);return {promise,release}};
async function until(test,label,timeout=30000){const end=Date.now()+timeout;while(Date.now()<end){if(await test())return;await delay(100)}throw Error('Timed out: '+label)}
const metrics=page=>page.evaluate(()=>gameMenu.metrics());
async function sampleStart(page){await page.evaluate(()=>{window.sectorSample={intervals:[],rockSizes:[],active:true};let last;const frame=t=>{if(last!==undefined)sectorSample.intervals.push(t-last);last=t;const m=gameMenu.metrics();sectorSample.rockSizes.push(Object.values(m.flight.drawnRockSurfaces||{}).map(s=>s[0]));if(sectorSample.active)requestAnimationFrame(frame)};requestAnimationFrame(frame)})}
async function sampleEnd(page){return page.evaluate(()=>{sectorSample.active=false;return sectorSample})}
async function controlProof(page,id){
 const before=readState().poseReceipts.find(p=>p.Id===id),samples=[];
 for(let i=0;i<4;i++){
  await page.locator('#theCanvas').focus();const key=i%2?'ArrowLeft':'ArrowRight';await page.keyboard.down(key);await page.keyboard.down('w');
  await until(()=>readState().ships.some(s=>s.Id===id&&(i%2?s.yaw>.9:s.yaw<-.9)),'server accepts reversal during streaming');
  samples.push(readState().poseReceipts.find(p=>p.Id===id));await delay(350);await page.keyboard.up(key);await page.keyboard.up('w');
 }
 await page.keyboard.press('Backspace',{delay:100});
 const after=readState().poseReceipts.find(p=>p.Id===id);
 assert.ok(after.accepted>before.accepted+4,'server continues accepting pose updates');assert.ok(after.lastClientTime>before.lastClientTime);
 return {before,after,samples,uninterruptedControls:true};
}
function watchShip(id){
 const samples=[];let previous=-1,lastChange=Date.now(),maximumGapMs=0;
 const timer=setInterval(()=>{const state=readState(),receipt=state.poseReceipts.find(p=>p.Id===id);if(receipt&&receipt.accepted!==previous){if(previous>=0)maximumGapMs=Math.max(maximumGapMs,Date.now()-lastChange);lastChange=Date.now();previous=receipt.accepted;samples.push({sampledAt:state.sampledAt,...receipt})}},60);
 watchTimers.add(timer);
 return ()=>{clearInterval(timer);watchTimers.delete(timer);assert.ok(samples.length>5,'server receipts span the swap');assert.ok(maximumGapMs<1000,'no input/pose stall during swap');return {samples,maximumGapMs}};
}
async function register(context,page,name){
 const cdp=await context.newCDPSession(page);await cdp.send('WebAuthn.enable');await cdp.send('WebAuthn.addVirtualAuthenticator',{options:{protocol:'ctap2',transport:'internal',hasResidentKey:true,hasUserVerification:true,isUserVerified:true,automaticPresenceSimulation:true}});
 await page.goto(origin+'/identity.html');await page.locator('#callsign').fill(name);await page.locator('#register').click();await page.locator('#account').waitFor({state:'visible'});
}
async function join(page,style){
 await page.goto(menu);await page.waitForFunction(()=>window.preview?.measurements.firstFrameMs!=null,null,{timeout:120000});
 await page.getByRole('button',{name:style,exact:true}).click();await page.locator('#online-open').click();await page.locator('.online-join').first().click();await page.waitForFunction(()=>document.querySelector('#online-panel')?.dataset.state==='Sides'||document.querySelector('#online-panel [role="alert"]'),null,{timeout:120000});assert.equal(await page.locator('#online-panel [role="alert"]').count(),0,await page.locator('#online-panel [role="alert"]').allTextContents());
 await page.locator('.online-side').first().click();await page.locator('#online-panel[data-state="Hangar"]').waitFor();await page.locator('#online-launch').click();await page.locator('#online-panel[data-state="Flight"]').waitFor({timeout:120000});
 await page.waitForFunction(()=>gameMenu.metrics().flight.frames>3);await page.locator('#theCanvas').focus();await page.keyboard.press('Backspace',{delay:100});
 return (await metrics(page)).online.shipId;
}
async function ready(page,sector){await page.waitForFunction(s=>gameMenu.metrics().flight.textures.readySectors.includes(s),sector,{timeout:120000})}
async function disconnect(page,name){await page.locator('#online-close').click();await page.locator('#online-panel').waitFor({state:'detached'});await until(()=>!readState().players.includes(name),'clean logoff')}
async function warp(page,name,sector){
 const current=(await metrics(page)).flight.sectorId;
 const chart=readState().chart.find(w=>w.ClusterId===current&&w.destinationSectorId===sector);
 assert.ok(chart,'authored map chart route for '+name);
 const selector=`#flight-warp option[value="${chart.WarpId}"]`;
 if(!await page.locator(selector).count()){
  // As in the desktop recorder, a pilot uses a real position waypoint to
  // approach a charted mouth until ordinary scanner discovery exports it.
  await page.locator('#flight-waypoint').click();
  for(const [i,axis]of ['x','y','z'].entries())await page.locator('#waypoint-'+axis).fill(String(chart.Position[i]*.85));
  await page.locator('#waypoint-go').click();
  await page.locator(selector).waitFor({state:'attached',timeout:180000});
 }
 await page.locator('#flight-warp').selectOption(String(chart.WarpId));
 await page.waitForFunction(id=>gameMenu.metrics().flight.sectorId===id,sector,{timeout:180000});
 await page.locator('#theCanvas').focus();await page.keyboard.press('Backspace',{delay:100});
}
async function captureSkyAngles(page,label,phase){
 await page.locator('#flight-contact').click();await page.locator('#flight-rock').click();
 for(let angle=0;angle<6;angle++){await delay(150);await page.screenshot({path:path.join(output,`${phase}-sky-angle${angle}-${label}.png`)});await page.locator('#flight-angle').click()}
 await page.locator('#flight-rock').click();await page.locator('#flight-contact').click();
}
async function captureEntryAngles(page,label,phase){
 await page.locator('#flight-contact').click();
 for(let angle=0;angle<6;angle++){await delay(150);await page.screenshot({path:path.join(output,`${phase}-angle${angle}-${label}.png`)});await page.locator('#flight-angle').click()}
 await page.locator('#flight-contact').click();
}
async function main(){
 fs.mkdirSync(output,{recursive:true});const chromium=(await import(require('node:url').pathToFileURL(path.join(modules,'@sparticuz/chromium/build/index.js')).href)).default;
 const launch=async()=>playwright.launch({executablePath:await chromium.executablePath(),headless:true,args:[...chromium.args.filter(a=>!['--disable-web-security','--allow-running-insecure-content'].includes(a)),'--proxy-server=http://127.0.0.1:9','--proxy-bypass-list=127.0.0.1;localhost']});
 let browser=await launch();
 const results=[];let page,releaseEntry,releaseRemote;const logs=[];
 try{
 const visualSwap=process.argv[6]?.startsWith('--visual-swap'),entryViews=process.argv[6]==='--entry-views';
 const profiles=visualSwap?[[process.argv[6]==='--visual-swap-modern'?'Modern':'Classic',800,480,false]]:entryViews?[['Classic',800,480,false],['Modern',800,480,false]]:process.argv[6]==='--smoke'?[['Classic',800,480,false]]:[['Classic',1920,1080,false],['Modern',1920,1080,false],['Classic',800,480,false],['Modern',800,480,true]];
 for(const [style,width,height,storageRefused]of profiles){
  const label=style.toLowerCase()+'-'+width+'x'+height;console.log('CASE',label,storageRefused?'storage-refused':'persistent');
  const context=await browser.newContext({viewport:{width,height}});page=await context.newPage();page.setDefaultTimeout(120000);
  const errors=[],requests=[],transfers=[],secrets=[];
  page.on('pageerror',e=>{errors.push(e.message);console.error(e.message)});page.on('console',m=>{logs.push(m.text());if(m.type()==='error')console.error(m.text())});
  page.on('request',r=>{const u=new URL(r.url());assert.ok(['localhost','127.0.0.1'].includes(u.hostname));requests.push({path:u.pathname,time:Date.now()})});
  if(storageRefused)await page.addInitScript(()=>{caches.open=async()=>{throw new DOMException('P1054 fixture refusal','QuotaExceededError')}});
  const cdp=await context.newCDPSession(page);await cdp.send('Network.enable');const urls=new Map();cdp.on('Network.requestWillBeSent',e=>urls.set(e.requestId,new URL(e.request.url).pathname));cdp.on('Network.loadingFinished',e=>transfers.push({path:urls.get(e.requestId)||'',bytes:e.encodedDataLength}));
  const name='Browser1054'+Date.now().toString().slice(-7);await register(context,page,name);
  await page.route('**/api/identity/join-token',async route=>{const response=await route.fetch();if(response.ok())secrets.push((await response.json()).token);await route.fulfill({response})});
  const entryGate=releaseEntry=gate(),remoteGate=releaseRemote=gate();let entryReleased=false,remoteReleased=false,failedOnce=false;
  const remote=index.sectors.find(s=>s.sectorId===1052),remoteOnly=new Set(remote.assets.map(a=>'/play/preview/'+a.path).filter(p=>!entryPaths.has(p)&&!basePaths.has(p)));
  await page.route('**/packs/*',async route=>{
   const p=new URL(route.request().url()).pathname;
   if(entryPaths.has(p)&&!entryReleased)await entryGate.promise;
   if(remoteOnly.has(p)&&!remoteReleased)await remoteGate.promise;
   if(p.endsWith(entry.assets[0].sha256)&&!failedOnce){failedOnce=true;await route.fulfill({status:503,body:'P1054 retry fixture'});return}
   await route.continue().catch(()=>{});
  });
  const id=await join(page,style),cold=await metrics(page);
  const ticket=requests.findIndex(r=>r.path==='/api/identity/join-token');assert.ok(ticket>=0);
  assert.ok(requests.slice(0,ticket).filter(r=>r.path.includes('/packs/')).every(r=>basePaths.has(r.path)),'base only before ticket');
  assert.ok(Object.values(cold.flight.drawnRockSurfaces).every(s=>s[0]===256),'entry is drawn with capped surfaces');
  const coldBytes=transfers.filter(t=>basePaths.has(t.path)).reduce((n,t)=>n+t.bytes,0);
  await page.locator('#flight-rock').click();await delay(100);await page.screenshot({path:path.join(output,'entry-capped-'+label+'.png')});
  if(entryViews)await captureEntryAngles(page,label,'entry-capped');
  await page.locator('#flight-starmap').click();await page.locator('#map-sector').selectOption('1052');await delay(100);await page.screenshot({path:path.join(output,'starmap-'+label+'.png')});await page.locator('#flight-starmap').click();
  const nonBaseBeforeUpgrade=requests.filter(r=>r.path.includes('/packs/')&&!basePaths.has(r.path));assert.ok(nonBaseBeforeUpgrade.every(r=>entryPaths.has(r.path)),'entry upgrade has highest priority');
  await sampleStart(page);const stopEntryWatch=watchShip(id);entryReleased=true;entryGate.release();
  const entryControls=await controlProof(page,id);await ready(page,1053);await delay(500);const entrySample=await sampleEnd(page),entryWatch=stopEntryWatch(),upgraded=await metrics(page);
  assert.ok(Object.values(upgraded.flight.drawnRockSurfaces).every(s=>s[0]===2048),'all entry surfaces replace capped textures');
  assert.ok(entrySample.rockSizes.every(sizes=>sizes.every(size=>size>=256)),'no blank entry rocks');assert.ok(upgraded.sectors.retries>=1&&failedOnce);
  await page.screenshot({path:path.join(output,'entry-original-'+label+'.png')});if(entryViews)await captureEntryAngles(page,label,'entry-original');await page.locator('#flight-rock').click();
  const entryP95=percentile(entrySample.intervals,.95);assert.ok(entryP95<34,'entry swap p95 below P1053 recovery budget');
  if(process.argv[6]==='--smoke'){results.push({style,width,height,coldBytes,entryP95,entryControls,entryWatch,cold,upgraded});await disconnect(page,name);await context.close();break}
  if(entryViews){
   results.push({style,width,height,coldBaseBytes:coldBytes,baseOnlyBeforeTicket:true,entryP95Ms:entryP95,entryControls,entryWatch,entryNeverBlank:true,cold,upgraded});
   fs.writeFileSync(path.join(stateDir,'desktop-join'),'');await until(()=>readState().desktop.desktopLaunched,'opposing desktop launches',30000);
   fs.writeFileSync(path.join(stateDir,'keeper-join'),'');await until(()=>readState().keeper.MySideId===0,'friendly keeper seats with native team balance',30000);
   await disconnect(page,name);remoteGate.release();await context.close();await browser.close();if(results.length<profiles.length)browser=await launch();continue;
  }
  // Warm reload occurs while the <=64 MiB base+entry working set is cached.
  let warm;
  // Keep the recorded map instance alive through warm reload via a normal,
  // authenticated desktop player, rather than changing server lifecycle rules.
  fs.writeFileSync(path.join(stateDir,'desktop-join'),'');await until(()=>readState().desktop.desktopLaunched,'opposing desktop launches',30000);
  fs.writeFileSync(path.join(stateDir,'keeper-join'),'');await until(()=>readState().keeper.MySideId===0,'friendly keeper seats with native team balance',30000);
  if(!storageRefused){await disconnect(page,name);const start=requests.length,transferStart=transfers.length;await join(page,style);await ready(page,1053);warm={packRequests:requests.slice(start).filter(r=>r.path.includes('/packs/')).length,packBytes:transfers.slice(transferStart).filter(t=>t.path.includes('/packs/')).reduce((n,t)=>n+t.bytes,0)};assert.equal(warm.packRequests,0);assert.equal(warm.packBytes,0)}
  const activeId=(await metrics(page)).online.shipId;
  await warp(page,'Borasisi',1052);const placeholder=await metrics(page);assert.ok(!placeholder.flight.textures.readySectors.includes(1052));assert.equal(placeholder.flight.sky.planetReady,false);assert.equal(placeholder.flight.sky.environmentReady,false);
  await page.screenshot({path:path.join(output,'placeholder-'+label+'.png')});if(visualSwap)await captureSkyAngles(page,label,'placeholder');await sampleStart(page);const stopRemoteWatch=watchShip(activeId);remoteReleased=true;remoteGate.release();
  const remoteControls=await controlProof(page,activeId);await ready(page,1052);await delay(500);const remoteSample=await sampleEnd(page),remoteWatch=stopRemoteWatch();await page.screenshot({path:path.join(output,'real-'+label+'.png')});if(visualSwap)await captureSkyAngles(page,label,'real');
  const remoteP95=percentile(remoteSample.intervals,.95);assert.ok(remoteP95<34,'remote swap p95 below P1053 recovery budget');
  await page.locator('#flight-starmap').click();await page.locator('#map-sector').selectOption('1051');await ready(page,1051);await page.locator('#flight-starmap').click();
  await warp(page,'Arrokoth',1051);const prefetched=await metrics(page);assert.ok(prefetched.flight.textures.readySectors.includes(1051),'prefetched pack ready on arrival');
  const server=readState(),end=await metrics(page);assert.equal(end.online.malformed,0);assert.equal(end.online.resumeSuccesses,0);assert.deepEqual(errors,[]);
  await disconnect(page,name);for(const secret of secrets)assert.ok(!logs.join('\n').includes(secret));
  results.push({style,width,height,storageRefused,coldBaseBytes:coldBytes,baseOnlyBeforeTicket:true,entryP95Ms:entryP95,entryMaximumFrameMs:Math.max(...entrySample.intervals),entryPumpMaximumMs:upgraded.flight.textures.maximumPumpMs,entryControls,entryWatch,entryRetries:upgraded.sectors.retries,entryNeverBlank:true,entryTextures:upgraded.flight.drawnRockSurfaces,warm,remoteP95Ms:remoteP95,remoteMaximumFrameMs:Math.max(...remoteSample.intervals),remoteControls,remoteWatch,placeholder,realTextures:end.flight.textures,prefetchedNoPlaceholder:true,serverSeesSector:server.ships.find(s=>s.Id===activeId)?.sectorId,cleanLogoff:true});
  fs.writeFileSync(path.join(output,'sector-proof.json'),JSON.stringify({passed:results.length===profiles.length,completedProfiles:results.length,expectedProfiles:profiles.length,browserVersion:browser.version(),headlessGpuRepresentative:false,results},null,2));
  await context.close();console.log('PASS',label,'entry/remote p95',entryP95,remoteP95);
  // The installed single-process Chromium exits with its last context. Each
  // independent cold/storage profile owns a fresh browser process.
  await browser.close();if(results.length<profiles.length)browser=await launch();
 }
 fs.writeFileSync(path.join(output,'sector-proof.json'),JSON.stringify({passed:results.length===profiles.length,completedProfiles:results.length,expectedProfiles:profiles.length,browserVersion:browser.version(),headlessGpuRepresentative:false,results},null,2));
 }catch(error){if(page&&!page.isClosed()){fs.writeFileSync(path.join(output,'failure.json'),JSON.stringify(await metrics(page).catch(()=>null),null,2));fs.writeFileSync(path.join(output,'failure-console.txt'),logs.join('\n'));await page.screenshot({path:path.join(output,'failure.png')}).catch(()=>{});}throw error}
 finally{for(const timer of watchTimers)clearInterval(timer);releaseEntry?.release();releaseRemote?.release();await browser.close()}
}
main().catch(error=>{console.error(error);process.exitCode=1});
