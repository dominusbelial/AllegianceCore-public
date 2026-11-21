using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>Which docked screen the browser game window shows: the native hangar
/// (<c>vmHangar</c>) or the loadout screen (<c>vmLoadout</c>).</summary>
public enum BrowserHangarScreen
{
    Hangar,
    Loadout,
}

/// <summary>
/// P1070 — the browser's docked hangar + loadout state machine. This is the browser
/// copy of the desktop glue; it mirrors <c>Game1.Hangar.cs</c>,
/// <c>Game1.LoadoutV2.cs</c> and <c>Game1.LoadoutV2Input.cs</c> and adds no rule of
/// its own. Every model, screen, rule and wire message is the shared one the desktop
/// uses:
/// <list type="bullet">
/// <item>the shop model and its offer filter — <c>Game1.Panes.cs:150-206</c>
/// (<see cref="ShopCatalog.FilteredFor"/> over the station's <c>TtbmLocal</c> and the
/// side's live developments, <c>WinTrek/load.cpp:1603-1606</c>);</item>
/// <item>the per-frame reconciliation — <c>Game1.Panes.cs:443-458</c>
/// (<see cref="ShopModel.RefreshMoney"/>/<see cref="ShopModel.RefreshOffers"/>) and
/// <c>Game1.Hangar.cs:141-160</c> (<see cref="LoadoutBuyFlow.Sync"/>,
/// <c>clintlib/appmsg.cpp:3176-3187</c>);</item>
/// <item>the launch / buy actions — <c>Game1.Hangar.cs:48-140</c>: native
/// <c>HangarScreen::OnButtonLaunch</c>'s buy-with-<c>fLaunch</c>
/// (<c>WinTrek/hangarscreen.cpp:145-160</c>), <c>OnButtonBuy</c>'s
/// <c>REQUEST_MONEY</c> for the shortfall (<c>WinTrek/load.cpp:1511-1531</c>) and the
/// disembark-then-act sequence (<see cref="HangarLaunchSequence"/>,
/// <c>clintlib/appmsg.cpp:1929-1938</c>);</item>
/// <item>the lifepod restore — <c>Game1.Panes.cs:2342-2350</c>
/// (<c>clintlib.h:1876-1886</c>);</item>
/// <item>the loadout view model and its presentation — <c>Game1.LoadoutV2.cs:44-119</c>
/// (<see cref="LoadoutV2Model"/>, <see cref="LoadoutV2Presentation.Build"/>);</item>
/// <item>the input router's request word — <c>Game1.LoadoutV2Input.cs:73-140</c>
/// (<see cref="LoadoutV2Input"/>, <see cref="Apply"/>).</item>
/// </list>
///
/// <para><b>Deliberately skipped: local training.</b> The desktop's
/// <c>CompleteLocalTrainingPurchase</c>/<c>ApplyLocalTrainingLaunch</c> branch
/// (<c>Game1.Hangar.cs:162-176</c>) exists only for the desktop's embedded skirmish
/// host (<c>_embeddedHost</c>). A WASM head has no local server, so the browser always
/// takes the else branch — the ordinary <c>FM_C_BUY_LOADOUT</c> on the wire.</para>
///
/// <para><b>No device.</b> Everything here is pure, so the whole state machine (open,
/// hull choice, part choice, buy, ACK success/failure, launch blockers, reset) is
/// unit-tested headlessly. The drawing/input partial is <c>Game1.Hangar.cs</c> in this
/// assembly.</para>
/// </summary>
public sealed class BrowserHangarModel
{
    // ── wire ports ─────────────────────────────────────────────────────────────
    // The browser renders one frame per animation frame, so a send is a direct call
    // (the desktop's `_session.SendX(...)`); tests substitute recorders and rebuild
    // the exact bytes with the shared encoder.

    /// <summary><c>FM_C_BUY_LOADOUT</c> (<c>Messages.h:65</c>) — the shared
    /// <see cref="ConnectionSession.SendBuyLoadout"/> signature.</summary>
    public Action<short, bool, IReadOnlyList<ExpandedPartData>>? SendBuyLoadout { get; set; }

    /// <summary><c>FM_CS_REQUEST_MONEY</c> (<c>Messages.h:940</c>).</summary>
    public Action<int, short>? SendMoneyRequest { get; set; }

