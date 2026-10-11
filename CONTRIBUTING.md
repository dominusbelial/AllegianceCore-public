# Contributing to AllegianceCore

Thanks for considering a contribution. This project is a from-scratch C#
port of Microsoft's *Allegiance* (MIT) and takes the native C++ reference
very seriously. Please read the short front page ([`README.md`](README.md)),
the [full guide](docs/GUIDE.md), and the [security notes](SECURITY.md) first.

## Ground rules

- **The native source is the oracle.** This is a *port*, not a re-imagining.
  Every ported behaviour must cite the original C++ `file:line` it came from
  (for example `ReceiveDamage`, `shipIGC.cpp:1212`). If you change simulated
  behaviour, show the native code that justifies it — or, if the change is a
  deliberate port *extension* (many exist: debug-mode aids, the relocate
  cheat, test unlocks), say so explicitly in a doc comment and don't pretend
  native does it.
- **Exact byte consumption is the acceptance property.** Decoders must land
  precisely on end-of-body for every file; a wrong field width anywhere
  desynchronises everything after it. If the native `igc.h` and the shipped
  corpus disagree, `igc.h` wins (it is the actual writer), and corpus-measured
  corrections are recorded in the design notes.
- **No personal emails in the repo.** Use GitHub for all communication.

## Build & test

```bash
export DOTNET_ROOT="$HOME/.dotnet"      # your SDK install, not the snap stub
export PATH="$DOTNET_ROOT:$PATH"

dotnet build AllegianceCore.slnx        # must be 0 warnings / 0 errors
scripts/run-tests.sh                    # the full suite (artwork-aware)
```

- The build treats warnings as errors (`TreatWarningsAsErrors=true` in
  `Directory.Build.props`); a clean build prints `0 Warning(s) / 0 Error(s)`.
- `scripts/run-tests.sh` runs the four test assemblies sequentially to avoid
  the cross-assembly live-network flakes that a bare `dotnet test` triggers.
  It needs the art corpus via `local.env` / `ALLEGIANCE_ARTWORK_PATH`.
- CI runs the non-corpus gate:
  `dotnet test AllegianceCore.slnx --filter "Requires!=Artwork&Requires!=NativeSource"`.
  It also builds **every** `tools/*/*.csproj` so the proof harnesses can't rot
  against later API changes.

### Test traits

- `[Trait("Requires", "Artwork")]` / `[Trait("Requires", "NativeSource")]` —
  these need the external corpus and are filtered out in CI; locally they must
  pass.
- `[Collection("LiveNetwork")]` — live-socket test classes; serialized so they
  don't contend with each other or the unit-test collections.
- New tests must **fail on today's code and pass after** your fix
  (failing-first). For behaviour fixes, prove the regression is caught.

### Flaky tests

[`docs/TEST_FLAKES.md`](docs/TEST_FLAKES.md) is the single source of truth for
flaky/failing tests. Cite it instead of free-text flake lists. A ledgered
load-flake is not something to casually "fix"; read the entry first.

## Coding conventions

Taken from the existing codebase (do not invent new ones):

- **Language/compile settings** come from `Directory.Build.props`: `net10.0`,
  nullable reference types, implicit usings, `TreatWarningsAsErrors=true`,
  latest language version.
- **File-scoped namespaces** (`namespace Allegiance.Core.Model;`).
- **XML doc comments** on every public/internal type and member, including the
  native `file:line` citation and the "port vs port-extension" distinction.
- **Immutable data carriers** for wire payloads are `readonly record struct`
  with a `Decode(ReadOnlySpan<byte>)` that throws on a wrong record size, and
  a matching `Encode()` for outbound messages.
- **The server is authoritative and single-owned.** Control input only sets
  state; `Mission.Update` alone moves ships and fires weapons, on the sim
  thread — never on a network reader.
- **Report-and-ignore, never crash.** Unknown/unimplemented wire inputs are
  logged plainly and dropped, never thrown and never guessed at.
- **Comments explain *why*, not *what*** — and where a port deviates from
  native, the deviation and its reason are stated in the comment.

## PR checklist

- [ ] Branch from `master`; never push directly to `master`.
- [ ] The solution builds: `0 Warning(s) / 0 Error(s)`.
- [ ] Every `tools/*` project still builds.
- [ ] Full suite passes: `scripts/run-tests.sh` (and check
      `docs/TEST_FLAKES.md` before declaring a failure a flake).
- [ ] New/ported behaviour cites the native `file:line`; port extensions are
      labelled as such.
- [ ] Failing-first test included for the fix.
- [ ] No personal email addresses or machine-specific paths committed
      (machine paths go in gitignored `local.env`, never in the repo).
- [ ] `git grep -nI "@/gmail\|@"` in your new files is clean (no email).
- [ ] Visual/feel changes include a real-client-window screenshot (the
      backbuffer, e.g. via `ALLEGIANCE_AUTOSHOT` / `scripts/live-proof.sh`)
      — offscreen/`--capture` renders do not count.

Please keep pull requests focused; if a change touches several subsystems,
split it. Thank you!
