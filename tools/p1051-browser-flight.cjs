// Chromium -> production passkey/session/prediction/renderers -> loopback server.
// Args: local npm directory, proof host state directory, evidence output, briefing fixtures, generation.
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict'),zlib=require('node:zlib'),crypto=require('node:crypto');
const modules=path.resolve(process.argv[2]||'.scratch/p1051/npm','node_modules');
const {chromium:playwright}=require(path.join(modules,'playwright'));
const stateDir=path.resolve(process.argv[3]||'.scratch/p1051/host');
const output=path.resolve(process.argv[4]||'docs/reports/BROWSER_FLIGHT_P1051_assets');
const fixtures=path.resolve(process.argv[5]||'.scratch/p1051/flight-opens4');
const generation=path.resolve(process.argv[6]||'.scratch/p1051/generation');
// P1059: the proof harness uses the developer/proof controls, so it opens
// the page with the documented dev switch on (off by default for players).
const origin='http://localhost:8048',menu=origin+'/play/preview/menu.html?dev=1';
const starter=JSON.parse(fs.readFileSync(path.join(generation,'starter-manifest.json'))),join=JSON.parse(fs.readFileSync(path.join(generation,'join-manifest.json')));
const starterHashes=new Set(starter.assets.map(a=>a.sha256)),flightAsset=join.assets.find(a=>!starterHashes.has(a.sha256)&&a.source.startsWith('bgrnd'));
const readState=()=>JSON.parse(fs.readFileSync(path.join(stateDir,'state.json')));
const delay=ms=>new Promise(r=>setTimeout(r,ms));
async function serverWait(predicate,label,timeout=15000){let end=Date.now()+timeout;while(Date.now()<end){let state=readState();if(predicate(state))return state;await delay(50)}throw Error('Server proof timed out: '+label)}
const percentile=(values,p)=>[...values].sort((a,b)=>a-b)[Math.min(values.length-1,Math.floor(values.length*p))];
async function screenshot(page,name){await page.screenshot({path:path.join(output,name+'.png')})}
async function main(){
 fs.mkdirSync(output,{recursive:true});
 const chromium=(await import(require('node:url').pathToFileURL(path.join(modules,'@sparticuz/chromium/build/index.js')).href)).default;
 const launch=async()=>playwright.launch({executablePath:await chromium.executablePath(),headless:true,args:[...chromium.args.filter(a=>!['--disable-web-security','--allow-running-insecure-content'].includes(a)),'--proxy-server=http://127.0.0.1:9','--proxy-bypass-list=127.0.0.1;localhost']});
 let browser=await launch();
 const results=[],secrets=[],allLogs=[],allUrls=[];let page,context,observedLogs=[];
 try{
  context=await browser.newContext({viewport:{width:1920,height:1080}});page=await context.newPage();page.setDefaultTimeout(30000);
  const cdp=await context.newCDPSession(page);await cdp.send('WebAuthn.enable');
  await cdp.send('WebAuthn.addVirtualAuthenticator',{options:{protocol:'ctap2',transport:'internal',hasResidentKey:true,hasUserVerification:true,isUserVerified:true,automaticPresenceSimulation:true}});
  await page.goto(origin+'/identity.html');await page.locator('#login').waitFor({state:'visible'});
  const name='Browser1051'+Date.now().toString().slice(-6);
  // This lane's headless Chromium build delivers keydown but not text insertion
  // (fill and Input.insertText both leave the field empty); set it through the DOM.
  await page.locator('#callsign').evaluate((input,value)=>{input.value=value;input.dispatchEvent(new Event('input',{bubbles:true}))},name);assert.equal(await page.locator('#callsign').inputValue(),name);await page.locator('#register').click();await page.locator('#account').waitFor({state:'visible'});
  await page.locator('#signout').click();await page.locator('#login').waitFor({state:'visible'});await page.locator('#signin').click();await page.locator('#account').waitFor({state:'visible'});
  const originalCookies=await context.cookies();secrets.push(...originalCookies.map(c=>c.value));
  let previousBytes=0;
  const scenarios=[[0,'Classic',1920,1080,'cold'],[1,'Modern',1920,1080,'warm'],[2,'Classic',800,480,'stale'],[3,'Modern',800,480,'storage-refused']];
  for(const [index,style,width,height,cacheCase] of (process.argv[7]==='--modern-cold'?[[0,'Modern',1920,1080,'cold']]:process.argv[7]==='--storage-only'?[scenarios[3]]:scenarios)){
   console.log('CASE',style,width,height,cacheCase);
   if(index===3){await context.close();await browser.close();browser=await launch();context=await browser.newContext({viewport:{width,height}});await context.addCookies(originalCookies);page=await context.newPage();page.setDefaultTimeout(30000);
    await page.addInitScript(()=>{caches.open=async()=>{throw new DOMException('P1051 storage-refused fixture','QuotaExceededError')}});
   }else await page.setViewportSize({width,height});
   const errors=[],logs=[],requests=[],transfers=[],webSocketUrls=[];observedLogs=logs;let currentToken;
   const errorHandler=e=>{errors.push(e.message);console.error('PAGEERROR',e.message)};
   const logHandler=m=>{logs.push(m.type()+': '+m.text());if(m.type()==='error')console.error(m.text());else if(/WS .* stopped|session lost|session restored/.test(m.text()))console.log(m.text())};
   const requestHandler=r=>{let u=new URL(r.url());assert.ok(['localhost','127.0.0.1'].includes(u.hostname),'loopback only');requests.push(u.pathname);allUrls.push(r.url())};
   // Read the real response before delivering it. An async response event can
   // lose its body during navigation and escape main's error/teardown path.
   await page.route('**/api/identity/join-token',async route=>{const response=await route.fetch();if(response.ok()){const body=await response.json();secrets.push(body.token);currentToken=body.token}await route.fulfill({response})});
   page.on('pageerror',errorHandler);page.on('console',logHandler);page.on('request',requestHandler);
   const wsHandler=socket=>{assert.equal(socket.url(),'ws://localhost:8048/game');webSocketUrls.push(socket.url());allUrls.push(socket.url())};page.on('websocket',wsHandler);
   const network=await context.newCDPSession(page);await network.send('Network.enable');const urls=new Map();
   network.on('Network.requestWillBeSent',e=>urls.set(e.requestId,new URL(e.request.url).pathname));network.on('Network.loadingFinished',e=>transfers.push({path:urls.get(e.requestId)||'',bytes:e.encodedDataLength}));
   let failedOnce=false;
   if(index===3)await page.route('**/'+flightAsset.path,async route=>{if(!failedOnce){failedOnce=true;await route.fulfill({status:503,body:'temporary fixture failure'})}else await route.continue()});
   await page.goto(menu);await page.waitForFunction(()=>window.preview?.measurements.firstFrameMs!=null,null,{timeout:90000});
   await page.evaluate(()=>{window.proofAudio=new AudioContext()});await page.waitForFunction(()=>gameFlight.audioStates().length>0&&gameFlight.audioStates().every(s=>s==='suspended'));
   await page.getByRole('button',{name:style,exact:true}).click();await page.waitForFunction(()=>gameFlight.audioStates().every(s=>s==='running'));await page.waitForFunction(s=>gameMenu.metrics().gameStyle===s,style);
   await page.locator('#online-open').click();await page.locator('#online-panel[data-state="Servers"]').waitFor();
   const ticketBefore=requests.filter(p=>p==='/api/identity/join-token').length;
   await page.locator('.online-join').first().click();
   if(index===3){await page.locator('#join-retry').waitFor({timeout:60000});assert.equal(requests.filter(p=>p==='/api/identity/join-token').length,ticketBefore);const timeOrigin=await page.evaluate(()=>performance.timeOrigin);await page.locator('#join-retry').click();await page.locator('#online-panel[data-state="Sides"]').waitFor({timeout:60000});assert.equal(await page.evaluate(()=>performance.timeOrigin),timeOrigin)}
   else await page.locator('#online-panel[data-state="Sides"]').waitFor({timeout:60000});
   const packRequests=requests.filter(p=>p.includes('/packs/'));
   if(index===1)assert.equal(packRequests.length,0,'warm visit fetches no pack files');
   if(index===2)assert.deepEqual(packRequests,['/play/preview/'+flightAsset.path],'only stale file fetched');
   if(index===3){assert.ok(logs.some(l=>l.includes('Storage refused')));const counts=new Map();for(const p of packRequests)counts.set(p,(counts.get(p)||0)+1);for(const [p,count]of counts)assert.equal(count,p.endsWith(flightAsset.sha256)?2:1,'retry must retain earlier memory files')}
   const ticketIndex=requests.findIndex(p=>p==='/api/identity/join-token'),lastAsset=requests.findLastIndex(p=>p.includes('/packs/'));assert.ok(ticketIndex>lastAsset,'ticket after staging');
   await page.locator('.online-side').first().click();await page.locator('#online-panel[data-state="Hangar"]').waitFor();
   const label=style.toLowerCase()+'-'+width+'x'+height;
   await screenshot(page,'docked-'+label);await page.locator('#online-briefing').click();await page.locator('#mission-briefing').waitFor();
   assert.ok((await page.locator('#mission-briefing').innerText()).length>20);await screenshot(page,'briefing-'+label);await page.locator('#online-briefing').click();
   const briefingProof=[];
   for(const file of fs.readdirSync(fixtures).filter(n=>/^briefing-.*\.bin$/.test(n))){const frame=fs.readFileSync(path.join(fixtures,file)),expected=fs.readFileSync(path.join(fixtures,file.replace(/\.bin$/,'.desktop.json')));assert.deepEqual(zlib.brotliDecompressSync(frame.subarray(4)),expected);const decoded=await page.evaluate(async bytes=>Array.from(await gameMenu.decodeBrotli(new Uint8Array(bytes))),[...frame.subarray(4)]);assert.deepEqual(Buffer.from(decoded),expected);briefingProof.push({file,decodedBytes:expected.length,sha256:crypto.createHash('sha256').update(expected).digest('hex'),byteEqual:true})}
   const firstMenu=await page.evaluate(()=>preview.measurements.firstFrameMs);
   const launchAt=await page.evaluate(()=>performance.now());await page.locator('#online-launch').click();await page.locator('#online-panel[data-state="Flight"]').waitFor({timeout:120000});await page.waitForFunction(()=>gameMenu.metrics().flight.frames>3,null,{timeout:60000});
   const firstFlight=await page.evaluate(()=>performance.now()),shipId=(await page.evaluate(()=>gameMenu.metrics())).online.shipId;
   // P1057: record the EXISTING context, never infer AA from browser defaults.
   const webGlContext=await page.evaluate(()=>{const c=document.getElementById('theCanvas'),gl=c.getContext('webgl2')||c.getContext('webgl');if(!gl)throw Error('No active WebGL context');return {attributes:gl.getContextAttributes(),samples:gl.getParameter(gl.SAMPLES),sampleBuffers:gl.getParameter(gl.SAMPLE_BUFFERS),drawingBufferWidth:gl.drawingBufferWidth,drawingBufferHeight:gl.drawingBufferHeight,devicePixelRatio}});
   fs.writeFileSync(path.join(output,'webgl-context-'+label+'.json'),JSON.stringify(webGlContext,null,2));
   const httpTransferToFlightBytes=transfers.reduce((total,item)=>total+item.bytes,0),httpRequestsToFlight=transfers.length;
   await page.locator('#theCanvas').focus();await page.keyboard.press('Backspace',{delay:100});
   await serverWait(s=>s.ships.some(x=>x.Id===shipId&&x.StationId==null&&x.throttle===-1),'initial stop throttle accepted');
   fs.writeFileSync(path.join(stateDir,'desktop-join'),'');await serverWait(s=>s.desktop.desktopLaunched&&s.ships.some(x=>x.Id===s.desktop.MyShipId&&x.StationId==null),'desktop launches');
   const desktopId=readState().desktop.MyShipId;
   await page.waitForFunction(()=>gameMenu.metrics().flight.ships.includes('DesktopPilot'),null,{timeout:360000});
   await serverWait(s=>s.desktop.ships.some(x=>x.ShipId===shipId&&x.StationId<0),'desktop sees browser',360000);
   await serverWait(s=>s.desktop.desktopNavigation==='on station','desktop closes to rendezvous for visible two-ship image',90000);
   await page.locator('#theCanvas').focus();await page.keyboard.press('Backspace',{delay:100});await page.keyboard.press('Equal',{delay:100});
   await page.keyboard.down('w');await page.keyboard.down('ArrowRight');await page.keyboard.down('ArrowUp');await page.waitForTimeout(1000);
   const commanded=(await page.evaluate(()=>gameMenu.metrics())).flight.controls;
   const controlState=await serverWait(s=>s.ships.some(x=>x.Id===shipId&&Math.abs(x.yaw)>.9&&Math.abs(x.pitch)>.9),'accepted yaw/pitch');
   const accepted=controlState.ships.find(x=>x.Id===shipId);for(const axis of ['yaw','pitch','throttle'])assert.ok(Math.abs(accepted[axis]-commanded[axis])<=1/127+.0001,'wire quantization '+axis);
   await page.keyboard.up('w');await page.keyboard.up('ArrowRight');await page.keyboard.up('ArrowUp');await page.keyboard.press('Backspace',{delay:100});
   await page.keyboard.press('Backslash',{delay:100});await serverWait(s=>s.ships.some(x=>x.Id===shipId&&x.throttle===1),'physical backslash reaches desktop full throttle');await page.keyboard.press('Backspace',{delay:100});
   const mountCount=(await page.evaluate(()=>gameMenu.metrics())).flight.fixedMountCount;
   await page.keyboard.press('BracketRight',{delay:100});assert.equal((await page.evaluate(()=>gameMenu.metrics())).flight.selectedMount,mountCount<=0?0:1%mountCount);
   await page.keyboard.press('BracketLeft',{delay:100});assert.equal((await page.evaluate(()=>gameMenu.metrics())).flight.selectedMount,0);await page.keyboard.press('Backquote',{delay:100});
   await page.keyboard.down('Space');await serverWait(s=>s.projectiles>0,'browser actually fires a weapon');await serverWait(s=>s.desktop.desktopShotLaunchers.includes(shipId),'desktop receives this browser ship shot');await page.waitForTimeout(600);await page.keyboard.up('Space');await page.keyboard.press('Backquote',{delay:100});
   fs.writeFileSync(path.join(stateDir,'desktop-fire'),'');await page.waitForFunction(id=>gameMenu.metrics().flight.shotLaunchers.includes(id),desktopId);fs.unlinkSync(path.join(stateDir,'desktop-fire'));
   await screenshot(page,'flight-own-'+label);await page.locator('#flight-contact').click();
   // Native external-camera angles, all real frames. Pick the clearest quiet-sky
   // view for the report's strict PNG budget; never edit screenshot pixels.
   let selectedView=0,bestImage;
   for(let angle=0;angle<6;angle++){await page.waitForTimeout(150);const candidate=await page.screenshot();if(!bestImage||candidate.length<bestImage.length){bestImage=candidate;selectedView=angle}await page.locator('#flight-angle').click()}
   for(let angle=0;angle<selectedView;angle++)await page.locator('#flight-angle').click();await page.waitForTimeout(150);
   fs.writeFileSync(path.join(output,'flight-ships-'+label+'.png'),await page.screenshot());
   if(process.argv[8]==='--small-contact'){
    await page.getByRole('button',{name:'Classic',exact:true}).click();await page.setViewportSize({width:800,height:480});
    await page.waitForTimeout(1000);assert.ok((await page.evaluate(()=>gameMenu.metrics())).flight.ships.includes('DesktopPilot'));
    await screenshot(page,'nearby-classic-800x480');
    await page.getByRole('button',{name:style,exact:true}).click();await page.setViewportSize({width,height});await page.waitForTimeout(1000);
   }
   // Fresh-gesture refusal -> next key retries; no RAF-driven lock requests.
   await page.evaluate(()=>{const canvas=document.getElementById('theCanvas'),original=canvas.requestPointerLock.bind(canvas);window.lockAttempts=0;canvas.requestPointerLock=()=>{if(++window.lockAttempts===1)return Promise.reject(new DOMException('fixture refusal','NotAllowedError'));return original()}});
   await page.locator('#theCanvas').click({position:{x:width/2,y:height/2}});await page.waitForTimeout(150);assert.equal(await page.evaluate(()=>lockAttempts),1);await page.keyboard.press('p');await page.waitForFunction(()=>document.pointerLockElement===document.getElementById('theCanvas'));assert.equal(await page.evaluate(()=>lockAttempts),2);
   await page.mouse.move(width/2+10,height/2+10);await page.waitForTimeout(30);await page.mouse.move(width/2+250,height/2+100);await page.waitForTimeout(200);
   // Headless CDP absolute moves emit cancelling recenter pairs under capture
   // (also measured on a plain canvas). Supply relative DOM input under the REAL
   // trusted pointer lock; production input code receives the standard event.
   await page.evaluate(()=>document.getElementById('theCanvas').dispatchEvent(new MouseEvent('mousemove',{bubbles:true,movementX:240,movementY:80})));
   await serverWait(s=>s.ships.some(x=>x.Id===shipId&&Math.abs(x.yaw)>.01),'locked mouse steering reaches server');await page.keyboard.press('Escape');await page.waitForFunction(()=>document.pointerLockElement===null);
   await page.keyboard.press('Alt+Enter');await page.waitForFunction(()=>!!document.fullscreenElement);await page.keyboard.press('Alt+Enter');await page.waitForFunction(()=>!document.fullscreenElement);
   await page.locator('#theCanvas').focus();await page.keyboard.down('w');await page.keyboard.down('Space');await page.waitForTimeout(250);await page.evaluate(()=>window.dispatchEvent(new Event('blur')));
   await serverWait(s=>s.ships.some(x=>x.Id===shipId&&x.StateM===0&&x.yaw===0&&x.pitch===0&&x.throttle===-1),'blur releases held server input');await page.keyboard.up('w');await page.keyboard.up('Space');
   await page.evaluate(()=>window.dispatchEvent(new Event('focus')));
   await page.evaluate(()=>{Object.defineProperty(document,'hidden',{configurable:true,value:true});document.dispatchEvent(new Event('visibilitychange'))});await page.waitForTimeout(100);assert.ok((await page.evaluate(()=>gameFlight.audioStates())).every(s=>s==='suspended'));
   await page.evaluate(()=>{delete document.hidden;document.dispatchEvent(new Event('visibilitychange'))});await page.waitForTimeout(100);await page.waitForFunction(()=>gameFlight.audioStates().every(s=>s==='running'));
   console.log('INPUT_PROOF_PASS',label);const beforeReconnect=await page.evaluate(()=>gameMenu.metrics());fs.writeFileSync(path.join(stateDir,'drop-ws'),'');await page.waitForFunction(n=>gameMenu.metrics().online.resumeSuccesses>n,beforeReconnect.online.resumeSuccesses,{timeout:15000});assert.equal((await page.evaluate(()=>gameMenu.metrics())).online.shipId,shipId);
   console.log('RECONNECT_PASS',label);
   // Measure a full minute of flight RAF intervals, including actual shared draw work.
   await page.evaluate(()=>{window.flightSample={start:performance.now(),intervals:[],done:false};let previous;const tick=now=>{if(previous!==undefined)flightSample.intervals.push(now-previous);previous=now;if(now-flightSample.start<60000)requestAnimationFrame(tick);else flightSample.done=true};requestAnimationFrame(tick)});
   await page.waitForFunction(()=>flightSample.done,null,{timeout:90000});const sample=await page.evaluate(()=>flightSample);
   console.log('FRAME_SAMPLE_PASS',label);await page.locator('#flight-dock').click();console.log('DOCK_REQUESTED',label);await page.locator('#online-panel[data-state="Hangar"]').waitFor({timeout:90000});
   const docked=await serverWait(s=>s.ships.some(x=>x.Id===shipId&&x.StationId!=null),'authoritative dock');await screenshot(page,'return-'+label);
   const cacheInspection=await page.evaluate(async forbidden=>{
    let files=0,bytes=0;const inspect=text=>{for(const secret of forbidden)if(secret&&text.includes(secret))throw Error('Secret in persistent asset storage')};
    try{for(const name of await caches.keys()){const cache=await caches.open(name);for(const key of await cache.keys()){inspect(key.url);const response=await cache.match(key),data=new Uint8Array(await response.arrayBuffer());inspect(new TextDecoder().decode(data));files++;bytes+=data.length}}}catch(error){if(error.name!=='QuotaExceededError')throw error}
    const database=await new Promise((resolve,reject)=>{const request=indexedDB.open('allegiance-menu-v2');request.onsuccess=()=>resolve(request.result);request.onerror=()=>reject(request.error)});
    for(const name of database.objectStoreNames){const values=await new Promise((resolve,reject)=>{const request=database.transaction(name).objectStore(name).getAll();request.onsuccess=()=>resolve(request.result);request.onerror=()=>reject(request.error)});inspect(JSON.stringify(values))}database.close();return {files,bytes,noSecrets:true};
   },secrets);
   const metrics=await page.evaluate(()=>gameMenu.metrics());assert.ok(!metrics.flight.position.includes('NaN'));assert.equal(metrics.online.malformed,0);assert.equal(metrics.online.resumeSuccesses,beforeReconnect.online.resumeSuccesses+1,'resumed WS survives the unchanged authentication deadline');assert.ok(metrics.flight.clockReady&&metrics.flight.seeded&&metrics.flight.position===metrics.flight.authoritative,'shared prediction applies the authoritative dock transition and clock');
   await page.locator('#online-close').click();await page.locator('#online-panel').waitFor({state:'detached'});await serverWait(s=>!s.players.includes(name)&&!s.ships.some(x=>x.Id===shipId),'deliberate logoff has no zombie');
   const packBytes=transfers.filter(t=>t.path.includes('/packs/')).reduce((n,t)=>n+t.bytes,0),runtimeBytes=transfers.filter(t=>t.path.includes('/_framework/')).reduce((n,t)=>n+t.bytes,0);
   if(index===1)assert.equal(packBytes,0);previousBytes=packBytes;
   results.push({style,width,height,cacheCase,httpTransferToFlightBytes,httpRequestsToFlight,selectedView,webSocketUrls,cacheInspection,packRequests:packRequests.length,packTransferBytes:packBytes,runtimeTransferBytes:runtimeBytes,timeToMenuFromNavigationMs:firstMenu,timeToFlightFromNavigationMs:firstFlight,timeFromLaunchMs:firstFlight-launchAt,briefingProof,shipId,desktopId,commanded,accepted,drawnProjectiles:metrics.flight.drawnProjectiles,receivedProjectiles:metrics.flight.receivedProjectiles,crossVisibility:true,crossShot:true,fullThrottleKeyAccepted:true,hullMountCycling:true,singleWeaponShot:true,pointerRefusalRetried:true,relativeMouseEventAccepted:true,physicalMouseCalibrationQualified:false,fullscreenGesture:true,blurServerNeutral:true,hiddenAudioSuspended:true,reconnectedSameShip:true,dockedStation:docked.ships.find(x=>x.Id===shipId).StationId,logoffNoZombie:true,frameSampleMilliseconds:sample.intervals.reduce((a,b)=>a+b,0),frames:sample.intervals.length,frameP50Ms:percentile(sample.intervals,.5),frameP95Ms:percentile(sample.intervals,.95),metrics});
   assert.deepEqual(errors,[]);assert.ok(!logs.some(line=>line.includes('contained malformed message')),'retired renderer callbacks do not corrupt late packets');fs.writeFileSync(path.join(output,'console-'+label+'.txt'),logs.join('\n'));allLogs.push(...logs);page.off('websocket',wsHandler);page.off('pageerror',errorHandler);page.off('console',logHandler);page.off('request',requestHandler);await page.unroute('**/api/identity/join-token');await network.detach();
   if(index===0){const cacheState=await page.evaluate(async()=>{const c=await caches.open('allegiance-immutable-v2');return (await c.keys()).length});assert.ok(cacheState>100)}
   if(index===1)await page.evaluate(async hash=>{const c=await caches.open('allegiance-immutable-v2');await c.put(new URL('__immutable/'+hash,document.baseURI),new Response(new Uint8Array([1,2,3])));const p=await import(preview.asset?.('./game-platform.js')||'./game-platform.js');await p.cachePut('f'.repeat(64),new Uint8Array([7,8,9]),'application/octet-stream')},flightAsset.sha256);
   if(index===2){assert.equal(await page.evaluate(async()=>{const c=await caches.open('allegiance-immutable-v2');return !!await c.match(new URL('__immutable/'+'f'.repeat(64),document.baseURI))}),false)}
   fs.writeFileSync(path.join(output,'browser-proof.json'),JSON.stringify({browserVersion:browser.version(),headlessGpuRepresentative:false,results},null,2));console.log('PASS',label,cacheCase,'p50/p95',results.at(-1).frameP50Ms,results.at(-1).frameP95Ms);
  }
  // Wrong/stale directory mission cookie must refuse and clean the reserved session.
  const shipsBeforeStaleCookie=readState().ships.map(ship=>ship.Id).sort((a,b)=>a-b);
  await page.route('**/api/servers',async route=>{const response=await route.fetch();let body=await response.json();for(const server of body)for(const mission of server.missions||[])mission.cookie=2147483646;await route.fulfill({response,json:body})});
  await page.locator('#online-open').click();await page.locator('.online-join').first().click();await page.locator('#online-panel [role="alert"]').waitFor({timeout:240000});assert.match(await page.locator('#online-panel [role="alert"]').innerText(),/refused|mission|join/i);await page.locator('#online-close').click();await page.locator('#online-panel').waitFor({state:'detached'});await serverWait(s=>!s.players.includes(name)&&JSON.stringify(s.ships.map(ship=>ship.Id).sort((a,b)=>a-b))===JSON.stringify(shipsBeforeStaleCookie),'stale mission cookie preserves the server ship registry');await page.unroute('**/api/servers');
  // Actual WebGL context loss must stop RAF with the visible reload explanation.
  await page.evaluate(()=>{const canvas=document.getElementById('theCanvas'),gl=canvas.getContext('webgl2')||canvas.getContext('webgl');gl.getExtension('WEBGL_lose_context').loseContext()});await page.getByText(/WebGL context lost\. Reload/).waitFor();
  for(const secret of secrets)if(secret){assert.ok(!allLogs.join('\n').includes(secret));assert.ok(!allUrls.join('\n').includes(secret))}
  const pngs=fs.readdirSync(output).filter(n=>n.endsWith('.png')&&!n.startsWith('flight-own-')),pngBytes=pngs.reduce((n,f)=>n+fs.statSync(path.join(output,f)).size,0);
  fs.writeFileSync(path.join(output,'browser-proof.json'),JSON.stringify({browserVersion:browser.version(),headlessGpuRepresentative:false,pngBytes,passkeyLogin:true,staleCookieNoZombie:true,contextLossReloadMessage:true,noSecretsInLogsOrUrls:true,results},null,2));
  assert.ok(pngBytes<=2*1024*1024,'PNG evidence <= 2 MiB');console.log(JSON.stringify({passed:true,scenarios:results.length,pngBytes}));
 }catch(error){if(page&&!page.isClosed()){fs.writeFileSync(path.join(output,'failure-console.txt'),observedLogs.join('\n'));fs.writeFileSync(path.join(output,'failure-metrics.json'),JSON.stringify(await page.evaluate(()=>window.gameMenu?.metrics()??null).catch(()=>null),null,2));await page.screenshot({path:path.join(output,'failure.png')}).catch(()=>{});fs.writeFileSync(path.join(output,'failure.txt'),String(error)+'\n'+await page.locator('body').innerText().catch(()=>''))}throw error}
 finally{await browser.close()}
}
main().catch(error=>{console.error(error);process.exitCode=1});
