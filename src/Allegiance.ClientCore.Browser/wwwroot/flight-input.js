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
    let alive = true, pointerPending = false, wantsPointer = false, padConnected = false, padIndex = null, toolbarNavigation = false, toggleWasDown = false;
    const playing = () => document.getElementById('online-panel')?.dataset.state === 'Flight';
    // P1070 — the hangar and loadout are driven in the game window (the canvas), so the
    // same key stream and a canvas pointer feed them: the desktop reads keys through the
    // action map (confirm/back/menu) and the pointer through Mouse.GetState() for hover
    // and click. Pointer lock, fullscreen and the audio gesture stay flight-only.
    const docked = () => document.getElementById('online-panel')?.dataset.state === 'Hangar';
    const textEntry = () => !!document.activeElement?.closest('input:not([type=button]):not([type=submit]):not([type=reset]):not([type=checkbox]):not([type=radio]):not([type=range]),textarea,[contenteditable]:not([contenteditable="false"]),[role="textbox"]');
    const control = () => document.activeElement?.closest('button,a,select,input[type=checkbox],input[type=radio],input[type=range]');
    const owns = () => (playing() || docked()) && !textEntry();
    const focusCanvas = () => { toolbarNavigation = false; canvas.focus(); };
    const invoke = (method, ...args) => { if (alive) return instance.invokeMethod(method, ...args); };
    const on = (target, name, handler, options) => { target.addEventListener(name, handler, options); listeners.push(() => target.removeEventListener(name, handler, options)); };
    const release = () => { padConnected = false; padIndex = null; wantsPointer = false; toggleWasDown = false; held.clear(); invoke('FlightKeys', []); invoke('ReleaseFlightInput'); if (document.pointerLockElement === canvas) document.exitPointerLock(); };
    const lock = () => {
      if (!wantsPointer || pointerPending || document.pointerLockElement === canvas) return;
      pointerPending = true;
      try { const request = canvas.requestPointerLock(); request?.catch(error => { console.info('[browser] Pointer lock refused; click the game view or use the mouse-flight toggle to retry.'); pointerPending = false; }); }
      catch { pointerPending = false; }
    };
    const fullscreen = () => {
      // Keep guest identity and the Sign in link visible with the flight toolbar.
      const request = document.fullscreenElement ? document.exitFullscreen() : document.documentElement.requestFullscreen();
      request?.catch(() => console.info('[browser] Fullscreen request refused; try another gesture.'));
    };
    // Page-wide input, including HTML fields and toolbar buttons. Only actual
    // page events count; blur/visibility changes and renderer frames do not.
    const activity = event => {
      if (event.isTrusted && !document.hidden && (event.type !== 'pointermove'
          || event.movementX || event.movementY)) invoke('PlayerActivityTick', true);
    };
    for (const name of ['pointermove', 'pointerdown', 'pointerup', 'keydown', 'keyup', 'wheel'])
      on(window, name, activity, { capture: true, passive: true });
    const activityTimer = window.setInterval(() => invoke('PlayerActivityTick', false), 1000);
    listeners.push(() => window.clearInterval(activityTimer));
    on(window, 'keydown', event => {
      if (event.isTrusted) audio(!document.hidden, true);
      if (!owns()) return;
      if (event.altKey && event.code === 'Enter' && playing()) { event.preventDefault(); if (!event.repeat) fullscreen(); return; }
      // Shift+Tab leaves the game; Tab then visits toolbar controls normally.
      // Escape returns to flight. Space/Enter deliberately activate a control
      // reached by Tab, while a mouse-clicked button cannot steal flight keys.
      if (event.code === 'Tab' && (event.shiftKey || control())) {
        toolbarNavigation = true; held.clear(); invoke('FlightKeys', []); return;
      }
      if (toolbarNavigation && control() && (['Enter','Space'].includes(event.code)
          || document.activeElement?.closest('select') && event.code !== 'Escape')) return;
      if (event.code === 'Escape') {
        event.preventDefault(); focusCanvas();
        if (invoke('FlightEscape') === true) return;
        if (docked()) { held.add('Escape'); invoke('FlightKeys', [...held]); } else release();
        return;
      }
      // Cancel every deliverable browser shortcut only inside the game. Hard
      // OS/tab-management chords have safe browser defaults in the action map.
      event.preventDefault();
      if (control()) focusCanvas();
      held.add(event.code);
      // The synchronous check keeps a deliberate mouse-toggle gesture inside
      // the trusted event, and respects the live action binding. No other key
      // press ever captures the pointer, including after a refused request.
      const toggle = playing() && invoke('MouseToggleKey', [...held]) === true;
      if (toggle && !toggleWasDown && !event.repeat && event.isTrusted) {
        if (document.pointerLockElement === canvas) { wantsPointer = false; document.exitPointerLock(); }
        else { wantsPointer = true; lock(); }
      }
      toggleWasDown = toggle;
      invoke('FlightKeys', [...held]);
    }, true);
    on(window, 'keyup', event => { held.delete(event.code); toggleWasDown = invoke('MouseToggleKey', [...held]) === true; invoke('FlightKeys', [...held]); }, true);
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
      if (!locked) { wantsPointer = false; release(); }
    });
    on(document, 'pointerlockerror', () => { pointerPending = false; console.info('[browser] Pointer lock refused; retry on the next gesture.'); });
    on(window, 'blur', () => { focused = false; release(); audio(false); });
    on(window, 'focus', () => { focused = true; audio(!document.hidden); });
    on(window, 'gamepaddisconnected', event => { if (padIndex === event.gamepad?.index || !event.gamepad) release(); });
    on(document, 'visibilitychange', () => { if (document.hidden) release(); audio(!document.hidden); });
    on(canvas, 'contextmenu', event => event.preventDefault());
    on(canvas, 'webglcontextlost', event => { event.preventDefault(); release(); invoke('ContextLost'); });
    // Pointer activation must not leave a toolbar button as the key owner.
    // Do not prevent select's popup, or input/text editing. Tab focus is intact.
    on(document, 'mousedown', event => {
      if ((playing() || docked()) && event.target.closest?.('#online-panel button, #menu-controls button')) event.preventDefault();
    }, true);
    on(document, 'change', event => {
      if (owns() && event.target.closest?.('#online-panel select')) focusCanvas();
    });
    on(document, 'focusin', () => {
      if (textEntry()) { held.clear(); invoke('FlightKeys', []); invoke('FlightMouseButtons', 0); }
    });
    on(document, 'click', event => {
      if (event.isTrusted) audio(!document.hidden, true);
      if (event.target.closest?.('[data-flight-fullscreen]')) fullscreen();
      if ((playing() || docked()) && event.target.closest?.('#online-panel button, #menu-controls button')) focusCanvas();
    });
    window.gameFlight = {
      activate: focusCanvas, release, audioStates: () => [...contexts].map(c => c.state),
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