    /// <summary><c>FM_C_BOARD_SHIP</c> (<c>Messages.h:686</c>) — disembark is
    /// <c>sidParent = -1</c>.</summary>
    public Action<short>? SendBoardShip { get; set; }

    /// <summary><c>FM_C_DOCKED</c> (<c>Messages.h:48</c>): the no-purchase undock.</summary>
    public Action? SendDocked { get; set; }

    /// <summary><c>FM_CS_QUIT_MISSION</c> (<c>Messages.h:812</c>).</summary>
    public Action<short, QuitSideReason>? SendQuitMission { get; set; }

    /// <summary>The live key label for a bound action (the desktop's
    /// <c>KeyOf(action).Label</c>); null keeps the action name.</summary>
    public Func<string, string?>? KeyLabel { get; set; }

    // ── shared models, exactly the desktop's ───────────────────────────────────
    private readonly LoadoutBuyFlow _buyFlow = new();
    private readonly HangarLaunchSequence _launchSequence = new();

    /// <summary>The docked shop (<c>Game1.Panes.cs:177-196</c>). Null while not docked.</summary>
    public ShopModel? Shop { get; private set; }

    /// <summary>The V2 view model, rebuilt when the shop, the tech set, the station's
    /// local tech or the source ship changes (<c>Game1.LoadoutV2.cs:64-95</c>).</summary>
    public LoadoutV2Model? ViewModel { get; private set; }

    /// <summary>The presentation frame the renderer draws
    /// (<c>Game1.LoadoutV2.cs:96-119</c>).</summary>
    public LoadoutV2Presentation? Frame { get; private set; }

    /// <summary>The catalogue the browser loaded at session attach
    /// (<c>Game1.Flight.cs:344</c> <c>_shopCatalog ??= ShopCatalog.TryLoad(_assets)</c>),
    /// or the empty catalogue. The hulls/parts offered are always the server's: the
    /// filtered catalogue, never a browser list.</summary>
    public ShopCatalog Catalog { get; set; } = ShopCatalog.Empty;

    private ShopCatalog? _viewCatalog;
    private TechTreeBitMask? _viewTechs;
    private byte[]? _viewLocal;
    private short _viewSource = -1;
    private TechTreeBitMask? _offerTechs;
    private short _offerStationType = -1;

    // ── view state ─────────────────────────────────────────────────────────────
    /// <summary>Which docked screen is up. Native docks into the hangar
    /// (<c>WinTrek/WinTrek.cpp:6565,9381</c>); the Loadout pane opens the loadout
    /// (<c>hangarscreen.cpp:186-190</c>) and Back returns
    /// (<c>Game1.LoadoutV2Input.cs:127-131</c>).</summary>
    public BrowserHangarScreen Screen { get; private set; } = BrowserHangarScreen.Hangar;

    /// <summary>The hangar verb under the pointer (<c>HangarControls.Hit</c>).</summary>
    public HangarAction Hover { get; private set; }

    /// <summary>Why the last action did nothing, in the shared shop's own words
    /// (<see cref="ShopModel.BlockReason"/>, <see cref="ShopModel.StateText"/>).
    /// Empty when there is nothing to say.</summary>
    public string Status { get; private set; } = "";

    /// <summary>A bought/launched/refused action's one-line log — the browser
    /// equivalent of the desktop's <c>[monogame]</c> console lines.</summary>
    public string LastAction { get; private set; } = "";

    /// <summary>The launch blockade (<c>Game1.Hangar.cs:50</c> <c>_launchSent</c> /
    /// <c>_hangarLaunch.AwaitingDisembark</c>): a second click while one is on its way
    /// is dropped, exactly as on the desktop.</summary>
    public bool LaunchSent { get; private set; }

    /// <summary>The page owns the ready → <c>STAGE_STARTED</c> → <c>DOCKED</c> launch
    /// pipeline (its <c>LaunchOnlineAsync</c>); the game window asks it to run through
    /// this one-shot flag (<see cref="TakeLaunchRequest"/>).</summary>
    public bool LaunchRequested { get; private set; }

    /// <summary>Set while the page's launch pipeline runs, so the hangar can show the
    /// same busy line the HTML control shows.</summary>
    public bool LaunchPending { get; private set; }

    /// <summary>A quit confirmation is up (<c>Game1.Hangar.cs:203-213</c>).</summary>
    public bool QuitConfirm { get; private set; }

    /// <summary>True while the world has the player docked and the model is live.</summary>
    public bool Active => Shop is not null;

