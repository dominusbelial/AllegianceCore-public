# P451 — no "base under attack" / "critically damaged" alert for the defending team

## Why this task exists

A gameplay-parity pass found that a team whose base is being bombed hears
nothing and reads nothing until the base dies. In the original game, the
defending side gets a throttled team chat line plus a voice cue ("Garrison
under attack", "…critically damaged") the moment an enemy starts hitting a
station. That is the trigger for the whole defend-the-base reaction to a
bomber run.

- **Native server** `FedSrvSiteBase::DamageStationEvent`
  (`~/Development/Allegiance/src/FedSrv/FedSrv.CPP:12183-12230`). It runs only
  when `amount >= 1.0f`, there is a launcher, and `(type & c_dmgidNoWarn) == 0`,
  and it is throttled to one report per station per **10 s**
  (`g.timeNow - station->GetLastDamageReport() > 10.0f`). When the launcher's
  side differs from the station's side:
  - If `station->GetFraction() < 0.30`, it sends team chat with the station
    type's `GetCriticalSound()`: `"%s in %s critically damaged and under
    attack by %s"` (station name, sector name, launcher name).
  - Otherwise it sends team chat with `GetUnderAttackSound()`: `"%s in %s under
    attack by %s"`.
  - A same-side player shooter gets a private `"Watch your fire"` with
    `droneWatchFireSound` (`:12221-12228`).
- **Where native raises it**: `CstationIGC::ReceiveDamage` calls
  `pigc->DamageStationEvent(this, launcher, type, amount)`
  (`Igc/stationIGC.cpp:169`). That covers every damage source: ordnance,
  explosions, collisions.
- **Native client** `BaseClient::DamageStationEvent`
  (`clintlib/clintlib.cpp:3075-3087`) plays a hull-hit or shield-hit sound
  when the damaged station is in the player's own cluster. That is a local
  sound only, and is out of scope here (see below).
- **Port**: `IIgcSite.DamageStationEvent` is only an empty default method
  (`src/Allegiance.Core/Sim/IIgcSite.cs:301`). `RecordingIgcSite.cs:183-184`
  wraps it, but nothing in `Sim/Station.cs` or `Model/Sector.cs` ever raises
  it, and `src/Allegiance.Server/ServerHost.cs` doesn't implement it (only
  `KillStationEvent`, `ServerHost.cs:460-488`).
  - The ordnance hit site is `Model/Sector.cs:1762`
    (`station.ReceiveDamage(ord.DamageType, ord.Power, now, position1, spos,
    ord.Launcher)`).
  - `Station.ReceiveDamage` is at `Sim/Station.cs:231+`.
- **The data already exists.** `DataStationTypeIGC` decodes `UnderAttackSound`
  and `CriticalSound` (`src/Allegiance.Core/Data/DataStationTypeIGC.cs:55-56`).
- **The client already renders chat**: `ChatMessage` → `ClientWorld`
  (`ConnectionSession.cs:653-654`). Chat has a wire `VoiceOver` sound id
  (`src/Allegiance.Core/Wire/ChatData.cs:38`).
- **The client can't play arbitrary voice ids yet.** The only voice path
  plays **quick-chat DB** entries whose caption matches the text
  (`src/Allegiance.ClientCore.MonoGame/Game1.Session.cs:668-678`,
  `_quickChatDb.ByVoiceOver`). Station voice lines are hardcoded per event
  (`Audio/SoundManager.cs:103-109, 243-246, 686-697`, garrison family, sounddef
  ids 860-927).
- **The assets exist.** They're in `~/Development/Artwork/`:
  `vo_attack_garrison.ogg`, `vo_attack_<type>.ogg` for each station family,
  and `vo_critical_station.ogg`. `~/Development/Artwork/sounddef.mdl` binds
  them (`garrisonAttackedSound`, `garrisonCritical`, …). Only the four
  destroyed/captured garrison clips are bundled in
  `src/Allegiance.ClientCore.MonoGame/Content/Sounds/`.
