'use strict';
// P1059 — browser developer/proof switch.
//
// A few flight-toolbar controls exist for the proof harnesses rather than for a
// player: the asteroid proof view (#flight-rock), the typed-coordinate waypoint
// form (#flight-waypoint, #waypoint-*), and the authoritative pose readout
// (#online-pose). They are hidden from players and never deleted:
//
//   ?dev=1 on the page URL, or localStorage['allegiance.dev'] = '1', renders them;
//   ?dev=0 turns the switch off again (and remembers that choice);
//   window.allegianceDev.setEnabled(true|false) switches it live.
//
// Default is off. This file only decides which page controls render: it touches
// no game rule, wire message, server check or limit. The game methods behind the
// controls (ToggleRockView, FlyToWaypoint, ...) stay in the shared client.
(function (root) {
  const STORAGE_KEY = 'allegiance.dev';
  const ON = new Set(['1', 'true', 'on', 'yes']);
  const OFF = new Set(['0', 'false', 'off', 'no']);

  // URL first, then storage; an unknown URL value falls through to storage so a
  // typo can never flip the switch on its own.
  function devRequested(location, storage) {
    try {
      const requested = new URLSearchParams(location.search || '').get('dev');
      if (requested !== null) {
        const value = requested.trim().toLowerCase();
        if (ON.has(value)) return true;
        if (OFF.has(value)) return false;
      }
      const stored = storage.getItem(STORAGE_KEY);
      return ON.has(String(stored || '').trim().toLowerCase());
    } catch { return false; }
  }

  function install(target, enabled) {
    let enabledNow = !!enabled;
    const api = {
      enabled: () => enabledNow,
      setEnabled: value => {
        enabledNow = !!value;
        try { target.localStorage.setItem(STORAGE_KEY, enabledNow ? '1' : '0'); } catch { /* storage refused */ }
        return enabledNow;
      },
    };
    target.allegianceDev = api;
    // Blazor reads the switch once while the menu loads; a plain global function
    // is the smallest interop surface (window.allegianceDevEnabled()).
    target.allegianceDevEnabled = api.enabled;
    return api;
  }

  const exported = { STORAGE_KEY, devRequested, install };
  if (typeof module === 'object' && module.exports) module.exports = exported;
  if (root && root.location) {
    let enabled = false;
    try { enabled = devRequested(root.location, root.localStorage); } catch { enabled = false; }
    const api = install(root, enabled);
    // An explicit ?dev= URL is a sticky choice, the way `?log=verbose` is in
    // console-log.js: `?dev=1` keeps the switch on for later visits and
    // `?dev=0` overrides a stored `1`.
    try {
      if (new URLSearchParams(root.location.search || '').has('dev')) api.setEnabled(api.enabled());
    } catch { /* no URL access */ }
  }
})(typeof window === 'object' ? window : null);
