using System.Diagnostics;
using System.Reflection;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.LiveProof;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Server;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Srv = Allegiance.Server.Server;

// Real Game1 + loopback server. No replacement UI. Flag on: the P1015 script
// plays the Loadout V2 screen with the scripted pointer, the wheel and real key
// states. --off: the old loadout, driven by its own unchanged keys.
string output = Path.GetFullPath(args[0]), style = args[1];
int width = int.Parse(args[2]), height = int.Parse(args[3]);
bool off = args.Contains("--off");
bool buy = args.Contains("--buy");   // P1017: the refused-buy script
Directory.CreateDirectory(output);
string content = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content");
string localContent = Path.Combine(AppContext.BaseDirectory, "Content");
if (!Directory.Exists(localContent)) Directory.CreateSymbolicLink(localContent, content);
KeyBindingStore.Instance.Map.SetLayout(InputLayout.Native);
using var server = new Srv { SpawnInitialMinersAtBoot = false };
server.Boot(new ServerConfig
{
    ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
    SideSelectAutoSeatSeconds = 30,
    Games = [new() { Name = "Loadout preview", Map = "XXV_simple.igc", Teams = 2,
        CivIds = [1, 3], SideNames = ["Alpha", "Bravo"], MaxTeamImbalance = 0x7fff }],
});
server.Missions[0].Stage = MissionStage.Started;
using var game = new Game1(new Game1.LaunchOptions
{
    PlayerName = "Loadout pilot", DirectHost = "127.0.0.1", DirectPort = server.BoundPort,
    WindowWidth = width, WindowHeight = height,
    AutoProof = new AutoProofPlan { AutoSide = 0, AutoReady = true },
});
game.Content.RootDirectory = content;
object simGate = new();
using var stop = new CancellationTokenSource();
var ticker = new Thread(() =>
{
    while (!stop.IsCancellationRequested)
    {
        lock (simGate) server.TickSim();
        Thread.Sleep(20);
    }
});
ticker.Start();
var proof = new Proof(game, output, style, width, height, off, simGate, buy ? server : null);
game.Components.Add(proof);
try { game.Run(); }
finally { stop.Cancel(); ticker.Join(); }
return proof.Passed ? 0 : 1;

