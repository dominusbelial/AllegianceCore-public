// NPM_DIRECTORY HOST_STATE EVIDENCE_DIRECTORY GENERATION [baseline]
// Existing loopback host; no installs. Delay every post-launch pack response so
// verified promotions cross flight frames. Baseline mode records original bugs.
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const npm=path.resolve(process.argv[2],'node_modules'),state=path.resolve(process.argv[3]),out=path.resolve(process.argv[4]),gen=path.resolve(process.argv[5]);
const baseline=process.argv[6]==='baseline', {chromium:pw}=require(path.join(npm,'playwright'));
const wait=ms=>new Promise(r=>setTimeout(r,ms)), host=()=>JSON.parse(fs.readFileSync(path.join(state,'state.json')));
const manifest=JSON.parse(fs.readFileSync(path.join(gen,'battle-manifest.json'))), resident=JSON.parse(fs.readFileSync(path.join(gen,'join-manifest.json')));
const assets=new Map([...resident.assets,...manifest.assets].map(a=>[a.source,a]));
const families=new Map();
function family(key){if(key.startsWith('station-'))return 'stations';if(key.startsWith('hull-'))return 'hulls';if(key.includes('-rock-'))return 'rocks';if(key.includes('-planet-'))return 'planets';if(key.includes('-globe-'))return 'globes';return 'scene';}
for(const p of manifest.packs) for(const s of p.sources){const a=assets.get(s);if(a&&!families.has(a.sha256))families.set(a.sha256,
 /^bgrnd/.test(a.source)?'rocks':/^nebplnt/.test(a.source)?'planets':/^(?:environment|globe)/.test(a.source)?'globes':family(p.key));}
