using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Browser.Tests;

/// <summary>
/// P1070 — the browser hangar and loadout glue (<see cref="BrowserHangarModel"/>), the
/// browser copy of the desktop's <c>Game1.Hangar.cs</c> / <c>Game1.LoadoutV2.cs</c>
/// state machine: docking opens the hangar and the shop, the loadout opens and closes,
/// a hull and a part can be chosen, the buy/launch bytes are the desktop's, the server's
/// acknowledgement lands, a refused buy shows the desktop's message, the launch blockers
/// are the shared <see cref="HangarLaunch"/> ones, and a session reset clears everything.
/// </summary>
public sealed class P1070HangarTests
{
    private const short Scout = 7;          // $500
    private const short Fighter = 8;        // $900
    private const short TechHull = 9;       // requires a development the station lacks
    private const short Gun = 10;           // $100 weapon
    private const short OwnShip = 1;
    private const short Station = 42;

    // ── fixtures ───────────────────────────────────────────────────────────────

    /// <summary>A tech mask with bit 1 set (a development the docked station lacks).</summary>
    private static byte[] Tech()
    {
        var bits = new byte[TechTreeBitMask.ByteCount];
        bits[0] = 0x40;   // MSB-first bit 1 (mask.h:159-181)
        return bits;
    }

    private static DataHullTypeIGC Hull(short id, string name, int price, byte[]? required = null) => new()
    {
        HullId = id, SuccessorHullId = -1, Name = name, Price = price,
        // One fixed weapon hardpoint that accepts the test gun (ShopModel.PartFitsSlot).
        Hardpoints = [new DataHullTypeIGC.Hardpoint(0, 0, "", "", 0x00FF, true)],
        PmEquipment = [], PreferredPartsTypes = [],
        TtbmRequired = required ?? new byte[TechTreeBitMask.ByteCount],
        TtbmEffects = new byte[TechTreeBitMask.ByteCount],
        MaxWeapons = 1, MaxFixedWeapons = 1, Length = 20, Mass = 1,
        MaxTurnRates = [1, 1, 1], TurnTorques = [1, 1, 1],
    };

    private static DataPartTypeIGC Part(short id, string name, int price) =>
        new(price, 0, "", "", name, "", 0, new byte[TechTreeBitMask.ByteCount],
            new byte[TechTreeBitMask.ByteCount], 0, 0, id, -1, 0x00FF, "",
            new DataPartTypeIGC.Weapon(0, 0, 0, 0, 0, 0, 0, 0, 0));

    /// <summary>The server's catalogue as the browser loads it, with one hull behind a
    /// development the docked station has not got (so the offer list is the server's,
    /// not a browser list).</summary>
    private static ShopCatalog Catalog() => new(
        [Hull(Scout, "Scout", 500), Hull(Fighter, "Fighter", 900),
         Hull(TechHull, "Cruiser", 1_200, required: Tech())],
        [Part(Gun, "Gun", 100)],
        StationTypes: new Dictionary<short, DataStationTypeIGC>
        {
            [1] = new() { StationTypeId = 1, Name = "Garrison",
                TtbmLocal = new byte[TechTreeBitMask.ByteCount],
                TtbmEffects = new byte[TechTreeBitMask.ByteCount] },
        });
    /// <summary>A session whose own ship is docked at <see cref="Station"/>, with the
    /// server's wallet, catalogue and garrison — the state the browser is in when the
    /// hangar appears.</summary>
    private static (ConnectionSession Session, BrowserHangarModel Hangar) Docked(int money = 5_000, short hull = Scout)
    {
        var session = new ConnectionSession();
        var world = session.World;
        world.Apply(new SetCluster
        {
            SectorId = 1,
            Ship = ServerShipUpdate.FromValues(OwnShip, 0, default, 0,
                Allegiance.Core.Collision.Vector3.Zero, Orientation.Identity,
                Allegiance.Core.Collision.Vector3.Zero, [0, 0, 0], 1, 1, 1, 1, 1, 0, 1),
        });
        world.Apply(new ExportShip { ShipId = OwnShip, HullId = hull, SideId = 0, CharacterName = "P1070 pilot" });
        world.Apply(new PlayerInfo { ShipId = OwnShip, ISide = 0, CharacterName = "P1070 pilot" });
        world.Apply(new ShipStatusMessage
        {
            ShipId = OwnShip, SectorId = 1, StationId = Station, ParentId = -1, HullId = hull,
            State = ShipStatusMessage.StateDocked,
        });
        world.Apply(new ExportStation
        {
            Station = new DataStationIGC([0f, 0f, 0f], [0f, 1f, 0f], [0f, 0f, 1f], [0f, 0f, 0f, 1f],
                ClusterId: 1, SideId: 0, StationId: Station, StationTypeId: 1, BpHull: 240, BpShield: 240, Name: "Garrison"),
        });
        world.Apply(new SetMoney { Money = money, ShipId = OwnShip });
        var hangar = new BrowserHangarModel { Catalog = Catalog() };
        return (session, hangar);
    }

