using System.Net.Http;
using System.Net.Http.Json;
using System.Linq;
using System.Text;
using System.Text.Json;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.QuickChat;
using Allegiance.ClientCore.MonoGame.Capture;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using XnaKeys = Microsoft.Xna.Framework.Input.Keys;
using XnaModel = Microsoft.Xna.Framework.Graphics.Model;
using NumericsVec2 = System.Numerics.Vector2;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// The panes slice of <see cref="Game1"/> (P384 stage-6 split from <c>Game1.cs</c>).
/// Partial class — shares the <c>Game1</c> type, so private field/method access
/// across the split files is unaffected.
/// </summary>
public sealed partial class Game1
{


    /// <summary>
    /// P268 — the docked/hangar loadout screen. Returns true (meaning "consume
    /// this frame: show the hangar, no flight input") while the local ship is
    /// docked (its <see cref="State.ShipEntity.StationId"/> is latched non-NA by
    /// the real DockedNotify / SHIP_STATUS wire traffic). Behaviour:
    ///   UP/DOWN     move the shop cursor (hulls then parts);
    ///   SPACE       select — pick the hull, or toggle a part in/out of the loadout;
    ///   L           LAUNCH — send a real BUY_LOADOUT(fLaunch=true) with the chosen
    ///               hull + toggled parts (HangarScreen::OnButtonLaunch →
    ///               BuyLoadout(ship,true), hangarscreen.cpp:145-160);
    ///   U           UNDOCK without buying — send the plain FM_C_DOCKED(NA);
    ///   (fallback)  after <see cref="DockFallbackMs"/> with no interaction,
    ///               auto-launch the current loadout so an unattended dock never
    ///               strands the POC player.
    /// The hangar exits itself once the server undocks (StationId reverts to NA).
    /// </summary>
    private bool HandleDockedScreen(KeyboardState ks, bool composing)
    {
        var world = _session.World;
        short me = world.MyShipId ?? -1;
        bool myDocked = world.Ships.TryGetValue(me, out _) && world.Ships[me].StationId != -1;

        if (myDocked)
        {
            var mySe = world.Ships[me]; // non-null (myDocked guaranteed membership)
            // Enter the hangar once per dock: build a fresh shop model from the
            // current ship + catalogue (never mutate the current ship itself).
            if (!_dockedActive)
            {
                _dockedActive = true;
                _launchSent = false;
                _dockSinceMs = Environment.TickCount64;
                // P284 — gate the offered hulls/parts by the docked station's
                // tech and the side's researched developments: an item shows iff
                // TtbmRequired ⊆ (stationLocal | mySideDev), the exact P283
                // containment. The docked station resolves through the world's
                // station table → its StationTypeIGC (TtbmLocal); the side's
                // live DevelopmentTechs comes from the wire (CREATE_BUCKETS at
                // join, SIDE_TECH_CHANGE on research). If either can't be
                // resolved, FilteredFor degrades to the full catalogue.
                var offerCat = _shopCatalog ?? Rendering.ShopCatalog.Empty;
                byte[]? builderStationLocal = null;
                Allegiance.Core.Model.TechTreeBitMask? builderSideDev = null;
                if (_shopCatalog is { StationTypes: not null } scat
                    && world.Stations.TryGetValue(mySe.StationId, out var dockStation)
                    && scat.StationTypes.TryGetValue(dockStation.StationTypeId, out var stype)
                    && world.DevelopmentTechsFor(world.MySideId ?? -1) is { } myDev)
                {
                    offerCat = _shopCatalog.FilteredFor(stype.TtbmLocal, myDev);
                    // P285 — keep the same tech terms so the Builder-target
                    // chooser is tech-gated the same way the hull/part offer is.
                    builderStationLocal = stype.TtbmLocal;
                    builderSideDev = myDev;
                }
                _shopModel = new Rendering.ShopModel(
                    offerCat,
                    mySe.HullId,
                    mySe.Loadout ?? Array.Empty<ExpandedPartData>(),
                    offerCat.BuilderTargetsFor(builderStationLocal, builderSideDev));
                // P273 — build the teleport destination list from the live world.
                _teleportModel = BuildTeleportModel(world, mySe);
                _teleportActive = false;
                Console.WriteLine($"[monogame] docked: hangar/loadout screen ({offerCat.Hulls.Count} hulls, {offerCat.Parts.Count} parts offered)");
            }

            // P273 — T toggles the docked sub-view between the hangar loadout
            // shop and the teleport pane (edge-triggered like every toggle).
            bool tKey = ks.IsKeyDown(XnaKeys.T);
            if (!composing && !_launchSent && tKey && !_telTWasDown)
            {
                _teleportActive = !_teleportActive;
                // Rebuild the destination list each time the pane opens so it
                // reflects the current world (a teammate may have docked/left).
                if (_teleportActive && world.Ships.TryGetValue(me, out var myNow))
                    _teleportModel = BuildTeleportModel(world, myNow);
                Console.WriteLine($"[monogame] teleport pane {( _teleportActive ? "OPEN" : "CLOSED" )}");
            }
            _telTWasDown = tKey;

            if (!composing && !_launchSent && _teleportActive && _teleportModel is { } tel)
            {
                // P273 — the teleport pane's own keys (sub-view of the docked screen).
                bool up = ks.IsKeyDown(XnaKeys.Up);
                bool down = ks.IsKeyDown(XnaKeys.Down);
                bool space = ks.IsKeyDown(XnaKeys.Space);
                bool bKey = ks.IsKeyDown(XnaKeys.B);

                if (up && !_telUpWasDown) { tel.MoveCursor(-1); Console.WriteLine($"[monogame] teleport cursor -> {tel.Cursor}"); }
                if (down && !_telDownWasDown) { tel.MoveCursor(+1); Console.WriteLine($"[monogame] teleport cursor -> {tel.Cursor}"); }
                if (space && !_telSpaceWasDown)
                {
                    ActivateTeleport(tel, mySe);
                }
                if (bKey && !_telBWasDown)
                {
                    _teleportActive = false;
                    Console.WriteLine("[monogame] teleport -> back to hangar");
                }

                _telUpWasDown = up; _telDownWasDown = down;
                _telSpaceWasDown = space; _telBWasDown = bKey;
            }
            // While composing (chat) the shop keys yield to the text line.
            else if (!composing && !_launchSent && _shopModel is { } shop)
            {
                bool up = ks.IsKeyDown(XnaKeys.Up);
                bool down = ks.IsKeyDown(XnaKeys.Down);
                bool space = ks.IsKeyDown(XnaKeys.Space);
                bool lKey = ks.IsKeyDown(XnaKeys.L);
                bool uKey = ks.IsKeyDown(XnaKeys.U);
                // P285 — Builder-target chooser: ','/'.' cycle the station type
                // a purchased Builder/Constructor hull should construct (the
                // same ','/'.' pair P272 uses on the starmap, free in the shop).
                bool btPrev = ks.IsKeyDown(XnaKeys.OemComma);
                bool btNext = ks.IsKeyDown(XnaKeys.OemPeriod);

                if (up && !_dockUpWasDown) { shop.MoveCursor(-1); Console.WriteLine($"[monogame] shop cursor -> {shop.Cursor}"); }
                if (down && !_dockDownWasDown) { shop.MoveCursor(+1); Console.WriteLine($"[monogame] shop cursor -> {shop.Cursor}"); }
                if (space && !_dockSpaceWasDown)
                {
                    shop.Activate();
                    Console.WriteLine($"[monogame] shop select: hull={shop.EffectiveHullId} parts={shop.ChosenParts.Count}");
                }
                if (btPrev && !_btPrevWasDown) { shop.CycleBuilderTarget(-1); Console.WriteLine($"[monogame] builder target -> {shop.CurrentBuilderTarget?.Name ?? "(none)"}"); }
                if (btNext && !_btNextWasDown) { shop.CycleBuilderTarget(+1); Console.WriteLine($"[monogame] builder target -> {shop.CurrentBuilderTarget?.Name ?? "(none)"}"); }
                if (lKey && !_dockLWasDown)
                {
                    SendBuilderTargetIfApplicable(shop);
                    _session.SendBuyLoadout(shop.EffectiveHullId, fLaunch: true, parts: shop.ChosenParts);
                    _launchSent = true;
                    Console.WriteLine($"[monogame] LAUNCH -> BUY_LOADOUT(fLaunch) hull {shop.EffectiveHullId} + {shop.ChosenParts.Count} part(s)");
                }
                else if (uKey && !_dockUWasDown)
                {
                    _session.SendDocked();
                    _launchSent = true;
                    Console.WriteLine("[monogame] UNDOCK (no buy) -> FM_C_DOCKED(NA)");
                }

                _dockUpWasDown = up; _dockDownWasDown = down;
                _dockSpaceWasDown = space; _dockLWasDown = lKey; _dockUWasDown = uKey;
                _btPrevWasDown = btPrev; _btNextWasDown = btNext;

                // Fallback: an unattended dock must never stall the POC, so after
                // the grace period we auto-launch the current (possibly shopped)
                // loadout — the same BUY_LOADOUT(fLaunch) the L key sends.
                if (!_launchSent && Environment.TickCount64 - _dockSinceMs >= DockFallbackMs)
                {
                    SendBuilderTargetIfApplicable(shop);
                    _session.SendBuyLoadout(shop.EffectiveHullId, fLaunch: true, parts: shop.ChosenParts);
                    _launchSent = true;
                    Console.WriteLine("[monogame] dock fallback: auto-launch current loadout");
                }
            }
            else if (_launchSent)
            {
                // Launch requested; release key latches so a held key can't
                // re-fire if we somehow re-enter before undock completes.
                _dockUpWasDown = _dockDownWasDown = _dockSpaceWasDown = _dockLWasDown = _dockUWasDown = false;
            }

            return true; // docked → consume the frame, no flight input
        }

        if (_dockedActive) // the server undocked us → leave the hangar
        {
            _dockedActive = false;
            _shopModel = null;
            _teleportModel = null;
            _teleportActive = false;
            Console.WriteLine("[monogame] undocked: leaving hangar");
        }
        return false;
    }

