// The KNI window has no desktop TextInput event; browser key text is delivered explicitly.
window.menuLayout = () => {
  const top = Math.ceil(document.getElementById('menu-controls')?.getBoundingClientRect().height || 0);
  const online = document.getElementById('online-panel');
  const bottom = 18 + (['Flight', 'Hangar'].includes(online?.dataset.state) ? Math.ceil(online.getBoundingClientRect().height) : 0);
  document.documentElement.style.setProperty('--game-top', top + 'px');
  document.documentElement.style.setProperty('--game-bottom', bottom + 'px');
};
window.menuViewport = () => {
  window.menuLayout();
  const box = document.getElementById('canvasHolder').getBoundingClientRect(), ratio = Math.min(devicePixelRatio || 1, 2);
  return [Math.round(box.width * ratio), Math.round(box.height * ratio)];
};
window.menuText = instance => {
  window.menuDetachText?.();
  window.gameMenu = { metrics: () => instance.invokeMethod('Metrics'), probeBrotli: bytes => instance.invokeMethodAsync('ProbeBrotli', bytes), decodeBrotli: bytes => instance.invokeMethodAsync('DecodeBrotli', bytes) };
  const detachFlight = window.installFlightInput(instance);
  let active = true;
  const ownsKeys = () => document.hasFocus() && !document.activeElement?.closest('button,a,input,select,textarea,[contenteditable="true"]');
  const focus = () => { if (active) instance.invokeMethod('SetKeyboardFocus', ownsKeys()); };
  const queueFocus = () => queueMicrotask(focus);
  const events = [[document, 'focusin'], [document, 'focusout'], [window, 'focus'], [window, 'blur']];
  const text = event => {
    if (!ownsKeys() || event.ctrlKey || event.altKey || event.metaKey) return;
    // Tab switches the native pilot/menu focus. Shift+Tab can leave the canvas
    // for the HTML toolbar; its buttons retain ordinary browser tab navigation.
    if (event.key === 'Tab' && !event.shiftKey && window.gameMenu.metrics().screen) { event.preventDefault(); return; }
    if (event.key === 'Backspace' || event.key.length === 1) {
      event.preventDefault(); instance.invokeMethod('TextInput', event.key);
    }
  };
  window.addEventListener('keydown', text);
  for (const [target, event] of events) target.addEventListener(event, queueFocus);
  window.menuDetachText = () => {
    detachFlight();
    active = false;
    window.removeEventListener('keydown', text);
    for (const [target, event] of events) target.removeEventListener(event, queueFocus);
  };
  focus();
};