    private static ShipEntity Own(ConnectionSession session) => session.World.Ships[OwnShip];

    private static HangarLaunch.Readiness Ready() => HangarLaunch.Evaluate(true, false, OwnShip, null, true);

    private static bool Start(ConnectionSession session, BrowserHangarModel hangar,
        bool missionStarted = true, HangarLaunch.Readiness? readiness = null)
        => hangar.Update(session.World, Own(session), readiness ?? Ready(), missionStarted);

    /// <summary>A recording wire: every send the desktop's glue would make. It mirrors
    /// <see cref="ConnectionSession.SendBuyLoadout"/>'s own bookkeeping
    /// (<c>ConnectionSession.cs:818-822</c>: the hardware ledger records the hull the
    /// request was made from) and every other call the desktop's glue makes.</summary>
    private sealed class Wire(ClientWorld world)
    {
        public List<(short HullId, bool Launch, ExpandedPartData[] Parts)> Buys { get; } = [];
        public List<(int Amount, short HullId)> Money { get; } = [];
        public List<short> Boards { get; } = [];
        public int Docked, Quits;
        public short QuitShip;

        public BrowserHangarModel Attach(BrowserHangarModel hangar)
        {
            hangar.SendBuyLoadout = (hull, launch, parts) =>
            {
                if (world.MyShipId is { } me && world.Ships.TryGetValue(me, out var own))
                    world.OwnHardware.RequestSent(own.HullId);
                Buys.Add((hull, launch, parts.ToArray()));
            };
            hangar.SendMoneyRequest = (amount, hull) => Money.Add((amount, hull));
            hangar.SendBoardShip = parent => Boards.Add(parent);
            hangar.SendDocked = () => Docked++;
            hangar.SendQuitMission = (ship, reason) => { Quits++; QuitShip = ship; };
            return hangar;
        }
    }

    // ── item 1: the hangar screen ──────────────────────────────────────────────

    [Fact]
    public void Docking_opens_the_hangar_with_the_servers_offer_list()
    {
        var (session, hangar) = Docked();
        Assert.True(Start(session, hangar));

        Assert.True(hangar.Active);
        Assert.Equal(BrowserHangarScreen.Hangar, hangar.Screen);
        Assert.Equal(Scout, hangar.Shop!.CurrentHullId);
        // The offer list is the server's: the hull behind an unreached development is
        // absent (ShopCatalog.FilteredFor, Game1.Panes.cs:150-176), and the price list
        // carries only what the catalogue holds.
        Assert.DoesNotContain(hangar.Shop.BuyableHulls, h => h.HullId == TechHull);
        Assert.Contains(hangar.Shop.BuyableHulls, h => h.HullId == Fighter);
        Assert.Equal(2, hangar.Shop.BuyableHulls.Count);
        Assert.Equal((int?)5_000, hangar.Shop.MyMoney);
    }

    [Fact]
    public void The_loadout_pane_opens_the_loadout_and_back_returns_to_the_hangar()
    {
        var (session, hangar) = Docked();
        Start(session, hangar);
        hangar.Activate(HangarAction.Loadout, session.World, Own(session), Ready(), true);
        Assert.Equal(BrowserHangarScreen.Loadout, hangar.Screen);
        // The V2 presentation is the shared one, with the shared wording.
        var frame = hangar.BuildFrame(session.World, Own(session))!;
        Assert.StartsWith("LOADOUT / ", frame.Title);
        Assert.True(frame.Hulls.Count >= 2);
        hangar.Back();
        Assert.Equal(BrowserHangarScreen.Hangar, hangar.Screen);
    }