    /// <summary>The native developments gate (<c>hangarscreen.cpp:129-130</c>): the
    /// Invest pane is hidden when the mission forbids developments.</summary>
    public bool Developments { get; private set; } = true;

    /// <summary>The board state the in-turret badge keys off (<c>hangar.mdl:74-80</c>,
    /// <c>WinTrek/console.cpp:797-804</c>): 0 free, 1 observer, 2 turret gunner.</summary>
    public int? BoardState { get; private set; }

    private long _boardFailureSnapshot;
    private bool _buyAfterDisembark;

    // ── lifecycle ──────────────────────────────────────────────────────────────

    /// <summary>Session reset (attach/detach, undock, death): everything the hangar
    /// held goes away (<c>Game1.Flight.cs:88-101</c> <c>AttachOnlineSession</c>).</summary>
    public void Reset()
    {
        Shop = null;
        ViewModel = null;
        Frame = null;
        _viewCatalog = null; _viewTechs = null; _viewLocal = null; _viewSource = -1;
        _offerTechs = null; _offerStationType = -1;
        _boardFailureSnapshot = 0; _buyAfterDisembark = false;
        Screen = BrowserHangarScreen.Hangar;
        Hover = HangarAction.None;
        Status = LastAction = "";
        LaunchSent = false;
        LaunchRequested = false;
        LaunchPending = false;
        QuitConfirm = false;
        BoardState = null;
    }

    /// <summary>Every docked frame (<c>Game1.Panes.cs:88-222</c>). Returns false when
    /// the player is not docked; the model then has reset itself.</summary>
    /// <param name="missionStarted">The mission has reached <c>STAGE_STARTED</c>
    /// (<c>FedSrv/FedSrv.CPP:7031-7034</c>), the server's undock gate
    /// (<c>ServerHost.cs:1776-1781</c>).</param>
    public bool Update(ClientWorld world, ShipEntity? own, HangarLaunch.Readiness readiness,
        bool missionStarted)
    {
        LastReadiness = readiness;
        if (own is null || own.StationId < 0 || own.ShipId != world.MyShipId
            || !world.Ships.TryGetValue(own.ShipId, out var mine) || mine.StationId < 0)
        {
            if (Active || LaunchRequested || LaunchPending) Reset();
            return false;
        }
        Developments = world.ResearchBriefing?.Settings.AllowDevelopments ?? true;
        // Native's BoardState (WinTrek/console.cpp:797-804): null when not berthed, 2 for a
        // turret gunner, 1 for a parented observer — the desktop's LoadoutBoardState()
        // (Game1.Rendering.cs:3570-3581) keys the same badge.
        BoardState = mine.ParentId < 0 ? null
            : mine.State == ShipStatusMessage.StateTurret ? 2 : 1;
        if (!Active) EnterDock(world, mine);
        RefreshOffers(world, mine);          // Game1.Panes.cs:443-458
        SyncShopWithServer(world, mine);     // Game1.Hangar.cs:141-160
        RestoreLifepodLoadout(world, mine);  // Game1.Panes.cs:2342-2350
        ContinueDisembark(world, mine, missionStarted);   // Game1.Hangar.cs:68-84
        return true;
    }

    /// <summary>Native's dock entry (<c>Game1.Panes.cs:127-206</c>): a fresh shop from
    /// the current ship, never a mutation of it. A ghost never docks into the hangar
    /// (<c>Game1.Panes.cs:106-126</c>); the browser's caller refuses one before here.</summary>
    private void EnterDock(ClientWorld world, ShipEntity own)
    {
        var offerCat = Catalog;
        if (Catalog.StationTypes is not null
            && world.Stations.TryGetValue(own.StationId, out var station)
            && Catalog.StationTypes.TryGetValue(station.StationTypeId, out var type))
            offerCat = Catalog.FilteredFor(type.TtbmLocal, world.DevelopmentTechsFor(world.MySideId ?? -1));
        Shop = new ShopModel(offerCat, own.HullId, own.Loadout ?? [], world.MyMoney,
            customLoadouts: null, currentCargo: own.Cargo,
            // The browser head has no local server, so there is no "training" ledger
            // to bypass: the server's own hardware ledger is authoritative (the
            // desktop's `_embeddedHost is not null` branch cannot apply).
            currentHullPaid: world.OwnHardware.HullPaid(own.HullId),
            currentFreeParts: world.OwnHardware.FreeParts.ToArray());
        _buyFlow.Attach(world.OwnHardware);
        Screen = BrowserHangarScreen.Hangar;
        Status = "";
        LastAction = "";
        LaunchSent = false;
        QuitConfirm = false;
        Hover = HangarAction.None;
        _boardFailureSnapshot = world.BoardFailureRevision;
        RebuildViewModel(world, own);
    }