    /// <summary>P285 — when the shop's effective hull is a Builder and the
    /// player has cycled to a Builder target station type, send the port-extension
    /// <c>SET_BUILDER_TARGET</c>(201) so the server sets the launched ship's
    /// <see cref="Allegiance.Core.Sim.Ship.BuilderStationType"/> to the choice
    /// (the purchase-time wiring the P268/P284 shop lacked). Sent just before
    /// the BUY_LOADOUT launch; the field is server-persisted on the ship, so
    /// ordering vs the launch is immaterial.</summary>
    private void SendBuilderTargetIfApplicable(Rendering.ShopModel shop)
    {
        if (shop.IsBuilderActive && shop.BuilderStationTypeId is { } stId)
        {
            _session.SendSetBuilderTarget(shop.EffectiveHullId, stId);
            Console.WriteLine($"[monogame] BUILDER TARGET -> SET_BUILDER_TARGET hull {shop.EffectiveHullId} stationType {stId}");
        }
    }

    /// <summary>
    /// P273 — build the docked teleport pane's destination list from the live
    /// world snapshot: boardable same-side docked ships with turret slots plus
    /// friendly restart-capable stations (excluding the one we're at). Hull and
    /// station-type lookups come from the shop catalogue's parsed core; a
    /// missing core degrades the list (ships need the hull's turret-slot count,
    /// so they are omitted when the core is absent, but stations still list by
    /// their wire name).
    /// </summary>
    private Rendering.TeleportPaneModel BuildTeleportModel(ClientWorld world, ShipEntity mySe)
    {
        var cat = _shopCatalog;
        var hulls = cat?.Hulls.ToDictionary(h => h.HullId) ?? new Dictionary<short, DataHullTypeIGC>();
        Func<short, DataHullTypeIGC?> hullLookup = id => hulls.TryGetValue(id, out var h) ? h : null;
        Func<short, DataStationTypeIGC?> stypeLookup =
            cat?.StationTypes is { } stypes
                ? id => stypes.TryGetValue(id, out var t) ? t : null
                : _ => null;

        short me;
        short mySide;
        var ships = new List<ShipView>();
        var stations = new List<StationView>();

        // P319 (finding H-3) — world.Ships / world.Stations are upserted by the
        // receive thread under _session.Gate (EXPORT(26), SHIP_STATUS, ExportStation /
        // StationCapture); enumerating them unlocked on dock entry threw
        // InvalidOperationException ("Collection was modified") — a crash at the
        // exact moment of docking. Same P306 precedent as elsewhere in Game1:
        // snapshot the two tables (and MyShipId/MySideId) into local lists under
        // the lock, then build the pane model outside it.
        lock (_session.Gate)
        {
            me = world.MyShipId ?? -1;
            mySide = world.MySideId ?? -1;

            foreach (var s in world.Ships.Values)
                if (s.ShipId != me && !s.IsNotVisible)
                    ships.Add(new ShipView(s.ShipId, s.SideId, s.StationId, s.HullId));
            foreach (var s in world.Stations.Values)
                stations.Add(new StationView(s.StationId, s.SideId, s.StationTypeId, s.Name));
        }

        return Rendering.TeleportPaneModel.Build(
            me, mySide, mySe.StationId, ships, stations, hullLookup, stypeLookup);
    }

