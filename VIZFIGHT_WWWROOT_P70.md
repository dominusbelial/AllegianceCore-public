# P70 — vizfight served a 404 on "/" when launched outside its own project directory

## The bug

`tools/vizfight`'s spectator page (`GET /`) silently returned `404 Not
Found` whenever the process was launched with a working directory other
than `tools/vizfight` itself — e.g. `cd ~/Development/AllegianceCore &&
dotnet tools/vizfight/bin/Release/net10.0/vizfight.dll explore 8765`, a
completely natural way to run a built tool. `dotnet run --project
tools/vizfight` happened to work because its own default CWD is the
project directory, masking the bug in the one invocation form used most
often in this project's own session history — but any other launch style
(the built DLL directly, a different CWD) hit it.

Root cause: `WebApplication.CreateBuilder`'s default content root is
`Directory.GetCurrentDirectory()` (the caller's CWD), and ASP.NET Core's
Static Web Assets manifest (`vizfight.staticwebassets.runtime.json`,
generated at build time with the real absolute `wwwroot` path baked in)
is only discovered relative to that content root — not relative to the
built assembly's own directory. Wrong CWD ⇒ manifest never loads ⇒
`UseStaticFiles()`/`UseDefaultFiles()` have nothing to serve ⇒ `/` 404s
with zero error output anywhere (not in the app's own console, not in the
HTTP response).

## The fix

Both `WebApplication.CreateBuilder` call sites (fight mode and explore
mode) now pass an explicit `WebRootPath`, computed via a new
`ResolveWwwroot()` helper: `AppContext.BaseDirectory` (always the built
assembly's own directory, regardless of caller CWD) walked up three
levels (`bin/<Configuration>/<TFM>/` → the project directory) then into
`wwwroot/`. This sidesteps the Static Web Assets manifest lookup entirely
and goes back to classic physical-file static serving, which only cares
about `WebRootPath` being correct — no dependency on CWD at all.

An earlier attempt (pinning `ContentRootPath` to `AppContext.BaseDirectory`
instead) did **not** fix it and in fact regressed the previously-working
`dotnet run` case too — worth noting since it looked like the obvious fix
first. `ContentRootPath` isn't what the Static Web Assets manifest lookup
keys off; `WebRootPath` bypassing that system entirely is what actually
works.

## Verification

Rebuilt, then curl-tested `GET /` from three different working
directories against the same built binary: the repo root (previously
404), `tools/vizfight` itself (previously 200, confirmed still 200 — no
regression), and `/tmp` (the most CWD-independent case possible) — all
three now return `200 OK` with the real page body. Confirmed visually too:
opened the page in a real browser from a repo-root launch (the originally
broken case), `ws: open`, page renders correctly.

No product-code paths besides the two builder setups were touched.
2290/2290 across 3 consecutive clean full-suite runs (this is a pure
tooling fix, but ran the full suite anyway since `vizfight` doesn't have
its own tests and the change touches shared builder-configuration code
pattern).
