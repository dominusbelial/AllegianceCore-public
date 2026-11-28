#if !BROWSER
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Rendering;

// Game-thread storage. Capture while holding the session gate. Map exports, wire
// updates, rank arrays and static core definitions are replace-only on receive.
// Mutable entity wrappers and receiver-owned collection views must not escape.
internal sealed class FlightRenderSnapshot
{
    internal readonly record struct View(ClientWorld Owner, long Mission, long Generation,
        short? Reference, short? Sector, short? Side, short? Hull, short? Station, short? Parent, bool Dead)
    {
        internal static View Read(ClientWorld world, short? reference, short? sector)
        {
            world.Ships.TryGetValue(reference ?? -1, out var ship);
            return new(world, world.MissionGeneration, world.SectorGeneration, reference, sector,
                world.MySideId, ship?.HullId, ship?.StationId, ship?.ParentId, ship?.IsNotVisible ?? false);
        }
    }

    internal readonly record struct Ship(short HullId, short SideId, short WingId, string CharacterName,
        short CarriedFlagSideId, ServerShipUpdate Latest, byte PilotType, bool DetectionEye,
        bool RadarVisible, int? RipSeconds);

    internal readonly record struct Missile(short MissileId, short TypeId, SimVec Position, SimVec Forward,
        short LauncherSide, bool TargetsOwnSide);
    internal readonly record struct Probe(short SideId, short ProbetypeId, SimVec Position, float Fraction, double Time0ClientSeconds);
    internal readonly record struct Mine(short MineId, short SideId, short MinetypeId, SimVec Position, float Strength, double Time0ClientSeconds);
    internal readonly record struct Treasure(short TreasureId, TreasureCode Code, short PayloadId, SimVec Position);

    internal View Identity;
    internal DataClusterIGC? Sky;
    private readonly Dictionary<short, (DataClusterIGC Export, DataClusterIGC Frozen)> _skyExports = [];

    private DataClusterIGC FreezeSky(DataClusterIGC cluster)
    {
        if (_skyExports.TryGetValue(cluster.ClusterId, out var entry) && entry.Export == cluster)
            return entry.Frozen;
        var frozen = cluster with { LightDirection = cluster.LightDirection is { } direction ? (float[])direction.Clone() : [] };
        _skyExports[cluster.ClusterId] = (cluster, frozen);
        return frozen;
    }
    internal readonly Dictionary<short, Ship> Ships = [];
    internal readonly Dictionary<short, DataStationIGC> Stations = [];
    internal readonly Dictionary<short, StationLiveState> StationLive = [];
    internal readonly Dictionary<short, DataAsteroidIGC> Asteroids = [];
    internal readonly Dictionary<short, AsteroidLiveState> AsteroidLive = [];
    internal readonly List<DataWarpIGC> Warps = [];
    internal readonly List<GlimpseTarget> Glimpses = [];
    internal readonly List<Missile> Missiles = [];
    internal readonly List<KeyValuePair<short, Probe>> Probes = [];
    internal readonly List<Mine> Mines = [];
    internal readonly List<Treasure> Treasures = [];
    internal readonly List<ExpandedPartData> OwnLoadout = [];
    internal readonly HashSet<short> DrawShips = [];
    internal readonly Dictionary<short, HudColor> Colors = [];
    internal readonly HashSet<short> Allies = [];
    internal QueuedCommandState? Accepted, Queued;
    internal StaticCore? MissionCore;
    internal TechTreeBitMask? Techs;
    internal float MiningCapacity;
    internal double SimSeconds;
    internal GameOver? Result;
    internal int? Countdown;
    internal IReadOnlyList<RankInfoEntry> Ranks = [];
    internal readonly List<GameOverPlayerEntry> Players = [];
    internal readonly HashSet<string> Commanders = [];

    internal void CaptureHeader(View view)
    {
        var world = view.Owner;
        if (Identity.Owner != view.Owner || Identity.Mission != view.Mission || Identity.Generation != view.Generation)
            _skyExports.Clear();
        Identity = view;
        Sky = view.Sector is { } sector && world.Clusters.TryGetValue(sector, out var cluster) ? FreezeSky(cluster) : null;
        Result = world.GameOverResult;
        Players.Clear();
        Commanders.Clear();
        Ranks = world.RankTable;
        Countdown = null;
        if (Result is null) return;
        Players.AddRange(world.GameOverPlayersList);
        foreach (var ship in world.Ships.Values)
            if (world.TeamLeaderShipIdFor(ship.SideId) is { } leader
                && world.Ships.TryGetValue(leader, out var commander) && commander.CharacterName.Length > 0)
                Commanders.Add(commander.CharacterName);
        Countdown = world.IsIntermission ? world.CountdownRemainingSeconds() : null;
    }

