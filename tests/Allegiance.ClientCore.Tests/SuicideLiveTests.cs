using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// Live integration proof for <see cref="ConnectionSession.SendSuicide"/> — the
/// outbound half of <c>FM_C_SUICIDE(80)</c>. The server's <c>HandleSuicide</c>
/// (<c>Server.cs:3869</c>) re-triggers the exact combat-kill path a normal death
/// uses (<c>KillShipEvent</c>, launcher NULL), so sending it must make the
/// suiciding ship die over the wire exactly like a combat death — the same
/// <c>KILL_SHIP(105)</c> the ship's own <see cref="ClientWorld"/> consumes (marks
/// the victim <c>IsDead</c> and records a <c>Kills</c> event).
///
/// This is the same-shape companion to <see cref="PlayerReadyLiveTests"/>
/// (real <see cref="ConnectionSession"/> + real in-process server over loopback).
/// Per the P107 note in <c>LiveSuicideShipDeleteTests</c>, suicide on a
/// NON-lifepod player ship routes to the in-space EJECT branch (native passes
/// amount = 1.0f → bKilled = false), so to make the suicide a real death the
/// ship-under-test rides its side's Lifepod hull from the start — exactly the
/// scenario P107's tests prove is a genuine death, not an eject.
/// </summary>
[Collection("LiveNetwork")]
public class SuicideLiveTests
{
    private static readonly string ArtRoot =
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Suicide Client Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 882u, DwCookie = 0x882u,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 8840000000000000000ul, DrmHash = "suicideclient",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    // ShipSpawner is `internal` and this assembly isn't friend-granted — reach
    // it through reflection, the same recipe PlayerReadyLiveTests uses.
    private static readonly FieldInfo ShipSpawnerField =
        typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Allegiance.Server.Server.ShipSpawner field not found");

    private static void SetShipSpawner(Srv server, Func<StaticCore, Mission, short, (Ship Ship, Sector Sector)> spawner) =>
        ShipSpawnerField.SetValue(server, spawner);

    /// <summary>⭐ The outbound round trip: a real <see cref="ConnectionSession"/>
    /// sends <see cref="ConnectionSession.SendSuicide"/>; the server's
    /// <c>HandleSuicide</c> fires; the suiciding ship dies over the wire and its
    /// OWN <see cref="ClientWorld"/> observes the death (entity flagged
    /// <c>IsDead</c> + a <c>Kills</c> event) — the same KILL_SHIP a normal combat
    /// death produces.</summary>
    [Fact]
    public void SendSuicide_KillsOwnShip_OverTheWire()
    {
        using var server = new Srv();
        // An ALREADY-a-lifepod ship so its suicide is a real death (not an
        // in-space eject) — the P107 note (see class doc). Placed flying in
        // sector 0 (StationId null) so HandleSuicide's gates pass.
        SetShipSpawner(server, (core, mission, shipId) =>
        {
            var hull = core.HullTypes.Values.First(h => h.HasCapability(DataHullTypeIGC.HabmLifepod));
            var ship = new Ship(shipId, hull, new Vector3(750f, 760f, 940f), Vector3.Zero, Orientation.Identity);
            if (mission.Sides.Count > 0) ship.SideId = mission.Sides[0].Id;
            mission.PlaceShip(ship, mission.Sectors[0]);
            return (ship, mission.Sectors[0]);
        });
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // bypass the ready-up lobby, like LiveSuicideShipDeleteTests

        using var session = new ConnectionSession();
        session.Connect(server.BoundPort, "SUICIDER");
        session.SendLogonRequest(Request("SUICIDER"));

        int joined = PumpUntil(server, () => session.World.MyShipId is not null, 4000);
        Assert.True(joined < 4000, "suicider must be assigned a ship");
        short myId = session.World.MyShipId!.Value;

        // Alive before we press the key…
        Assert.False(session.World.Ships[myId].IsDead);

        // …then press it: one fire-and-forget SUICIDE(80) over the real transport.
        session.SendSuicide();

        // HandleSuicide → KillShipEvent → the ship dies over the wire, observed by
        // its own ConnectionSession's ClientWorld (IsDead + a Kills event).
        int died = PumpUntil(server,
            () => session.World.Events.Kills.Any(k => k.ShipId == myId) && session.World.Ships.TryGetValue(myId, out var e) && e.IsDead,
            2000);
        Assert.True(died < 2000, $"suicide never killed ship {myId} as observed by its own ConnectionSession (died={died})");

        Assert.Contains(session.World.Events.Kills, k => k.ShipId == myId);
        Assert.True(session.World.Ships[myId].IsDead);

        Console.WriteLine($"SUICIDE-CLIENT-LIVE: ship {myId} died over the wire after {died} ticks via ConnectionSession.SendSuicide()");
    }
}