    /// <summary>The per-frame offer filter — the desktop keeps it in
    /// <c>RefreshDockedOffers</c> and only rebuilds when the tech set or the station
    /// type moved (<c>Game1.Panes.cs:443-458</c>).</summary>
    private void RefreshOffers(ClientWorld world, ShipEntity own)
    {
        if (Shop is not { } shop || Catalog.StationTypes is null) { Shop?.RefreshMoney(world.MyMoney); return; }
        shop.RefreshMoney(world.MyMoney);
        if (!world.Stations.TryGetValue(own.StationId, out var dock)
            || !Catalog.StationTypes.TryGetValue(dock.StationTypeId, out var type)) return;
        var techs = world.DevelopmentTechsFor(world.MySideId ?? -1);
        if (techs is { } known)
        {
            foreach (var owned in world.Stations.Values)
                if (owned.SideId == world.MySideId
                    && Catalog.StationTypes.TryGetValue(owned.StationTypeId, out var ownedType))
                    known = known.Or(TechTreeBitMask.FromBytes(ownedType.TtbmEffects));
            techs = known;
        }
        if (_offerTechs == techs && _offerStationType == dock.StationTypeId) return;
        _offerTechs = techs;
        _offerStationType = dock.StationTypeId;
        shop.RefreshOffers(Catalog.FilteredFor(type.TtbmLocal, techs), world.MyMoney);
    }

    /// <summary>The acknowledgement reconciliation (<c>Game1.Hangar.cs:141-160</c>):
    /// wallet, then the ship the server says we have now; a launch the server did not
    /// perform releases the latch (<c>clintlib/appmsg.cpp:3180-3181</c>).</summary>
    private void SyncShopWithServer(ClientWorld world, ShipEntity own)
    {
        if (Shop is not { } shop) return;
        long? richest = null;
        foreach (var (shipId, money) in world.WalletByShipId)
            if (shipId != own.ShipId && (richest is not { } best || money > best)) richest = money;
        var outcome = _buyFlow.Sync(shop, own, world.OwnHardware, world.MyMoney,
            trackFreeHardware: true, richestTeammate: richest);
        if (!outcome.Changed) return;
        RebuildViewModel(world, own);
        if (outcome.LaunchRefused && LaunchSent && own.StationId >= 0)
        {
            LaunchSent = false;
            LastAction = "launch not performed by the server -> loadout screen released";
        }
        if (outcome.Answered && !outcome.Complete)
            Status = shop.BlockReason ?? ShopModel.PartsUnavailableText;
    }

    /// <summary>The P996 berthed-pod restore (<c>Game1.Panes.cs:2342-2350</c>): native
    /// restores the last real loadout instead of relaunching the pod
    /// (<c>clintlib.h:1876-1886</c>).</summary>
    private void RestoreLifepodLoadout(ClientWorld world, ShipEntity own)
    {
        if (Shop is not { } shop || shop.ChosenHullId is not null) return;
        if (!world.OwnShipDockedInLifepod || shop.EffectiveHullId != own.HullId) return;
        bool restored = world.OwnSavedLoadout is { } saved
            ? shop.RestoreSavedLoadout(saved.HullId, saved.Parts)
            : shop.RestoreDefaultLoadout();
        if (!restored) return;
        LastAction = $"docked in a lifepod: restored last loadout hull {shop.EffectiveHullId} + {shop.ChosenParts.Count} part(s)";
    }