    [Fact]
    public void Choosing_a_hull_and_a_part_changes_only_the_pending_loadout()
    {
        var (session, hangar) = Docked();
        Start(session, hangar);
        hangar.Activate(HangarAction.Loadout, session.World, Own(session), Ready(), true);
        var model = hangar.ViewModel!;

        Assert.True(model.SelectHull(Fighter));
        Assert.Equal(Fighter, hangar.Shop!.EffectiveHullId);
        Assert.Equal(Scout, hangar.Shop.CurrentHullId);          // the ship itself is untouched
        Assert.True(model.ChooseOffer(hangar.Shop.Slots[0], Gun));
        Assert.Contains(hangar.Shop.ChosenParts, p => p.PartId == Gun);

        var frame = hangar.BuildFrame(session.World, Own(session))!;
        Assert.Contains("Fighter", frame.Hulls.Where(h => h.Selected).Select(h => h.Main.Text).First());
        Assert.True(frame.Price.Count > 0);
    }

    [Fact]
    public void Away_hangar_verbs_say_they_are_not_ported_instead_of_doing_nothing()
    {
        var (session, hangar) = Docked();
        Start(session, hangar);
        foreach (var verb in new[] { HangarAction.Command, HangarAction.Team, HangarAction.Invest,
                     HangarAction.Teleport, HangarAction.Help })
        {
            hangar.Activate(verb, session.World, Own(session), Ready(), true);
            Assert.Contains("not available in the browser", hangar.Status);
            Assert.Equal(BrowserHangarScreen.Hangar, hangar.Screen);
        }
    }

    // ── item 2: the buy flow and its bytes ─────────────────────────────────────

    /// <summary>The desktop's own request, built exactly as
    /// <c>Game1.Hangar.cs:155</c> (<c>PurchaseHangarLoadout</c>) hands it to
    /// <c>ConnectionSession.SendBuyLoadout</c> (<c>ConnectionSession.cs:815-824</c>), which
    /// packs this record. Same shared encoder, so equal arguments are equal bytes.</summary>
    private static byte[] DesktopBytes(short hullId, bool launch, IReadOnlyList<ExpandedPartData> parts)
        => new BuyLoadoutRequest { HullId = hullId, FLaunch = launch, Parts = parts }.Encode();

    private static byte[] BrowserBytes(ExpandedPartData[] parts, short hullId, bool launch)
        => new BuyLoadoutRequest { HullId = hullId, FLaunch = launch, Parts = parts }.Encode();

    [Fact]
    public void Buy_sends_the_desktop_buy_loadout_request_without_launch()
    {
        var (session, hangar) = Docked();
        var wire = new Wire(session.World);
        wire.Attach(hangar);
        Start(session, hangar);
        hangar.Activate(HangarAction.Loadout, session.World, Own(session), Ready(), true);
        hangar.ViewModel!.SelectHull(Fighter);
        hangar.ViewModel.ChooseOffer(hangar.Shop!.Slots[0], Gun);

        hangar.Buy(session.World, Own(session));

        var buy = Assert.Single(wire.Buys);
        Assert.Equal(Fighter, buy.HullId);
        Assert.False(buy.Launch);                                // OnButtonBuy, load.cpp:1524
        Assert.Contains(buy.Parts, p => p.PartId == Gun);
        Assert.Equal(DesktopBytes(Fighter, false, hangar.Shop.ChosenParts),
            BrowserBytes(buy.Parts, buy.HullId, buy.Launch));
    }

    [Fact]
    public void Launch_sends_the_desktop_buy_loadout_request_with_launch()
    {
        var (session, hangar) = Docked();
        var wire = new Wire(session.World);
        wire.Attach(hangar);
        Start(session, hangar);
        hangar.ViewModel!.SelectHull(Scout);
        hangar.ViewModel.ChooseOffer(hangar.Shop!.Slots[0], Gun);

        hangar.Launch(session.World, Own(session), Ready(), missionStarted: true);

        var launch = Assert.Single(wire.Buys);
        Assert.Equal(Scout, launch.HullId);
        Assert.True(launch.Launch);                              // OnButtonLaunch, hangarscreen.cpp:145-160
        Assert.Equal(DesktopBytes(Scout, true, hangar.Shop.ChosenParts),
            BrowserBytes(launch.Parts, launch.HullId, launch.Launch));
        Assert.True(hangar.LaunchSent);
        // A second click while the launch is on its way is dropped (Game1.Hangar.cs:50).
        hangar.Launch(session.World, Own(session), Ready(), true);
        Assert.Single(wire.Buys);
    }