async function until(fn,label,ms=180000){const end=Date.now()+ms;while(Date.now()<end){if(await fn())return;await wait(100);}throw Error('Timeout: '+label);}
async function main(){fs.mkdirSync(out,{recursive:true});
const c=(await import(require('node:url').pathToFileURL(path.join(npm,'@sparticuz/chromium/build/index.js')).href)).default;
const browser=await pw.launch({executablePath:process.env.CHROMIUM_PATH||await c.executablePath(),headless:true,args:[...c.args.filter(a=>!['--disable-web-security','--allow-running-insecure-content','--single-process'].includes(a)),'--ignore-certificate-errors','--proxy-server=http://127.0.0.1:9','--proxy-bypass-list=localhost;127.0.0.1']});
const evidence={baseline,browser:browser.version(),runs:[]};
try{for(const style of ['Classic','Modern']){
 const context=await browser.newContext({viewport:{width:1280,height:800},ignoreHTTPSErrors:true});
 await context.addInitScript(require('./p1075-frame-probe.cjs'));
 const page=await context.newPage();page.setDefaultTimeout(180000);const errors=[],requests=[],hangarRequests=[],consoleLines=[];let launchAt=0,hangarAt=0,held=false;const releases=[];
 const stationType=Number((process.argv[7]||'4,5').split(',')[style==='Classic'?0:1]);
 const heldHashes=new Set(manifest.packs.find(p=>p.key==='station-'+stationType).sources.map(s=>assets.get(s)?.sha256));
 await context.route('**/*',async route=>{const u=new URL(route.request().url());assert.ok(['localhost','127.0.0.1'].includes(u.hostname));
  const hash=u.pathname.split('/').pop();if(hangarAt&&/^[a-f0-9]{64}$/.test(hash)){
   const at=Date.now();if(held&&heldHashes.has(hash))await new Promise(r=>releases.push(r));
   if(launchAt)await wait(150); const response=await route.fetch();const body=await response.body();
   const start=launchAt||hangarAt;
   (launchAt?requests:hangarRequests).push({hash,family:families.get(hash)||'other',startedMs:at-start,completedMs:Date.now()-start,decodedBytes:body.length,networkBytes:Number(response.headers()['content-length'])||body.length,encoding:response.headers()['content-encoding']||'identity'});
   return route.fulfill({response,body});
  }return route.continue();});
 page.on('pageerror',e=>errors.push(e.message));
 page.on('console',m=>{consoleLines.push(m.type()+': '+m.text());if(consoleLines.length>500)consoleLines.shift();});
 console.log(style,'opening');
 await page.goto('https://localhost:8443/play/menu.html?online=1&dev=1');
 await page.locator('#guest-disclosure').waitFor();
 const password=fs.readFileSync(path.join(state,'game-password'),'utf8').trim();
 await page.locator('#join-password').evaluate((e,v)=>{e.value=v;e.dispatchEvent(new Event('input',{bubbles:true}))},password);
 await page.locator('.online-join:enabled').first().click();await page.locator('#online-panel[data-state="Sides"]').waitFor();hangarAt=Date.now();await page.locator('.online-side').first().click();
 await page.locator('#online-panel[data-state="Hangar"]').waitFor();await page.waitForFunction(()=>gameMenu.hangar().visible&&gameMenu.hangar().launchArt);
 await page.getByRole('button',{name:style,exact:true}).click();
 const run={style,errors,requests,hangarRequests,consoleLines,captures:[]};console.log(style,"hangar");evidence.runs.push(run);
 for(const [width,height] of [[1920,1080],[1280,800],[800,480]]){
  await page.setViewportSize({width,height});await wait(500);
  const h=await page.evaluate(()=>gameMenu.hangar());run['hangar-'+width]=h;
  for(const action of ['Buy','Default']){const rect=h.verbs.find(v=>v.action===action)?.bounds;assert.ok(rect,action+' drawn');}
  const file=`hangar-${style.toLowerCase()}-${width}x${height}.jpg`;await page.screenshot({path:path.join(out,file),type:'jpeg',quality:85});run.captures.push(file);
 }
 await page.setViewportSize({width:1280,height:800});launchAt=Date.now();await page.evaluate(()=>p1075Frames.enabled=true);
 await page.waitForFunction(()=>{gameMenu.launch();return document.getElementById('online-panel')?.dataset.state==='Flight'},null,{polling:500});
 await page.waitForFunction(()=>gameMenu.metrics().flight.frames>3);console.log(style,"flight");
 await page.locator('#theCanvas').focus();await page.keyboard.press('Backspace',{delay:180});
 await page.waitForFunction(()=>gameMenu.metrics().flight.controls.throttle===-1);
 // Exercise destination-sky preparation early, while other packs promote.
 {const sector=(await page.evaluate(()=>gameMenu.metrics())).flight.sectorId;const warp=host().chart.find(w=>w.ClusterId===sector);if(warp)await page.evaluate(id=>gameMenu.proofView('warp',id),warp.WarpId);}
 await wait(Math.max(0,30000-(Date.now()-launchAt)));
 console.log(style,"first30",requests.length);
 run.hangarCompressed=Object.fromEntries(['stations','hulls','rocks','planets','globes','scene','other'].map(f=>[f,hangarRequests.filter(r=>r.family===f).reduce((n,r)=>n+r.networkBytes,0)]));
 run.first30Compressed=Object.fromEntries(['stations','hulls','rocks','planets','globes','scene','other'].map(f=>[f,requests.filter(r=>r.family===f&&r.completedMs<=30000).reduce((n,r)=>n+r.networkBytes,0)]));
 run.first30=Object.fromEntries(['stations','hulls','rocks','planets','globes','scene','other'].map(f=>[f,requests.filter(r=>r.family===f&&r.completedMs<=30000).reduce((n,r)=>n+r.decodedBytes,0)]));
 run.metrics30=await page.evaluate(()=>gameMenu.metrics());run.frames30=await page.evaluate(()=>p1075Frames);
 // Hold a newly built station's closure until its first failed draw/lookup.
 held=true;const sector=run.metrics30.flight.sectorId;fs.writeFileSync(path.join(state,'build-station.json'),JSON.stringify({sector,type:stationType}));
 await until(()=>fs.existsSync(path.join(state,'built-station.json')),'station built');console.log(style,"built");
 const built=JSON.parse(fs.readFileSync(path.join(state,'built-station.json')));fs.unlinkSync(path.join(state,'built-station.json'));
 await page.waitForFunction(id=>gameMenu.metrics().flight.sectorArt.stations.some(s=>s.id===id),built.id);
 run.beforePromotion=await page.evaluate(id=>gameMenu.metrics().flight.sectorArt.stations.find(s=>s.id===id),built.id);
 if(baseline)await page.evaluate(type=>gameMenu.requestProofArt?.('station-'+type),stationType);
 held=false;for(const release of releases.splice(0))release();
 if(!baseline){await page.waitForFunction(id=>{const s=gameMenu.metrics().flight.sectorArt.stations.find(s=>s.id===id);return s?.modelResident&&s.textureResident&&s.texture&&s.drawFrames>0},built.id);
  await page.waitForFunction(()=>gameMenu.prepareLateArtProof(),null,{polling:500});
  run.rendererProof=await page.evaluate(()=>gameMenu.probeLateArt());
  for(const row of run.rendererProof.rows){assert.ok(row.initialMissing&&row.modelAfterPromotion&&row.textureMissing&&row.textureAfterPromotion,row.name+' late art');assert.ok(row.textureStem,row.name+' resolved texture');assert.ok(row.nonBlackPixels>0,row.name+' actually drew');}
  await page.evaluate(id=>gameMenu.proofView('station',id),built.id);
 }else await wait(10000);
 console.log(style,"promoted");
 run.afterPromotion=await page.evaluate(id=>gameMenu.metrics().flight.sectorArt.stations.find(s=>s.id===id),built.id);
 await page.locator('#theCanvas').focus();await page.keyboard.down('ShiftLeft');await wait(500);
 run.afterburner=await page.evaluate(()=>gameMenu.metrics().flight.hud);if(!baseline)assert.equal(run.afterburner.readoutActive,false,'no mounted afterburner cannot read ACTIVE');
 const stationFile=`built-station-${style.toLowerCase()}.jpg`;await page.locator('#theCanvas').screenshot({path:path.join(out,stationFile),type:'jpeg',quality:90});run.captures.push(stationFile);await page.keyboard.up('ShiftLeft');
 await page.locator('#flight-keys').click();await page.waitForFunction(()=>gameMenu.metrics().flight.keyboard.keyHelpOpen);await wait(300);const helpFile=`help-${style.toLowerCase()}.jpg`;await page.locator('#theCanvas').screenshot({path:path.join(out,helpFile),type:'jpeg',quality:85});run.captures.push(helpFile);await page.keyboard.press('Escape',{delay:180});await page.waitForFunction(()=>!gameMenu.metrics().flight.keyboard.keyHelpOpen);
 // Let deferred decorative art finish too: unchanged eventual visuals and no
 // discard during their later promotions are part of this proof.
 if(!baseline)await page.waitForFunction(()=>gameMenu.metrics().flight.scene.paintingsReady===true,null,{timeout:180000,polling:500});
 await wait(250);run.metricsFinal=await page.evaluate(()=>gameMenu.metrics());
 run.frames=await page.evaluate(()=>p1075Frames);if(!baseline){assert.ok(run.frames.sampled>30,'per-frame sampling ran');assert.deepEqual(run.frames.discarded,[],'zero discard-colour frames');assert.deepEqual(run.frames.errors,[]);}
 assert.deepEqual(errors,[]);fs.writeFileSync(path.join(out,'followups.json'),JSON.stringify(evidence,null,2));await context.unrouteAll({behavior:'ignoreErrors'});await context.close();
 }
 console.log('P1075_FOLLOWUPS_PASS',baseline?'baseline':'fixed');
}catch(e){evidence.failure=String(e);fs.writeFileSync(path.join(out,'followups-failure.json'),JSON.stringify(evidence,null,2));throw e;}finally{await browser.close();}}
main().catch(e=>{console.error(e);process.exitCode=1;});
