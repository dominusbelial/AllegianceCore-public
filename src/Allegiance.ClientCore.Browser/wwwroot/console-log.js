'use strict';
// P1058 — browser console levels.
//
// The browser build sends the shared desktop diagnostics to the browser console
// through .NET's stdout sink (Console.WriteLine in shared MonoGame/HUD code),
// so a normal session fills with per-asset information lines:
//   [monogame] status art '<stem>': 64x64 '<file>'
//   [monogame] hud-10 art '<stem>': ...
//   [monogame] target-pane '<stem>': ...
//   [sim] freeze / resume ...
//
// This router gives each line a level and keeps the browser console on warnings
// and errors by default:
//   info    -> console.debug  (DevTools "Verbose" only; hidden by default)
//   warning -> console.warn   (always shown)
//   error   -> console.error  (always shown)
// A line whose own text reports a failure is promoted, so a real problem is
// never hidden by the quiet default. Full stream for debugging:
//   ?log=verbose on the page URL, or localStorage['allegiance.log'] = 'verbose'
//   (window.allegianceLog.setVerbose(true) also switches it live).
//
// Loaded before the .NET runtime, which binds console.log at startup
// (dotnet.runtime.*.js: `e.out = console.log.bind(console)`).
(function (root) {
  const ERROR = /(?:\berror\b|\bexception\b|unhandled|\bfatal\b|\bfailed\b|\bfailure\b|\brefused\b|\bdenied\b|\bcrash\b|\babort)/i;
  const WARNING = /(?:\bwarning\b|\bwarn\b|\bmissing\b|\bfallback\b|\bcannot\b|\bunable\b|\blost\b|\btimeout\b|\btimed out\b|\bretry\b|\binvalid\b|\bignored\b|\bdropped\b)/i;
  const STORAGE_KEY = 'allegiance.log';

  const classify = text => ERROR.test(text) ? 'error' : WARNING.test(text) ? 'warning' : 'info';
  const textOf = args => args.map(value => typeof value === 'string' ? value : String(value)).join(' ');

  function verboseRequested(location, storage) {
    try {
      const requested = new URLSearchParams(location.search || '').get('log');
      if (requested !== null) return requested === 'verbose';
      return storage.getItem(STORAGE_KEY) === 'verbose';
    } catch { return false; }
  }

  // A router over a console-like object. `target` stays untouched; only the
  // log/info entries are replaced, so warnings and errors keep their own path.
  function install(target, verbose) {
    const native = {
      log: target.console.log.bind(target.console),
      debug: target.console.debug.bind(target.console),
      warn: target.console.warn.bind(target.console),
      error: target.console.error.bind(target.console),
    };
    let verboseNow = !!verbose;
    const write = (level, args) => {
      if (level === 'error') native.error(...args);
      else if (level === 'warning') native.warn(...args);
      else if (verboseNow) native.log(...args);
      else native.debug(...args);
    };
    const log = (...args) => write(classify(textOf(args)), args);
    target.console.log = log;
    const api = {
      verbose: () => verboseNow,
      classify,
      setVerbose: value => {
        verboseNow = !!value;
        try { target.localStorage.setItem(STORAGE_KEY, verboseNow ? 'verbose' : 'quiet'); } catch { /* storage refused */ }
        return verboseNow;
      },
    };
    target.allegianceLog = api;
    return api;
  }

  const exported = { classify, verboseRequested, install };
  if (typeof module === 'object' && module.exports) module.exports = exported;
  if (root && root.console)
    install(root, (() => { try { return verboseRequested(root.location, root.localStorage); } catch { return false; } })());
})(typeof window === 'object' ? window : null);
