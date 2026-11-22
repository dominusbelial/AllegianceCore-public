# Browser flight traces (2026-11)

Desktop asset-open traces (`version 1`: scenes and the content/artwork files each opened, with
size and SHA-256) recorded during the P1051 browser-flight work. `scripts/publish-browser-generation.sh`
feeds them to `scripts/publish-browser-flight.sh` to rebuild the browser generation the public
host serves (deployed generations `978250f4`, `bc8d5d34` and the `6bc0dade` star hotfix were built
from exactly these files).

They contain public artwork/content file names and hashes only: no paths, hosts or identities.
Generated-map and random-civ art is not fully covered by these traces; P1073 adds on-demand
packs for it.
