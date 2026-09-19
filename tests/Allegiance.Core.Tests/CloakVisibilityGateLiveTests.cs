using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;

namespace Allegiance.Core.Tests;

/// <summary>
/// P270 — the DATA-LEVEL half of cloak rendering: an enemy client that cannot
/// detect a cloaked ship (P269's cross-side sweep, driven by the cloaked
/// <see cref="Ship.EffectiveSignature"/>) must receive NO data about it at all,
/// not just a transparent render it could still see. The MonoGame client is a
/// pure renderer with no client-side IGC sim, so the server gates its per-peer
/// heavy ship-update broadcast by <see cref="SideVisibilityState.SeenBySide"/>
/// (<c>Server.BroadcastShipsUpdate</c>, the P270 change). Live loopback proof
/// over a real in-process server + two seated bot peers (enemy + teammate):
/// <list type="number">
/// <item><b>Baseline</b> — an uncloaked enemy ship in the same sector reaches
/// the enemy peer's HEAVY updates.</item>
/// <item><b>Cloaked → no data</b> — once cloaked (and beyond the collapsed
/// detection radius) the SAME ship vanishes from the enemy peer's HEAVY
/// updates entirely, while its own-side teammate still receives it — carrying
/// the <c>CloakActive</c> stateM bit so the client knows to render it
/// transparent (P270's renderer key).</item>
/// <item><b>Detectable cloak → still seen</b> — an enemy close enough to
/// detect the cloaked ship (inside the collapsed radius) receives it again:
/// the gate is the real detection mechanic, not \"never send cloaked ships to
/// enemies\".</item>
/// </list>
/// Geometry replicates <see cref="CloakSimTests"/> (faohshcar scanner ↔
/// faohshcar cloaked target, 2000 u apart: uncloaked detection radius ~5700,
/// fully-cloaked collapsed ~332) so uncloaked is seen, cloaked is not.
/// </summary>
[Collection("LiveNetwork")]
public sealed class CloakVisibilityGateLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static DataHullTypeIGC Faohshcar =>
        PayloadDecodeTests.Hulls().First(h => h.V.ModelName == "faohshcar").V;

    /// <summary>The same near-zero-draw cloak as <see cref="CloakSimTests"/> so
    /// a real hull's energy pool never throttles it (isolating the signature
    /// mechanic from the separate energy-throttle behaviour).</summary>
    private static readonly DataPartTypeIGC LowDrawCloak = new(
        Price: 9810, TimeToBuild: 440, ModelName: "bcl1gncr.igc", IconName: "",
        Name: "Cloak Gate Test Cloak", Description: "", GroupId: 0,
        TtbmRequired: [], TtbmEffects: [],
        Mass: 966f, Signature: 0.0f, PartId: 9821, SuccessorPartId: -1, PartMask: 983,
        InventoryLineMdl: "", Type: new DataPartTypeIGC.Cloak(
            EnergyConsumption: 0.005f, MaxCloaking: 0.994f, OnRate: 4.0f, OffRate: 4.0f,
            EngageSound: -1, DisengageSound: -1));

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Cloak Gate Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 977u, DwCookie = 0x977u,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 96100000000000001ul, DrmHash = "cloakgate",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static Vector3 CleanBase(Sector sector, float clearance = 8000f)
    {
        var content = new List<Vector3>();
        foreach (var a in sector.Asteroids)
            if (a.Position is { Length: 3 } p) content.Add(new Vector3(p[0], p[1], p[2]));
        foreach (var st in sector.Stations)
            if (st.Position is { Length: 3 } p) content.Add(new Vector3(p[0], p[1], p[2]));
        float cy = content.Count == 0 ? 0f : content.Average(v => v.Y);
        float cz = content.Count == 0 ? 0f : content.Average(v => v.Z);
        float maxX = content.Count == 0 ? 0f : content.Max(v => v.X);
        return new Vector3(maxX + clearance, cy, cz);
    }

    private sealed class Seat : IDisposable
    {
        public Srv Server = null!;
        public FltBot Enemy = null!, Teammate = null!;
        public Ship EnemyShip = null!, MateShip = null!, Npc = null!;
        public Sector Sector = null!;
        public Mission Mission = null!;
        public Vector3 Base;
        public short EnemySide, MateSide;

        public void Dispose()
        {
            Enemy?.Dispose();
            Teammate?.Dispose();
            Server?.Dispose();
        }
    }

    private static Seat SeatGate()
    {
        var seat = new Seat();
        int spawnIdx = 0;
        seat.Server = new Srv();
        seat.Server.ShipSpawner = (m, mission, id) =>
        {
            seat.Mission = mission;
            seat.Sector = mission.Sectors[0];
            if (spawnIdx == 0) seat.Base = CleanBase(seat.Sector);

            short side = spawnIdx == 0 ? mission.Sides[0].Id : mission.Sides[1].Id;
            var peer = new Ship(id, Faohshcar, seat.Base, Vector3.Zero, Orientation.Identity) { SideId = side };
            mission.PlaceShip(peer, seat.Sector);
            spawnIdx++;
            return (peer, seat.Sector);
        };
        seat.Server.Boot(Config());
        var port = seat.Server.BoundPort;

        try
        {
            seat.Enemy = new FltBot(); seat.Enemy.Connect(port, "p270_enemy");
            seat.Enemy.SendLogonRequest(Request("p270_enemy"));
            seat.Teammate = new FltBot(); seat.Teammate.Connect(port, "p270_mate");
            seat.Teammate.SendLogonRequest(Request("p270_mate"));
            PumpUntil(seat.Server, () => seat.Mission is not null
                && seat.Enemy.ShipAssignment is not null && seat.Teammate.ShipAssignment is not null, 5000);

            seat.EnemyShip = seat.Server.Ships.First(s => s.Ship.Id == seat.Enemy.ShipAssignment!.Ship.ShipId).Ship;
            seat.MateShip = seat.Server.Ships.First(s => s.Ship.Id == seat.Teammate.ShipAssignment!.Ship.ShipId).Ship;
            Assert.NotEqual(seat.EnemyShip.SideId, seat.MateShip.SideId);
            seat.EnemySide = seat.EnemyShip.SideId!.Value;
            seat.MateSide = seat.MateShip.SideId!.Value;

            // The cloaked NPC on the TEAMMATE's side, 2000 u from the enemy observer
            // (inside uncloaked detection ~5700, beyond the cloaked collapse ~332).
            seat.Npc = seat.Server.SpawnNpcShip(seat.Mission, seat.Sector, seat.MateSide, seat.Base + new Vector3(2000f, 0f, 0f));
            seat.Npc.ChangeHull(Faohshcar);
            seat.Npc.MountPart(LowDrawCloak, 1, mountId: 0, fractionMounted: 255);
            // ChangeHull swaps only the hull record (Ship.cs:86-89); the energy
            // pool stays what the SPAWN hull seeded it to (the first-by-id hull,
            // often ~0 — the P269 r² throttle would then keep Cloaking high and
            // the ship detectable). Give the cloak a real pool so it reaches full
            // strength, like the fight-hull live harness tops up energy.
            seat.Npc.Energy = Faohshcar.MaxEnergy * 100f;

            // The mission only drives Sector.Update (and thus the P132 cross-side
            // visibility sweep that P270's broadcast gate reads) once it has
            // started (Mission.Update early-returns on NotStarted, Mission.cs:438).
            // Force it to Started — the same direct-stage idiom as the suicide /
            // cloak-sim live tests — so the sweep runs every tick thereafter.
            seat.Mission.Stage = MissionStage.Started;
            return seat;
        }
        catch
        {
            seat.Dispose();
            throw;
        }
    }

    private static bool BotSaw(FltBot b, short id) =>
        b.HeavyShipsUpdates.SelectMany(u => u.HeavyShipEntries).Any(e => e.ShipId == id);

    private static bool BotSawCloaked(FltBot b, short id) =>
        b.HeavyShipsUpdates.SelectMany(u => u.HeavyShipEntries)
            .Any(e => e.ShipId == id && (e.StateM & ShipControlState.CloakActive) != 0);

    [Fact]
    public void Cloaked_enemy_receives_no_data_while_teammate_still_sees_it_transparent()
    {
        using var seat = SeatGate();

        // 0) Baseline — uncloaked enemy reaches the enemy peer's HEAVY updates.
        PumpUntil(seat.Server, () => BotSaw(seat.Enemy, seat.Npc.Id), 4000);
        Assert.True(BotSaw(seat.Enemy, seat.Npc.Id), "uncloaked in-range enemy must be broadcast to the enemy peer");

        // 1) Engage the cloak and let the ramp converge; verify the P269 sweep flips.
        seat.Npc.StateM |= ShipControlState.CloakActive;
        PumpUntil(seat.Server, () => seat.Npc.IsCloaked && seat.Npc.Cloaking < 0.05f, 3000);
        Assert.True(seat.Npc.IsCloaked);
        Assert.True(seat.Npc.Cloaking < 0.05f, $"cloak must reach near-full (Cloaking={seat.Npc.Cloaking:F3})");
        Assert.False(seat.Npc.SeenBySide(seat.EnemySide), "cloaked enemy must be undetectable to the enemy side");
        Assert.True(seat.Npc.SeenBySide(seat.MateSide), "own side always sees its cloaked ship");

        // 2) Clear both bots' histories, pump a window of heavy updates.
        seat.Enemy.ResetHeavyShipsUpdates();
        seat.Teammate.ResetHeavyShipsUpdates();
        PumpUntil(seat.Server, () => seat.Teammate.HeavyShipsUpdates.Count >= 10, 6000);

        // The enemy receives NO data about the cloaked ship at all…
        Assert.False(BotSaw(seat.Enemy, seat.Npc.Id),
            "the enemy peer must receive ZERO heavy-update entries for an undetectable cloaked ship");
        // …while the teammate still receives it, carrying the CloakActive bit so
        // the client renders it transparent (P270).
        Assert.True(BotSaw(seat.Teammate, seat.Npc.Id),
            "the cloaked ship must still reach its own-side teammate");
        Assert.True(BotSawCloaked(seat.Teammate, seat.Npc.Id),
            "the teammate's received entry must carry the CloakActive stateM bit (the renderer's transparency key)");

        Console.WriteLine($"P270-GATE: cloaked NPC {seat.Npc.Id} side {seat.MateSide} at " +
            $"{seat.Npc.Position.Length():F0}u — enemy side {seat.EnemySide} got 0 entries, " +
            $"mate side {seat.MateSide} got the entry + cloak bit {ShipControlState.CloakActive:x}");
    }

    [Fact]
    public void Detectable_enemy_still_receives_the_cloaked_ship_inside_the_collapsed_radius()
    {
        using var seat = SeatGate();

        seat.Npc.StateM |= ShipControlState.CloakActive;
        PumpUntil(seat.Server, () => seat.Npc.IsCloaked && seat.Npc.Cloaking < 0.05f, 3000);
        Assert.False(seat.Npc.SeenBySide(seat.EnemySide), "precondition: cloaked + beyond collapsed radius = undetectable");

        seat.Enemy.ResetHeavyShipsUpdates();
        PumpUntil(seat.Server, () => seat.Enemy.HeavyShipsUpdates.Count >= 6, 4000);
        Assert.False(BotSaw(seat.Enemy, seat.Npc.Id), "still no data while beyond the collapsed radius");

        // Close the enemy observer to within the collapsed detection radius (~332 u):
        // now the P269 sweep flips SeenBySide true again and the enemy must receive
        // the (cloaked, transparent) ship once more.
        seat.EnemyShip.Position = seat.Npc.Position + new Vector3(100f, 0f, 0f);
        PumpUntil(seat.Server, () => seat.Npc.SeenBySide(seat.EnemySide), 3000);
        Assert.True(seat.Npc.SeenBySide(seat.EnemySide),
            "an enemy within the collapsed radius must detect the cloaked ship");

        seat.Enemy.ResetHeavyShipsUpdates();
        PumpUntil(seat.Server, () => BotSaw(seat.Enemy, seat.Npc.Id), 5000);
        Assert.True(BotSaw(seat.Enemy, seat.Npc.Id),
            "a detectable cloaked ship must be sent to the enemy (it renders transparent) — the gate is the detection mechanic");
        Assert.True(BotSawCloaked(seat.Enemy, seat.Npc.Id),
            "and the detectable enemy's entry carries the CloakActive bit (so it renders the cloak visually)");

        Console.WriteLine($"P270-DETECT: enemy {seat.EnemySide} within 100u of cloaked {seat.Npc.Id} — " +
            $"SeenBySide flipped, HEAVY entry + cloak bit received again");
    }
}