    /// <summary>The V2 view model (<c>Game1.LoadoutV2.cs:64-95</c>), rebuilt only when
    /// one of its inputs moved.</summary>
    private void RebuildViewModel(ClientWorld world, ShipEntity own)
    {
        if (Shop is not { } shop) { ViewModel = null; return; }
        var catalog = Catalog;
        var techs = world.DevelopmentTechsFor(world.MySideId ?? -1) ?? TechTreeBitMask.Empty;
        byte[]? local = null;
        short source = own.ShipId;
        if (own.ParentId >= 0) source = own.ParentId;
        if (world.Stations.TryGetValue(own.StationId, out var station)
            && catalog.StationTypes?.TryGetValue(station.StationTypeId, out var stationType) == true)
            local = stationType.TtbmLocal;
        if (catalog.StationTypes is { } stationTypes)
            foreach (var owned in world.Stations.Values)
                if (owned.SideId == world.MySideId
                    && stationTypes.TryGetValue(owned.StationTypeId, out var type))
                    techs = techs.Or(TechTreeBitMask.FromBytes(type.TtbmEffects));
        if (ViewModel is not null && ReferenceEquals(ViewModel.Shop, shop) && _viewTechs == techs
            && ReferenceEquals(_viewLocal, local) && _viewSource == source && _viewCatalog == catalog)
            return;
        var reachable = catalog.Core is { } core ? BucketCatalog.ReachableTechs(core, techs) : (TechTreeBitMask?)null;
        ViewModel = new LoadoutV2Model(shop, catalog.Hulls, reachable, local, techs,
            mount =>
            {
                int currentFixed = world.Ships.TryGetValue(source, out var ship)
                    ? catalog.Hulls.FirstOrDefault(h => h.HullId == ship.HullId).MaxFixedWeapons : 0;
                int chosenFixed = catalog.Hulls.FirstOrDefault(h => h.HullId == shop.EffectiveHullId).MaxFixedWeapons;
                return HudInventoryPaneBuilder.SourceShipOccupants(world.Ships, source).Occupant(
                    LoadoutV2Presentation.GunnerMount(mount, currentFixed, chosenFixed));
            });
        _viewTechs = techs;
        _viewLocal = local;
        _viewSource = source;
        _viewCatalog = catalog;
    }

    /// <summary>Bring the V2 view model up to date without building a presentation, so
    /// the input router can step the model it will draw this frame.</summary>
    public void Prepare(ClientWorld world, ShipEntity own) => RebuildViewModel(world, own);

    /// <summary>The presentation the renderer draws (<c>Game1.LoadoutV2.cs:96-119</c>);
    /// a pure snapshot, rebuilt every frame the loadout is on screen.</summary>
    public LoadoutV2Presentation? BuildFrame(ClientWorld world, ShipEntity own, LoadoutV2Input? input = null)
    {
        if (Shop is not { } shop) return null;
        RebuildViewModel(world, own);
        if (ViewModel is not { } model) return null;
        ShipStatSheet? Sheet(short hullId, IReadOnlyList<ExpandedPartData> parts)
        {
            DataHullTypeIGC? found = null;
            foreach (var candidate in Catalog.Hulls)
                if (candidate.HullId == hullId) { found = candidate; break; }
            if (found is not { } hull) return null;
            var mounted = new List<MountedStatPart>();
            foreach (var part in parts)
                if (part.MountId < 0x80 && shop.PartById(part.PartId) is { } type)
                    mounted.Add(new(type, part.MountId));
            return ShipStatSheet.Build(hull, mounted, world.GlobalAttributesFor(world.MySideId ?? -1),
                Catalog.Core is { } core ? new StaticCoreShipStatLookup(core) : EmptyShipStatLookup.Instance);
        }
        Frame = LoadoutV2Presentation.Build(model,
            Sheet(shop.CurrentHullId, shop.CurrentParts), Sheet(shop.EffectiveHullId, shop.ChosenParts),
            input, LoadoutV2Input.Hint(action => KeyLabel?.Invoke(action) ?? action));
        return Frame;
    }

    // ── actions ────────────────────────────────────────────────────────────────

    /// <summary>Hover for this frame (<c>Game1.Hangar.cs:233</c>).</summary>
    public void SetHover(HangarAction hover) => Hover = hover;

