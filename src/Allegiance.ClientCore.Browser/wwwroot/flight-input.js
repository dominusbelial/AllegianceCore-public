// Page events own gestures. No request is deferred into RAF or a .NET continuation.
(() => {
  const contexts = new Set();
  let audioEnabled = false;
  if (window.AudioContext) {
    const NativeAudioContext = window.AudioContext;
    window.AudioContext = class extends NativeAudioContext {
      constructor(...args) { super(...args); contexts.add(this); if (!audioEnabled || document.hidden) void this.suspend().catch(() => {}); }
    };
  }
  function audio(visible, gesture = false) {
    if (gesture) audioEnabled = true;
    for (const context of contexts) {
      if (context.state === 'closed') { contexts.delete(context); continue; }
      void (visible && audioEnabled ? context.resume() : context.suspend()).catch(() => {});
    }
  }
  window.installFlightInput = instance => {
    const canvas = document.getElementById('theCanvas'), held = new Set(), listeners = [];
    let alive = true, pointerPending = false, wantsPointer = false;
    const playing = () => document.getElementById('online-panel')?.dataset.state === 'Flight';
    const owns = () => playing() && !document.activeElement?.closest('button,a,input,select,textarea,[contenteditable="true"]');
    const invoke = (method, ...args) => { if (alive) instance.invokeMethod(method, ...args); };
    const on = (target, name, handler, options) => { target.addEventListener(name, handler, options); listeners.push(() => target.removeEventListener(name, handler, options)); };
    const release = () => { wantsPointer = false; held.clear(); invoke('FlightKeys', []); invoke('ReleaseFlightInput'); if (document.pointerLockElement === canvas) document.exitPointerLock(); };
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
      if (event.code === 'Escape') { release(); return; }
      if (['Tab','Space','ArrowUp','ArrowDown','ArrowLeft','ArrowRight','Backspace'].includes(event.code)) event.preventDefault();
      if (event.isTrusted && !event.repeat) lock();
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
    on(canvas, 'mousemove', event => { if (document.pointerLockElement === canvas) invoke('PointerMotion', Math.round(event.movementX), Math.round(event.movementY)); });
    on(document, 'pointerlockchange', () => {
      pointerPending = false;
      const locked = document.pointerLockElement === canvas;
      invoke('PointerCapture', locked);
      if (!locked) { held.clear(); invoke('FlightKeys', []); invoke('ReleaseFlightInput'); }
    });
    on(document, 'pointerlockerror', () => { pointerPending = false; console.info('[browser] Pointer lock refused; retry on the next gesture.'); });
    on(window, 'blur', () => { release(); audio(false); });
    on(document, 'visibilitychange', () => { if (document.hidden) release(); audio(!document.hidden); });
    on(canvas, 'contextmenu', event => event.preventDefault());
    on(canvas, 'webglcontextlost', event => { event.preventDefault(); release(); invoke('ContextLost'); });
    on(document, 'click', event => {
      if (event.isTrusted) audio(!document.hidden, true);
      if (event.target.closest?.('[data-flight-fullscreen]')) fullscreen();
    });
    window.gameFlight = { activate: () => canvas.focus(), audioStates: () => [...contexts].map(c => c.state) };
    return () => { release(); alive = false; for (const detach of listeners) detach(); delete window.gameFlight; };
  };
})();