- The server already sends team chat announcements; the pattern is
  `SendChatAnnouncement`, `src/Allegiance.Server/Server.Lobby.cs:1101-1125`.

## Scope

Do the server half first and commit it before starting the client half.

1. **Raise the event.** Raise `DamageStationEvent` for every real station
   hit with the native preconditions (`amount >= 1`, launcher present,
   no `c_dmgidNoWarn` bit; find the port's constant for it or add it with a
   citation). Prefer raising it inside `Station.ReceiveDamage`, reaching the
   site through the launcher's mission, so explosions and collisions are
   covered as in native. If that isn't reachable cleanly, raise it at each
   station-damage call site, list them all, and document the choice.
2. **Server alert** (`ServerHost` → `Server`):
   - Keep a per-station last-report time and send at most one alert per
     10 s.
   - Pick critical (station hull fraction `< 0.30`) or under-attack.
   - Send team-targeted `ChatMessage` only to the **station's side**. Use
     native's exact text shapes, with the launcher's pilot name (or a model
     name for a non-player launcher). Set `VoiceOver` to the station type's
     `CriticalSound`/`UnderAttackSound`.
   - Port the same-side "Watch your fire" private line too. It's the same
     function and a few lines.
3. **Client voice.**
   - When a chat arrives whose `VoiceOver` is one of the real station
     types' `UnderAttackSound`/`CriticalSound` ids, play the matching clip.
   - Resolve id → asset stem from `sounddef.mdl` for just the ids the real
     `PCore001` station types use. Put that in a small static table, cited,
     next to the existing garrison constants.
   - Bundle the needed `.ogg` files from `~/Development/Artwork/` into
     `Content/Sounds` the same way the four existing clips are bundled.
   - The chat text itself already shows in the chat log. Confirm this, and
     don't restyle it.

## Explicitly out of scope

- Native's client-local station **hit sounds**
  (`clintlib.cpp:3075-3087`, `otherHullHitSound`/`otherShieldHitSound`).
  That's a possible small follow-up.
- Constructor/builder "under attack" lines (`ConstructorUnderAttackSound`).
- Alliances (`CHAT_ALLIES`, "(Ally)" prefix). The port has no alliance
  concept.
- Station capture's shield gate (P450), minefields (P452/P453) and teleport
  probes (P454). Those are separate tasks in this batch.
- Named follow-ups from the same parity pass, not scoped yet: armed probes,
  enemy-spotting reports and the in-flight turret seat switch.

## Verification

- **A wire-level test that fails on the unfixed code first.** Two loopback
  clients are on different sides. The attacker's ordnance hits the other
  side's station through the real combat path. Assert:
  - the defender receives a team `ChatMessage` with the under-attack text
    and `VoiceOver == UnderAttackSound` for that station type;
  - the attacker's side receives nothing;
  - a second hit within 10 s produces no second alert;
  - a hit that drops the station below 30% produces the critical variant
    once the throttle window has passed.
  Record in the report that the test fails before the fix (no chat).
- A small pure test for the client id → stem table against real
  `PCore001` station types.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0 warnings
  and 0 errors. Bare `dotnet` is a broken snap stub; always use the full
  path.
- Full test suite via `/home/domingo/.dotnet/dotnet test`, 3 consecutive
  clean runs. Only the pre-existing documented live-wire timing flakes are
  acceptable, and you must name any you see.

## Deliverables

1. Commits on **`master`** (not a side branch, not pushed; Domingo pushes).
   Use two commits if you finished both halves: server, then client. Check
   `git branch --show-current` before committing.
2. Report doc `docs/reports/P451_STATION_UNDER_ATTACK_ALERTS.md`. Include the
   pre-fix failing test evidence, where the event is raised and why, the
   throttle, the id → stem table and its sounddef source, and which `.ogg`
   files were bundled. If you ran out of time on the client half, say so
   plainly and describe exactly what's left.