    /// <summary>
    /// P289 — resolve a station's real per-type Radius, the scale driver for the
    /// procedural station visual, from its StationTypeId via the shop
    /// catalogue's station-type table (loaded once at boot from the same
    /// PCore001.igc the server boots). Falls back to
    /// <see cref="StationRenderer.DefaultFallbackRadius"/> when the static core
    /// is absent or the type id is unknown, so a missing core never breaks
    /// station rendering — every real station still appears at a sensible
    /// size proportional to the same data the server uses.
    /// </summary>
    private float ResolveStationRadius(Allegiance.Core.DataStationIGC st)
    {
        if (_shopCatalog is { StationTypes: not null } cat
            && cat.StationTypes.TryGetValue(st.StationTypeId, out var type)
            && type.Radius > 0f)
            return type.Radius;
        return Rendering.StationRenderer.DefaultFallbackRadius;
    }

    /// <summary>
    /// P273 — act on the teleport pane's SELECT: translate the pure verdict into
    /// the real wire message(s), mirroring native <c>TeleportPane::OnButtonTeleport</c>
    /// (<c>teleportpane.cpp:296-361</c>):
    /// <list type="bullet">
    /// <item>board a ship — if already on a different ship, leave first (the
    /// native <c>DisembarkAndBoard</c>, :312-319) then <c>BOARD_SHIP</c>;</item>
    /// <item>teleport to a station — if on a ship, leave first (the native
    /// <c>DisembarkAndTeleport</c>, :328-338) then <c>DOCKED(stationID)</c>;</item>
    /// </list>
    /// After acting, close the pane and suppress the auto-launch fallback (the
    /// player did something intentional).
    /// </summary>
    private void ActivateTeleport(Rendering.TeleportPaneModel tel, ShipEntity mySe)
    {
        var action = tel.Activate();
        bool onboard = mySe.ParentId != -1;   // are we currently a gunner/observer on another ship?

        switch (action.Kind)
        {
            case Rendering.TeleportPaneModel.ActionKind.BoardShip:
                if (onboard) { _session.SendBoardShip(Rendering.TeleportPaneModel.Na); Console.WriteLine("[monogame] teleport: leave current ship (DisembarkAndBoard)"); }
                _session.SendBoardShip(action.TargetId);
                Console.WriteLine($"[monogame] teleport: board ship {action.TargetId} as gunner -> BOARD_SHIP");
                break;

            case Rendering.TeleportPaneModel.ActionKind.TeleportToStation:
                if (onboard) { _session.SendBoardShip(Rendering.TeleportPaneModel.Na); Console.WriteLine("[monogame] teleport: leave current ship (DisembarkAndTeleport)"); }
                _session.SendDocked(action.TargetId);
                Console.WriteLine($"[monogame] teleport: to station {action.TargetId} -> DOCKED(stationID)");
                break;

            default:
                Console.WriteLine("[monogame] teleport: nothing selected");
                return;
        }

        _teleportActive = false;
        _launchSent = true;   // suppress the unattended-dock auto-launch after an intentional action
    }

