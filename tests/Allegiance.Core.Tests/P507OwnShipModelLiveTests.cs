using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P507 — live proof of the server side of the NATIVE own-ship model
/// (part 1, server): the server accepts the peer's reported pose
/// (<c>ProcessShipUpdate</c>), enforces the per-player cookie, sends
/// <c>SHIP_RESET(183)</c> with a fresh cookie on rejection, and keeps LEGACY
/// (Time=0 / zero-pose) clients working controls-only.
///
/// Faithful native references: FedSrv.CPP:4851-4864 (cookie gate + in-cluster /
/// no-parent), fsship.cpp:1173-1200 (SetShipUpdate + SHIP_RESET), shipIGC.cpp:
/// 3139-3210 (ProcessShipUpdate: ±1.5s window, LegalPosition, CalculateShip,
/// always-accept orientation, d&lt;400 accept).
///
/// The subject ship is a <b>zero-thrust</b> hull so the server simulation holds
/// it exactly at <see cref="Spawn"/> — the only moving part in each scenario is
/// the pose the client reports, making the accept/reject/out-of-date/invalid
/// outcomes deterministic.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P507OwnShipModelLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static StaticCore Core()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    // DemoHull numbers (same as BotFlightTests) but Thrust forced to 0 so the
    // ship never accelerates from its spawn — a stationary subject.
    private static readonly Lazy<DataHullTypeIGC> StationaryHull = new(() => new(
        Price: 0, TimeToBuild: 0, ModelName: "mdl", IconName: "ico", Name: "stat",
        Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
        Mass: 535f, Signature: 0f, Speed: 120f,
        MaxTurnRates: [4.666f, 4.444f, 5.022f],
        TurnTorques: [5100f, 4900f, 4700f],
        Thrust: 0f, SideMultiplier: 4f, BackMultiplier: 4f, ScannerRange: 950f,
        MaxFuel: 100f, Ecm: 0f, Length: 30f, MaxEnergy: 0f, RechargeRate: 0f,
        RipcordSpeed: 0f, RipcordCost: 0f, MaxAmmo: 0, HullId: 509, SuccessorHullId: -1,
        MaxWeapons: 0, MaxFixedWeapons: 0, HitPoints: 515f, HardpointOffset: 560,
        DefenseType: 0, CapacityMagazine: 0, CapacityDispenser: 0, CapacityChaffLauncher: 0,
        PreferredPartsTypes: [], HabmCapabilities: 0, TextureName: "", PmEquipment: [],
        InteriorSound: 0, ExteriorSound: 0, MainThrusterInteriorSound: 0,
        MainThrusterExteriorSound: 0, ManuveringThrusterInteriorSound: 0,
        ManuveringThrusterExteriorSound: 0, Hardpoints: []));

    private static readonly Vector3 Spawn = new(500f, 620f, 830f);

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm
            {
                Name = "P507 Test", Map = "XXV_simple.igc", Teams = 2,
                CivIds = [1, 3], SideNames = ["Alpha", "Bravo"],
            },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 777u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 82000000000000000ul, DrmHash = "p507",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static float Dist(Vector3 a, Vector3 b) => (a - b).Length();

    /// <summary>Boot a server that places the peer's own ship (stationary hull at
    /// <see cref="Spawn"/>) and seat one bot. Returns the server, the held Ship
    /// reference, and the bot's SET_CLUSTER (which carries the P507 per-peer
    /// cookie the client must echo).</summary>
    private sealed class Seated : IDisposable
    {
        public Srv Server = null!;
        public Ship Ship = null!;
        public FltBot Bot = null!;
        public SetCluster Assignment = null!;

        public void Dispose()
        {
            Bot?.Dispose();
            Server?.Dispose();
        }
    }

    private static Seated SeatOne()
    {
        var s = new Seated();
        s.Server = new Srv();
        Ship? placed = null;
        s.Server.ShipSpawner = (core, mission, id) =>
        {
            var ship = new Ship(id, StationaryHull.Value, Spawn, Vector3.Zero, Orientation.Identity);
            mission.PlaceShip(ship, mission.Sectors[0]);
            placed = ship;
            return (ship, mission.Sectors[0]);
        };
        s.Server.Boot(Config());
        foreach (var m in s.Server.Missions) m.Stage = MissionStage.Started;   // P180: flyable immediately

        var bot = new FltBot();
        s.Bot = bot;
        bot.Connect(s.Server.BoundPort, "P507");
        bot.SendLogonRequest(Request("P507"));
        var boot = PumpUntil(s.Server, () => placed is not null, 3000);
        Assert.True(boot < 3000, "ship was never placed within the bootstrap window");
        s.Ship = placed!;

        var assignment = bot.WaitForShipAssignment(5000);
        Assert.NotNull(assignment);
        s.Assignment = assignment!;
        return s;
    }

    /// <summary>Send a native-style pose update from the bot at ~server-now,
    /// with the given reported position/velocity and cookie.</summary>
    private static void SendPose(Seated s, Vector3 pos, byte cookie, uint timeOffsetMs = 0,
        Vector3? vel = null, uint? timeOverrideMs = null)
    {
        uint nowMs = timeOverrideMs ?? (uint)(s.Server.SimNow * 1000.0) + timeOffsetMs;
        s.Bot.SendOwnShipPose(nowMs, cookie, pos, vel ?? Vector3.Zero, Orientation.Identity,
            ControlData.Neutral, 0, new float[3]);
    }

    // ── ACCEPT ────────────────────────────────────────────────────────────────
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Native_pose_within_20m_is_accepted_and_applied()
    {
        Require.Artwork();
        using var s = SeatOne();
        var setCookie = s.Assignment.Cookie;               // P507 per-peer cookie from SET_CLUSTER
        var target = Spawn + new Vector3(0f, 0f, 10f);     // 10 m from the server's stationary pos

        SendPose(s, target, setCookie);

        var pumped = PumpUntil(s.Server, () => Dist(s.Ship.Position, target) < 8f, 200);
        Assert.True(pumped < 200, $"accepted pose was not applied; ship at {s.Ship.Position}, target {target}, dist {Dist(s.Ship.Position, target):0.##}");
        Assert.True(Dist(s.Ship.Position, target) < 8f,
            $"native pose was not accepted/applied: expected ≈{target}, got {s.Ship.Position} (dist {Dist(s.Ship.Position, target):0.##})");
    }

    // ── REJECT → SHIP_RESET + new cookie ──────────────────────────────────────
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Native_pose_over_20m_is_rejected_and_sends_SHIP_RESET_with_new_cookie()
    {
        Require.Artwork();
        using var s = SeatOne();
        var setCookie = s.Assignment.Cookie;
        var basePos = s.Ship.Position;

        // A wild pose 1000 m away — must be rejected, ship must not jump there.
        SendPose(s, Spawn + new Vector3(0f, 0f, 1000f), setCookie);

        var settled = PumpUntil(s.Server, () => s.Bot.ShipResets.Count >= 1, 200);
        Assert.True(settled < 200, "no SHIP_RESET was sent on rejection");
        Assert.True(Dist(s.Ship.Position, basePos) < 5f,
            $"rejected pose was (wrongly) applied: ship jumped to {s.Ship.Position} from {basePos}");

        // The SHIP_RESET carries a FRESH cookie; the old cookie must no longer be valid.
        byte newCookie = s.Bot.ShipResets[^1].Cookie;
        Assert.NotEqual(setCookie, newCookie);

        // An update echoing the NEW cookie (within 20 m) is accepted — proving the
        // reset re-armed the pipeline.
        var target = Spawn + new Vector3(0f, 0f, 5f);
        SendPose(s, target, newCookie);
        var rearmed = PumpUntil(s.Server, () => Dist(s.Ship.Position, target) < 8f, 200);
        Assert.True(rearmed < 200, "update echoing the new SHIP_RESET cookie was not accepted");
    }

    // ── COOKIE MISMATCH ───────────────────────────────────────────────────────
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Wrong_cookie_drops_the_update_whole()
    {
        Require.Artwork();
        using var s = SeatOne();
        var basePos = s.Ship.Position;
        // Within the 20 m window — would be accepted if the cookie matched — but the
        // cookie is wrong, so native drops the whole update (no controls, no pose).
        SendPose(s, Spawn + new Vector3(0f, 0f, 5f), (byte)(s.Assignment.Cookie + 7));

        Thread.Sleep(30);
        for (int i = 0; i < 20 && Dist(s.Ship.Position, basePos) < 5f; i++) s.Server.TickSim();

        Assert.True(Dist(s.Ship.Position, basePos) < 5f,
            $"a wrong-cookie update was applied (should be dropped): {basePos} -> {s.Ship.Position}");
        Assert.Empty(s.Bot.ShipResets);   // and certainly no SHIP_RESET
    }

    // ── OUT OF DATE ───────────────────────────────────────────────────────────
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Stale_update_is_out_of_date_pose_ignored()
    {
        Require.Artwork();
        using var s = SeatOne();
        var basePos = s.Ship.Position;
        // time 5 s in the past → deltaT ≈ 5 s > ±1.5 s window → c_susOutOfDate.
        SendPose(s, Spawn + new Vector3(0f, 0f, 5f), s.Assignment.Cookie, timeOverrideMs: (uint)(s.Server.SimNow * 1000.0) - 5000);

        Thread.Sleep(30);
        for (int i = 0; i < 20 && Dist(s.Ship.Position, basePos) < 5f; i++) s.Server.TickSim();

        Assert.True(Dist(s.Ship.Position, basePos) < 5f,
            $"an out-of-date pose was applied: {basePos} -> {s.Ship.Position}");
        Assert.Empty(s.Bot.ShipResets);
    }

    // ── INVALID POSITION ──────────────────────────────────────────────────────
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Illegal_position_is_invalid_pose_ignored()
    {
        Require.Artwork();
        using var s = SeatOne();
        var basePos = s.Ship.Position;
        // |pos|² == (90000)² fails LegalPosition (0 < |pos|² < (90000)²) → c_susInvalid.
        SendPose(s, new Vector3(95000f, 0f, 0f), s.Assignment.Cookie);

        Thread.Sleep(30);
        for (int i = 0; i < 20 && Dist(s.Ship.Position, basePos) < 5f; i++) s.Server.TickSim();

        Assert.True(Dist(s.Ship.Position, basePos) < 5f,
            $"an illegal position was applied: {basePos} -> {s.Ship.Position}");
        Assert.Empty(s.Bot.ShipResets);
    }

    // ── LEGACY BACKWARD COMPAT ────────────────────────────────────────────────
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Timed_origin_does_not_bypass_the_native_cookie_gate()
    {
        Require.Artwork();
        using var s = SeatOne();
        var controls = new ControlData(.75f, 0, 0, -1);
        // Only explicit Time=0 commands are legacy. A timed pose at the
        // origin must obey cookie validation even outside local training.
        s.Bot.SendOwnShipPose((uint)(s.Server.SimNow * 1000),
            (byte)(s.Assignment.Cookie ^ 0x80), Vector3.Zero, Vector3.Zero,
            Orientation.Identity, controls, 0, new float[3]);
        Thread.Sleep(30);
        for (int i = 0; i < 20; i++) { s.Server.TickSim(); Thread.Sleep(1); }
        Assert.Equal(0, s.Ship.Controls.Yaw);
        Assert.Equal(Spawn, s.Ship.Position);
        Assert.Empty(s.Bot.ShipResets);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Legacy_Time0_zero_pose_update_still_applies_controls()
    {
        Require.Artwork();
        using var s = SeatOne();
        // CommandControls sends Time=0 / zero pose / Ack-derived cookie (≠ peer cookie).
        // The legacy detector must bypass the cookie gate and apply controls, as before P507.
        // Axes are 7-bit-quantized on the wire (char/127), so assert the command clearly
        // took effect (moved off neutral) with a quantization-tolerant band.
        var cmd = new ControlData(0.5f, -0.25f, 0.333f, 0f);
        s.Bot.CommandControls(cmd, 0);

        var applied = PumpUntil(s.Server,
            () => MathF.Abs(s.Ship.Controls.Yaw - cmd.Yaw) < 0.05f
               && MathF.Abs(s.Ship.Controls.Pitch - cmd.Pitch) < 0.05f
               && MathF.Abs(s.Ship.Controls.Roll - cmd.Roll) < 0.05f, 200);
        Assert.True(applied < 200, "legacy (Time=0) control input was NOT applied — cookie gate wrongly blocked it");
    }

    // ── WIRE CODECK ───────────────────────────────────────────────────────────
    /// <summary>SHIP_RESET(183) = 4-byte header + 47-byte ServerSingleShipUpdate +
    /// 1-byte cookie, packed (no alignment pad), 52 bytes total — the same
    /// no-padding invariant as SET_CLUSTER (P36) / SHIP_UPDATE (P33).</summary>
    [Fact]
    public void ShipReset_round_trips_on_the_wire()
    {
        var ship = new Ship((short)123, StationaryHull.Value, Spawn, Vector3.Zero, Orientation.Identity);
        var msg = new ShipReset
        {
            Ship = ServerShipUpdate.FromModel(ship.Id, ship, 4321u),
            Cookie = 0xAB,
        };

        var bytes = msg.Encode();
        Assert.Equal(ShipReset.FixedSize, bytes.Length);          // 52
        Assert.Equal(183, FedMessageHeader.Read(bytes).Fmid);
        Assert.Equal(bytes.Length, FedMessageHeader.Read(bytes).CbMsg);

        var back = ShipReset.Decode(bytes);
        Assert.Equal((byte)0xAB, back.Cookie);
        Assert.Equal(ship.Id, back.Ship.ShipId);
        Assert.True(Dist(back.Ship.Position, Spawn) < 1f, "exported position survived the round trip");
    }
}