    [Fact]
    public void A_short_wallet_asks_the_team_for_the_shortfall_instead_of_buying()
    {
        var (session, hangar) = Docked(money: 100);
        var wire = new Wire(session.World);
        wire.Attach(hangar);
        Start(session, hangar);
        hangar.ViewModel!.SelectHull(Fighter);                   // $900 against a $100 wallet

        hangar.Buy(session.World, Own(session));

        Assert.Empty(wire.Buys);
        var ask = Assert.Single(wire.Money);
        Assert.Equal(Fighter, ask.HullId);                        // REQUEST_MONEY carries the pending hull
        Assert.Equal((int)hangar.Shop!.BuyShortfall, ask.Amount);
        Assert.Contains("Asked your team", hangar.Status);        // ShopModel.NoteMoneyRequested
    }

    [Fact]
    public void The_servers_acknowledgement_replaces_the_ship_and_clears_the_pending_change()
    {
        var (session, hangar) = Docked();
        var wire = new Wire(session.World);
        wire.Attach(hangar);
        Start(session, hangar);
        hangar.ViewModel!.SelectHull(Fighter);
        hangar.Buy(session.World, Own(session));
        var bought = wire.Buys[0].Parts;

        // The server grants the hull and the part (FM_S_BUY_LOADOUT_ACK).
        session.World.Apply(new BuyLoadoutAck { HullId = Fighter, FBoughtEverything = true, FLaunch = false, Parts = bought });
        session.World.Apply(new ExportShip { ShipId = OwnShip, HullId = Fighter, SideId = 0, CharacterName = "P1070 pilot" });
        Start(session, hangar);

        Assert.Equal(Fighter, hangar.Shop!.CurrentHullId);
        Assert.Equal(Fighter, hangar.Shop.EffectiveHullId);
        Assert.Null(hangar.Shop.ChosenHullId);
        Assert.Equal("", hangar.Status);
    }

    [Fact]
    public void A_refused_buy_shows_the_desktops_message()
    {
        var (session, hangar) = Docked();
        var wire = new Wire(session.World);
        wire.Attach(hangar);
        Start(session, hangar);
        hangar.ViewModel!.SelectHull(Fighter);
        hangar.Buy(session.World, Own(session));

        // The server could not grant every part: the shop reverts the pending ship and
        // prints native's own text (ShopModel.PurchaseCompleted, load.cpp:2195-2205).
        session.World.Apply(new BuyLoadoutAck { HullId = Scout, FBoughtEverything = false, FLaunch = false, Parts = [] });
        Start(session, hangar);

        Assert.Equal(ShopModel.PartsUnavailableText, hangar.Status);
        Assert.Equal(Scout, hangar.Shop!.CurrentHullId);
        Assert.Equal(Scout, hangar.Shop.EffectiveHullId);         // the pending hull was reverted
    }

    [Fact]
    public void An_acknowledgement_without_the_launch_releases_the_launch_latch()
    {
        var (session, hangar) = Docked();
        var wire = new Wire(session.World);
        wire.Attach(hangar);
        Start(session, hangar);
        hangar.Launch(session.World, Own(session), Ready(), true);
        Assert.True(hangar.LaunchSent);

        session.World.Apply(new BuyLoadoutAck { HullId = Scout, FBoughtEverything = true, FLaunch = false, Parts = [] });
        Start(session, hangar);

        // appmsg.cpp:3180-3181 — native ends the lock-down on an ack without fLaunch.
        Assert.False(hangar.LaunchSent);
        Assert.Contains("not performed by the server", hangar.LastAction);
    }

    // ── item 1/4: launch blockers, transitions ─────────────────────────────────