sealed class Proof(Game1 game, string output, string style, int width, int height,
    bool off, object gate, Srv? buyServer = null) : GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch watch = Stopwatch.StartNew();
    private readonly List<Func<bool>> steps = [];
    private int step;
    private double next;
    private int wheel;
    private short dockedHull;
    private ExpandedPartSnapshot docked = new([]);
    private SlotDef slot;
    private short pickedPart;
    public bool Passed { get; private set; }

    private sealed record ExpandedPartSnapshot(IReadOnlyList<(short Part, byte Mount, short Amount)> Parts)
    {
        public static ExpandedPartSnapshot Of(ShopModel shop) =>
            new(shop.ChosenParts.Select(p => (p.PartId, p.MountId, p.Amount)).ToArray());
        public bool Same(ShopModel shop) => Parts.SequenceEqual(Of(shop).Parts);
    }

    private T Get<T>(string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private object? Raw(string name) => typeof(Game1).GetField(name, Hidden)!.GetValue(game);
    private void Set(string name, object? value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);
    private void Keys(params Keys[] keys) => Set("_introProofKeys", keys);
    private ShopModel Shop => Get<ShopModel>("_shopModel");
    private LoadoutV2Model Model => Get<LoadoutV2Model>("_loadoutV2Model");
    private LoadoutV2Layout Layout => Get<LoadoutV2Layout>("_loadoutV2Layout");
    private LoadoutV2Input Router => Get<LoadoutV2Input>("_loadoutV2InputState");
    private LoadoutV2Presentation Frame => Get<LoadoutV2Presentation>("_loadoutV2Frame");

    private static void Require(bool condition, string message)
    {
        Console.WriteLine($"P1015_CHECK {message}: {condition}");
        if (!condition) throw new InvalidOperationException(message);
    }
    private void Shot(string name)
    {
        Set("_autoshotDir", output);
        Set("_autoShotName", $"{style}-{width}x{height}-{name}");
        Set("_autoShotScheduled", true);
    }
    // The scripted pointer is the same seam the hangar proof uses; Game1 reads
    // it exactly where it would read the real mouse.
    private void Pointer(float x, float y, bool pressed = false, int notches = 0)
    {
        wheel += notches * 120;
        Mouse.SetPosition((int)x, (int)y);
        Set("_hangarScriptMouse", new MouseState((int)x, (int)y, wheel,
            pressed ? ButtonState.Pressed : ButtonState.Released, ButtonState.Released,
            ButtonState.Released, ButtonState.Released, ButtonState.Released));
    }
    private LoadoutV2Layout.Element Element(LoadoutV2Layout.Kind kind, int index = -1) =>
        Layout.Elements.First(e => e.Kind == kind && (index < 0 || e.Index == index));
    private void Hover(Func<LoadoutV2Layout.Element> target) => Do(() =>
    {
        var e = target();
        Pointer(e.Bounds.CenterX, e.Bounds.CenterY);
    });
    private void Click(Func<LoadoutV2Layout.Element> target)
    {
        Hover(target);
        // Release where the press landed: the target row may be gone by then.
        (float X, float Y) at = default;
        Do(() => { var e = target(); at = (e.Bounds.CenterX, e.Bounds.CenterY); Pointer(at.X, at.Y, pressed: true); });
        Do(() => Pointer(at.X, at.Y));
    }
    private void Do(Action action) => steps.Add(() => { action(); return true; });
    private void Press(params Keys[] keys) { Do(() => Keys(keys)); Do(() => Keys()); }
    // Tab (the default focus key; direct focus verbs ship unbound) until the column has focus.
    private void TabTo(LoadoutV2Column column)
    {
        for (int i = 0; i < 3; i++)
        {
            Do(() => { if (Router.Column != column) Keys(Microsoft.Xna.Framework.Input.Keys.Tab); });
            Do(() => Keys());
        }
    }
    private void Check(Func<bool> condition, string message) => Do(() => Require(condition(), message));

    public override void Initialize()
    {
        base.Initialize();
        steps.Add(() =>
        {
            var session = Get<ConnectionSession>("_session");
            if (!Get<bool>("_dockedActive") || Raw("_shopModel") is null
                || string.IsNullOrEmpty(session.World.BriefingText)) return false;
            Require(game.GraphicsDevice.Viewport.Width == width && game.GraphicsDevice.Viewport.Height == height,
                "real viewport matches requested dimensions");
            Get<VisualOptions>("_visuals").SetToggle("hangar.loadoutV2", !off);
            // The server's advanced starting scout is not sold by this fresh
            // station; start from its real stock scout and native default.
            if (buyServer is null)
            {
                Shop.SelectHull(Shop.BuyableHulls.First().HullId);
                Shop.ApplyDefaultLoadout();
            }
            dockedHull = Shop.EffectiveHullId;
            Pointer(2, 2);
            Keys(Microsoft.Xna.Framework.Input.Keys.F4);
            return true;
        });
        Do(() => Keys());
        Check(() => !Get<bool>("_hangarOnly"), "native F4 opens loadout");
        if (buyServer is not null) BuyFlow(buyServer); else if (off) OldScreen(); else V2Screen();
        Do(() =>
        {
            Passed = true;
            Console.WriteLine($"{(buyServer is null ? "P1015" : "P1017")}_PASS {style} {width}x{height} off={off}");
            game.Exit();
        });
    }

    /// <summary>Flag off: today's keys still drive today's three-page shop and
    /// nothing of V2 is ever built.</summary>
    private void OldScreen()
    {
        Check(() => Raw("_loadoutV2Frame") is null && Raw("_loadoutV2InputState") is null,
            "flag off builds no V2 frame and no V2 input router");
        Press(Microsoft.Xna.Framework.Input.Keys.Down);
        Check(() => Shop.Page == ShopModel.ShopPage.HullList && Shop.Cursor == 1, "old Down moves the old hull cursor");
        Press(Microsoft.Xna.Framework.Input.Keys.Tab);
        Check(() => Shop.Tab == ShopTab.Parts, "old Tab switches to the PARTS tab");
        Press(Microsoft.Xna.Framework.Input.Keys.Space);
        Check(() => Shop.SlotOpen, "old Space opens the old slot page");
        Press(Microsoft.Xna.Framework.Input.Keys.B);
        Check(() => !Shop.SlotOpen && Shop.Page == ShopModel.ShopPage.SlotList, "old B goes back a page");
        // V2-only keys do nothing on the old screen.
        Press(Microsoft.Xna.Framework.Input.Keys.D);
        Check(() => Raw("_loadoutV2InputState") is null && Shop.Tab == ShopTab.Parts, "V2 keys are inert with the flag off");
    }

    private void V2Screen()
    {
        Check(() => Raw("_loadoutV2Frame") is not null && Raw("_loadoutV2InputState") is not null,
            "flag on builds the V2 frame and its input router");
        Check(() => Frame.Hint!.Contains("hulls/slots") && Frame.Hint.Contains("default"),
            "footer hint comes from the key table");

        // ── mouse: hull row ─────────────────────────────────────────────────
        int hullRow = -1;
        Do(() => hullRow = Enumerable.Range(0, Model.HullRows.Count).First(i =>
            Model.HullRows[i].IsBuyable && !Model.HullRows[i].IsChosen
            && Layout.Elements.Any(e => e.Kind == LoadoutV2Layout.Kind.Hull && e.Index == i)));
        Click(() => Element(LoadoutV2Layout.Kind.Hull, hullRow));
        Check(() => Shop.EffectiveHullId == Model.HullRows[hullRow].HullId, "clicking a hull row selects that hull");
        Check(() => Shop.Page == ShopModel.ShopPage.HullList && !Shop.SlotOpen, "old shop pages are not driven");
        // Back to the stock scout for the part work.
        Click(() => Element(LoadoutV2Layout.Kind.Hull,
            Enumerable.Range(0, Model.HullRows.Count).First(i => Model.HullRows[i].HullId == dockedHull)));
        Do(() => docked = ExpandedPartSnapshot.Of(Shop));

        // ── mouse: slot → inline picker → offer ─────────────────────────────
        int slotRow = -1, offerRow = -1;
        Do(() =>
        {
            slotRow = Enumerable.Range(0, Model.SlotRows.Count)
                .First(i => Model.OffersFor(Model.SlotRows[i].Slot).Count > 1);
            slot = Model.SlotRows[slotRow].Slot;
        });
        Click(() => Element(LoadoutV2Layout.Kind.Slot, slotRow));
        Check(() => Model.OpenSlot == slot && Layout.ExpandedSlot == slotRow, "clicking a slot opens its inline picker");
        Click(() => Element(LoadoutV2Layout.Kind.Slot, slotRow));
        Check(() => Model.OpenSlot is null, "clicking the open slot again closes the picker");
        Click(() => Element(LoadoutV2Layout.Kind.Slot, slotRow));
        Do(() =>
        {
            var offers = Model.Offers;
            int pick = Enumerable.Range(0, offers.Count).First(i => !offers[i].IsChosen);
            pickedPart = offers[pick].PartId;
            offerRow = pick + 1;
        });
        Hover(() => Element(LoadoutV2Layout.Kind.Offer, offerRow));
        Check(() => Router.Hover is { Kind: LoadoutV2Layout.Kind.Offer } h && h.Index == offerRow,
            "the hovered offer is the router's hover");
        Do(() => Shot("1-click-slot-picker"));
        Click(() => Element(LoadoutV2Layout.Kind.Offer, offerRow));
        Check(() => Shop.PartInSlot(slot)?.PartId == pickedPart && Model.OpenSlot is null,
            "clicking an offer mounts it and closes the picker");

        // ── wheel ───────────────────────────────────────────────────────────
        int maxHull = 0;
        Do(() => { maxHull = Layout.MaxHullScroll; Pointer(Layout.HullList.CenterX, Layout.HullList.CenterY); });
        Do(() => Pointer(Layout.HullList.CenterX, Layout.HullList.CenterY, notches: -3));
        Check(() => Layout.HullScroll == Math.Min(3, maxHull) && Layout.SlotScroll == 0,
            "the wheel scrolls the hull column under the pointer only");
        Do(() => Pointer(Layout.HullList.CenterX, Layout.HullList.CenterY, notches: -200));
        Check(() => Layout.HullScroll == maxHull && Router.HullScroll == maxHull, "wheel stops at the end of the list");
        Do(() => Shot("2-offer-mounted-wheel-scrolled"));
        Do(() => Pointer(Layout.HullList.CenterX, Layout.HullList.CenterY, notches: 200));
        Check(() => Layout.HullScroll == 0, "wheel scrolls back to the top");
        Do(() => Pointer(2, 2));

        // ── keyboard ────────────────────────────────────────────────────────
        TabTo(LoadoutV2Column.Slots);
        Check(() => Router.Column == LoadoutV2Column.Slots, "Tab focuses the slot column");
        Press(Microsoft.Xna.Framework.Input.Keys.Down);
        Check(() => Router.SlotFocus == 1 && Frame.Slots[1].Focused, "Down moves the slot focus");
        Press(Microsoft.Xna.Framework.Input.Keys.Up);
        Press(Microsoft.Xna.Framework.Input.Keys.Space);
        Check(() => Model.OpenSlot == Model.SlotRows[0].Slot, "Space opens the focused slot's picker");
        Press(Microsoft.Xna.Framework.Input.Keys.Up);
        Check(() => Frame.OfferFocus == Router.OfferFocus && Frame.Offers[Router.OfferFocus].Focused,
            "arrows move the picker focus");
        Do(() => Shot("3-keys-picker-focus"));
        Press(Microsoft.Xna.Framework.Input.Keys.Escape);
        Check(() => Model.OpenSlot is null && !Get<bool>("_hangarOnly") && !Get<bool>("_mainMenuVisible"),
            "Esc closes the picker and stays on the loadout");
        short before = 0;
        Do(() => before = Shop.EffectiveHullId);
        Press(Microsoft.Xna.Framework.Input.Keys.Right);
        Check(() => Shop.EffectiveHullId != before, "Right selects the next hull (native GetNextHullType)");
        Press(Microsoft.Xna.Framework.Input.Keys.Left);
        Check(() => Shop.EffectiveHullId == before, "Left selects the previous hull");
        TabTo(LoadoutV2Column.Hulls);
        Check(() => Router.Column == LoadoutV2Column.Hulls, "Tab cycles back to the hull column");

        // ── Default / Revert ────────────────────────────────────────────────
        ExpandedPartSnapshot expected = new([]);
        Do(() =>
        {
            Model.ClearSlot(slot);
            var twin = new ShopModel(Shop.Catalog, Shop.CurrentHullId, Shop.CurrentParts.ToArray(), 0);
            twin.SelectHull(Shop.EffectiveHullId);
            twin.ApplyDefaultLoadout();
            expected = ExpandedPartSnapshot.Of(twin);
        });
        Check(() => !expected.Same(Shop), "the edited loadout differs from the default");
        Press(Microsoft.Xna.Framework.Input.Keys.D);
        Check(() => expected.Same(Shop), "D applies the native default loadout");
        Click(() => Element(LoadoutV2Layout.Kind.Slot, slotRow));
        Click(() => Element(LoadoutV2Layout.Kind.Offer, 0));
        Check(() => Shop.PartInSlot(slot) is null, "the empty row empties the slot");
        Click(() => Element(LoadoutV2Layout.Kind.Default));
        Check(() => expected.Same(Shop), "the Default button applies the native default loadout");
        Check(() => Router.Column == LoadoutV2Column.Actions && Frame.ActionFocus == LoadoutV2Layout.Kind.Default,
            "a clicked button takes the focus");
        Do(() => Shot("4-default-button"));
        Do(() => Pointer(2, 2));
        Press(Microsoft.Xna.Framework.Input.Keys.Right);
        Check(() => Frame.ActionFocus == LoadoutV2Layout.Kind.Revert, "arrows walk the button row");
        Press(Microsoft.Xna.Framework.Input.Keys.Space);
        Check(() => Shop.EffectiveHullId == Shop.CurrentHullId
            && Shop.ChosenParts.Select(p => (p.PartId, p.MountId)).SequenceEqual(
                Shop.CurrentParts.Select(p => (p.PartId, p.MountId))),
            "Revert restores the docked ship's loadout");
        Press(Microsoft.Xna.Framework.Input.Keys.Tab);
        Check(() => Router.Column == LoadoutV2Column.Hulls && Frame.ActionFocus is null,
            "Tab steps the focus ring past the buttons");
        Check(() => !Get<bool>("_launchSent"), "nothing was bought or launched by the script");

        // ── leaving ─────────────────────────────────────────────────────────
        Press(Microsoft.Xna.Framework.Input.Keys.Escape);
        Check(() => Get<bool>("_hangarOnly"), "Esc with no picker open leaves to the hangar as before");
    }

    // ── P1017: Buy / Launch refusals on the V2 screen ───────────────────────

    private static object? Call(Srv server, string name, params object?[] args) =>
        typeof(Srv).GetMethod(name, Hidden)!.Invoke(server, args);

    private static (Ship Ship, object Wallet) Peer(Srv server)
    {
        var peers = (System.Collections.IDictionary)typeof(Srv).GetField("_peerShips", Hidden)!.GetValue(server)!;
        foreach (System.Collections.DictionaryEntry e in peers)
            return ((Ship)e.Value!.GetType().GetProperty("Ship")!.GetValue(e.Value)!,
                e.Value.GetType().GetProperty("Wallet")!.GetValue(e.Value)!);
        throw new InvalidOperationException("no peer ship");
    }

    private static int ServerMoney(Srv server)
    {
        var wallet = Peer(server).Wallet;
        return (int)wallet.GetType().GetProperty("Money")!.GetValue(wallet)!;
    }

    /// <summary>Move the wallet the way the server does: the wallet and the
    /// side-wide MONEY_CHANGE, so the client follows.</summary>
    private void SetMoney(Srv server, int amount)
    {
        var (ship, wallet) = Peer(server);
        int now = ServerMoney(server);
        wallet.GetType().GetProperty("Money")!.SetValue(wallet, amount);
        Call(server, "BroadcastMoneyChangeSideWide", ship.Mission!, ship.SideId!.Value, amount - now, ship.Id);
    }

    private void Wait(Func<bool> condition) => steps.Add(condition);

    private void BuyFlow(Srv server)
    {
        var all = new TechTreeBitMask(Enumerable.Repeat((byte)0xFF, TechTreeBitMask.ByteCount).ToArray());
        var none = TechTreeBitMask.Empty;
        short priced = 0;
        long need = 0;
        int rich = 0;
        Side SideOf() => server.Missions[0].Sides.First(s => s.Id == Peer(server).Ship.SideId);

        // A fully developed side with $100: priced hulls are on offer and out of reach.
        Do(() =>
        {
            var side = SideOf();
            side.DevelopmentTechs = all;
            Call(server, "BroadcastSideTechChange", server.Missions[0], side);
            SetMoney(server, 100);
        });
        Wait(() => Shop.MyMoney == 100 && Shop.BuyableHulls.Any(h => h.Price > 100));
        Do(() =>
        {
            priced = Shop.BuyableHulls.Where(h => h.Price > 100).OrderBy(h => h.Price).First().HullId;
            dockedHull = Shop.CurrentHullId;
        });
        Check(() => Frame.Status.Text == "This is your current loadout." && Frame.Buy.Ink == LoadoutV2Ink.Muted,
            "the docked ship: native 'current loadout' line, Buy disabled");

        // ── short of money ──────────────────────────────────────────────────
        // The hull column is long with every tech; pick through the view-model
        // (the row click itself is P1015's proof), then use the real buttons.
        Do(() => Model.SelectHull(priced));
        Do(() => need = Shop.BuyShortfall);
        Check(() => Shop.EffectiveHullId == priced && need > 0, "a hull the wallet cannot cover is pending");
        Check(() => Frame.Status.Text == "You can't afford this loadout." && Frame.Status.Ink == LoadoutV2Ink.Worse,
            "status line: native 'can't afford' text");
        Check(() => Frame.Buy.Text == $"Need ${need:N0}" && Frame.Launch.Ink == LoadoutV2Ink.Muted,
            "Buy reads Need $X, Launch is disabled");
        Click(() => Element(LoadoutV2Layout.Kind.Launch));
        Check(() => !Get<bool>("_launchSent") && Shop.BlockReason!.StartsWith("You can't afford this loadout."),
            "Launch sends nothing when unaffordable");
        Click(() => Element(LoadoutV2Layout.Kind.Buy));
        Check(() => Frame.Status.Text == $"You can't afford this loadout. Asked your team for ${need:N0}.",
            "Buy asks the team for the shortfall and says so");
        Check(() => Peer(server).Ship.HullType.HullId == dockedHull && ServerMoney(server) == 100,
            "the server ship and wallet are untouched");
        Do(() => Pointer(2, 2));
        Do(() => Shot("p1017-1-cannot-afford"));

        // ── the server refuses: the side lost the tech while the buy was on its way ──
        Do(() => { rich = 1_000_000; SetMoney(server, rich); });
        Wait(() => Shop.MyMoney == rich);
        Check(() => Frame.Buy.Text == "Buy" && Frame.Status.Text == "Press 'buy' to purchase this loadout.",
            "the money arrives: Need $X becomes Buy");
        Do(() => SideOf().DevelopmentTechs = none);
        Click(() => Element(LoadoutV2Layout.Kind.Buy));
        Wait(() => Shop.BlockReason == ShopModel.PartsUnavailableText);
        Check(() => Frame.Status.Text == "Some of the parts you requested are no longer available"
            && Frame.Status.Ink == LoadoutV2Ink.Worse, "status line: native refusal text from the server's answer");
        Check(() => Shop.EffectiveHullId == dockedHull && Shop.CurrentHullId == dockedHull && Shop.AlreadyOwn,
            "the pending loadout went back to the ship the server says we have");
        Check(() => Peer(server).Ship.HullType.HullId == dockedHull && ServerMoney(server) == rich,
            "the server changed nothing and took nothing");
        Do(() => Pointer(2, 2));
        Do(() => Shot("p1017-2-server-refused"));

        // ── and a buy that goes through ─────────────────────────────────────
        long cost = 0;
        Do(() => SideOf().DevelopmentTechs = all);
        Do(() => Model.SelectHull(priced));
        Do(() => cost = Shop.Cost);
        Click(() => Element(LoadoutV2Layout.Kind.Buy));
        Wait(() => Shop.CurrentHullId == priced && Shop.MyMoney == rich - cost);
        Check(() => Peer(server).Ship.HullType.HullId == priced && ServerMoney(server) == rich - cost,
            "the server charged exactly the shown cost");
        Check(() => Frame.Status.Text == "This is your current loadout." && Shop.AlreadyOwn && !Get<bool>("_launchSent"),
            "bought, not launched: the screen shows the new ship as current");
        Check(() => Peer(server).Ship.Parts.All(p => p.MountedFraction >= 1f), "every bought part is armed on the server");
        Do(() => Pointer(2, 2));
        Do(() => Shot("p1017-3-bought"));
    }

    public override void Update(GameTime gameTime)
    {
        if (watch.Elapsed.TotalSeconds > 150) throw new TimeoutException("P1015 proof");
        if (buyServer is not null) Get<VisualOptions>("_visuals").SetToggle("hangar.loadoutV2", true);
        Get<HudSettings>("_hudSettings").Style = style == "classic" ? HudStyle.Classic : HudStyle.Modern;
        // Fix only the proof's orbit angle for reproducible pixels.
        Set("_hullPreviewSpin", .8f - DockedHullOrbit.Advance(0, gameTime.ElapsedGameTime.TotalSeconds));
        if (watch.Elapsed.TotalSeconds < next || step >= steps.Count) return;
        lock (gate)
            if (steps[step]()) { step++; next = watch.Elapsed.TotalSeconds + .4; }
    }
}
