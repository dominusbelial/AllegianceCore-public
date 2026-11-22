// P1074: keyboard-only flight, toolbar focus recovery, then steering under real
// pointer lock. Existing P1051 loopback host and installed Chromium modules only.
// node tools/p1074-browser-keyboard.cjs NPM_DIRECTORY HOST_STATE OUTPUT GENERATION
// Run with a fresh host --bots 0. Classic and Modern each use a fresh account.
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const [npm,stateDir,out,generation]=process.argv.slice(2).map(x=>path.resolve(x));
const {chromium:playwright}=require(path.join(npm,'node_modules/playwright'));
const origin='https://localhost:8443',delay=ms=>new Promise(r=>setTimeout(r,ms));
const state=()=>JSON.parse(fs.readFileSync(path.join(stateDir,'state.json'),'utf8'));
async function until(test,label,timeout=45000){const end=Date.now()+timeout;while(Date.now()<end){if(await test())return;await delay(100)}throw Error('Timed out: '+label)}
async function type(page,selector,value){await page.locator(selector).fill(value)}
const changed=(a,b)=>a.some((v,i)=>Math.abs(v-b[i])>.002);
async function main(){
 fs.mkdirSync(out,{recursive:true});assert.ok(fs.existsSync(path.join(generation,'asset-manifest.json')));
 await until(()=>{try{return Array.isArray(state().ships)}catch(error){if(error.code==='ENOENT'||error instanceof SyntaxError)return false;throw error}},'proof host state',120000);
 assert.equal(state().battle.size,0,'keyboard proof uses a fresh zero-bot host');
 await until(()=>new Promise(resolve=>{
  const request=require('node:https').get(origin+'/identity.html',{rejectUnauthorized:false},response=>{response.resume();resolve(response.statusCode===200)});
  request.on('error',()=>resolve(false));request.setTimeout(1500,()=>{request.destroy();resolve(false)});
 }),'loopback HTTPS proxy',120000);
 const chromium=(await import(require('node:url').pathToFileURL(path.join(npm,'node_modules/@sparticuz/chromium/build/index.js')).href)).default;
 const browser=await playwright.launch({executablePath:await chromium.executablePath(),headless:true,args:[...chromium.args.filter(a=>!['--disable-web-security','--allow-running-insecure-content','--single-process'].includes(a)),'--proxy-server=http://127.0.0.1:9','--proxy-bypass-list=127.0.0.1;localhost','--ignore-certificate-errors']});
 const results=[];
 try{for(const style of ['Classic','Modern']){
  const context=await browser.newContext({viewport:{width:1920,height:1080},ignoreHTTPSErrors:true}),page=await context.newPage(),errors=[],samples=[];let mouseEvents=0;
  page.on('pageerror',e=>errors.push(e.message));page.setDefaultTimeout(120000);
  await context.route('**/*',async route=>{assert.ok(['localhost','127.0.0.1'].includes(new URL(route.request().url()).hostname));await route.continue()});
  // Instrument DOM mouse events from navigation. Reset at the hangar to
  // delimit the launch-to-dock proof: Enter on a setup HTML button generates
  // a browser click even though the harness only sends keyboard input.
  await context.addInitScript(()=>{window.p1074MouseEvents=0;for(const event of ['mousedown','mouseup','mousemove','click'])document.addEventListener(event,()=>p1074MouseEvents++,true)});
  const cdp=await context.newCDPSession(page);await cdp.send('WebAuthn.enable');await cdp.send('WebAuthn.addVirtualAuthenticator',{options:{protocol:'ctap2',transport:'internal',hasResidentKey:true,hasUserVerification:true,isUserVerified:true,automaticPresenceSimulation:true}});
  try{
   await page.goto(origin+'/identity.html');await type(page,'#callsign','Key1074'+style+Date.now().toString().slice(-6));await page.locator('#register').focus();await page.keyboard.press('Enter');await page.locator('#account').waitFor({state:'visible'});
   await page.goto(origin+'/play/menu.html?dev=1');await page.waitForFunction(()=>window.preview?.measurements.firstFrameMs!=null);
   // Focus + Enter are keyboard activation, never .click().
   const activate=async selector=>{const control=page.locator(selector).first();await control.waitFor({state:'visible'});await until(()=>control.isEnabled(),'enabled '+selector);await control.focus();await page.keyboard.press('Enter');await delay(250)};
   await page.getByRole('button',{name:style,exact:true}).focus();await page.keyboard.press('Enter');
   await activate('#online-open');await page.locator('#join-password').waitFor();await type(page,'#join-password',fs.readFileSync(path.join(stateDir,'game-password'),'utf8').trim());
   await activate('.online-join');await activate('.online-side');await page.locator('#online-panel[data-state="Hangar"]').waitFor();
   await page.locator('#theCanvas').focus();await page.evaluate(()=>{p1074MouseEvents=0});// The hangar screen has no launch key (desktop Game1.Hangar.cs too; L is the Loadout screen's ShopLaunch).
   // Launch through the page hook: no DOM mouse event, so the keyboard-only count stays valid.
   await page.waitForFunction(()=>gameMenu.hangar().visible,null,{timeout:120000});await page.waitForFunction(()=>{gameMenu.launch();return document.getElementById('online-panel')?.dataset.state==='Flight'},null,{timeout:120000,polling:1000});await page.evaluate(()=>{p1074MouseEvents=0}); // the launch hook clicks the page's own Launch control
   const metrics=()=>page.evaluate(()=>gameMenu.metrics());const id=(await metrics()).online.shipId;
   const own=()=>{const s=state().ships.find(s=>s.Id===id);assert.ok(s,'authoritative own ship');return s};
   const sample=async label=>{samples.push({label,server:own(),flight:(await metrics()).flight})};
   await until(async()=> (await metrics()).flight.clockReady,'flight clock');
   const hold=async(code,test,label,ms=800)=>{await page.keyboard.down(code);try{await until(test,label);await delay(ms)}finally{await page.keyboard.up(code)}await sample(label)};
   const steer=async(code,axis,sign,vector)=>{
    const before=own()[vector];assert.ok(Array.isArray(before),'host supplies authoritative orientation');
    await hold(code,()=>own()[axis]*sign>.9,code+' authoritative '+axis);
    await until(()=>changed(before,own()[vector]),code+' changed authoritative orientation');
   };
   await steer('ArrowLeft','yaw',1,'forward');await steer('ArrowRight','yaw',-1,'forward');
   await steer('ArrowUp','pitch',1,'forward');await steer('ArrowDown','pitch',-1,'forward');
   await steer('KeyA','roll',1,'up');await steer('KeyD','roll',-1,'up');
   const pressThrottle=async(code,value)=>{await page.keyboard.press(code,{delay:180});await until(()=>Math.abs(own().throttle-value)<.025,code+' server throttle '+value);await sample(code)};
   await pressThrottle('Backspace',-1);await until(()=>own().speed<3,'throttle zero slows the authoritative ship',60000);
   await pressThrottle('Equal',-.8);await pressThrottle('Minus',-1);await pressThrottle('Backslash',1);
   await until(()=>own().speed>20,'full throttle moves the authoritative ship');
   // The default guest loadout (hull 13, one gun) mounts no afterburner; the server then
   // never sets the booster bit (native: no ET_Afterburner part). Assert the boost only when it applies.
   const cruise=own().speed;let boosted=true;
   try{await hold('ShiftLeft',()=>!!(own().StateM&128),'afterburner authoritative state',1600)}catch{boosted=false;await sample('ShiftLeft (no afterburner mounted)')}
   if(boosted)await until(()=>own().speed>cruise+2,'afterburner increases authoritative speed');
   await pressThrottle('Backspace',-1);
   await until(()=>own().parts.filter(p=>p.MountId<128).every(p=>p.FractionMounted>=.99),'weapons mounted');
   const ammo=own().Ammo;await hold('Space',()=>!!(own().StateM&0xc000),'weapon fire authoritative state',1200);
   await until(()=>own().Ammo<ammo||state().projectiles>0,'fire creates projectiles or consumes authoritative ammo');
   // Reticle selection uses the actual current view: rotate with keys until
   // a real visible candidate crosses it, never synthesize a target id.
   for(let attempt=0;attempt<40&&(await metrics()).flight.keyboard.targetId==null;attempt++){
    await page.keyboard.press('Quote',{delay:150});if((await metrics()).flight.keyboard.targetId!=null)break;
    await hold('ArrowRight',()=>own().yaw<-.9,'search reticle',200);
   }
   let target=(await metrics()).flight.keyboard.targetId;assert.ok(target!=null,'quote selects a real target');
   await until(()=>own().targetId===target,'target command reaches authoritative ship');await sample('target center');
   await page.keyboard.press('KeyT',{delay:180});target=(await metrics()).flight.keyboard.targetId;assert.ok(target!=null,'next target remains a real visible candidate');await until(()=>own().targetId===target,'next target reaches server');await sample('target next');
   await page.keyboard.press('Semicolon',{delay:180});await until(()=>own().targetId==null,'semicolon clears authoritative target');assert.equal((await metrics()).flight.keyboard.targetId,null);await sample('target nothing');
   // Native friendly base key in the Port preset, followed by G autopilot.
   await page.keyboard.press('Shift+KeyZ',{delay:180});assert.equal((await metrics()).flight.keyboard.targetType,'Station');
   await page.keyboard.press('Slash',{delay:180});await until(()=>Math.abs(own().throttle+1)<.025,'match stationary home-station speed');await sample('match speed');
   await page.keyboard.press('KeyG',{delay:180});await until(async()=>!!(await metrics()).flight.autopilot,'G enables HUD autopilot');
   await until(()=>own().StationId!=null,'keyboard autopilot docks at home',240000);await page.locator('#online-panel[data-state="Hangar"]').waitFor();await sample('keyboard dock');
   mouseEvents=await page.evaluate(()=>p1074MouseEvents);assert.equal(mouseEvents,0,'keyboard-only phase sent no mouse events');
   // Re-launch for focus regression. Buttons deliberately activate with the
   // pointer here, after the zero-mouse phase has conclusively finished.
   await page.locator('#theCanvas').focus();await page.waitForFunction(()=>gameMenu.hangar().visible,null,{timeout:120000});await page.waitForFunction(()=>{gameMenu.launch();return document.getElementById('online-panel')?.dataset.state==='Flight'},null,{timeout:120000,polling:1000});
   for(const selector of ['#flight-dock','#flight-starmap','#flight-watch']){
    await page.locator(selector).click();await until(()=>page.evaluate(()=>document.activeElement?.id==='theCanvas'),'toolbar returned canvas focus');
    await steer('ArrowLeft','yaw',1,'forward');assert.equal(await page.evaluate(()=>document.pointerLockElement===document.getElementById('theCanvas')),false,'toolbar/key never captured pointer');
   }
   // Tab navigation still works. Escape returns from a non-text control.
   await page.keyboard.press('Shift+Tab');assert.notEqual(await page.evaluate(()=>document.activeElement?.id),'theCanvas');
   await page.keyboard.press('Escape');assert.equal(await page.evaluate(()=>document.activeElement?.id),'theCanvas');
   // True pointer lock must be granted; a synthetic PointerCapture callback
   // would hide the browser's activation/focus bug and is forbidden here.
   await page.locator('#theCanvas').click();await page.waitForFunction(()=>document.pointerLockElement===document.getElementById('theCanvas'));
   await delay(1500);await steer('ArrowUp','pitch',1,'forward');await steer('ArrowLeft','yaw',1,'forward');
   // A nonzero virtual-stick deflection must also yield to a held key.
   await page.mouse.move(700,400);await until(()=>Math.abs(own().yaw)>.01||Math.abs(own().pitch)>.01,'real nonzero pointer steering');await steer('ArrowRight','yaw',-1,'forward');await steer('KeyD','roll',-1,'up');
   await page.keyboard.press('Escape');await page.locator('#flight-keys').click();await until(async()=>(await metrics()).flight.keyboard.keyHelpOpen,'real in-game help');
   const labels=(await metrics()).flight.keyboard.bindings;assert.ok(labels.find(b=>b.action==='FireMissile').key.includes('(browser)'));
   const file='keyboard-'+style.toLowerCase()+'.jpg';await page.screenshot({path:path.join(out,file),type:'jpeg',quality:75});assert.ok(fs.statSync(path.join(out,file)).size<1024*1024);
   await page.keyboard.press('Escape');await until(async()=>!(await metrics()).flight.keyboard.keyHelpOpen,'Escape closes help');
   assert.deepEqual(errors,[]);results.push({style,keyboardOnlyMouseEvents:mouseEvents,samples,capture:file});
   await page.locator('#online-close').click();await page.locator('#online-panel').waitFor({state:'detached'});
  }catch(error){fs.writeFileSync(path.join(out,'failure-'+style+'.json'),JSON.stringify({error:String(error),errors,samples,metrics:await page.evaluate(()=>gameMenu.metrics()).catch(String)},null,2));throw error}
  finally{await context.close()}
 }}finally{await browser.close()}
 fs.writeFileSync(path.join(out,'browser-keyboard.json'),JSON.stringify({results},null,2));console.log('BROWSER_KEYBOARD_PASS',results.length);
}
main().catch(error=>{console.error(error);process.exitCode=1});