    [Fact]
    public void Launch_blockers_are_the_html_controls_own_words_in_the_game_window()
    {
        var (session, hangar) = Docked();
        var wire = new Wire(session.World);
        wire.Attach(hangar);
        Start(session, hangar);

        foreach (var (blocker, text) in new[]
                 {
                     (HangarLaunch.Blocker.NoSession, HangarLaunchText.For(HangarLaunch.Blocker.NoSession)),
                     (HangarLaunch.Blocker.Reconnecting, HangarLaunchText.For(HangarLaunch.Blocker.Reconnecting)),
                     (HangarLaunch.Blocker.NoShipAssigned, HangarLaunchText.For(HangarLaunch.Blocker.NoShipAssigned)),
                 })
        {
            hangar.Launch(session.World, Own(session), new HangarLaunch.Readiness(blocker, null), true);
            Assert.Equal(text, hangar.Status);
            Assert.False(hangar.LaunchSent);
        }
        Assert.Empty(wire.Buys);
        // The wording is the P1058 HTML control's, unchanged.
        Assert.Equal("Not connected to the server. Return to the list to join again.",
            HangarLaunchText.For(HangarLaunch.Blocker.NoSession));
        Assert.Equal("Reconnecting to the server…", HangarLaunchText.For(HangarLaunch.Blocker.Reconnecting));
        Assert.Equal("Waiting for the server to assign your ship…",
            HangarLaunchText.For(HangarLaunch.Blocker.NoShipAssigned));
    }

    [Fact]
    public void Before_the_mission_starts_the_launch_defers_to_the_pages_ready_pipeline()
    {
        var (session, hangar) = Docked();
        var wire = new Wire(session.World);
        wire.Attach(hangar);
        Start(session, hangar, missionStarted: false);

        hangar.Launch(session.World, Own(session), Ready(), missionStarted: false);

        // Nothing on the wire: the server refuses an undock before STAGE_STARTED
        // (ServerHost.cs:1776-1781), so the page's ready -> started -> DOCKED pipeline
        // runs instead (Menu.Online.cs LaunchOnlineAsync).
        Assert.Empty(wire.Buys);
        Assert.True(hangar.LaunchPending);
        Assert.True(hangar.TakeLaunchRequest());
        Assert.False(hangar.TakeLaunchRequest());                 // one shot
        hangar.LaunchAttemptFinished(launched: true, reason: null);
        Assert.False(hangar.LaunchPending);
        Assert.True(hangar.LaunchSent);
    }

    [Fact]
    public void The_undock_button_always_leaves_without_buying()
    {
        var (session, hangar) = Docked();
        var wire = new Wire(session.World);
        wire.Attach(hangar);
        Start(session, hangar);
        hangar.Undock();
        Assert.Equal(1, wire.Docked);
        Assert.Empty(wire.Buys);
        Assert.True(hangar.LaunchSent);
        hangar.Undock();
        Assert.Equal(1, wire.Docked);                             // latched
    }

    [Fact]
    public void A_berthed_gunner_disembarks_first_and_repeats_the_action_after_the_ack()
    {
        var (session, hangar) = Docked();
        var wire = new Wire(session.World);
        wire.Attach(hangar);
        Start(session, hangar);
        // Berth the pilot on a carrier (native BoardShip child link).
        Set(Own(session), "ParentId", (short)5);

        hangar.Buy(session.World, Own(session));
        Assert.Equal(-1, Assert.Single(wire.Boards));             // DisembarkAndBuy
        Assert.Empty(wire.Buys);

        // The authoritative parent-clear acknowledgement (appmsg.cpp:1929-1938) then
        // runs the action that was waiting.
        Set(Own(session), "ParentId", (short)-1);
        Start(session, hangar);
        Assert.Single(wire.Buys);
        Assert.False(wire.Buys[0].Launch);
    }

    [Fact]
    public void A_board_refusal_cancels_the_waiting_action()
    {
        var (session, hangar) = Docked();
        var wire = new Wire(session.World);
        wire.Attach(hangar);
        Start(session, hangar);
        Set(Own(session), "ParentId", (short)5);
        hangar.Buy(session.World, Own(session));
        Assert.Single(wire.Boards);

        // A BOARD_NACK (own connection only, in game: appmsg.cpp:1899-1911) bumps the
        // world's failure revision, which cancels the wait (Game1.Hangar.cs:70-72).
        session.World.Apply(new EnterGame());
        session.World.Apply(new BoardNack { SidRequestedParent = 5 });
        Start(session, hangar);

        Assert.Empty(wire.Buys);
        Assert.False(hangar.LaunchSent);
    }

