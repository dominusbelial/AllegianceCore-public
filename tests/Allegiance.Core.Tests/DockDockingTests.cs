using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
// 'Server'/'Bot' are also namespaces — alias to unambiguous identifiers (same
// pattern as WarpTransitServerTests / ShipStatusServerTests).
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;

namespace Allegiance.Core.Tests;

/// <summary>
/// The P53 loopback deliverable — real docking at the wire level, closing the
/// gap P52 found (this server exports real station objects to clients but
/// hardcoded "no docking" into every <c>SHIP_STATUS</c>; Server.cs:414-415).
/// A real <see cref="Srv"/> boots the multi-sector map, a loopback
/// <see cref="FltBot"/> joins, and its ship is spawned inside a <b>friendly</b>
/// station's dock radius. The first sector tick trips the dock pass
/// (<see cref="Sector.ResolveDockCollisions"/>), the host latches the docked
/// state (<see cref="ServerHost.DockWithStationEvent"/> →
/// <see cref="Ship.StationId"/>), and we prove: (a) the ship's server-side
/// docked state is set, and (b) the peer receives a <c>SHIP_STATUS(90)</c>
/// carrying the real <c>StationID</c> and <c>State = c_ssDocked</c> — the whole
/// wire-side visibility riding the existing broadcast (no new message, no new
/// fmid). Then an <c>FM_C_DOCKED(48)</c> with <c>stationID == NA</c> (the
/// undock announce, <c>Messages.h:402-404</c>) clears both on the next
/// broadcast. Hostile (different-side) stations and ships far from any station
/// are separately proven not to trigger. No Windows VM / native client needed —
/// the managed <see cref="FltBot"/> harness is sufficient, matching P41/P51's
/// precedent.
/// </summary>
[Collection("LiveNetwork")]
public class DockDockingTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Dock Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 48u, DwCookie = 0x48u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 48000000000000000ul, DrmHash = "docktest",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static Vector3 StationPos(DataStationIGC st) =>
        new(st.Position[0], st.Position[1], st.Position[2]);

    // ── the codec ───────────────────────────────────────────────────────────

    /// <summary><c>FM_C_DOCKED</c>(48) round-trips exactly — header plus the
    /// single 2-byte <c>StationID</c>, cbmsg = 6 (Messages.h:399-405).</summary>
    [Fact]
    public void Docked_message_round_trips()
    {
        var msg = new Docked { StationId = -1 };
        var d = Docked.Decode(msg.Encode());
        Assert.Equal(Docked.Fmid, d.GetFmid());
        Assert.Equal(-1, d.StationId);

        var at = new Docked { StationId = 1051 };
        var d2 = Docked.Decode(at.Encode());
        Assert.Equal((short)1051, d2.StationId);
    }

    /// <summary><c>FM_C_DOCKED</c>(48) emits the exact 6 declared bytes,
    /// hand-composed from <c>Messages.h:399-405</c> — cbmsg=6, fmid=48, then the
    /// <c>StationID</c> short, no padding.</summary>
    [Fact]
    public void Docked_message_emits_exact_declared_bytes()
    {
        var got = new Docked { StationId = 0x4142 }.Encode();
        Assert.Equal(6, got.Length);
        Assert.Equal(new byte[] { 6, 0, 48, 0, 0x42, 0x41 }, got);
        // Decode is symmetric.
        Assert.Equal((short)0x4142, Docked.Decode(got).StationId);
    }

    // ── the P54 codec (FM_S_DOCKED, fmid 61) ──────────────────────────────

    /// <summary><c>FM_S_DOCKED</c>(61) round-trips exactly — header plus the
    /// single 2-byte <c>StationID</c>, cbmsg = 6 (Messages.h:448-450).</summary>
    [Fact]
    public void DockedNotify_message_round_trips()
    {
        var msg = new DockedNotify { StationId = 1051 };
        var d = DockedNotify.Decode(msg.Encode());
        Assert.Equal(DockedNotify.Fmid, d.GetFmid());
        Assert.Equal((short)1051, d.StationId);

        var na = new DockedNotify { StationId = -1 };
        Assert.Equal((short)-1, DockedNotify.Decode(na.Encode()).StationId);
    }

    /// <summary><c>FM_S_DOCKED</c>(61) emits the exact 6 declared bytes,
    /// hand-composed from <c>Messages.h:448-450</c> — cbmsg=6, fmid=61, then the
    /// <c>StationID</c> short, no padding (same shape as the fmid-48 frame).</summary>
    [Fact]
    public void DockedNotify_message_emits_exact_declared_bytes()
    {
        var got = new DockedNotify { StationId = 0x4142 }.Encode();
        Assert.Equal(6, got.Length);
        Assert.Equal(new byte[] { 6, 0, 61, 0, 0x42, 0x41 }, got);
        // Decode is symmetric.
        Assert.Equal((short)0x4142, DockedNotify.Decode(got).StationId);
    }

    // ── loopback wire (real TCP) ──────────────────────────────────────────

    /// <summary>A spawner that plants the bot's ship inside a chosen station's
    /// dock radius on the chosen side — the controlled hook into placement
    /// (Server.cs:88 ShipSpawner), exactly as the warp tests plant a ship in a
    /// warp's volume. The friendly spawner sits the ship ON the station's own
    /// side; the hostile spawner sits it on the OTHER side but still inside the
    /// radius, so a failed hostile dock is provably a gate failure, not geometry.</summary>
    private static (Ship Ship, Sector Sector) SpawnAt(
        Mission mission, short shipId, DataStationIGC station, bool friendly, Vector3 offset)
    {
        var hull = mission.Core.HullTypes.Values.OrderBy(h => h.HullId).First();
        var sector = mission.Sectors.First(s => s.Stations.Contains(station));
        var ship = new Ship(shipId, hull, StationPos(station) + offset, Vector3.Zero, Orientation.Identity);
        // Friendlies sit on the station's side; hostiles sit on the other
        // (team 2's side id differs on this 2-team map). A side-less ship would
        // never dock either, but we seat a real opposing side so the friendly
        // gate — not absence of a side — is what is exercised.
        ship.SideId = friendly ? station.SideId
                               : (short)(station.SideId == 0 ? 1 : 0);
        mission.PlaceShip(ship, sector);
        return (ship, sector);
    }

    /// <summary>⭐ A ship placed inside a friendly station's dock radius docks:
    /// the server latches <see cref="Ship.StationId"/> and the peer receives a
    /// <c>SHIP_STATUS(90)</c> bearing the real <c>StationID</c> +
    /// <c>State = c_ssDocked</c>. Moving it away afterwards keeps it latched
    /// docked (proving the latch), until an <c>FM_C_DOCKED(48)</c> undock
    /// announce clears both — visible on the next broadcast.</summary>
    [Fact]
    public void FriendlyDock_sets_StationId_and_StateDocked_then_Undock_clears()
    {
        using var server = new Srv();
        DataStationIGC friendly = default;
        Vector3 favpos = default;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            friendly = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0);
            favpos = StationPos(friendly);
            return SpawnAt(mission, shipId, friendly, friendly: true, offset: new Vector3(20f, 0f, 0f));
        };

        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "dock_probe");
        bot.SendLogonRequest(Request("dock_probe"));

        short shipId = bot.ShipAssignment is null
            ? (PumpUntil(server, () => bot.ShipAssignment is not null, 4000), bot.ShipAssignment!.Ship.ShipId).Item2
            : bot.ShipAssignment.Ship.ShipId;

        // (a) server-side proof: the docked state is latched within a tick or two
        // of spawn (the ship is already inside the radius), and the wire carries
        // it — SHIP_STATUS(90) with the real StationId + State = c_ssDocked.
        PumpUntil(server, () => server.Ships.First(s => s.Ship.Id == shipId).Ship.StationId is not null, 200);
        var live = server.Ships.First(s => s.Ship.Id == shipId);
        Assert.NotNull(live.Ship.StationId);
        Assert.Equal(friendly.StationId, live.Ship.StationId!.Value);

        // Wire proof: a SHIP_STATUS(90) arrived carrying the real StationId + docked state.
        PumpUntil(server,
            () => bot.ShipStatuses.Any(s => s.ShipId == shipId && s.StationId == friendly.StationId && s.State == ShipStatusMessage.StateDocked),
            4000);
        Assert.Contains(bot.ShipStatuses,
            s => s.ShipId == shipId && s.StationId == friendly.StationId && s.State == ShipStatusMessage.StateDocked);

        // Move the ship out of the radius. WITHOUT undocking the latched docked
        // state must survive (the sector pass skips already-docked ships) — so a
        // later free status is attributable to the undock, not the move.
        live.Ship.Position = favpos + new Vector3(600f, 0f, 0f);
        Assert.Equal((short)friendly.StationId, live.Ship.StationId!.Value);  // latch holds after moving away

        // Undock: the client sends FM_C_DOCKED(48) with stationID == NA (the
        // pure-undock announce, Messages.h:402-404). Reset statuses so the
        // subsequent free status is unambiguous.
        bot.ResetShipStatuses();
        bot.SendDocked();                       // stationId = -1 = NA
        PumpUntil(server,
            () => bot.ShipStatuses.Any(s => s.ShipId == shipId && s.State == ShipStatusMessage.StateFlying && s.StationId == -1),
            4000);
        Assert.Contains(bot.ShipStatuses,
            s => s.ShipId == shipId && s.StationId == -1 && s.State == ShipStatusMessage.StateFlying);
        Assert.Null(server.Ships.First(s => s.Ship.Id == shipId).Ship.StationId);
    }

    /// <summary>⭐ P54: docking now ALSO emits the dedicated <c>FM_S_DOCKED(61)</c>
    /// notify to the docking peer — the wire trigger that makes the native
    /// client's <c>ship.GetStation()</c> resolve (appmsg.cpp:1113 sets
    /// <c>m_station</c>, which <c>IshipIGC::GetStation()</c> reads,
    /// shipIGC.h:417-420). Proves it is sent <b>exactly once per dock event</b>,
    /// carries the real <c>StationId</c>, and rounds again on a re-dock — so it
    /// is attributable to the transition, not a periodic broadcast.</summary>
    [Fact]
    public void FriendlyDock_sends_exactly_one_DockedNotify_per_event_with_correct_station()
    {
        using var server = new Srv();
        DataStationIGC friendly = default;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            friendly = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0);
            return SpawnAt(mission, shipId, friendly, friendly: true, offset: new Vector3(20f, 0f, 0f));
        };

        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "notify_probe");
        bot.SendLogonRequest(Request("notify_probe"));
        short shipId = bot.ShipAssignment is null
            ? (PumpUntil(server, () => bot.ShipAssignment is not null, 4000), bot.ShipAssignment!.Ship.ShipId).Item2
            : bot.ShipAssignment.Ship.ShipId;

        // The very first dock event (ship is already inside the radius) must
        // yield exactly ONE FM_S_DOCKED(61), carrying the real station id.
        PumpUntil(server, () => bot.DockedNotices.Count >= 1, 8000);
        Assert.Single(bot.DockedNotices);
        Assert.Equal((short)friendly.StationId, bot.DockedNotices[0].StationId);

        // The wire SHIP_STATUS also reflects it (P53's independent proof).
        PumpUntil(server,
            () => bot.ShipStatuses.Any(s => s.ShipId == shipId && s.State == ShipStatusMessage.StateDocked && s.StationId == friendly.StationId),
            4000);
        Assert.Contains(bot.ShipStatuses, s => s.ShipId == shipId && s.State == ShipStatusMessage.StateDocked && s.StationId == friendly.StationId);

        // Undock, then fly back into the radius: a SECOND event must produce
        // exactly ONE more notify — i.e. once per transition, never a stream.
        // (Undock now pushes the ship just outside DockRadius with an outward
        // velocity — Server.cs's FM_C_DOCKED handler, mirroring the native
        // reference's own CstationIGC::Launch — so it no longer re-docks on
        // its own the instant it clears StationId; re-approach it explicitly.)
        bot.ResetDockedNotices();
        bot.SendDocked();   // FM_C_DOCKED(48), stationID == NA
        PumpUntil(server, () => server.Ships.First(s => s.Ship.Id == shipId).Ship.StationId is null, 4000);
        server.Ships.First(s => s.Ship.Id == shipId).Ship.Position = StationPos(friendly) + new Vector3(20f, 0f, 0f);
        server.Ships.First(s => s.Ship.Id == shipId).Ship.Velocity = Vector3.Zero;
        PumpUntil(server, () => bot.DockedNotices.Count >= 1, 8000);
        Assert.Single(bot.DockedNotices);
        Assert.Equal((short)friendly.StationId, bot.DockedNotices[0].StationId);
        Assert.NotNull(server.Ships.First(s => s.Ship.Id == shipId).Ship.StationId);   // latched again
    }

    /// <summary>⭐ A ship parked INSIDE a hostile station's dock radius does not
    /// dock — the friendly-only gate (only same-side stations dock, the task's
    /// explicit scope) is what blocks it, not geometry (the ship is demonstrably
    /// within <see cref="Sector.DockRadius"/> of the hostile station).</summary>
    [Fact]
    public void HostileStation_in_radius_does_not_trigger_dock()
    {
        using var server = new Srv();
        DataStationIGC hostile = default;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            hostile = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0);
            return SpawnAt(mission, shipId, hostile, friendly: false, offset: Vector3.Zero);
        };

        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "hostile_probe");
        bot.SendLogonRequest(Request("hostile_probe"));
        short shipId = bot.ShipAssignment is null
            ? (PumpUntil(server, () => bot.ShipAssignment is not null, 4000), bot.ShipAssignment!.Ship.ShipId).Item2
            : bot.ShipAssignment.Ship.ShipId;

        // The ship is genuinely inside the hostile radius (geometry is not the
        // blocker), yet the dock pass must not fire.
        var sh = server.Ships.First(s => s.Ship.Id == shipId).Ship;
        Assert.True((sh.Position - StationPos(hostile)).Length() <= Sector.DockRadius,
            "test must be inside the hostile dock radius for the gate to be what is proven");

        PumpUntil(server, () => server.Ships.First(s => s.Ship.Id == shipId).Ship.StationId is not null, 120);
        Assert.Null(server.Ships.First(s => s.Ship.Id == shipId).Ship.StationId);   // never docked

        // Wire agrees: every SHIP_STATUS about this ship stayed flying/free.
        PumpUntil(server, () => bot.ShipStatuses.Count >= 3, 4000);
        Assert.DoesNotContain(bot.ShipStatuses,
            s => s.ShipId == shipId && (s.StationId != -1 || s.State == ShipStatusMessage.StateDocked));
    }

    /// <summary>⭐ A ship nowhere near any station (default spawn — the first
    /// sector of this map, Arrokoth, has zero stations) is completely unaffected:
    /// it keeps broadcasting normal flying SHIP_STATUS(90)s with StationID NA,
    /// and its server-side docked state stays null. No regression to ordinary
    /// flight/status broadcasting.</summary>
    [Fact]
    public void Ship_nowhere_near_a_station_is_unaffected()
    {
        using var server = new Srv();   // default spawn: first sector (no stations)
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "free_probe");
        bot.SendLogonRequest(Request("free_probe"));
        short shipId = bot.ShipAssignment is null
            ? (PumpUntil(server, () => bot.ShipAssignment is not null, 4000), bot.ShipAssignment!.Ship.ShipId).Item2
            : bot.ShipAssignment.Ship.ShipId;

        PumpUntil(server, () => bot.ShipStatuses.Count >= 3, 4000);
        // P382: the port now spawns one docked initial miner per side at its home
        // station at boot, so a peer also hears those docked NPCs' SHIP_STATUSs —
        // scope the "ordinary flight unaffected" check to the player's own ship.
        Assert.All(bot.ShipStatuses.Where(s => s.ShipId == shipId),
            s => { Assert.Equal(-1, s.StationId); Assert.Equal(ShipStatusMessage.StateFlying, s.State); });
        Assert.Null(server.Ships.First(s => s.Ship.Id == shipId).Ship.StationId);
    }
}
