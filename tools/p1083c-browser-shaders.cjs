// NPM_DIRECTORY HOST_STATE OUTPUT GENERATION
// Headless SwiftShader only; installed Playwright/Chromium. Run against a fresh
// zero-bot P1051 proof host serving the published generation. HUD-independent.
const fs = require('node:fs'), path = require('node:path'), assert = require('node:assert/strict');
const [npm, state, output, generation] = process.argv.slice(2).map(p => path.resolve(p));
const origin = JSON.parse(fs.readFileSync(path.join(state, 'state.json'))).origin || 'https://localhost:8443';
const {chromium: playwright} = require(path.join(npm, 'node_modules/playwright'));
const expected = {Bloom:3, DisplacedSphere:1, FsrEasu:1, FsrRcas:3, LinearDepth:1,
  NativePbr:4, SoftParticle:1, Tonemap:2, Volumetric:5};
async function main() {
  fs.mkdirSync(output, {recursive:true});
  const manifest = JSON.parse(fs.readFileSync(path.join(generation, 'battle-manifest.json')));
  const effects = Object.keys(expected).map(name => {
    const asset = manifest.assets.find(a => a.source === 'content/shaders/' + name.toLowerCase() + '.xnb');
    assert.ok(asset, 'published verified ' + name); return {name,...asset};
  });
  const chromium = (await import(require('node:url').pathToFileURL(path.join(npm, 'node_modules/@sparticuz/chromium/build/index.js')).href)).default;
  const browser = await playwright.launch({executablePath:await chromium.executablePath(), headless:true,
    args:[...chromium.args.filter(a => !['--disable-web-security','--allow-running-insecure-content','--single-process'].includes(a)), '--ignore-certificate-errors']});
  const receipt = {rendererMode:'headless SwiftShader SOFTWARE', browser:browser.version(), errors:[], console:[], effects, requests:[]};
  const context = await browser.newContext({viewport:{width:1280,height:800},ignoreHTTPSErrors:true});
  try {
    await context.route('**/*', async route => {
      assert.ok(['localhost','127.0.0.1'].includes(new URL(route.request().url()).hostname), 'proof stays local');
      await route.continue();
    });
    await context.addInitScript(() => {
      window.p1083GL = {active:false, draws:0, compiled:0, linked:0, errors:[]};
      const getContext = HTMLCanvasElement.prototype.getContext;
      HTMLCanvasElement.prototype.getContext = function(type,...args) {
        const gl = getContext.call(this,type,...args);
        if (this.id !== 'theCanvas' || !['webgl','webgl2'].includes(type) || !gl || gl.p1083Wrapped) return gl;
        gl.p1083Wrapped = true;
        const error = gl.getError.bind(gl);
        gl.getError = () => {const code=error();if (code && p1083GL.active) p1083GL.errors.push('GL error '+code);return code;};
        for (const method of ['drawArrays','drawElements']) {
          const draw = gl[method].bind(gl);
          gl[method] = (...values) => {if(p1083GL.active)p1083GL.draws++;return draw(...values);};
        }
        const compile = gl.compileShader.bind(gl), link = gl.linkProgram.bind(gl);
        gl.compileShader = shader => {
          compile(shader); if(p1083GL.active)p1083GL.compiled++;
          if(!gl.getShaderParameter(shader,gl.COMPILE_STATUS)) p1083GL.errors.push(gl.getShaderInfoLog(shader));
        };
        gl.linkProgram = program => {
          link(program); if(p1083GL.active)p1083GL.linked++;
          if(!gl.getProgramParameter(program,gl.LINK_STATUS)) p1083GL.errors.push(gl.getProgramInfoLog(program));
        };
        window.p1083CheckGL = () => gl.getError();
        return gl;
      };
    });
    const page = await context.newPage(); page.setDefaultTimeout(120000);
    page.on('pageerror',error => receipt.errors.push(error.message));
    page.on('console',message => receipt.console.push(message.text()));
    page.on('request',request => {
      const hash = request.url().split('/packs/')[1];
      if(effects.some(e => e.sha256===hash)) receipt.requests.push(hash);
    });
    const cdp = await context.newCDPSession(page); await cdp.send('WebAuthn.enable');
    await cdp.send('WebAuthn.addVirtualAuthenticator',{options:{protocol:'ctap2',transport:'internal',hasResidentKey:true,
      hasUserVerification:true,isUserVerified:true,automaticPresenceSimulation:true}});
    await page.goto(origin + '/identity.html');
    await page.locator('#callsign').fill('Load1083' + Date.now().toString().slice(-8));
    await page.locator('#register').click(); await page.locator('#account').waitFor({state:'visible'});
    await page.goto(origin + '/play/menu.html?dev=1');
    await page.waitForFunction(() => window.preview?.measurements.firstFrameMs != null);
    await page.locator('#online-open').click(); await page.locator('#join-password').waitFor();
    await page.locator('#join-password').fill(fs.readFileSync(path.join(state,'game-password'),'utf8').trim());
    await page.locator('.online-join').first().click(); await page.locator('.online-side').first().click();
    await page.locator('#online-panel[data-state="Hangar"]').waitFor();
    await page.waitForFunction(() => gameMenu.prepareShaderProof(), null, {polling:100});
    receipt.before = await page.evaluate(() => gameMenu.metrics());
    receipt.probe = await page.evaluate(() => {
      p1083GL.active=true;
      try {const result=gameMenu.probeShaders();p1083CheckGL();return result;}
      finally {p1083GL.active=false;}
    });
    receipt.gl = await page.evaluate(() => p1083GL);
    assert.equal(receipt.probe.profile,'HiDef');
    assert.equal(receipt.probe.rows.length,9);
    assert.equal(receipt.probe.rows.reduce((n,r)=>n+r.techniques,0),16);
    assert.equal(receipt.gl.linked,21,'each pass linked in the real context');
    receipt.webgl=receipt.before.webgl;
    assert.match(receipt.webgl.unmaskedRenderer,/SwiftShader/i);
    assert.deepEqual(receipt.probe.rows.map(r=>r.name),Object.keys(expected));
    for(const row of receipt.probe.rows) {
      assert.equal(row.passes.length,expected[row.name],row.name+' every pass');
      assert.ok(row.passes.every(p=>p.drawn));
    }
    assert.equal(receipt.gl.draws,22,'21 effect passes plus perspective depth-coordinate test');
    assert.deepEqual(receipt.gl.errors,[]);
    assert.deepEqual(receipt.errors,[]);
    assert.deepEqual(receipt.console.filter(s=>/Volumetric unavailable|shader.*fallback|shader compilation failed/i.test(s)),[]);
    assert.equal(new Set(receipt.requests).size,9,'all nine downloaded through verified content path');
    assert.equal(receipt.before.flight?.combat?.volumetric?.requested ?? false,false);
    assert.equal(receipt.probe.softParticle.rendererUsable,true);
    receipt.probe.softParticle.perspectiveDepthMask = Array.from(receipt.probe.softParticle.perspectiveDepthMask);
    assert.deepEqual(receipt.probe.softParticle.perspectiveDepthMask,[255,0,0,0]);
    receipt.passed=true;
    console.log('P1083C PASS: 9/9 effects, 16 techniques, 21 passes drawn; HiDef; no fallback or WebGL errors');
    await page.locator('#online-close').click();
  } catch(error) {receipt.failure=String(error);const p=context.pages()[0];if(p)receipt.diagnostic=await p.evaluate(()=>({gl:p1083GL})).catch(()=>null);throw error;}
  finally {
    fs.writeFileSync(path.join(output,'load.json'),JSON.stringify(receipt,null,2)+'\n');
    await context.close();await browser.close();
  }
}
main().catch(error=>{console.error('P1083C FAIL:',error);process.exitCode=1;});