    [Fact]
    public void A_confirmed_quit_sends_quit_mission()
    {
        var (session, hangar) = Docked();
        var wire = new Wire(session.World);
        wire.Attach(hangar);
        Start(session, hangar);

        hangar.Activate(HangarAction.Exit, session.World, Own(session), Ready(), true);
        Assert.True(hangar.QuitConfirm);
        hangar.ConfirmQuit(false, OwnShip);
        Assert.Equal(0, wire.Quits);                              // cancelled
        hangar.Activate(HangarAction.Exit, session.World, Own(session), Ready(), true);
        hangar.ConfirmQuit(true, OwnShip);
        Assert.Equal(1, wire.Quits);
        Assert.Equal(OwnShip, wire.QuitShip);
    }

    [Fact]
    public void Undocking_resets_the_hangar_and_a_new_dock_reopens_it()
    {
        var (session, hangar) = Docked();
        var wire = new Wire(session.World);
        wire.Attach(hangar);
        Start(session, hangar);
        hangar.Activate(HangarAction.Loadout, session.World, Own(session), Ready(), true);
        hangar.ViewModel!.SelectHull(Fighter);

        // The launch/undock landed: the server's SET_CLUSTER clears the station.
        session.World.Apply(new ShipStatusMessage
        {
            ShipId = OwnShip, SectorId = 1, StationId = -1, ParentId = -1, HullId = Scout,
            State = ShipStatusMessage.StateFlying,
        });
        Assert.False(Start(session, hangar));
        Assert.False(hangar.Active);
        Assert.Null(hangar.Shop);
        Assert.Null(hangar.ViewModel);
        Assert.False(hangar.LaunchSent);
        Assert.False(hangar.LaunchPending);
        Assert.Equal(BrowserHangarScreen.Hangar, hangar.Screen);

        // Docking again opens the hangar on the ship's current hull, never the stale pick.
        session.World.Apply(new ShipStatusMessage
        {
            ShipId = OwnShip, SectorId = 1, StationId = Station, ParentId = -1, HullId = Scout,
            State = ShipStatusMessage.StateDocked,
        });
        Assert.True(Start(session, hangar));
        Assert.Equal(Scout, hangar.Shop!.EffectiveHullId);
    }

    [Fact]
    public void Reset_clears_the_shop_and_every_latch()
    {
        var (session, hangar) = Docked();
        Start(session, hangar, missionStarted: false);
        hangar.Launch(session.World, Own(session), Ready(), false);
        hangar.Activate(HangarAction.Exit, session.World, Own(session), Ready(), false);
        hangar.Activate(HangarAction.Loadout, session.World, Own(session), Ready(), false);
        Assert.True(hangar.Active);

        hangar.Reset();

        Assert.False(hangar.Active);
        Assert.Null(hangar.Shop);
        Assert.Null(hangar.Frame);
        Assert.Equal(BrowserHangarScreen.Hangar, hangar.Screen);
        Assert.False(hangar.LaunchSent);
        Assert.False(hangar.LaunchPending);
        Assert.False(hangar.LaunchRequested);
        Assert.False(hangar.QuitConfirm);
        Assert.Equal("", hangar.Status);
        Assert.False(hangar.TakeLaunchRequest());
    }

    [Fact]
    public void A_lifepod_dock_restores_the_last_real_loadout()
    {
        var (session, hangar) = Docked();
        // The browser client learns its own last real loadout the way the desktop does:
        // from the ship's hull + mounted parts (ClientWorld.RememberOwnLoadout). The pod
        // hull resolves through the session's lifepod resolver.
        session.World.LifepodHullResolver = static _ => 91;
        session.World.Apply(new ExportShip
        {
            ShipId = OwnShip, HullId = Scout, SideId = 0, CharacterName = "P1070 pilot",
            Parts = [new PartData(Gun, 1, 0)],
        });
        Assert.NotNull(session.World.OwnSavedLoadout);
        Start(session, hangar);
        hangar.Reset();
        // A lifepod-pod hull the station does not offer.
        session.World.Apply(new ExportShip { ShipId = OwnShip, HullId = 91, SideId = 0, CharacterName = "P1070 pilot" });
        Set(Own(session), "StationId", Station);
        Set(Own(session), "Loadout", Array.Empty<ExpandedPartData>());

        Start(session, hangar);

        Assert.NotEqual((short)91, hangar.Shop!.EffectiveHullId);
        Assert.Contains("restored last loadout", hangar.LastAction);
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static void Set(ShipEntity ship, string property, object? value)
        => typeof(ShipEntity).GetProperty(property)!.SetValue(ship, value);
}