    /// <summary>The hangar's pointer dispatcher, shared by the real pointer and the
    /// tests (<c>Game1.Hangar.cs:167-201</c> <c>ActivateHangar</c>).</summary>
    public void Activate(HangarAction action, ClientWorld world, ShipEntity own,
        HangarLaunch.Readiness readiness, bool missionStarted)
    {
        if (!Active || LaunchSent || _launchSequence.AwaitingDisembark || QuitConfirm) return;
        switch (action)
        {
            case HangarAction.Launch:
                Launch(world, own, readiness, missionStarted);
                break;
            case HangarAction.Buy:
                Buy(world, own);
                break;
            case HangarAction.Loadout:
                Screen = BrowserHangarScreen.Loadout;
                Status = "";
                break;
            case HangarAction.Default:
                Shop?.ApplyDefaultLoadout();
                break;
            case HangarAction.Exit:
                QuitConfirm = true;
                break;
            // Native verbs whose screens are later tasks (command, team, invest,
            // teleport/board, help). They stay visible exactly as native draws them
            // and say so instead of doing nothing silently.
            default:
                Status = action switch
                {
                    HangarAction.Command => "The command screen is not available in the browser yet.",
                    HangarAction.Team => "The team pane is not available in the browser yet.",
                    HangarAction.Invest => "Investments are not available in the browser yet.",
                    HangarAction.Teleport => "Teleport and boarding are not available in the browser yet.",
                    HangarAction.Help => "The help screen is not available in the browser yet.",
                    _ => "",
                };
                break;
        }
    }

    /// <summary>Native <c>OnButtonLaunch</c> (<c>hangarscreen.cpp:145-160</c>):
    /// disembark first when berthed on another ship, otherwise buy-and-launch.
    /// <para>The wire action follows the two situations the browser can be in. With the
    /// mission started, the desktop's own <c>BUY_LOADOUT(fLaunch=true)</c> applies
    /// (<c>Game1.Hangar.cs:62-66</c>; the server undocks on exactly that message,
    /// <c>Server.ShipCommands.cs:3168-3180</c>). Before the mission starts the server
    /// refuses to launch (<c>ServerHost.cs:1776-1781</c>), so the browser uses the
    /// pipeline its HTML launch control has always used — ready →
    /// <c>STAGE_STARTED</c> → <c>DOCKED</c> (<c>Menu.Online.cs:252-300</c>) — gated by
    /// the same <see cref="HangarLaunch"/> readiness rule with the same blocker
    /// text.</para></summary>
    public void Launch(ClientWorld world, ShipEntity own, HangarLaunch.Readiness readiness,
        bool missionStarted)
    {
        if (!Active || LaunchSent || _launchSequence.AwaitingDisembark) return;
        if (own.ParentId >= 0)
        {
            _boardFailureSnapshot = world.BoardFailureRevision;
            _buyAfterDisembark = false;
            _launchSequence.Begin(own.ParentId);
            SendBoardShip?.Invoke(-1);
            return;
        }
        if (!readiness.CanLaunch)
        {
            Status = HangarLaunchText.For(readiness.Blocker);
            LastAction = "launch blocked: " + readiness.Blocker;
            return;
        }
        if (!missionStarted)
        {
            LaunchRequested = true;
            LaunchPending = true;
            LastAction = "launch requested (ready -> mission start -> dock)";
            return;
        }
        if (Shop is not { } shop || _buyFlow.Launch(shop) is not { Step: LoadoutBuyFlow.Step.Purchase } order)
        {
            Status = Shop?.BlockReason ?? ShopModel.StateText(ShopModel.ShipState.Unaffordable);
            LastAction = "launch refused: " + Status;
            return;
        }
        SendBuyLoadout?.Invoke(order.HullId, true, order.Parts);
        LaunchSent = true;
        LastAction = $"launch hull {order.HullId} + {order.Parts.Count} part(s)";
    }

    /// <summary>Native <c>OnButtonBuy</c> (<c>WinTrek/load.cpp:1503-1521</c>):
    /// <c>REQUEST_MONEY</c> for the shortfall, else <c>BUY_LOADOUT</c> without launch,
    /// after disembarking when berthed (<c>Game1.Hangar.cs:88-139</c>).</summary>
    public void Buy(ClientWorld world, ShipEntity own)
    {
        if (!Active || LaunchSent || _launchSequence.AwaitingDisembark) return;
        if (own.ParentId >= 0)
        {
            _boardFailureSnapshot = world.BoardFailureRevision;
            _buyAfterDisembark = true;
            _launchSequence.Begin(own.ParentId);
            SendBoardShip?.Invoke(-1);
            return;
        }
        if (Shop is not { } shop) return;
        // P1017: one flow for both screens; only V2 has native's disabled Buy.
        var order = _buyFlow.Buy(shop, nativeGate: Screen == BrowserHangarScreen.Loadout);
        switch (order.Step)
        {
            case LoadoutBuyFlow.Step.RequestMoney:
                SendMoneyRequest?.Invoke(order.Amount, order.HullId);
                Status = shop.BlockReason ?? "";
                LastAction = $"buy short ${order.Amount} -> REQUEST_MONEY hull {order.HullId}";
                break;
            case LoadoutBuyFlow.Step.Purchase:
                SendBuyLoadout?.Invoke(order.HullId, false, order.Parts);
                LastAction = $"purchase hull {order.HullId} + {order.Parts.Count} part(s)";
                break;
        }
    }

