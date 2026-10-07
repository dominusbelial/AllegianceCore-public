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
    ///   U           UNDOCK without buying — send the plain FM_C_DOCKED(NA).
    /// P519 — there is NO auto-launch fallback: the player launches only on
    /// their own action (L / pad Y / touch Launch), matching native docked
    /// players who stay docked until they launch.
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
            // P499 — read the pad once per docked frame so the shop/teleport/
            // invest panes can accept DPAD+face-button navigation alongside the
            // keyboard (all-false on desktop without a connected pad).
            var nav = GamePadNavNow();
            // Enter the hangar once per dock: build a fresh shop model from the
            // current ship + catalogue (never mutate the current ship itself).
            if (!_dockedActive)
            {
                _dockedActive = true;
                // P571 — entering the docked hangar tears down the P540 eject/pod
                // autopilot (and any autopilot goto/target), so a freshly launched
                // hull never resumes steering to the garrison after death. Native
                // enters the docked ship state (DockWithStationEvent →
                // c_ssDocked, trekigc.cpp:4146) and leaves the flight interface —
                // incl. the eject goto + autopilot (appmsg.cpp:235) — behind; the
                // pod autopilot lives only while actually in the pod (P540), i.e.
                // only while NOT docked.
                if (!EjectAutopilotStaysEngaged(myDocked))
                    DisengageAutopilot("docked");
                _launchSent = false;
                // P284 — gate the offered hulls/parts by the docked station's
                // tech and the side's researched developments: an item shows iff
                // TtbmRequired ⊆ (stationLocal | mySideDev), the exact P283
                // containment. The docked station resolves through the world's
                // station table → its StationTypeIGC (TtbmLocal); the side's
                // live DevelopmentTechs comes from the wire (CREATE_BUCKETS at
                // join, SIDE_TECH_CHANGE on research). If either can't be
                // resolved, FilteredFor degrades to the full catalogue.
                var offerCat = _shopCatalog ?? Rendering.ShopCatalog.Empty;
                if (_shopCatalog is { StationTypes: not null } scat
                    && world.Stations.TryGetValue(mySe.StationId, out var dockStation)
                    && scat.StationTypes.TryGetValue(dockStation.StationTypeId, out var stype))
                {
                    // P468 fail-closed: route BOTH terms through FilteredFor even
                    // when the side's dev mask is unknown — FilteredFor then
                    // offers ONLY no-tech items, never the full catalogue. (The
                    // old path left offerCat as the unfiltered _shopCatalog
                    // whenever the mask was unknown, silently offering every
                    // item the player hadn't researched or couldn't afford.)
                    offerCat = _shopCatalog.FilteredFor(stype.TtbmLocal, world.DevelopmentTechsFor(world.MySideId ?? -1));
                }
                _shopModel = new Rendering.ShopModel(
                    offerCat,
                    mySe.HullId,
                    mySe.Loadout ?? Array.Empty<ExpandedPartData>(),
                    world.MyMoney);
                // P273 — build the teleport destination list from the live world.
                _teleportModel = BuildTeleportModel(world, mySe);
                _teleportActive = false;
                // P487 — synthesize the side's bucket definitions once per mission
                // (first dock after the side's techs are known) so their ids stay
                // aligned with the server's fixed bucket list. Lazily re-attempted
                // while null (e.g. techs not yet observed), never rebuilt later.
                _investDefs ??= BuildInvestDefs();
                Console.WriteLine($"[monogame] docked: hangar/loadout screen ({offerCat.Hulls.Count} hulls, {offerCat.Parts.Count} parts, {offerCat.Launchers?.Count ?? 0} launchers offered)");
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

            // P487 — I toggles the investment pane (native's Invest button /
            // ofInvestment overlay). Opening it banishes the teleport pane the way
            // native's OnButtonInvest turns off the other banishable panes
            // (hangarscreen.cpp:201-206).
            bool iKey = ks.IsKeyDown(XnaKeys.I);
            if (!composing && !_launchSent && iKey && !_invIWasDown)
            {
                _investActive = !_investActive;
                if (_investActive) _teleportActive = false;
                Console.WriteLine($"[monogame] investment pane {( _investActive ? "OPEN" : "CLOSED" )}");
            }
            _invIWasDown = iKey;

            // P501 — touch: docked verb taps (launch/undock, pane toggles, and
            // row-tap to buy/select/invest on the active sub-view).
            HandleDockedTouch(mySe);

            if (!composing && !_launchSent && _investActive && _investDefs is not null)
            {
                // P487 — rebuild the pane model each frame so funding, the wallet
                // and drone counts stay live; cursor/toggles are persisted across
                // the model instances (fields), so navigation state survives.
                var inv = BuildInvestmentModel();
                _investModelLive = inv;   // P501 — keep the live model for touch taps
                // P499 — DPAD Up/Down = cursor, A = invest, B = back to hangar.
                bool up = ks.IsKeyDown(XnaKeys.Up) || nav.Up;
                bool down = ks.IsKeyDown(XnaKeys.Down) || nav.Down;
                bool space = ks.IsKeyDown(XnaKeys.Space) || nav.Accept;
                bool rKey = ks.IsKeyDown(XnaKeys.R);
                bool sKey = ks.IsKeyDown(XnaKeys.S);
                bool hKey = ks.IsKeyDown(XnaKeys.H);
                bool bKey = ks.IsKeyDown(XnaKeys.B) || nav.Back;

                if (up && !_invUpWasDown) { inv.MoveCursor(-1); Console.WriteLine($"[monogame] invest cursor -> {inv.Cursor}"); }
                if (down && !_invDownWasDown) { inv.MoveCursor(+1); Console.WriteLine($"[monogame] invest cursor -> {inv.Cursor}"); }
                if (space && !_invSpaceWasDown)
                {
                    // native OnInvest: put in min(price - invested, wallet)
                    // (loadout.cpp:807-826). The server debits the wallet via
                    // MONEY_CHANGE — we do not locally subtract (P442, P487).
                    if (inv.TryInvest(out var bid, out var amt))
                    {
                        _session.SendBucketDonate(bid, amt);
                        Console.WriteLine($"[monogame] INVEST bucket {bid} -> BUCKET_DONATE ${amt} (server MONEY_CHANGE is the debit)");
                    }
                    else
                    {
                        Console.WriteLine("[monogame] INVEST blocked (not investable)");
                    }
                }
                if (rKey && !_invRWasDown)
                {
                    // native OnPartial (right-click, loadout.cpp:883-914): 10% of
                    // the total price, never enough to complete the bucket.
                    if (inv.TryPartialInvest(out var pbid, out var pamt))
                    {
                        _session.SendBucketDonate(pbid, pamt);
                        Console.WriteLine($"[monogame] PARTIAL INVEST bucket {pbid} -> BUCKET_DONATE ${pamt}");
                    }
                }
                if (sKey && !_invSWasDown) { inv.ToggleShowAll(); Console.WriteLine($"[monogame] invest show-all -> {inv.ShowAll}"); }
                if (hKey && !_invHWasDown) { inv.ToggleHideCompleted(); Console.WriteLine($"[monogame] invest hide-completed -> {inv.HideCompleted}"); }
                if (bKey && !_invBWasDown)
                {
                    _investActive = false;
                    Console.WriteLine("[monogame] investment -> back to hangar");
                }

                _invUpWasDown = up; _invDownWasDown = down; _invSpaceWasDown = space;
                _invRWasDown = rKey; _invSWasDown = sKey; _invHWasDown = hKey; _invBWasDown = bKey;

                // Persist cursor/toggles (may have been clamped by a filter change).
                _investCursor = inv.Cursor;
                _investShowAll = inv.ShowAll;
                _investHideCompleted = inv.HideCompleted;
            }
            else if (!composing && !_launchSent && _teleportActive && _teleportModel is { } tel)
            {
                // P273 — the teleport pane's own keys (sub-view of the docked screen).
                // P499 — DPAD Up/Down = cursor, A = select, B = back to hangar.
                bool up = ks.IsKeyDown(XnaKeys.Up) || nav.Up;
                bool down = ks.IsKeyDown(XnaKeys.Down) || nav.Down;
                bool space = ks.IsKeyDown(XnaKeys.Space) || nav.Accept;
                bool bKey = ks.IsKeyDown(XnaKeys.B) || nav.Back;

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
                // P572 — --auto-launch + the dock-preview sweep, before the keys
                // so an automated run never contends with (or needs) a keypress.
                ApplyAutoProofDocked(shop);

                // P499/P519 — merge the pad's DPAD/face/shoulder buttons into the
                // shop keys: DPAD Up/Down = cursor, A = select, Y = launch, B =
                // back, shoulders = switch HULLS/PARTS tab. P519 added the Tab,
                // Q/E (hull cycle), Enter (select) and B (back) keyboard verbs.
                bool up = ks.IsKeyDown(XnaKeys.Up) || nav.Up;
                bool down = ks.IsKeyDown(XnaKeys.Down) || nav.Down;
                bool space = ks.IsKeyDown(XnaKeys.Space) || nav.Accept;
                bool enter = ks.IsKeyDown(XnaKeys.Enter) || nav.Accept;
                bool tabKey = ks.IsKeyDown(XnaKeys.Tab);
                bool lKey = ks.IsKeyDown(XnaKeys.L) || nav.Extra;
                bool uKey = ks.IsKeyDown(XnaKeys.U);
                bool bKey = ks.IsKeyDown(XnaKeys.B) || nav.Back;
                bool qKey = ks.IsKeyDown(XnaKeys.Q) || nav.Left;
                bool eKey = ks.IsKeyDown(XnaKeys.E) || nav.Right;

                if (up && !_dockUpWasDown) { shop.MoveCursor(-1); Console.WriteLine($"[monogame] shop cursor -> {shop.Cursor}"); }
                if (down && !_dockDownWasDown) { shop.MoveCursor(+1); Console.WriteLine($"[monogame] shop cursor -> {shop.Cursor}"); }

                // P519 — switch the HULLS/PARTS tab (Tab key or pad shoulders).
                if ((tabKey || nav.LeftShoulder || nav.RightShoulder) && !_dockTabWasDown)
                {
                    shop.ToggleTab();
                    Console.WriteLine($"[monogame] docked tab -> {shop.Tab}");
                }
                // P519 — cycle the hull next/previous (Q/E or pad Left/Right),
                // like native OnButtonNext/OnButtonPrevious.
                if (qKey && !_dockQWasDown) { shop.CycleHull(false); Console.WriteLine($"[monogame] hull cycle prev -> {shop.EffectiveHullId}"); }
                if (eKey && !_dockEWasDown) { shop.CycleHull(true); Console.WriteLine($"[monogame] hull cycle next -> {shop.EffectiveHullId}"); }

                if ((space || enter) && !_dockSpaceWasDown)
                {
                    shop.Activate();
                    Console.WriteLine($"[monogame] shop select: page={shop.Page} hull={shop.EffectiveHullId} parts={shop.ChosenParts.Count}");
                }
                if (bKey && !_dockBWasDown)
                {
                    shop.Back();
                    Console.WriteLine($"[monogame] shop back -> {shop.Page}");
                }

                if (lKey && !_dockLWasDown)
                {
                    // P468 — block a launch the player can't afford CLIENT-side,
                    // with a visible reason (the layout renders shop.BlockReason),
                    // instead of sending a BUY_LOADOUT the server (P441) will
                    // refuse because the cost exceeds the wallet.
                    if (shop.TryLaunch())
                    {
                        _session.SendBuyLoadout(shop.EffectiveHullId, fLaunch: true, parts: shop.ChosenParts);
                        _launchSent = true;
                        // P445 — undock into flight resets the persistent
                        // throttle to full (+1), native's on-line default.
                        _throttle = FlightInput.DefaultThrottle;
                        Console.WriteLine($"[monogame] LAUNCH -> BUY_LOADOUT(fLaunch) hull {shop.EffectiveHullId} + {shop.ChosenParts.Count} part(s)");
                    }
                    else
                    {
                        Console.WriteLine($"[monogame] LAUNCH BLOCKED (unaffordable): {shop.BlockReason}");
                    }
                }
                else if (uKey && !_dockUWasDown)
                {
                    _session.SendDocked();
                    _launchSent = true;
                    // P445 — undock (no buy) also returns to full-throttle flight.
                    _throttle = FlightInput.DefaultThrottle;
                    Console.WriteLine("[monogame] UNDOCK (no buy) -> FM_C_DOCKED(NA)");
                }

                _dockUpWasDown = up; _dockDownWasDown = down;
                _dockSpaceWasDown = space || enter; _dockLWasDown = lKey; _dockUWasDown = uKey;
                _dockTabWasDown = tabKey || nav.LeftShoulder || nav.RightShoulder;
                _dockQWasDown = qKey; _dockEWasDown = eKey; _dockBWasDown = bKey; _dockEnterWasDown = enter;
            }
            else if (_launchSent)
            {
                // Launch requested; release key latches so a held key can't
                // re-fire if we somehow re-enter before undock completes.
                _dockUpWasDown = _dockDownWasDown = _dockSpaceWasDown = _dockLWasDown = _dockUWasDown = false;
                _dockTabWasDown = _dockQWasDown = _dockEWasDown = _dockBWasDown = _dockEnterWasDown = false;
            }

            return true; // docked → consume the frame, no flight input
        }

        if (_dockedActive) // the server undocked us → leave the hangar
        {
            _dockedActive = false;
            _shopModel = null;
            _teleportModel = null;
            _teleportActive = false;
            _investActive = false;
            Console.WriteLine("[monogame] undocked: leaving hangar");
        }
        return false;
    }

    /// <summary>
    /// P501 — docked-screen touch taps: launch/undock (shop), pane toggles
    /// (teleport/invest), and row-tap to buy/select/invest on the active sub-view.
    /// Composing (chat) and a pending launch yield the frame to the keys. Called
    /// every docked frame; zones from <see cref="Rendering.DockedTouchZones"/>.
    /// </summary>
    private void HandleDockedTouch(ShipEntity mySe)
    {
        if (!TouchAvailable()) return;
        var vp = GraphicsDevice.Viewport;
        float w = vp.Width, h = vp.Height;
        var taps = TouchReleasedPointsNow();
        if (taps.Count == 0) return;

        bool active = !_launchSent && !_chatActive && !_qcMenuOpen;

        foreach (var (tx, ty) in taps)
        {
            // Pane toggles are always offered while not launch-pending.
            if (active)
            {
                var telRect = Rendering.DockedTouchZones.TeleportRect(w, h);
                if (telRect.Contains(tx, ty))
                {
                    _teleportActive = !_teleportActive;
                    if (_teleportActive && _shopModel is not null)
                        _teleportModel = BuildTeleportModel(_session.World, mySe);
                    Console.WriteLine($"[monogame] docked touch: teleport pane {(_teleportActive ? "OPEN" : "CLOSED")}");
                    return;
                }
                var invRect = Rendering.DockedTouchZones.InvestRect(w, h);
                if (invRect.Contains(tx, ty))
                {
                    _investActive = !_investActive;
                    if (_investActive) _teleportActive = false;
                    Console.WriteLine($"[monogame] docked touch: investment pane {(_investActive ? "OPEN" : "CLOSED")}");
                    return;
                }
            }

            // Active sub-view row taps (move cursor onto the tapped row + act).
            if (active && _investActive && _investModelLive is { } inv)
            {
                int idx = Rendering.DockedTouchZones.RowIndex(w, h, inv.Items.Count, tx, ty);
                if (idx < 0) continue;
                inv.MoveCursor(idx - inv.Cursor);
                if (inv.TryInvest(out var bid, out var amt))
                {
                    _session.SendBucketDonate(bid, amt);
                    Console.WriteLine($"[monogame] docked touch: INVEST bucket {bid} -> BUCKET_DONATE ${amt}");
                }
                return;
            }
            if (active && _teleportActive && _teleportModel is { } tel)
            {
                int idx = Rendering.DockedTouchZones.RowIndex(w, h, tel.RowCount, tx, ty);
                if (idx < 0) continue;
                tel.MoveCursor(idx - tel.Cursor);
                ActivateTeleport(tel, mySe);
                return;
            }
            if (active && !_investActive && !_teleportActive && _shopModel is { } shop)
            {
                // P519 — tap the HULLS/PARTS tab headers to switch views.
                var hullsTabRect = Rendering.DockedTouchZones.HullsTabRect(w, h);
                var partsTabRect = Rendering.DockedTouchZones.PartsTabRect(w, h);
                if (hullsTabRect.Contains(tx, ty) && shop.Tab != ShopTab.Hulls)
                {
                    shop.SwitchTab(ShopTab.Hulls);
                    Console.WriteLine("[monogame] docked touch: HULLS tab");
                    return;
                }
                if (partsTabRect.Contains(tx, ty) && shop.Tab != ShopTab.Parts)
                {
                    shop.SwitchTab(ShopTab.Parts);
                    Console.WriteLine("[monogame] docked touch: PARTS tab");
                    return;
                }
                var launchRect = Rendering.DockedTouchZones.LaunchRect(w, h);
                if (launchRect.Contains(tx, ty))
                {
                    if (shop.TryLaunch())
                    {
                        _session.SendBuyLoadout(shop.EffectiveHullId, fLaunch: true, parts: shop.ChosenParts);
                        _launchSent = true;
                        _throttle = FlightInput.DefaultThrottle;
                        Console.WriteLine($"[monogame] docked touch: LAUNCH -> BUY_LOADOUT(fLaunch) hull {shop.EffectiveHullId} + {shop.ChosenParts.Count} part(s)");
                    }
                    else
                    {
                        Console.WriteLine($"[monogame] docked touch: LAUNCH BLOCKED (unaffordable): {shop.BlockReason}");
                    }
                    return;
                }
                var undockRect = Rendering.DockedTouchZones.UndockRect(w, h);
                if (undockRect.Contains(tx, ty))
                {
                    _session.SendDocked();
                    _launchSent = true;
                    _throttle = FlightInput.DefaultThrottle;
                    Console.WriteLine("[monogame] docked touch: UNDOCK (no buy) -> FM_C_DOCKED(NA)");
                    return;
                }
                int sIdx = Rendering.DockedTouchZones.RowIndex(w, h, shop.RowCount, tx, ty);
                if (sIdx >= 0)
                {
                    shop.MoveCursor(sIdx - shop.Cursor);
                    shop.Activate();
                    Console.WriteLine($"[monogame] docked touch: buy/select row {shop.Cursor} (hull={shop.EffectiveHullId} parts={shop.ChosenParts.Count})");
                    return;
                }
            }
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
    /// P487 — synthesize the local side's bucket definitions via
    /// <see cref="Allegiance.Core.Model.BucketCatalog.Describe"/> — the SAME id
    /// numbering the server baked into its list at <c>Mission.Assemble</c> (same
    /// core + same initial techs) — fed the side's development-tech mask and GA.
    /// Returns null when the core is unavailable or the side's techs aren't
    /// observed yet, so the caller (<c>_investDefs ??= BuildInvestDefs()</c>)
    /// retries on a later dock instead of caching a wrong list.
    /// </summary>
    private IReadOnlyList<Rendering.InvestmentBucket>? BuildInvestDefs()
    {
        var core = _shopCatalog?.Core;
        if (core is null) return null;
        var world = _session.World;
        short mySide = world.MySideId ?? -1;
        if (world.DevelopmentTechsFor(mySide) is not { } techs) return null;   // techs unknown — retry later
        var ga = world.GlobalAttributesFor(mySide);
        var views = Allegiance.Core.Model.BucketCatalog.Describe(core, techs, ga);
        var list = new List<Rendering.InvestmentBucket>(views.Count);
        foreach (var v in views)
            list.Add(new Rendering.InvestmentBucket(v.Id, v.ItemType, v.Name, v.Price,
                v.TimeToBuildSec, v.HullIdDrone, v.DroneTypeId, v.DevelopmentId, v.StationTypeId));
        Console.WriteLine($"[monogame] invest: side {mySide} -> {list.Count} buckets " +
            $"(ids {string.Join(",", list.Take(8).Select(b => b.Id))}{(list.Count > 8 ? "..." : "")})");
        return list;
    }

    /// <summary>
    /// P487/P495 — build the investment pane's pure model for THIS frame: the cached
    /// bucket defs, the side's live <c>BUCKET_STATUS</c> funding, the wallet, and
    /// live drone counts (this side's world ships by hull id, for the drone cap).
    /// The station-type build-spot / no-builder sub-gate of native <c>CanInvest</c>
    /// (loadout.cpp:1124-1131) is fed REAL values from the client's world by
    /// <see cref="Rendering.InvestmentBuildGates"/> (P495): build-spot found over
    /// the known asteroids' <c>AabmBuild</c> capability, and no existing builder
    /// (constructor hull on the side + active construction on a compatible rock).
    /// Without the static core the gate stays open (the pre-P495 default).
    /// </summary>
    private Rendering.InvestmentPaneModel BuildInvestmentModel()
    {
        var world = _session.World;
        short mySide = world.MySideId ?? -1;
        IReadOnlyDictionary<short, ClientWorld.BucketFunding>? funding = null;
        var droneCounts = new Dictionary<short, int>();
        // P495 — snapshots the build gates read (taken under the lock so the
        // stationCanBuild lambda below — which the pane calls later, off this
        // lock — never touches live mutable world state).
        DataAsteroidIGC[] asteroids = Array.Empty<DataAsteroidIGC>();
        var sideHulls = new List<short>();
        var constructedIds = new List<short>();
        lock (_session.Gate)
        {
            if (mySide >= 0)
            {
                funding = world.BucketFundingFor(mySide);
                foreach (var s in world.Ships.Values)
                    if (s.SideId == mySide && s.HullId > 0)
                        droneCounts[s.HullId] = droneCounts.TryGetValue(s.HullId, out var c) ? c + 1 : 1;
                sideHulls.AddRange(world.Ships.Values
                    .Where(s => s.SideId == mySide && s.HullId > 0)
                    .Select(s => s.HullId));
            }
            asteroids = world.Asteroids.Values.ToArray();
            constructedIds.AddRange(world.BuildingEffects.Keys);
        }
        bool inProgress = world.Mission?.Stage == (int)MissionStage.Started;
        bool hasSide = mySide >= 0;
        var catalog = _shopCatalog;
        Func<short, bool> stationCanBuild = stId =>
        {
            // P495 — the REAL native gates (loadout.cpp:1124-1131) from the
            // client's world: build-spot found + no existing builder.
            if (catalog?.StationTypes is not { } sts || !sts.TryGetValue(stId, out var st))
                return true;
            return Rendering.InvestmentBuildGates.CanBuildStation(
                asteroids, constructedIds, sideHulls, st, catalog.DroneTypes);
        };
        return new Rendering.InvestmentPaneModel(
            _investDefs ?? Array.Empty<Rendering.InvestmentBucket>(),
            funding, inProgress, hasSide, world.MyMoney,
            droneCounts, MaxDronesPerTeam,
            stationCanBuild,
            showAll: _investShowAll, hideCompleted: _investHideCompleted, cursor: _investCursor);
    }

    /// <summary>
    /// P495 — build (or reuse) this frame's investment model and resolve the
    /// asteroid-build-spot mask of the hovered station-type bucket. Called once
    /// per frame from the radar pre-pass so <c>DrawRadar</c> can light up the
    /// matching rocks (native <c>SetInvestAsteroid</c>, loadout.cpp:1396-1409);
    /// the pane draw then reuses <see cref="_investModel"/> so the highlight and
    /// the list always agree.
    /// </summary>
    private void RefreshInvestState()
    {
        _investModel = null;
        _investAsteroidAabm = null;
        if (!(_dockedActive && _investActive && _investDefs is not null)) return;
        var m = BuildInvestmentModel();
        _investModel = m;
        if (m.Current is { } cur && cur.ItemType == Allegiance.Core.Model.BucketItemType.BitStationType
            && _shopCatalog?.StationTypes is { } sts && sts.TryGetValue(cur.StationTypeId, out var st))
            _investAsteroidAabm = st.AabmBuild;
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

    /// <summary>
    /// P403 — issue a "goto" order for the player's OWN ship toward a sector selected
    /// on the expanded starmap. This port has no wingman/squadron-selection concept
    /// (documented in P403_BUOY_GOTO_COMMAND.md), so the commanded ship is the local
    /// player's own ship. Faithful to native's map "goto": the client builds the
    /// equivalent of a <c>DataBuoyIGC</c> (<c>position = (0,0,0)</c>,
    /// <c>clusterID</c> = the clicked/selected sector, <c>type = c_buoyCluster</c> —
    /// <c>WinTrek/sectormap.cpp:1550-1565</c>; this port's keyboard-only ",/."
    /// selection stands in for the mouse click, see P249/P272) and sends it as a
    /// <c>FM_CS_CHATBUOY(108)</c> (the message <c>BaseClient::SendChat</c> uses when a
    /// chat targets a buoy, <c>clintlib.cpp:2460-2469</c>). The server — as the ship's
    /// owner/simulation authority — translates the received order into a real autopilot
    /// move (warp-pathing, cleared on arrival); the client just renders the resulting
    /// ship updates. Addressed to the sender's own ship as an individual command.
    /// </summary>
    private void IssueStarmapGoto(short clusterId)
    {
        short ownShip = (short)(_session.World.MyShipId ?? -1);
        var db = Rendering.StarmapClick.BuildClusterGotoBuoy(clusterId);
        _session.SendChatBuoy("goto", ChatTarget.Individual, ownShip, db);
        Console.WriteLine($"[monogame] goto order: own ship {ownShip} -> sector {clusterId} (CHATBUOY c_buoyCluster)");

        // P541 — the goto must actually FLY the player. The own ship is flown by
        // the CLIENT (P508 local sim), so a server-side GotoPlan would fight the
        // local sim (measured: a native-pose client pins the server's gotoing ship
        // at spawn, never reaching the target). Faithful to native — where the
        // client receives its own c_cidGoto and its own autopilot flies the ship
        // (clintlib command receipt, clintlib.cpp:3711) — this engages the CLIENT
        // autopilot (P524) toward the target cluster, with warp-path routing to
        // its sector via ClusterGraph.FindShortestPath + StarmapGotoRouter.
        // EngageAutopilotToward plays the engage sound and refreshes the HUD title.
        string name = "";
        lock (_session.Gate)
            if (_session.World.Clusters.TryGetValue(clusterId, out var dc)) name = dc.Name;
        EngageAutopilotToward(new AutopilotTarget(TargetKind.Cluster, clusterId, name), "starmap goto:");
    }

    /// <summary>
    /// P481 — the game-over screen's Back action, mirroring native
    /// <c>GameOverScreen::OnButtonBack</c> (gameoverscreen.cpp:609-625).
    /// Branches on the real mission stage (resolved headlessly by
    /// <see cref="State.GameOverBackPlanner"/>):
    /// <list type="bullet">
    /// <item>stage Over → <b>leave the mission</b>: LOG OFF (the port's only
    /// client-initiated leave) and return to the server browser. The outer
    /// mission-list lobby after leaving is out of scope (P481).</item>
    /// <item>otherwise → <b>team lobby</b>: clear <c>GameOverResult</c> and show
    /// the ready-check overlay (player re-seated on an active side after a P480
    /// rotation; ready cleared) or the side-select overlay (an eliminated P455b
    /// side, inactive sides excluded) so the player can request a still-active
    /// side.</item>
    /// </list>
    /// Reads world state under the session lock; the state-machine transition
    /// happens on the single rendering/input thread.</summary>
    private void HandleGameOverBack()
    {
        int? stage;
        bool ownSideActive;
        lock (_session.Gate)
        {
            var w = _session.World;
            stage = w.Mission?.Stage;
            ownSideActive = w.IsSideActive(w.MySideId ?? -1);
        }

        switch (State.GameOverBackPlanner.Plan(stage, ownSideActive))
        {
            case State.GameOverBackAction.LeaveMission:
                Console.WriteLine("[monogame] game-over Back: stage Over -> leave mission (LOG OFF)");
                _session.SendLogoff();
                _state = ClientState.Lobby;
                break;

            case State.GameOverBackAction.TeamLobbyReadyCheck:
                Console.WriteLine("[monogame] game-over Back: team lobby (ready-check for the next game)");
                _session.World.ClearGameOver();
                _ownReady = false;
                _state = ClientState.ReadyCheck;
                break;

            case State.GameOverBackAction.TeamLobbySideSelect:
                Console.WriteLine("[monogame] game-over Back: team lobby (side-select - eliminated side)");
                _session.World.ClearGameOver();
                _sideSelectFromGameOver = true;
                _sideSelectIndex = 0;
                _state = ClientState.SideSelect;
                break;
        }
    }

    /// <summary>Drive the post-connect side-select screen. We sit here (no ship)
    /// until the server seats us after we send FM_C_POSITIONREQ(39); the moment
    /// SET_CLUSTER assigns us a ship (<see cref="ClientWorld.MyShipId"/> is set),
    /// transition to the pre-flight ReadyCheck screen (P282) — NOT straight to
    /// Flying: the mission only starts once every occupied side is
    /// aggregate-ready, so the player must toggle ready first.
    /// <para>P481 — when entered from the game-over Back on an <b>eliminated</b>
    /// side (<see cref="_sideSelectFromGameOver"/>), the player already owns a
    /// (dead) ship, so the exit condition is not "MyShipId set" but "seated on
    /// an ACTIVE side". The picker excludes inactive sides
    /// (<see cref="Rendering.SideSelectLayout.SelectableSideIds"/>) so an
    /// eliminated side can't be re-picked.</para></summary>
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

        var world = _session.World;

        // P572 — --auto-side: request the side as soon as the roster is known
        // (a one-shot; falls back to the server's 30 s auto-assign if unknown).
        ApplyAutoProofSideSelect();

        // P481 — elimination-mode exit: entered from the game-over Back on an
        // eliminated side; once the player is seated on an ACTIVE side (the
        // server's TryJoinSide re-seated them after their POSITIONREQ), leave
        // the picker for ReadyCheck (which immediately flies a Started mission).
        if (_sideSelectFromGameOver)
        {
            if (world.MySideId is { } sid && world.IsSideActive(sid))
            {
                _sideSelectFromGameOver = false;
                _state = ClientState.ReadyCheck;
                return;
            }
        }

        // Picker over the ACTIVE (selectable) sides only (P481): an eliminated /
        // inactive side is excluded so it can't be re-picked.
        var selectable = Rendering.SideSelectLayout.SelectableSideIds(world.SideRoster, world.IsSideActive);
        int count = selectable.Length;

        // P501 — gamepad DPAD / A act as Up / Down / Confirm alongside the keys
        // (all-false on desktop without a connected pad).
        var nav = GamePadNavNow();

        bool up = ks.IsKeyDown(XnaKeys.Up) || nav.Up;
        if (up && !_ssUpWasDown && count > 0)
            _sideSelectIndex = (_sideSelectIndex - 1 + count) % count;
        _ssUpWasDown = up;

        bool down = ks.IsKeyDown(XnaKeys.Down) || nav.Down;
        if (down && !_ssDownWasDown && count > 0)
            _sideSelectIndex = (_sideSelectIndex + 1) % count;
        _ssDownWasDown = down;

        bool enter = ks.IsKeyDown(XnaKeys.Enter) || nav.Accept;
        bool space = ks.IsKeyDown(XnaKeys.Space) || nav.Accept;
        if (count > 0 && (enter && !_ssEnterWasDown || space && !_ssSpaceWasDown))
        {
            int sel = _sideSelectIndex % count;
            short sideId = selectable[sel];
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

        // P501 — touch: a tap on a side row selects AND confirms it (the same
        // SendPositionReq the Enter/A key path sends), so a phone can pick a side
        // without a keyboard or pad.
        if (TouchAvailable() && count > 0)
        {
            var vp = GraphicsDevice.Viewport;
            foreach (var (tx, ty) in TouchReleasedPointsNow())
            {
                int idx = Rendering.PreFlightTaps.SideBand(vp.Width, vp.Height, count, tx, ty);
                if (idx < 0) continue;
                _sideSelectIndex = idx;
                short sideId = selectable[idx % count];
                Console.WriteLine($"[monogame] side-select: tapped side {sideId}");
                _session.SendPositionReq(sideId);
                return;
            }
        }
    }

    /// <summary>Render the post-connect side-select overlay from the current
    /// SIDE_ROSTER snapshot and the highlighted roster index.</summary>
    private void DrawSideSelect()
    {
        var rows = Rendering.SideSelectLayout.Build(
            _session.World.SideRoster, _sideSelectIndex, _session.World.AmMissionOwner,
            _session.World.IsSideActive);
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
        // handoff left open.
        //
        // P522 — the ONLY release is STAGE_STARTED. Previously a normal pre-game
        // peer (present when the countdown began) exited on countdown-began
        // (STAGE_STARTING) and started flying while the server's P180 gate was
        // still dropping every SHIP_UPDATE as "pregame" (mission not Started), so
        // the first accepted update after the true start was ~1.26 km off, got
        // rejected, and SHIP_RESET snapped the ship back to the start point —
        // the repeated first-dock-teleport cycle. Native holds the player through
        // the countdown (STAGE_STARTING shows the countdown, ship held) and only
        // enters the game at STAGE_STARTED (appmsg.cpp:2330-2346 → OnEnterMission
        // → screen(ScreenIDCombat)). The countdown is surfaced on the ReadyCheck
        // screen while held (DrawReadyCheck), and the hold releases here — the
        // instant the mission is Started — so the client's first SHIP_UPDATE is
        // always accepted (zero pregame drops, no first-launch SHIP_RESET).
        if (world.Mission?.Stage == (int)MissionStage.Started)
        {
            Console.WriteLine("[monogame] ready-check: mission stage -> Started, entering flying");
            _state = ClientState.Flying;
            return;
        }

        if (world.MyShipId is not { } pid) return;   // not seated yet (shouldn't happen)

        // P572 — --auto-ready: send PLAYER_READY(true) once seated (one-shot).
        ApplyAutoProofReadyCheck();

        // P501 — gamepad: A toggles ready (confirm), X is the owner's force-ready
        // (all-false on desktop without a connected pad).
        var nav = GamePadNavNow();

        bool y = ks.IsKeyDown(XnaKeys.Y) || nav.Accept;
        if (y && !_rcYWasDown)
        {
            _ownReady = !_ownReady;
            _session.SendPlayerReady(_ownReady, pid);
            Console.WriteLine($"[monogame] ready-check: toggled own ready -> {_ownReady} (PLAYER_READY ship {pid})");
        }
        _rcYWasDown = y;

        bool o = ks.IsKeyDown(XnaKeys.O) || nav.X;
        if (o && !_rcOWasDown && world.AmMissionOwner && world.MySideId is { } sideId)
        {
            // Mission-owner override, gated exactly like P281's randomize-teams
            // trigger (the same AmMissionOwner / IsMissionOwnerPlayer gate).
            _session.SendForceTeamReady(true, sideId);
            Console.WriteLine($"[monogame] ready-check: mission owner FORCE_TEAM_READY side {sideId}");
        }
        _rcOWasDown = o;

        // P501 — touch: tap the big READY/UNREADY button to toggle; the owner taps
        // FORCE READY to force their whole side ready.
        if (TouchAvailable())
        {
            var vp = GraphicsDevice.Viewport;
            var toggle = Rendering.PreFlightTaps.ReadyToggleRect(vp.Width, vp.Height);
            var force = Rendering.PreFlightTaps.ForceReadyRect(vp.Width, vp.Height);
            foreach (var (tx, ty) in TouchReleasedPointsNow())
            {
                if (toggle.Contains(tx, ty))
                {
                    _ownReady = !_ownReady;
                    _session.SendPlayerReady(_ownReady, pid);
                    Console.WriteLine($"[monogame] ready-check: tapped toggle -> {_ownReady} (PLAYER_READY ship {pid})");
                    return;
                }
                if (world.AmMissionOwner && world.MySideId is { } tside && force.Contains(tx, ty))
                {
                    _session.SendForceTeamReady(true, tside);
                    Console.WriteLine($"[monogame] ready-check: tapped FORCE_TEAM_READY side {tside}");
                    return;
                }
            }
        }
    }

    /// <summary>Render the READY-CHECK overlay from the live world ready state
    /// (own toggle + same-side roster + per-side TEAM_READY aggregate).</summary>
    private void DrawReadyCheck()
    {
        var world = _session.World;
        var (sideName, players, agg) = Rendering.ReadyCheckRoster.Gather(world, _session.Gate, _ownReady);
        bool forced = world.MySideId is { } sideId
            && world.ForceReadyBySideId.TryGetValue(sideId, out var f) && f;

        // P522 — while the mission is STAGE_STARTING the local ship is held
        // (ReadyCheck stays up; the ReadyCheck→Flying handoff waits for
        // STAGE_STARTED), so surface the launch countdown loudly. Same
        // countdown formula as the flying-state briefing (IsBriefingVisible).
        int? launchCountdown = null;
        if (world.CountdownStartLocalMs is { } cd)
        {
            double elapsed = (Environment.TickCount64 - cd) / 1000.0;
            int remaining = (int)Math.Ceiling(Audio.CountdownSound.BriefingCountdownSeconds - elapsed);
            if (remaining > 0) launchCountdown = remaining;
        }

        var rows = Rendering.ReadyCheckLayout.Build(
            sideName, _ownReady, players, agg, forced, world.AmMissionOwner, launchCountdown);
        _readyCheckRenderer.Draw(GraphicsDevice.Viewport, rows);
    }

    /// <summary>Whether the B-key scoreboard toggle may fire this frame. Gated
    /// off composing surfaces (chat, quick-chat menu) and — M-2 — the docked
    /// hangar/teleport screen, where B means "back to hangar" instead.</summary>
    internal static bool ScoreboardToggleAllowed(bool chatActive, bool qcMenuOpen, bool dockedActive)
        => !chatActive && !qcMenuOpen && !dockedActive;
}
