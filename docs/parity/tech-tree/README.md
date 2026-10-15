# Loaded-core research trees

Generate these documents with `tools/tech-tree`; see
[commands and plan format](../../../data/showcase-plans/README.md).
Each `<core>-civ<id>.md` is derived from that civ's loaded core records with
native-default path permissions. The matchup table links each side to its
complete reachable purchase catalog.

Each completion row gives actual newly buyable purchases after its prerequisites
have completed in the listed order. The separate dependency list includes
purchases that consume an effect/local bit but still need other prerequisites.
Reachability excludes captures and assumes prior stations are retained, unlimited
funding and enough compatible unoccupied asteroids.

Parts carry their native equipment role: Weapon, Shield, Cloak, Afterburner,
Magazine, Dispenser, ChaffLauncher or Pack. Resolved launcher/dispenser records
are included alongside ordinary parts.

Hulls carry their exact core name, group and native capability mask as their
role descriptor. These capabilities can overlap. The principal role bits are:

| Bit | Role/capability |
|---|---|
| `0x0001` | Boarding |
| `0x0002` | Rescue |
| `0x0004` | Lifepod |
| `0x0008` | Capture threat |
| `0x0080` | Fighter |
| `0x0200` | Threat to stations |
| `0x0400` | Carrier |
| `0x4000` | Miner |
| `0x8000` | Builder |

These are native engine constants in
`~/Development/Allegiance/src/Igc/igc.h:726-741`. Group IDs and display names are
core data. The query reports raw purchase-gate eligibility, including NPC hulls;
loadout consumers must also apply human-hull, equipment-mount and launch rules.

The dump's required/effect/local masks use native bit indices, not byte values.
GA changes use native global-attribute indices and multiplicative factors
(`Igc/igc.h:585-623,935-946`). Station/drone price and bucket time are core values;
only development buckets receive civ cost/time scaling
(`Igc/bucketigc.cpp:66-74`).
