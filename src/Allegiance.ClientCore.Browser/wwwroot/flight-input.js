// Page events own gestures. No request is deferred into RAF or a .NET continuation.
(() => {
  const contexts = new Set();
  const audioTransitions = new WeakMap();
  let audioEnabled = false, audioVisible = true, framesLate = false, focused = true;
  function settleAudio(context) {
    const pending = audioTransitions.get(context);
    if (pending) { pending.dirty = true; return; }
    const work = { dirty: true };
    audioTransitions.set(context, work);
    // resume() is asynchronous. A suspend() called while that resume is
    // pending can be a no-op against the still-suspended context, allowing
    // the resume to finish after blur/hidden. Coalesce changes and apply the
    // latest lifecycle state after each operation, with one worker/context.
    void (async () => {
      try {
        while (work.dirty) {
          work.dirty = false;
          if (context.state === 'closed') { contexts.delete(context); break; }
          const playing = audioVisible && audioEnabled && !document.hidden && !framesLate && focused;
          try { await (playing ? context.resume() : context.suspend()); } catch { /* Audio is optional. */ }
        }
      } finally { audioTransitions.delete(context); }
    })();
  }
  if (window.AudioContext) {
    const NativeAudioContext = window.AudioContext;
    window.AudioContext = class extends NativeAudioContext {
      constructor(...args) { super(...args); contexts.add(this); if (!audioVisible || !audioEnabled || document.hidden || framesLate || !focused) settleAudio(this); }
    };
  }
  function audio(visible, gesture = false) {
    audioVisible = visible;
    if (gesture) audioEnabled = true;
    for (const context of contexts) {
      if (context.state === 'closed') { contexts.delete(context); continue; }
      settleAudio(context);
    }
  }
  window.installFlightInput = instance => {
    const canvas = document.getElementById('theCanvas'), held = new Set(), listeners = [];
    let alive = true, pointerPending = false, wantsPointer = false, padConnected = false, padIndex = null;
    const playing = () => document.getElementById('online-panel')?.dataset.state === 'Flight';
    // P1070 — the hangar and loadout are driven in the game window (the canvas), so the
    // same key stream and a canvas pointer feed them: the desktop reads keys through the
    // action map (confirm/back/menu) and the pointer through Mouse.GetState() for hover
    // and click. Pointer lock, fullscreen and the audio gesture stay flight-only.
    const docked = () => document.getElementById('online-panel')?.dataset.state === 'Hangar';
    const owns = () => (playing() || docked()) && !document.activeElement?.closest('button,a,input,select,textarea,[contenteditable="true"]');
    const invoke = (method, ...args) => { if (alive) instance.invokeMethod(method, ...args); };
    const on = (target, name, handler, options) => { target.addEventListener(name, handler, options); listeners.push(() => target.removeEventListener(name, handler, options)); };
    const release = () => { padConnected = false; padIndex = null; wantsPointer = false; held.clear(); invoke('FlightKeys', []); invoke('ReleaseFlightInput'); if (document.pointerLockElement === canvas) document.exitPointerLock(); };
    const lock = () => {
      if (!wantsPointer || pointerPending || document.pointerLockElement === canvas) return;
      pointerPending = true;
      try { const request = canvas.requestPointerLock(); request?.catch(error => { console.info('[browser] Pointer lock refused; use the next key press or click to retry.'); pointerPending = false; }); }
      catch { pointerPending = false; }
    };
    const fullscreen = () => {
      const request = document.fullscreenElement ? document.exitFullscreen() : canvas.requestFullscreen();
      request?.catch(() => console.info('[browser] Fullscreen request refused; try another gesture.'));
    };
    on(window, 'keydown', event => {
      if (event.isTrusted) audio(!document.hidden, true);
      if (event.altKey && event.key === 'Enter' && playing()) { event.preventDefault(); if (!event.repeat) fullscreen(); return; }
      if (!owns()) return;
      // P1070 — Escape is a real screen verb while docked (native back from the
      // loadout), so the hangar gets the key instead of it only releasing flight input.
      if (event.code === 'Escape') { if (docked()) { held.add('Escape'); invoke('FlightKeys', [...held]); } else release(); return; }
      if (['Tab','Space','ArrowUp','ArrowDown','ArrowLeft','ArrowRight','Backspace'].includes(event.code)) event.preventDefault();
      if (event.isTrusted && !event.repeat && !docked()) lock();
      held.add(event.code); invoke('FlightKeys', [...held]);
    }, true);
    on(window, 'keyup', event => { held.delete(event.code); invoke('FlightKeys', [...held]); }, true);
    on(canvas, 'mousedown', event => {
      if (!playing()) return;
      canvas.focus();
      if (event.isTrusted) { wantsPointer = true; audio(true, true); lock(); }
      invoke('FlightMouseButtons', event.buttons);
      event.preventDefault();
    });
    on(window, 'mouseup', event => invoke('FlightMouseButtons', event.buttons));
    // P1070 — the docked screens' pointer. The canvas drawing buffer is the CSS box
    // scaled by the same ratio menuViewport() uses, so the game window receives the
    // exact pixel space HangarScreen.Rect and LoadoutV2Layout are laid out in. Wheel is
    // the absolute accumulated value MonoGame reports (120 per notch, up-positive).
    let hangarWheel = 0, hangarX = -1, hangarY = -1, hangarDown = false;
    const hangarPoint = event => {
      const box = canvas.getBoundingClientRect(), ratio = Math.min(devicePixelRatio || 1, 2);
      hangarX = Math.round((event.clientX - box.left) * ratio);
      hangarY = Math.round((event.clientY - box.top) * ratio);
      return hangarX >= 0 && hangarY >= 0;
    };
    const hangarFeed = () => { if (docked()) invoke('HangarPointer', hangarX, hangarY, hangarDown, hangarWheel); };
    on(canvas, 'mousemove', event => { if (docked() && hangarPoint(event)) hangarFeed(); });
    on(canvas, 'mousedown', event => {
      if (!docked()) return;
      canvas.focus(); hangarPoint(event); hangarDown = true; hangarFeed(); event.preventDefault();
    });
    on(window, 'mouseup', event => {
      if (!docked()) return;
      if (hangarPoint(event)) { hangarDown = false; hangarFeed(); }
    });
    on(canvas, 'wheel', event => {
      if (!docked()) return;
      event.preventDefault();
      hangarWheel += event.deltaY < 0 ? 120 : -120;
      if (hangarPoint(event)) hangarFeed();
    }, { passive: false });
    on(canvas, 'mousemove', event => { if (document.pointerLockElement === canvas) invoke('PointerMotion', Math.round(event.movementX), Math.round(event.movementY)); });
    on(document, 'pointerlockchange', () => {
      pointerPending = false;
      const locked = document.pointerLockElement === canvas;
      invoke('PointerCapture', locked);
      if (!locked) { held.clear(); invoke('FlightKeys', []); invoke('ReleaseFlightInput'); }
    });
    on(document, 'pointerlockerror', () => { pointerPending = false; console.info('[browser] Pointer lock refused; retry on the next gesture.'); });
    on(window, 'blur', () => { focused = false; release(); audio(false); });
    on(window, 'focus', () => { focused = true; audio(!document.hidden); });
    on(window, 'gamepaddisconnected', event => { if (padIndex === event.gamepad?.index || !event.gamepad) release(); });
    on(document, 'visibilitychange', () => { if (document.hidden) release(); audio(!document.hidden); });
    on(canvas, 'contextmenu', event => event.preventDefault());
    on(canvas, 'webglcontextlost', event => { event.preventDefault(); release(); invoke('ContextLost'); });
    on(document, 'click', event => {
      if (event.isTrusted) audio(!document.hidden, true);
      if (event.target.closest?.('[data-flight-fullscreen]')) fullscreen();
    });
    window.gameFlight = {
      activate: () => canvas.focus(), audioStates: () => [...contexts].map(c => c.state),
      pacing(slow) { if (framesLate !== slow) { framesLate = slow; audio(!document.hidden); } },
      frame() {
        // Poll once per game frame; only the W3C standard layout is portable.
        // Browser Y axes are down-positive; the shared desktop pad path is up-positive.
        let pads;
        try { pads = navigator.getGamepads?.() || []; }
        catch { if (padConnected) release(); return; }
        const pad = [...pads].find(p => p?.connected && p.mapping === 'standard');
        if (!focused || document.hidden || !owns()) {
          if (padConnected) release();
          return;
        }
        if (!pad) { if (padConnected) release(); return; }
        if (padIndex !== null && padIndex !== pad.index) release();
        padConnected = true; padIndex = pad.index;
        const axis = i => Number.isFinite(pad.axes[i]) ? Math.max(-1, Math.min(1, pad.axes[i])) : 0;
        invoke('FlightGamepad', [axis(0), -axis(1), axis(2), -axis(3)],
          pad.buttons.map(b => Number.isFinite(b.value) ? Math.max(0, Math.min(1, b.value)) : b.pressed ? 1 : 0));
      }
    };
    return () => { release(); framesLate = false; audio(false); alive = false; for (const detach of listeners) detach(); delete window.gameFlight; };
  };
})();
