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

// Real Game1 + loopback server. No replacement UI. --off uses the same fixture
// captured by the pre-implementation harness/binaries for the baseline pair.
string output = Path.GetFullPath(args[0]), style = args[1];
int width = int.Parse(args[2]), height = int.Parse(args[3]);
bool off = args.Contains("--off");
string? hardpoints = args.FirstOrDefault(a => a.StartsWith("--hardpoints="))?.Split('=', 2)[1];
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
var proof = new Proof(game, output, style, width, height, off, simGate, hardpoints, args.Contains("--extras"));
game.Components.Add(proof);
try { game.Run(); }
finally { stop.Cancel(); ticker.Join(); }
return proof.Passed ? 0 : 1;

sealed class Proof(Game1 game, string output, string style, int width, int height,
    bool off, object gate, string? hardpoints, bool extras) : GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch watch = Stopwatch.StartNew();
    private int step;
    private double next;
    private float orbit = .8f;
    public bool Passed { get; private set; }
    private T Get<T>(string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private void Set(string name, object? value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);
    private object? Call(string name, params object[] args) => typeof(Game1).GetMethod(name, Hidden)!.Invoke(game, args);
    private void Keys(params Keys[] keys) => Set("_introProofKeys", keys);
    private static void Require(bool condition, string message)
    {
        Console.WriteLine($"P1014_CHECK {message}: {condition}");
        if (!condition) throw new InvalidOperationException(message);
    }
    private void Shot(string name)
    {
        Set("_autoshotDir", output);
        Set("_autoShotName", $"{style}-{width}x{height}-{name}");
        Set("_autoShotScheduled", true);
    }
    private void Advance() { step++; next = watch.Elapsed.TotalSeconds + .5; }
    public override void Update(GameTime gameTime)
    {
        if (watch.Elapsed.TotalSeconds > 120) throw new TimeoutException("P1014 proof");
        Get<HudSettings>("_hudSettings").Style = style == "classic" ? HudStyle.Classic : HudStyle.Modern;
        if (!off && hardpoints is null)
        {
            // Keep the pointer outside the old screen's hover bands while
            // testing its unchanged keyboard path (V2 input is P1015).
            Mouse.SetPosition(2, 2);
            Set("_hangarScriptMouse", new MouseState(2, 2, 0, ButtonState.Released,
                ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released));
        }
        // Fix only the proof's orbit angle for reproducible before/after pixels.
        Set("_hullPreviewSpin", (off ? 0 : orbit) - DockedHullOrbit.Advance(0, gameTime.ElapsedGameTime.TotalSeconds));
        if (watch.Elapsed.TotalSeconds < next) return;
        lock (gate)
        {
            var session = Get<ConnectionSession>("_session");
            var shop = Get<ShopModel?>("_shopModel");
            if (hardpoints is not null) { HardpointScene(session, shop); return; }
            switch (step)
            {
                case 0:
                    if (!Get<bool>("_dockedActive") || shop is null || string.IsNullOrEmpty(session.World.BriefingText)) return;
                    Require(game.GraphicsDevice.Viewport.Width == width && game.GraphicsDevice.Viewport.Height == height,
                        "real viewport matches requested dimensions");
                    if (!off)
                    {
                        Get<VisualOptions>("_visuals").SetToggle("hangar.loadoutV2", true);
                        // The server's advanced starting scout is not sold by
                        // this fresh station. Choose its real stock scout so
                        // the unchanged legacy slot keys have an offered hull.
                        shop.SelectHull(shop.BuyableHulls.First().HullId);
                        shop.ApplyDefaultLoadout();
                    }
                    Keys(Microsoft.Xna.Framework.Input.Keys.F4); Advance(); break;
                case 1:
                    Keys();
                    Require(!Get<bool>("_hangarOnly"), "native F4 opens loadout");
                    Require(shop is not null, "shared shop exists");
                    if (!off)
                    {
                        var model = Get<LoadoutV2Model>("_loadoutV2Model");
                        var layout = Get<LoadoutV2Layout>("_loadoutV2Layout");
                        Require(layout.Elements.Any(e => e.Kind == LoadoutV2Layout.Kind.Hull
                            && !model.HullRows[e.Index].IsBuyable), "a locked hull is visibly laid out");
                        Require(Get<LoadoutV2Presentation>("_loadoutV2Frame").Title.Contains(shop!.EffectiveHull!.Value.Name),
                            "V2 renderer reads the chosen real hull");
                    }
                    else
                        Require(typeof(Game1).GetField("_loadoutV2Frame", Hidden)?.GetValue(game) is null,
                            "flag off never builds a V2 frame");
                    Shot(off ? "off" : "default"); Advance(); break;
                case 2:
                    if (off) { Finish(); break; }
                    // Existing keyboard navigation opens a weapon picker.
                    Keys(Microsoft.Xna.Framework.Input.Keys.Tab); Advance(); break;
                case 3:
                    Keys(); Advance(); break;
                case 4:
                    Keys(Microsoft.Xna.Framework.Input.Keys.Space); Advance(); break;
                case 5:
                    Keys();
                    Require(shop!.SlotOpen && shop.OpenSlot?.Type == EquipmentType.Weapon,
                        "existing keys open weapon offers on shared shop");
                    Require(Get<LoadoutV2Layout>("_loadoutV2Layout").Elements.Any(e => e.Kind == LoadoutV2Layout.Kind.Offer),
                        "open weapon offers are visible inline");
                    Shot("offers"); Advance(); break;
                case 6:
                    // Move the old parts cursor using its production key path.
                    Keys(Microsoft.Xna.Framework.Input.Keys.Down); Advance(); break;
                case 7:
                    Keys();
                    Require(Get<LoadoutV2Presentation>("_loadoutV2Frame").OfferFocus == shop!.Cursor && shop.Cursor > 0,
                        "existing arrow key moves V2 offer focus");
                    Advance(); break;
                case 8:
                    // A later research state, using the same real core and
                    // station filters, supplies a priced hull. Base stock parts
                    // and the two starting hulls in PCore001 are all free.
                    shop!.Back();
                    var catalog = Get<ShopCatalog>("_shopCatalog");
                    var core = catalog.Core!;
                    var side = session.World.MySideId!.Value;
                    var techs = BucketCatalog.ReachableTechs(core, session.World.DevelopmentTechsFor(side)!.Value);
                    var liveTechs = (Dictionary<short, TechTreeBitMask>)session.World.GetType()
                        .GetField("_developmentTechsBySideId", Hidden)!.GetValue(session.World)!;
                    liveTechs[side] = techs;
                    var own = session.World.Ships[session.World.MyShipId!.Value];
                    var station = session.World.Stations[own.StationId];
                    var local = catalog.StationTypes![station.StationTypeId].TtbmLocal;
                    shop.RefreshOffers(catalog.FilteredFor(local, techs), 0);
                    var expensive = shop.BuyableHulls.OrderByDescending(h => h.Price).First();
                    shop.SelectHull(expensive.HullId);
                    // Freeze the client wallet fixture, including refresh input.
                    var wallets = (Dictionary<short, int>)session.World.GetType()
                        .GetField("_moneyByShipId", Hidden)!.GetValue(session.World)!;
                    wallets[session.World.MyShipId!.Value] = 0;
                    shop.RefreshMoney(0);
                    Require(shop.BuyShortfall > 0, "real loadout is unaffordable");
                    Advance(); break;
                case 9:
                    Require(Get<LoadoutV2Presentation>("_loadoutV2Frame").Buy.Text.StartsWith("Need $"),
                        "V2 buy button displays shared shop shortfall");
                    // Allow the real newly selected model/texture to finish its
                    // first draw before reading back the photographed frame.
                    Shot("need"); Advance(); break;
                default: Finish(); break;
            }
        }
    }

    private void HardpointScene(ConnectionSession session, ShopModel? shop)
    {
        // This is a display fixture: use authentic core hulls/default parts in
        // the existing ShopModel, without buying or changing server mount rules.
        // Passenger data enters through production EXPORT / LOADOUT_CHANGE.
        Set("_hangarScriptMouse", new MouseState(2, 2, 0, ButtonState.Released,
            ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released));
        switch (step)
        {
            case 0:
                if (!Get<bool>("_dockedActive") || shop is null || string.IsNullOrEmpty(session.World.BriefingText)) return;
                Require(game.GraphicsDevice.Viewport.Width == width && game.GraphicsDevice.Viewport.Height == height,
                    "hardpoint real viewport matches requested dimensions");
                Get<VisualOptions>("_visuals").SetToggle(VisualFlags.LoadoutV2Key, true);
                Keys(Microsoft.Xna.Framework.Input.Keys.F4); Advance(); break;
            case 1:
                Keys(); Mouse.SetPosition(2, 2);
                Require(!Get<bool>("_hangarOnly"), "hardpoint fixture opens real loadout");
                var catalog = Get<ShopCatalog>("_shopCatalog");
                string name = hardpoints switch { "fighter" => "Fighter", "bomber" => "Bomber", "capital" => "Cruiser",
                    _ => throw new ArgumentException("Use fighter, bomber or capital") };
                var hull = catalog.Hulls.First(h => h.Name == name);
                var fixture = new ShopModel(catalog, hull.HullId, [], 10000);
                fixture.ApplyDefaultLoadout();
                fixture = new ShopModel(catalog, hull.HullId, fixture.ChosenParts.ToArray(), 10000);
                Set("_shopModel", fixture);
                if (hardpoints == "fighter") fixture.ToggleTab(); // Existing keyboard focus, no new input path.
                else
                {
                    var world = session.World;
                    short source = world.MyShipId!.Value;
                    int currentFixed = catalog.Hulls.First(h => h.HullId == world.Ships[source].HullId).MaxFixedWeapons;
                    world.Apply(new Allegiance.Core.Wire.ExportShip { ShipId = 30000, HullId = hull.HullId,
                        SideId = world.MySideId!.Value, CharacterName = "Proof gunner" });
                    world.Apply(new Allegiance.Core.Wire.LoadoutChange { SidShip = source,
                        HullId = world.Ships[source].HullId, Parts = world.Ships[source].Loadout ?? [],
                        Passengers = [new(30000, (byte)currentFixed)] });
                }
                Advance(); break;
            case 2:
                var model = Get<LoadoutV2Model>("_loadoutV2Model");
                var layout = Get<LoadoutV2Layout>("_loadoutV2Layout");
                Require(model.EffectiveHull!.Value.Name == (hardpoints == "capital" ? "Cruiser" : hardpoints == "bomber" ? "Bomber" : "Fighter"),
                    "real core hull is rendered");
                if (hardpoints != "fighter")
                {
                    var row = layout.Elements.First(e => e.Kind == LoadoutV2Layout.Kind.Slot && model.SlotRows[e.Index].IsTurret);
                    Mouse.SetPosition((int)row.Bounds.X + 5, (int)row.Bounds.Y + 5);
                }
                Advance(); break;
            case 3:
                var marker = Get<LoadoutV2HardpointMarker?>("_loadoutV2Marker");
                Require(marker is not null, "hover/focus produces a real frame marker");
                Require(marker!.IsTurret == (hardpoints != "fighter"), "fixed and turret selection agree with core");
                Require(marker.Arc.Count == (marker.IsTurret ? 49 : 0), "only turrets draw the direction arc");
                if (marker.IsTurret) Require(marker.GunnerName == "Proof gunner", "seated gunner reaches the marker");
                Require(!marker.ArcLimitKnown, "PCore001 has no native firing limit; guide is labelled");
                if (hardpoints == "capital") Require(Get<LoadoutV2Model>("_loadoutV2Model").SlotRows.Count(s => s.IsTurret) >= 3,
                    "capital fixture has several authentic turret rows");
                Console.WriteLine($"P1016_MARKER {hardpoints} {marker.Point} occluded={marker.Occluded}");
                Shot("hardpoint-" + hardpoints); Advance(); break;
            case 4:
                if (!extras) { Finish(); break; }
                orbit += MathHelper.Pi;
                Advance(); break;
            case 5:
                Require(Get<LoadoutV2HardpointMarker?>("_loadoutV2Marker") is not null, "marker survives opposite orbit");
                Shot("opposite-orbit"); Advance(); break;
            case 6:
                var slots = Get<LoadoutV2Model>("_loadoutV2Model").SlotRows;
                var element = Get<LoadoutV2Layout>("_loadoutV2Layout").Elements.First(e =>
                    e.Kind == LoadoutV2Layout.Kind.Slot && slots[e.Index].Slot.Type == EquipmentType.Shield);
                Mouse.SetPosition((int)element.Bounds.X + 5, (int)element.Bounds.Y + 5);
                Advance(); break;
            case 7:
                Require(Get<LoadoutV2HardpointMarker?>("_loadoutV2Marker") is null, "shield hover has no invented mount");
                Shot("shield-no-marker"); Advance(); break;
            default: Finish(); break;
        }
    }
    private void Finish()
    {
        Passed = true;
        Console.WriteLine($"P1014_PASS {style} {width}x{height} off={off}");
        game.Exit();
    }
}
