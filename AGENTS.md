# AGENTS.md — notes for coding agents working in AllegianceCore

- **Start with [GLOSSARY.md](GLOSSARY.md)**: the per-domain index of terms, key files and the native source each
  system ports. **Update it** when you add a system, a recurring term or an architectural pattern
  (`tools/check-glossary-links.py` checks every path it names; CI runs it).
- **Behaviour parity with native Allegiance** (`FreeAllegiance/Allegiance`): match native behaviour exactly; visuals may
  differ. Cite native `file:line` for every rule you port, and track it in `docs/parity/*.csv`
  (`scripts/parity-scorecard.py` validates them).
- **HUD work covers both styles** (`HudStyle.Classic` and `HudStyle.Modern`): put behaviour in the shared model, test
  both, capture both.
- **New apps** go under `src/Allegiance.<Name>/` (csproj + own `Dockerfile`, registered in `AllegianceCore.slnx` and
  `docker-compose.yml`), tests under `tests/Allegiance.<Name>.Tests/`; one-off tools under `tools/`.
- **Any new service, store, port or env var** is reflected in `docker-compose.yml` (+ `docker/`), and
  `docker compose config` must validate.
- **Public repo hygiene:** no secrets, personal emails or absolute home paths in code; new media is reviewed before a
  push. `scripts/publish-guard.sh` (pre-push hook `.githooks/pre-push`, and CI) enforces this.
- **Gate:** `dotnet build AllegianceCore.slnx` with 0 warnings, `tools/build-tools.sh`, new tests 3× alone, one
  `tools/run-tests.sh --tier fast`; known flakes are ledgered in `docs/TEST_FLAKES.md`.
- Generated maps log `seed=N`; `--map-seed N` rebuilds the same map. `scripts/dev-stack.sh up|down|status` runs a local
  server + lobby (+ bots).