    /// <summary>
    /// P271 — whether the mission-briefing popup should be shown this frame and,
    /// when the countdown is known, the integer seconds remaining. The popup is
    /// visible once the server's SET_BRIEFING_TEXT(9) has arrived, until it is
    /// dismissed (Enter/Esc, see <see cref="DismissBriefing"/>) or the briefing
    /// countdown expires (<c>remaining &lt;= 0</c> — the mission is going live,
    /// native MissionBriefPopup's stage-advances-and-closes behaviour,
    /// teamscreen.cpp:1668-1704). <paramref name="remainingSeconds"/> is null
    /// while the countdown hasn't begun (SET_BRIEFING_TEXT can arrive before the
    /// later SET_START_TIME countdown start).
    /// </summary>
    private bool IsBriefingVisible(out int? remainingSeconds)
    {
        remainingSeconds = null;
        var world = _session.World;
        if (string.IsNullOrEmpty(world.BriefingText) || _briefingDismissed)
            return false;
        if (world.CountdownStartLocalMs is { } cd)
        {
            double elapsed = (Environment.TickCount64 - cd) / 1000.0;
            int remaining = (int)Math.Ceiling(Audio.CountdownSound.BriefingCountdownSeconds - elapsed);
            if (remaining <= 0)
                return false;      // countdown over → mission live → auto-close
            remainingSeconds = remaining;
        }
        return true;
    }

