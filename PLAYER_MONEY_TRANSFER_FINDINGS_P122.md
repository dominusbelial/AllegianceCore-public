# P122 — PLAYER_DONATE(67) / AUTODONATE(112/116) / REQUEST_MONEY(182): disposition under the pooled-`Side.Money` architecture

Date: 2026-09-09
Repo: `/home/domingo/Development/AllegianceCore` (branch `master`, HEAD `78a724f` P121)
Task: determine, with real evidence, whether the three remaining Phase 4 economy fmids are
genuinely not portable given this port's committed pooled-money design — not to force a
reinterpretation, and not to assume N/A without checking for a real angle first.

---

## Bottom line

**All three are N/A — a pure finding, no code.** They are, by construction, per-player-money
features, and this port has exactly one money number per side with no per-player wallet
(standing P118 design fact). Under a single pooled `Side.Money`, same-side transfers are
structural no-ops, and the two behaviors that could still be interesting (cross-side donation,
"forward to the richest teammate") either aren't a real native case or are uncomputable on one
pool. There is no small, real, honest piece of code a finding justifies here — building explicit
handlers would only duplicate the port's existing report-and-ignore catch-all, and building
decoders that nothing calls is dead weight. Reported in full below, the same way `SHIP_RESET`(183)
was marked N/A rather than silently dropped (see P110's "separate, unscoped" note, TELEPORT_ACK_P110.md:112-114).

---

## 1. The load-bearing architecture fact (re-verified fresh)

This port tracks money at the **`Side` level only**. `Side.Money` is a `public long`
(Model/Side.cs:60), the port of the native FedSrv accumulator `m_rgMoney[sideID]`
(fsmission.cpp:61,3504), deliberately widened to `long` so the sim accumulator can't overflow
(Model/Side.cs:50-60). There is **no per-ship / per-player money field anywhere**, and a fresh
grep of the whole `src` tree for per-player financial concepts (`Money|Wallet|Allowance|Budget|
Balance|wealth`) turns up only:

- `Side.Money` — the one pooled treasury (Model/Side.cs:60);
- the two S→C wire codecs `MoneyChange`(60) and `SetMoney`(115) (Wire/MoneyChange.cs, Wire/SetMoney.cs);
- civilization `IncomeMoney`/`BonusMoney` — static core attributes, not balances (Data/DataCivilizationIGC.cs:12-13);
- a mission `fGoalTeamMoney` — a mission parameter, not a balance (Wire/MissionDef.cs:126);
- the declared IGC-site hook `GetMoneyRequest(...)` — a **no-op with zero call sites** (see §5).

Nothing per-player was added by P119 (bucket funding), P120 (asteroid ore broadcast) or P121
(set-briefing sync). P119's `Bucket` investment is drawn from the shared `Side.Money` into
per-`Side` construction buckets — a collective team-funding surface, not a per-player wallet
(`git show` of 28c1e80 touches `Model/Bucket.cs` + the `BUCKET_DONATE` handler reading the pooled
treasury, Server.cs:1964-1969). So the picture P118 established (MONEY_CHANGE_SET_MONEY_P118.md:11-33)
still holds unchanged.

Consequence stated plainly: **a "donation" between two players on the same side is a no-op by
construction** — both already draw on the one shared number; there is no owner-wise decomposition
of `Side.Money` into which a transfer could move anything. Native's donor-gets-poorer /
recipient-gets-richer requires the native per-player `CFSPlayer::GetMoney()`/`SetMoney()`
(P118 doc:27-33), which this port does not have.

---

## 2. PLAYER_DONATE(67) — N/A

Definition (native, `Inc/Messages.h`; fmid confirmed at FedMessageDumper.cs:29):
```cpp
DEFINE_FEDMSG(C, PLAYER_DONATE, 67)
  Money     moneyGiven;
  ShipID    shipID;
END_FEDMSG
```
Semantics (native handler `FedSrv/FedSrv.CPP:7213-7239`, documented in P118's donation/deferral
row at MONEY_CHANGE_SET_MONEY_P118.md:85): the donating player gives `moneyGiven` of their own
per-player money to the target ship — `moneyGiven` deducted from the donor's `GetMoney()`, added
to the recipient's.

Port disposition: **N/A.** Under one pooled `Side.Money`, a same-side donation moves nothing
meaningful (§1). There is no per-player debit/credit to perform.

Two things checked, per the brief:

- **Cross-side donation isn't a real native case.** Native resolves the target purely from the
  client-supplied `shipID` and implicitly trusts it to be a teammate (the excerpt shows no side
  check); there is no legitimate cross-side giving. And — the decisive point for this port — the
  N/A disposition **renders any same-side safety check moot**: this port implements no
  `PLAYER_DONATE` handler at all, so a client-supplied `shipID` is never decoded or trusted. An
  inbound fmid-67 frame drops into the dispatch's terminal `else` — "unsupported fmid — ignored at
  headless milestone" (Server.cs:1988-1991) — which reports and discards *by fmid, before any
  payload is read*. No malicious-enemy-ship-ID frame can reach any effect, so there is nothing to
  guard with a same-side check. (Caveat, stated honestly: native `FedSrv/FedSrv.CPP` is not
  vendored in this repo's tree — only cited in the P-docs — so the "native shows no side check"
  detail rests on the P118-documented excerpt, not an in-repo re-read.)

---

## 3. AUTODONATE(112/116) — N/A

Definitions (fmid 112 = client→server, 116 = server→client; FedMessageDumper.cs:38-39):
```cpp
DEFINE_FEDMSG(C, AUTODONATE, 112)
    ShipID      sidDonateTo;
    Money       amount;
END_FEDMSG

DEFINE_FEDMSG(S, AUTODONATE, 116)
    ShipID      sidDonateBy;
    ShipID      sidDonateTo;
    Money       amount;
END_FEDMSG
```
Semantics: a player designates another ship (`sidDonateTo`) to automatically receive a portion of
their future income; the S→C variant announces the (donor→recipient) standing arrangement.

Port disposition: **N/A.** An autodonate arrangement is only meaningful if (a) players hold
individually distinguishable money, and (b) per-player income is routed to an individual wallet —
so a designation can redirect a *specific* player's future earnings. This port has neither:
income accrues to the single `Side.Money` (Mission.cs:299 `side.Money += income`), with no owner,
and there is no designation target to hold. Confirmed there is **no `GetAutoDonate`/`SetAutoDonate`
reference anywhere in `src`** (only the dumper name table; the earlier `SedAutoDonate` spell-check
hit is in FedMessageDumper.cs).

**The named join-sync gap (worth recording, not building):** native `CFSMission::SendMissionInfo`
sends `SET_MONEY` and `AUTODONATE` in the same join burst (fsmission.cpp:4270-4299 — one SET_MONEY
per teammate, P118 doc:106-121). This port's own join burst (Server.cs:1257-1298) sends the pooled
`SET_MONEY(115)` (Server.cs:1262-1267) and, per P119, `CREATE_BUCKETS(191)` + one `BUCKET_STATUS(65)`
per bucket (Server.cs:1282-1298) — but **no AUTODONATE(116)**. That omission is *correct*, not a
lacuna: with no per-player wallet there is no autodonate target whose existence a syncing player
should be told about. The native loop's "one SET_MONEY per teammate" correctly collapsed to one
shared-treasury message (P118), and the AUTODONATE half of the same native loop has no analogue to
collapse. Naming it here is the honest P118-style "gap worth naming even if not building" note the
brief asked for; it does not justify a codec or handler.

---

## 4. REQUEST_MONEY(182) — N/A

Definition (fmid confirmed FedMessageDumper.cs:45; `CS` = client↔server, bidirectional):
```cpp
DEFINE_FEDMSG(CS, REQUEST_MONEY, 182)
    Money               amount;
    ShipID              shipidRequest;
    HullID              hidFor;
END_FEDMSG
```
Semantics (native handler `FedSrv/FedSrv.CPP:5361-5404`, read in full this session per the brief):
a player asks for money; the server either forwards the request to the player's designated
autodonate target, or — if none, or that target can't cover it — scans **every teammate's
individual `GetMoney()`** to find whoever currently holds the most, and forwards the request to
them instead.

Port disposition: **N/A.** Both branches of the resolution are uncomputable on one pooled number:
there is no per-player `GetMoney()` to rank teammates by richest, and no autodonate designation
exists (§3). The only "player with money" is the side, which both requester and donor would
already share — so the whole lookup collapses into "give the requester money out of the shared
pool," i.e. the exact no-op of §1.

`hidFor` (the hull the money is meant to buy) is context worth recording, not building: it marks
`REQUEST_MONEY` as a lightweight in-game *loan-request UI flow* (a downed/rebuilding pilot pinging
a mate to fund a specific hull) rather than a money-movement primitive — reinforcing why, absent
individual wallets, there is no honest wire-level realization here.

The port's declared seam: `IIgcSite.GetMoneyRequest(IShipIgc pshipSender, int amount, short hidFor)`
exists as a default no-op on the IGC-site interface (Sim/IIgcSite.cs:117-118, mirroring
igc.h:4828) with a recording stub (Sim/RecordingIgcSite.cs:141-142). Grep confirms **zero call
sites** — no ship code path and no wire handler invoke it. An inbound fmid-182 frame likewise
drops into the report-and-ignore catch-all (Server.cs:1990).

---

## 5. Why no code was written

The brief's conditional ("build only if a real, different angle justifies a small handler") does
not fire:

- There is no **genuine** loopback/broadcast-back or rejection behavior worth an explicit handler.
  The port's terminal `else` already reports-and-ignores any unimplemented fmid (Server.cs:1988-1991)
  — the established, tested precedent for unimplemented protocol messages (the same posture P110
  records for `SHIP_RESET`(183), TELEPORT_ACK_P110.md:112-114). Adding three bespoke "do nothing"
  handlers or three unused decoders would be dead duplication of a path that already exists.
- Inventing per-player money to make them meaningful is explicitly out of scope (the pooled
  `Side.Money` design is a standing, already-committed fact; the brief forbids revisiting it).
- Inventing a donation/loan flow is likewise out of scope.

Zero code changes ⇒ no new runtime behavior exists to test ⇒ the suite passing **unmodified** is
the verification (the SHIP_RESET precedent). No commit is made for the same reason — this is a
roadmap/report note, matching how SHIP_RESET was recorded, not a forced commit.

---

## 6. Phase 4 economy cluster — what remains open after P122

Closing out the donate/request family:

- **Done / wired:** `MONEY_CHANGE`(60) + `SET_MONEY`(115) — P118; `PAYDAY`(114) — P51;
  `BUCKET_DONATE`(64) / `BUCKET_STATUS`(65) / `CREATE_BUCKETS`(191) — P119;
  `ASTEROID_MINED`(198) — P120.
- **Closed N/A here (P122):** `PLAYER_DONATE`(67), `AUTODONATE`(112/116), `REQUEST_MONEY`(182) —
  all per-player-money features unmappable onto the pooled model.

**Remaining open in the economy cluster** (both inherited from P118's forward-look,
MONEY_CHANGE_SET_MONEY_P118.md:163-175):

1. **Purchase / loadout costing** — the port's buy path `ApplyLoadout` (Server.cs:3622-3690)
   still grants hull/parts free; adding real part/hull prices (native FedSrv.CPP:6203-6207,
   7162-7166) would finally give `MONEY_CHANGE` its negative-`dMoney` purchase-deduction site.
2. **Station economy depth** — `STATIONS_UPDATE`(57) / `STATION_CAPTURE`(58) /
   `STATION_DESTROYED`(59): no station-state broadcast or capture mechanic exists yet.
   (`BUILDINGEFFECT_DESTROYED`(181) remains out of scope per its entanglement with the
   `OT_development`/tech-research object model, per the brief.)

Nothing in this task alters P103–P121 code; all of it was read for context only.

---

## 7. Citations (file:line actually read this session)

- `src/Allegiance.Core/Model/Side.cs:50-60` — `Side.Money` pooled `long`; no per-player money.
- `src/Allegiance.Server/Server.cs:1257-1298` — join burst: pooled `SET_MONEY` + `CREATE_BUCKETS`
  + `BUCKET_STATUS`; **no `AUTODONATE`** (the named gap, §3).
- `src/Allegiance.Server/Server.cs:1959-1991` — `BUCKET_DONATE` handler tail and the terminal
  report-and-ignore catch-all for unimplemented fmids.
- `src/Allegiance.Core/Wire/FedMessageDumper.cs:29,38,39,45` — fmid table: 67=PLAYER_DONATE,
  112/116=AUTODONATE, 182=REQUEST_MONEY.
- `src/Allegiance.Core/Sim/IIgcSite.cs:117-118` — `GetMoneyRequest(..., amount, hidFor)` default
  no-op (igc.h:4828); `src/Allegiance.Core/Sim/RecordingIgcSite.cs:141-142` — recording stub.
- Fresh `src` grep: no `GetAutoDonate`/`SetAutoDonate`, no per-player balance/budget/allowance field.
- Context cross-read: `MONEY_CHANGE_SET_MONEY_P118.md:11-33,85,106-121,163-175`;
  `TELEPORT_ACK_P110.md:112-114` (SHIP_RESET-style N/A precedent);
  `git show` of 28c1e80 (P119 bucket = shared-pool, not per-player).
- Native `FedSrv/FedSrv.CPP` / `Inc/Messages.h` are **not vendored in this repo**; their handler
  lines are taken from the P118-documented excerpts, flagged in §2 rather than asserted as an
  in-repo re-read.

---

Session stats logged via
`python3 ~/Development/allegiance-tools/session_stats.py 20260909_214251_d3e7cf`.
No code changed ⇒ no commit; this is a report-only task (SHIP_RESET precedent).
