// Local P1065 battle flow: --bots 10 as requested; --bots 5 measures true 5v5.
// The existing proof host counts ships PER SIDE. Uses installed modules only.
// NPM_DIRECTORY HOST_STATE OUTPUT GENERATION [SAMPLE_SECONDS=120] [--baseline]
const fs = require('node:fs'), path = require('node:path'), assert = require('node:assert/strict');
const modules = path.resolve(process.argv[2], 'node_modules'), stateDir = path.resolve(process.argv[3]);
const out = path.resolve(process.argv[4]), generation = path.resolve(process.argv[5]);
const seconds = Number(process.argv[6] || 120), baseline = process.argv.includes('--baseline');
assert.ok(seconds >= 120 && seconds <= 300, 'at least 120 seconds of live battle per style');
const { chromium: playwright } = require(path.join(modules, 'playwright'));
const state = () => JSON.parse(fs.readFileSync(path.join(stateDir, 'state.json')));
const wait = ms => new Promise(resolve => setTimeout(resolve, ms));
async function until(test, label, timeout = 180000) {
  const end = Date.now() + timeout;
  while (Date.now() < end) { if (await test()) return; await wait(50); }
  throw Error('Timed out: ' + label);
}
async function input(page, selector, value) {
  await page.locator(selector).evaluate((element, text) => {
    element.value = text; element.dispatchEvent(new Event('input', { bubbles: true }));
    element.dispatchEvent(new Event('change', { bubbles: true }));
  }, String(value));
}
async function fixture(ship, action) {
  const response = path.join(stateDir, 'combat-response.json');
  fs.rmSync(response, { force: true });
  fs.writeFileSync(path.join(stateDir, 'combat-request.tmp'), JSON.stringify({ ship, action }));
  fs.renameSync(path.join(stateDir, 'combat-request.tmp'), path.join(stateDir, 'combat-request.json'));
  await until(() => fs.existsSync(response), 'host fixture ' + action, 10000);
  const receipt = JSON.parse(fs.readFileSync(response)); assert.equal(receipt.action, action); return receipt;
}
const percentile = (a, p) => [...a].sort((x, y) => x - y)[Math.min(a.length - 1, Math.floor(a.length * p))];
async function main() {
  fs.mkdirSync(out, { recursive: true });
  await until(() => { try { return state().battle.ready; } catch { return false; } }, '5v5 ready', 600000);
  const teamSize = state().battle.size;
  assert.ok([5, 10].includes(teamSize), '5v5 measurement or requested --bots 10 stress fight');
  fs.writeFileSync(path.join(stateDir, 'combat-observe'), '');
  const chromium = (await import(require('node:url').pathToFileURL(path.join(modules, '@sparticuz/chromium/build/index.js')).href)).default;
  const launch = async () => playwright.launch({ executablePath: await chromium.executablePath(), headless: true,
    args: [...chromium.args.filter(a => !['--disable-web-security', '--allow-running-insecure-content', '--single-process'].includes(a)),
      '--proxy-server=http://127.0.0.1:9', '--proxy-bypass-list=127.0.0.1;localhost', '--ignore-certificate-errors'] });
  let browser = await launch();
  const evidence = { baseline, seconds, teamSize, browser: browser.version(), softwareGpuRepresentative: false, runs: [] };
  const save = () => fs.writeFileSync(path.join(out, 'combat.json'), JSON.stringify(evidence, null, 2));
  const errors = [];
  try {
    for (const style of ['Classic', 'Modern']) {
      const context = await browser.newContext({ viewport: { width: 1280, height: 800 }, ignoreHTTPSErrors: true });
      await context.addInitScript(require('./p1075-frame-probe.cjs'));
      const page = await context.newPage(); page.setDefaultTimeout(120000);
      page.on('pageerror', error => errors.push(error.message));
      const consoleLines = [];
      page.on('console', message => { consoleLines.push(message.text()); if (consoleLines.length > 300) consoleLines.shift(); });
      const run = { style, samples: [], captures: [], fixtures: [], consoleLines }; evidence.runs.push(run);
      try {
        const cdp = await context.newCDPSession(page);
        await cdp.send('WebAuthn.enable');
        await cdp.send('WebAuthn.addVirtualAuthenticator', { options: { protocol: 'ctap2', transport: 'internal',
          hasResidentKey: true, hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true } });
        await page.goto('https://localhost:8443/identity.html');
        await input(page, '#callsign', 'Browser1079' + Date.now().toString().slice(-8));
        await page.locator('#register').click(); await page.locator('#account').waitFor({ state: 'visible' });
        await page.goto('https://localhost:8443/play/menu.html?dev=1');
        await page.waitForFunction(() => window.preview?.measurements.firstFrameMs != null);
        await page.locator('#online-open').click(); await page.locator('#join-password').waitFor();
        await input(page, '#join-password', fs.readFileSync(path.join(stateDir, 'game-password'), 'utf8').trim());
        await page.locator('.online-join').first().click(); await page.locator('.online-side').first().click();
        await page.locator('#online-panel[data-state="Hangar"]').waitFor();
        await page.getByRole('button', { name: style, exact: true }).click();
        await page.waitForFunction(() => gameMenu.hangar().launchArt);
        await page.waitForFunction(() => { gameMenu.launch(); return document.getElementById('online-panel')?.dataset.state === 'Flight'; }, null, { polling: 100 });
        const id = (await page.evaluate(() => gameMenu.metrics())).online.shipId; run.pilot = id;
        await fixture(id, 'protect');
        await page.locator('#theCanvas').focus(); await page.keyboard.press('Backspace', { delay: 100 });
        const start = await page.evaluate(() => gameMenu.metrics());
        const target = state().chart.find(w => w.ClusterId === start.flight.sectorId && w.Name === state().battle.targetSector)?.destinationSectorId;
        assert.ok(target != null && target !== start.flight.sectorId);
        const warp = state().chart.find(w => w.ClusterId === start.flight.sectorId && w.destinationSectorId === target);
        const option = `#flight-warp option[value="${warp.WarpId}"]`;
        if (!await page.locator(option).count()) {
          await page.locator('#flight-waypoint').click();
          for (const [i, axis] of ['x', 'y', 'z'].entries()) await input(page, '#waypoint-' + axis, warp.Position[i] * .85);
          await page.locator('#waypoint-go').click(); await page.locator(option).waitFor({ state: 'attached', timeout: 240000 });
        }
        await page.locator('#flight-warp').selectOption(String(warp.WarpId));
        await page.waitForFunction(sector => gameMenu.metrics().flight.sectorId === sector, target, { timeout: 240000 });
        await page.locator('#flight-waypoint').click();
        for (const axis of ['x', 'y', 'z']) await input(page, '#waypoint-' + axis, axis === 'y' ? 550 : 0);
        await page.locator('#waypoint-go').click();
        await until(async () => (await page.evaluate(() => gameMenu.metrics())).flight.battle.contacts >= 3, 'real battle contacts', 240000);
        await until(async () => {
          const m = await page.evaluate(() => gameMenu.metrics().flight);
          const point = m.position.match(/X = ([^,]+), Y = ([^,]+), Z = ([^ }]+)/).slice(1).map(Number);
          return Math.hypot(point[0], point[1] - 550, point[2]) < 180;
        }, 'pilot reaches rendezvous through waypoint input', 240000);
        await page.locator('#theCanvas').focus(); await page.keyboard.press('Backspace', { delay: 100 });
        if (!baseline) await page.waitForFunction(() => gameMenu.metrics().flight.combat.ready);
        console.log(style, 'parked in', target, 'sampling', seconds, 'seconds');
        await page.evaluate(() => {
          p1075Frames.enabled = true;
          window.p1079Timing = { active: true, intervals: [] };
          let previous;
          function tick(now) {
            if (previous != null) p1079Timing.intervals.push(now - previous);
            previous = now;
            if (p1079Timing.active) requestAnimationFrame(tick);
          }
          requestAnimationFrame(tick);
        });
        run.startMs = state().sampledAt;
        const end = Date.now() + seconds * 1000;
        while (Date.now() < end) {
          const beforeMs = state().sampledAt;
          const m = await page.evaluate(() => gameMenu.metrics().flight);
          assert.ok(m.flying && m.sectorId === target, 'pilot remains in the rendezvous');
          run.samples.push({ hostMs: state().sampledAt, beforeMs, combat: m.combat, battle: m.battle, gunBolts: m.drawnProjectiles });
          await wait(100);
        }
        run.endMs = state().sampledAt;
        const intervals = await page.evaluate(() => { p1079Timing.active = false; return p1079Timing.intervals; });
        run.timing = { frames: intervals.length, p50Ms: percentile(intervals, .5), p95Ms: percentile(intervals, .95) };
        assert.ok(run.samples.at(-1).gunBolts > run.samples[0].gunBolts || run.samples.some(s => s.combat && ['missiles', 'shieldFlares', 'hullSparks'].some(k => s.combat[k] > 0)), 'live combat during the timing window');
        if (!baseline) {
          // Keep the timed 120-second window intact, then allow every event
          // in its final second to reach its first draw before correlation.
          run.correlationTail = [];
          const tailEnd = Date.now() + 1000;
          while (Date.now() < tailEnd) {
            const beforeMs = state().sampledAt;
            const m = await page.evaluate(() => gameMenu.metrics().flight);
            assert.ok(m.flying && m.sectorId === target);
            run.correlationTail.push({ beforeMs, hostMs: state().sampledAt, combat: m.combat });
            await wait(100);
          }
          if (teamSize === 10) for (const key of ['missiles', 'missileTrailParticles', 'explosions', 'shieldFlares', 'hullSparks', 'debris'])
            assert.ok(run.samples.some(s => s.combat[key] > 0), 'live battle draws ' + key);
          const draws = new Map();
          // WASM TickCount64 and host uptime have different origins. Translate
          // first-draw time through the simultaneous sample's monotonic clocks.
          // Host state refresh is 50 ms; include that uncertainty in evidence.
          for (const sample of [...run.samples, ...run.correlationTail]) for (const d of sample.combat.explosionDraws) {
            const key = [d.sector, d.type, ...d.position, d.receivedMs].join(',');
            if (!draws.has(key)) draws.set(key, { ...d,
              hostDrawnMs: (sample.beforeMs + sample.hostMs) * .5 - (sample.combat.localMs - d.drawnMs),
              clockUncertaintyMs: 100 + (sample.hostMs - sample.beforeMs) * .5 });
          }
          const events = fs.readFileSync(path.join(stateDir, 'combat-events.jsonl'), 'utf8').trim().split('\n').filter(Boolean).map(JSON.parse);
          run.correlations = events.filter(e => e.localMs >= run.startMs && e.localMs < run.endMs).map(e => {
            const match = e.line.match(/type (\d+) cluster (-?\d+).* X = ([^,]+), Y = ([^,]+), Z = ([^ }]+)/);
            assert.ok(match, 'server explosion log parses');
            const [type, sector, ...position] = match.slice(1).map(Number);
            if (sector !== target) return null;
            const drawn = [...draws.values()].find(d => d.type === type && d.sector === sector && d.position.every((v, i) => Math.abs(v - position[i]) < .02) && d.hostDrawnMs >= e.localMs - d.clockUncertaintyMs);
            assert.ok(drawn, 'every local server explosion reaches a draw');
            assert.ok(drawn.hostDrawnMs - e.localMs + drawn.clockUncertaintyMs <= 1000, 'server explosion draws within one second');
            return { serverMs: e.localMs, ...drawn, latencyMs: drawn.hostDrawnMs - e.localMs };
          }).filter(Boolean);
          if (teamSize === 10) assert.ok(run.correlations.length > 0, 'actual server explosion correlation');
          // 250-ms event bursts. Fixture metrics are separate from live assertions.
          for (const action of ['missile', 'shield', 'props', 'explosion', 'pod']) {
            if (action === 'missile') await page.locator('#theCanvas').focus();
            if (action === 'pod') {
              // Leave the firing scrum through ordinary flight input before the
              // controlled own-death hook. A native pod can die to one round;
              // this gives the capture burst time to show its actual mesh.
              await page.locator('#flight-waypoint').click();
              for (const [axis, value] of [['x', 0], ['y', 4000], ['z', 0]]) await input(page, '#waypoint-' + axis, value);
              await page.locator('#waypoint-go').click();
              await until(async () => {
                const m = await page.evaluate(() => gameMenu.metrics().flight);
                const p = m.position.match(/X = ([^,]+), Y = ([^,]+), Z = ([^ }]+)/).slice(1).map(Number);
                return Math.hypot(p[0], p[1] - 4000, p[2]) < 180;
              }, 'quiet own-death capture position', 180000);
              await page.locator('#theCanvas').focus(); await page.keyboard.press('Backspace', { delay: 100 });
              run.ownDeathPosition = (await page.evaluate(() => gameMenu.metrics().flight)).position;
            }
            const receipt = await fixture(id, action); run.fixtures.push(receipt);
            const burstStarted = Date.now();
            for (let frame = 0; frame < 8; frame++) {
              const m = await page.evaluate(() => gameMenu.metrics().flight);
              const file = `${style.toLowerCase()}-${action}-${frame}.jpg`;
              await page.locator('#theCanvas').screenshot({ path: path.join(out, file), type: 'jpeg', quality: 85 });
              run.captures.push({ action, frame, file, combat: m.combat, flying: m.flying, position: m.position, authoritative: m.authoritative, elapsedMs: Date.now() - burstStarted });
              await wait(Math.max(0, burstStarted + (frame + 1) * 250 - Date.now()));
            }
            if (action === 'pod') {
              assert.ok(run.captures.some(c => c.action === 'pod' && c.combat.ownPods > 0 && c.combat.ownPodMeshes > 0), 'own lifepod draws after real eject');
              assert.equal(receipt.details.Dead, false, 'the production death path leaves a live pod');
              assert.ok(run.captures.some(c => c.action === 'pod' && c.combat.explosionDraws.some(d =>
                d.ejectShipId === id && d.drawnMs - d.receivedMs <= 1000)), 'the own death blast draws within one second');
            }
          }
          for (const key of ['mines', 'chaff', 'probes', 'treasures'])
            assert.ok(run.captures.some(c => c.action === 'props' && c.combat[key] > 0), 'nearby fixture draws ' + key);
        }
        run.frames = await page.evaluate(() => p1075Frames);
        assert.ok(run.frames.sampled > 30); assert.deepEqual(run.frames.discarded, []); assert.deepEqual(run.frames.errors, []);
        assert.deepEqual(errors, []); save();
        await page.locator('#online-close').click();
        await page.locator('#online-panel').waitFor({ state: 'detached' });
      } finally { await context.close(); await browser.close(); save(); }
      if (style !== 'Modern') browser = await launch();
    }
    console.log('P1079_COMBAT_PASS', baseline ? 'baseline' : 'after');
  } catch (error) { evidence.failure = String(error); evidence.errors = errors; save(); throw error; }
  finally { await browser.close(); }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