    /// <summary>P271 — latch a manual Enter/Esc dismissal of the mission-briefing
    /// popup so it does not reappear for the rest of the mission (SET_BRIEFING_TEXT
    /// is one-shot per join).</summary>
    private void DismissBriefing()
    {
        _briefingDismissed = true;
        Console.WriteLine("[monogame] briefing dismissed");
    }

    /// <summary>
    /// P362 — build the per-mounted-system status list for the local ship.
    /// Resolves each mounted weapon/missile system in the Loadout (identity +
    /// ammo semantics from the BUY_LOADOUT_ACK-granted entries) to its name and
    /// ammo semantics via the shop catalogue, then advances the reload tracker
    /// with the ship's live ammo fraction. Systems that consume the shared
    /// magazine (projectile weapons) can be Ready/Reloading/Depleted; systems
    /// that never consume it (energy weapons, missile racks) are always Ready.
    /// Returns empty when the loadout is unknown or the catalogue is unavailable.
    /// </summary>
    private IReadOnlyList<Flight.WeaponStatusRow> BuildWeaponStatusRows(ShipEntity entity, float ammoFrac)
    {
        // A successful P361 auto-reload is signalled to the client by the
        // sector RELOAD(129) relay the server broadcasts (the ammo fraction
        // never shows 0 — the server refills synchronously in the same tick),
        // so edge-detect the own ship's ReloadCount across frames.
        bool reloadArrived = entity.ReloadCount != _lastWeaponReloadCount;
        _lastWeaponReloadCount = entity.ReloadCount;

        var cat = _shopCatalog;
        if (cat is null || entity.Loadout is not { Count: > 0 } lo)
            return _weaponReloadTracker.Update((float)_nowSeconds, ammoFrac, reloadArrived, Array.Empty<Flight.WeaponSlotDescriptor>());

        var slots = new List<Flight.WeaponSlotDescriptor>(lo.Count);
        foreach (var p in lo)
        {
            DataPartTypeIGC part = default;
            bool found = false;
            foreach (var pt in cat.Parts) if (pt.PartId == p.PartId) { part = pt; found = true; break; }
            if (!found) continue;

            bool consumes;
            if (part.Type is DataPartTypeIGC.Weapon w) consumes = w.CAmmoPerShot != 0;
            else if (part.Type is DataPartTypeIGC.MissileLauncher) consumes = false;
            else continue;   // not a weapon system (shield/cloak/pack/afterburner)

            slots.Add(new Flight.WeaponSlotDescriptor(
                p.MountId, Rendering.ShopLayout.AsciiSafe(part.Name), consumes, p.Amount));
        }
        return _weaponReloadTracker.Update((float)_nowSeconds, ammoFrac, reloadArrived, slots);
    }

    /// <summary>
    /// P272 — advance the starmap's selected cluster one step (delta +1 next,
    /// -1 prev) through the whole-mission graph's cluster-id order. First press
    /// (no selection yet) picks the player's current sector. Pure wrap-around
    /// lives in <see cref="SectorInfoPaneLayout.NextSelection"/> (unit-tested);
    /// this only pulls the ordered id list + the player's sector out of the
    /// world under the Gate lock.
    /// </summary>
    private void CycleSelectedSector(int delta)
    {
        var world = _session.World;
        lock (_session.Gate)
        {
            if (world.Graph is not { } g || g.Clusters.Count == 0)
                return;
            var ids = g.Clusters.Select(c => c.Id).ToList();
            short mySector = ids[0];
            if (world.MyShipId is { } me && world.Ships.TryGetValue(me, out var own))
                mySector = own.SectorId;
            _selectedSectorId = SectorInfoPaneLayout.NextSelection(_selectedSectorId, ids, mySector, delta);
            Console.WriteLine($"[monogame] starmap select: {_selectedSectorId}");
        }
    }

