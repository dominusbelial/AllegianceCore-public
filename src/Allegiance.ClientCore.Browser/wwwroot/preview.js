/* The capability check runs before any runtime, framework or artwork download. */
(() => {
  'use strict';
  const app = document.getElementById('app');
  const assets = JSON.parse(document.currentScript?.dataset.assets || '{}');
  const asset = name => { const mapped = assets[name.replace(/^\.\//, '')]; return mapped ? (name.startsWith('./') ? './' : '') + mapped : name; };
  const measurements = { firstFrameMs: null, cpu: [], interval: [], webgl: null };
  let frameId = 0;
  let startupTimer;
  const pacing = window.FramePacing ? new window.FramePacing() : null;
  window.preview = {
    measurements,
    asset,
    pacing: () => ({...pacing?.snapshot(), bufferWidth:document.getElementById('theCanvas')?.width,bufferHeight:document.getElementById('theCanvas')?.height,dpr:Math.min(window.devicePixelRatio||1,2)}),
    startFrames(instance) {
      cancelAnimationFrame(frameId);
      pacing?.reset();
      const canvas = document.getElementById('theCanvas');
      let previous, effectLoad;
      const tick = now => {
        pacing?.sample(now, document.hidden);
        window.gameFlight?.pacing(pacing?.slow ?? false);
        window.gameFlight?.frame();
        if (window.menuLayout && effectLoad !== (pacing?.effectLoad ?? true)) {
          effectLoad = pacing?.effectLoad ?? true;
          instance.invokeMethod('EffectLoad', effectLoad);
        }
        const note = document.getElementById('pacing-note');
        if (note) note.hidden = !pacing?.slow;
        window.menuLayout?.();
        const box = document.fullscreenElement === canvas ? {width:innerWidth,height:innerHeight} : canvas.parentElement.getBoundingClientRect();
        const ratio = window.menuLayout ? Math.min(window.devicePixelRatio || 1, 2) * (pacing?.scale ?? 1) : 1;
        const width = Math.max(1, Math.round(box.width * ratio)), height = Math.max(1, Math.round(box.height * ratio));
        if (canvas.width !== width || canvas.height !== height) {
          canvas.width = width; canvas.height = height;
          if (window.menuLayout) instance.invokeMethod('PresentationSize', width, height);
        }
        const start = performance.now();
        let drawn;
        try { drawn = instance.invokeMethod('TickDotNet'); }
        catch { fail('The renderer stopped. Reload to retry.'); return; }
        const end = performance.now();
        if (!drawn) return;
        if (measurements.firstFrameMs === null) {
          measurements.firstFrameMs = end; clearTimeout(startupTimer);
          if (window.allegianceWebGLCapabilities) {
            const gl = canvas.getContext('webgl2') || canvas.getContext('webgl');
            if (gl) {
              measurements.webgl = window.allegianceWebGLCapabilities(gl);
              console.info('[browser] WebGL capabilities ' + JSON.stringify(measurements.webgl));
            }
          }
        }
        if (measurements.cpu.length < 1200) {
          measurements.cpu.push(end - start);
          if (previous) measurements.interval.push(now - previous);
        }
        previous = now;
        frameId = requestAnimationFrame(tick);
      };
      frameId = requestAnimationFrame(tick);
    }
  };
  function fail(message, fallback = false) {
    clearTimeout(startupTimer);
    cancelAnimationFrame(frameId);
    app.replaceChildren();
    const panel = document.createElement('div'); panel.className = 'panel'; panel.id = fallback ? 'fallback' : 'runtime-error';
    const title = document.createElement('h2'); title.textContent = fallback ? 'Browser preview unavailable' : 'Preview could not load';
    const detail = document.createElement('p'); detail.textContent = message;
    const retry = document.createElement('button'); retry.id = 'retry'; retry.textContent = 'Retry'; retry.addEventListener('click', () => location.reload());
    panel.append(title, detail, retry); app.append(panel);
  }
  // Some runtime initialization failures are global module errors rather than a
  // rejected Blazor.start promise. Keep those failures on the same Retry path.
  window.addEventListener('unhandledrejection', () => fail('The WebAssembly runtime could not start. Retry to load it again.'));
  window.addEventListener('error', event => { if (event.error) fail('A preview component could not start. Retry to load it again.'); });
  window.addEventListener('securitypolicyviolation', () => fail('The browser blocked a required preview component. Retry or check your browser settings.'));
  let context;
  try {
    if (typeof WebAssembly !== 'object' || typeof WebAssembly.instantiate !== 'function') throw new Error('WebAssembly');
    const probe = document.createElement('canvas');
    context = probe.getContext('webgl');
    if (!context) throw new Error('WebGL');
    context.getExtension('WEBGL_lose_context')?.loseContext();
  } catch {
    fail('This browser needs WebAssembly and WebGL to show the scout. Try a supported browser or use the desktop client.', true);
    return;
  }
  const preloads = document.getElementById('boot-preloads');
  if (preloads) document.head.append(preloads.content.cloneNode(true));
  function script(path, before) {
    return new Promise((resolve, reject) => {
      const node = document.createElement('script'); node.src = asset(path);
      if (before) before(node);
      node.onload = resolve; node.onerror = () => reject(new Error('Could not download ' + path));
      document.body.append(node);
    });
  }
  (async () => {
    startupTimer = setTimeout(() => fail('The preview download timed out. Retry to download again.'), 120000);
    const status = document.getElementById('runtime-status');
    status.textContent = 'Downloading C# / WebAssembly…';
    // KNI interop is self-hosted. Explicit order preserves its global registrations.
    const interop = ['JSInterop/js/JSObject', 'Dom/js/Window', 'Dom/js/Document', 'Dom/js/Navigator', 'Dom/js/Gamepad', 'Dom/js/Media', 'XHR/js/XHR', 'Canvas/js/Canvas', 'Canvas/js/CanvasGLContext', 'Audio/js/Audio', 'XR/js/XR'];
    let done = 0;
    // Fetch independent scripts together, evaluate in their required order.
    for (const file of interop) {
      const [pkg, ...rest] = file.split('/'), link = document.createElement('link');
      link.rel = 'preload'; link.as = 'script'; link.href = asset('_content/nkast.Wasm.' + pkg + '/' + rest.join('/') + '.10.0.3.js');
      document.head.append(link);
    }
    for (const file of interop) {
      const [pkg, ...rest] = file.split('/');
      await script('_content/nkast.Wasm.' + pkg + '/' + rest.join('/') + '.10.0.3.js');
    }
    await script('_framework/blazor.webassembly.js', node => node.setAttribute('autostart', 'false'));
    await Blazor.start({
      loadBootResource(type, name, uri, integrity) {
        if (type === 'dotnetjs') return asset(uri);
        return fetch(asset(uri), { integrity, cache: 'default', credentials: 'same-origin' }).then(response => {
          if (!response.ok) throw new Error('Could not download ' + name);
          done++;
          const progress = document.getElementById('runtime-progress');
          if (progress) progress.value = Math.min(95, done);
          status.textContent = 'Loading runtime components (' + done + ')…';
          return response;
        });
      }
    });
    clearTimeout(startupTimer);
  })().catch(error => fail(error.message || 'The runtime could not start. Retry to download again.'));
})();