    /// <summary>The no-purchase undock (<c>Game1.LoadoutV2Input.cs:118-134</c>): the
    /// Undock button always leaves, whether or not the ship changed.</summary>
    public void Undock()
    {
        if (LaunchSent) return;
        SendDocked?.Invoke();
        LaunchSent = true;
        LastAction = "UNDOCK (no buy) -> FM_C_DOCKED(NA)";
    }

    /// <summary>Back to the hangar (<c>Game1.LoadoutV2Input.cs:127-131</c>); native
    /// training keeps the loadout, a browser head has no training host.</summary>
    public void Back()
    {
        if (Screen != BrowserHangarScreen.Loadout) return;
        Screen = BrowserHangarScreen.Hangar;
        Status = "";
    }

    /// <summary>The loadout router's request word (<c>Game1.LoadoutV2Input.cs:73-87</c>).</summary>
    public void Apply(LoadoutV2Request request, ClientWorld world, ShipEntity own,
        HangarLaunch.Readiness readiness, bool missionStarted)
    {
        if ((request & LoadoutV2Request.Undock) != 0) Undock();
        if ((request & LoadoutV2Request.Buy) != 0) Buy(world, own);
        if ((request & LoadoutV2Request.Launch) != 0) Launch(world, own, readiness, missionStarted);
        if ((request & LoadoutV2Request.Leave) != 0) Back();
    }

    /// <summary>The disembark continuation (<c>Game1.Hangar.cs:68-84</c>): a
    /// <c>BOARD_NACK</c> cancels the wait (<c>clintlib/appmsg.cpp:1899-1911</c>); the
    /// authoritative parent-clear acknowledgement runs the action that was waiting.</summary>
    private void ContinueDisembark(ClientWorld world, ShipEntity own, bool missionStarted)
    {
        if (!_launchSequence.AwaitingDisembark) return;
        if (world.BoardFailureRevision != _boardFailureSnapshot)
        {
            _launchSequence.Cancel();
            _buyAfterDisembark = false;
            Status = "Boarding refused: you stay where you are.";
            return;
        }
        if (!_launchSequence.Acknowledged(own.ParentId, own.StationId >= 0)) return;
        if (_buyAfterDisembark)
        {
            _buyAfterDisembark = false;
            Buy(world, own);
        }
        else
        {
            Launch(world, own, LastReadiness, missionStarted);
        }
    }

    /// <summary>The quit confirmation (<c>Game1.Hangar.cs:203-213</c>
    /// <c>ConfirmHangarQuit</c>): a confirmed quit sends
    /// <c>QUIT_MISSION</c>; the authoritative departure path returns to the browser.</summary>
    public void ConfirmQuit(bool confirm, short ownShipId)
    {
        if (!QuitConfirm) return;
        QuitConfirm = false;
        if (!confirm) return;
        _launchSequence.Cancel();
        _buyAfterDisembark = false;
        SendQuitMission?.Invoke(ownShipId, QuitSideReason.Quit);
    }

    /// <summary>The readiness the last docked frame saw — the same
    /// <see cref="HangarLaunch.Of"/> snapshot the page's HTML control computes, so the
    /// hangar's blocker text can never disagree with it.</summary>
    public HangarLaunch.Readiness LastReadiness { get; private set; } =
        new(HangarLaunch.Blocker.NoSession, null);

    /// <summary>The page took the launch request (one shot).</summary>
    public bool TakeLaunchRequest()
    {
        if (!LaunchRequested) return false;
        LaunchRequested = false;
        return true;
    }

    /// <summary>The page reported the end of its launch attempt (started, or refused)
    /// so the hangar's busy line clears.</summary>
    public void LaunchAttemptFinished(bool launched, string? reason)
    {
        LaunchPending = false;
        if (launched) { LaunchSent = true; return; }
        LaunchSent = false;
        Status = reason ?? "";
    }
}