    /// <summary>Drive the post-connect side-select screen. We sit here (no ship)
    /// until the server seats us after we send FM_C_POSITIONREQ(39); the moment
    /// SET_CLUSTER assigns us a ship (<see cref="ClientWorld.MyShipId"/> is set),
    /// transition to the pre-flight ReadyCheck screen (P282) — NOT straight to
    /// Flying: the mission only starts once every occupied side is
    /// aggregate-ready, so the player must toggle ready first.</summary>
    private void UpdateSideSelect(KeyboardState ks)
    {
        // P306 — headless capture auto-seats on side 0 (where all the stress
        // bots live) instead of waiting for a human to pick a side / the
        // server's 30s auto-assign, so the video starts promptly.
        if (_captureSpec is not null)
        {
            if (_session.World.MyShipId is not null)
            {
                _state = ClientState.ReadyCheck;
                return;
            }
            var rosterCap = _session.World.SideRoster;
            if (rosterCap?.Sides.Length > 0 && !_capSideRequested)
            {
                _capSideRequested = true;
                short sideCap = rosterCap.Sides[0].SideId;
                _session.SendPositionReq(sideCap);
                Console.WriteLine($"[capture] auto side-select -> side {sideCap}");
            }
            return;
        }

        if (_session.World.MyShipId is not null)
        {
            _state = ClientState.ReadyCheck;
            return;
        }

        var roster = _session.World.SideRoster;
        int count = roster?.Sides.Length ?? 0;

        bool up = ks.IsKeyDown(XnaKeys.Up);
        if (up && !_ssUpWasDown && count > 0)
            _sideSelectIndex = (_sideSelectIndex - 1 + count) % count;
        _ssUpWasDown = up;

        bool down = ks.IsKeyDown(XnaKeys.Down);
        if (down && !_ssDownWasDown && count > 0)
            _sideSelectIndex = (_sideSelectIndex + 1) % count;
        _ssDownWasDown = down;

        bool enter = ks.IsKeyDown(XnaKeys.Enter);
        bool space = ks.IsKeyDown(XnaKeys.Space);
        if (count > 0 && (enter && !_ssEnterWasDown || space && !_ssSpaceWasDown))
        {
            int sel = _sideSelectIndex % count;
            short sideId = roster!.Sides[sel].SideId;
            Console.WriteLine($"[monogame] side-select: requesting side {sideId}");
            _session.SendPositionReq(sideId);
        }
        _ssEnterWasDown = enter;
        _ssSpaceWasDown = space;

        bool r = ks.IsKeyDown(XnaKeys.R);
        if (r && !_ssRWasDown && _session.World.AmMissionOwner)
        {
            Console.WriteLine("[monogame] side-select: mission owner -> RANDOMIZE_TEAMS");
            _session.SendRandomizeTeams();
        }
        _ssRWasDown = r;
    }

    /// <summary>Render the post-connect side-select overlay from the current
    /// SIDE_ROSTER snapshot and the highlighted roster index.</summary>
    private void DrawSideSelect()
    {
        var rows = Rendering.SideSelectLayout.Build(
            _session.World.SideRoster, _sideSelectIndex, _session.World.AmMissionOwner);
        _sideSelectRenderer.Draw(GraphicsDevice.Viewport, rows);
    }