    internal GameOverScreenRenderer.Tables BuildTables(GameOverTablesLayout.SideSort sideSort, GameOverTablesLayout.PlayerSort playerSort)
    {
        var result = Result!;
        var colors = new Dictionary<short, Color>();
        for (int i = 0; i < result.Sides.Length && i < result.NumSides; i++)
        {
            var side = result.Sides[i];
            colors[(short)i] = new Color(side.R, side.G, side.B, side.A);
        }
        short Civ(short side) => side >= 0 && side < result.Sides.Length ? result.Sides[side].CivId : (short)-1;
        return new(GameOverTablesLayout.BuildSides(result.Sides, result.NumSides, sideSort),
            GameOverTablesLayout.BuildPlayers(Players, rank => RankNameLookup.Lookup(Ranks, rank), Civ,
                Commanders, result.EjectPods, playerSort), sideSort, playerSort, colors);
    }

    internal void CaptureSector(bool glimpses)
    {
        var world = Identity.Owner;
        short? sector = Identity.Sector;
        short side = Identity.Side ?? -1;
        Accepted = world.MyAcceptedCommand;
        Queued = world.MyQueuedCommand;
        MissionCore = world.MissionCore;
        Techs = world.DevelopmentTechsFor(side);
        MiningCapacity = world.GlobalAttributesFor(side).Get(GlobalAttributeSet.MiningCapacity);
        SimSeconds = world.ServerClockSeeded ? world.ClientSimulationTimeSeconds : double.NaN;
        Colors.Clear();
        Allies.Clear();
        if (world.SideRoster is { } roster)
            foreach (var s in roster.Sides)
                if (world.TryGetSideColor(s.SideId, out float r, out float g, out float b))
                    Colors[s.SideId] = new HudColor(r, g, b);
        for (short s = 0; s < ChangeAlliances.SideAlliesMax; s++)
            if (world.AreAllied(side, s)) Allies.Add(s);
        Stations.Clear();
        StationLive.Clear();
        foreach (var (id, station) in world.Stations)
            if (station.ClusterId == sector)
            {
                Stations.Add(id, station);
                if (world.StationLive.TryGetValue(id, out var live)) StationLive.Add(id, live);
            }
        Asteroids.Clear();
        AsteroidLive.Clear();
        foreach (var (id, rock) in world.Asteroids)
            if (rock.ClusterId == sector)
            {
                Asteroids.Add(id, rock);
                if (world.AsteroidLive.TryGetValue(id, out var live)) AsteroidLive.Add(id, live);
            }
        Warps.Clear();
        Glimpses.Clear();
        foreach (var warp in world.Warps.Values)
            if (warp.ClusterId == sector)
            {
                Warps.Add(warp);
                if (glimpses && WarpDestinationResolver.DestinationSector(world.Warps, warp.WarpId) is { } dest
                    && world.Clusters.TryGetValue(dest, out var dc))
                    Glimpses.Add(new(warp, dest, FreezeSky(dc), 0));
            }
        Missiles.Clear();
        foreach (var m in world.Missiles.Values)
            if (m.ClusterId == sector)
            {
                bool targetsOwn = m.TargetType switch
                {
                    IgcObjectType.Ship => world.Ships.TryGetValue(m.TargetId, out var tgt) && tgt.SideId == side,
                    IgcObjectType.Station => world.Stations.TryGetValue(m.TargetId, out var st) && st.SideId == side,
                    _ => false
                };
                short launcher = m.LauncherId >= 0 && world.Ships.TryGetValue(m.LauncherId, out var ship) ? ship.SideId : (short)-1;
                Missiles.Add(new(m.MissileId, m.TypeId, m.Position, m.Forward, launcher, targetsOwn));
            }
        Probes.Clear();
        foreach (var (id, p) in world.Probes)
            if (p.ClusterId == sector) Probes.Add(new(id, new(p.SideId, p.ProbetypeId, p.Position, p.Fraction, p.Time0ClientSeconds)));
        Mines.Clear();
        foreach (var m in world.Mines.Values)
            if (m.ClusterId == sector) Mines.Add(new(m.MineId, m.SideId, m.MinetypeId, m.Position, m.Strength, m.Time0ClientSeconds));
        Treasures.Clear();
        foreach (var t in world.Treasures.Values)
            if (t.ClusterId == sector) Treasures.Add(new(t.TreasureId, t.Code, t.PayloadId, t.Position));
    }
}
#endif