    /// <summary>Drive the READY-CHECK screen (shown once seated, before the
    /// mission starts). The server holds the mission at NotStarted until every
    /// occupied side is aggregate-ready (P180 TryAutoStart); here the player
    /// toggles their own ready flag (Y), which sends a real PLAYER_READY(45)
    /// — the exact thing the old auto-ready hack faked. The mission owner can
    /// force their whole side ready (O, FORCE_TEAM_READY(47)), gated the same
    /// way P281's randomize-teams trigger is. As soon as the start countdown
    /// begins (all sides ready → server SET_START_TIME broadcast), leave for
    /// Flying — the client's Mission.Stage is pinned to Started by the join
    /// burst, so the countdown-began receipt is the real "the game is
    /// launching" signal (P216).</summary>
    private void UpdateReadyCheck(KeyboardState ks)
    {
        var world = _session.World;

        // P306 — headless capture: auto-ready once seated, then force the
        // ReadyCheck→Flying handoff after a short grace even if the server
        // never re-broadcasts a start countdown to a join-after-start client.
        if (_captureSpec is not null)
        {
            if (!_capReadySent && world.MyShipId is { } cpid)
            {
                _capReadySent = true;
                _capReadyWallMs = Environment.TickCount64;
                _session.SendPlayerReady(true, cpid);
                Console.WriteLine($"[capture] auto-ready PLAYER_READY ship {cpid}");
            }
            if (world.CountdownStartLocalMs is not null)
            {
                Console.WriteLine("[capture] ready-check: countdown began -> flying");
                _state = ClientState.Flying;
                return;
            }
            if (_capReadySent && !_capFlyForced &&
                Environment.TickCount64 - _capReadyWallMs >= 2000)
            {
                _capFlyForced = true;
                _state = ClientState.Flying;
                Console.WriteLine("[capture] ready-check: forced -> flying (2s grace)");
            }
            return;
        }

        // P309 — the server now reports the mission's REAL stage, including a
        // MISSION_STAGE(37) Started broadcast the moment the mission truly starts
        // (CompleteStartCountdown). A peer who joined during the pre-game hold or
        // countdown never received the countdown-began SET_START_TIME broadcast
        // (it went out to the peers present at that moment, before this peer
        // joined), so CountdownStartLocalMs stays null for them; the stage
        // reaching Started is the reliable, server-authoritative "the game is
        // live" signal that releases them into Flying. This is the client half of
        // the P309 stage fix: the local flight simulation is gated on MissionStage
        // reaching Started (the same stage the server's pre-game hold enforces),
        // closing the mid-countdown / late-join gap the old SET_START_TIME-only
        // handoff left open. Normal pre-game peers still exit on the countdown
        // below (stage is Starting, not yet Started, while the countdown runs).
        if (world.Mission?.Stage == (int)MissionStage.Started)
        {
            Console.WriteLine("[monogame] ready-check: mission stage -> Started, entering flying");
            _state = ClientState.Flying;
            return;
        }

        if (world.CountdownStartLocalMs is not null)
        {
            Console.WriteLine("[monogame] ready-check: mission start countdown began -> flying");
            _state = ClientState.Flying;
            return;
        }

        if (world.MyShipId is not { } pid) return;   // not seated yet (shouldn't happen)

        bool y = ks.IsKeyDown(XnaKeys.Y);
        if (y && !_rcYWasDown)
        {
            _ownReady = !_ownReady;
            _session.SendPlayerReady(_ownReady, pid);
            Console.WriteLine($"[monogame] ready-check: toggled own ready -> {_ownReady} (PLAYER_READY ship {pid})");
        }
        _rcYWasDown = y;

        bool o = ks.IsKeyDown(XnaKeys.O);
        if (o && !_rcOWasDown && world.AmMissionOwner && world.MySideId is { } sideId)
        {
            // Mission-owner override, gated exactly like P281's randomize-teams
            // trigger (the same AmMissionOwner / IsMissionOwnerPlayer gate).
            _session.SendForceTeamReady(true, sideId);
            Console.WriteLine($"[monogame] ready-check: mission owner FORCE_TEAM_READY side {sideId}");
        }
        _rcOWasDown = o;
    }

    /// <summary>Render the READY-CHECK overlay from the live world ready state
    /// (own toggle + same-side roster + per-side TEAM_READY aggregate).</summary>
    private void DrawReadyCheck()
    {
        var world = _session.World;
        var (sideName, players, agg) = Rendering.ReadyCheckRoster.Gather(world, _session.Gate, _ownReady);
        bool forced = world.MySideId is { } sideId
            && world.ForceReadyBySideId.TryGetValue(sideId, out var f) && f;
        var rows = Rendering.ReadyCheckLayout.Build(
            sideName, _ownReady, players, agg, forced, world.AmMissionOwner);
        _readyCheckRenderer.Draw(GraphicsDevice.Viewport, rows);
    }

    /// <summary>Whether the B-key scoreboard toggle may fire this frame. Gated
    /// off composing surfaces (chat, quick-chat menu) and — M-2 — the docked
    /// hangar/teleport screen, where B means "back to hangar" instead.</summary>
    internal static bool ScoreboardToggleAllowed(bool chatActive, bool qcMenuOpen, bool dockedActive)
        => !chatActive && !qcMenuOpen && !dockedActive;
}
