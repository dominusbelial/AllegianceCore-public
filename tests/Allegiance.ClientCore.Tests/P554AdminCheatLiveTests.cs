using System.Collections.Generic;
using System.Globalization;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Allegiance.Tools.McpSpawner;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Ls = global::Allegiance.Server.LiveSettingsConfig;
using Ac = global::Allegiance.Server.AdminCheatsConfig;
using Allegiance.Core;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P554 — the config-gated, privileged "relocate" admin cheat on a real
/// (networked) server. Exercises the port of native's cheat-code hook
/// (<c>DoCheatCode</c>, <c>FedSrv.CPP:2814</c>; chat-handler call,
/// <c>FedSrv.CPP:5023</c>) end-to-end over the real wire with a real
/// <see cref="ConnectionSession"/> peer: privilege gating (non-privileged
/// refused + broadcast as normal chat; option disabled refused even for an
/// allow-listed name; privileged accepted), the shared server relocation
/// (<see cref="Srv.RelocateShipToSector"/>, the same path the network cheat
/// uses), SET_CLUSTER delivery, and the client world reflecting the new sector.
/// [Collection("LiveNetwork")] keeps this sequential with the other live tests.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P554AdminCheatLiveTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static Cfg Config(Ac? adminCheats) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        SideSelectAutoSeatSeconds = 30,   // P763: preserve the pre-flag 30 s auto-seat (these peers never send POSITIONREQ)
        LiveSettings = new Ls { DebugMode = true, AdminCheats = adminCheats },
        Games =
        [
            new Gm { Name = "P554 Admin Cheat", Map = "m2xspiderw.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 554u, DwCookie = 0x554u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 5540000000000000000ul, DrmHash = "p554",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static void Flush(Srv server) => PumpUntil(server, () => false, 40);

    /// <summary>Seat a real peer (DebugMode=true auto-seats side 0 in Omega 1053).</summary>
    private static ConnectionSession Seat(Srv server, string name)
    {
        var cs = new ConnectionSession();
        cs.Connect(server.BoundPort, name);
        cs.SendLogonRequest(Request(name));
        int n = PumpUntil(server, () => cs.World.MyShipId is not null, 4000);
        Assert.True(n < 4000, $"{name} not seated");
        return cs;
    }

    private static short CsSector(ConnectionSession cs)
    {
        lock (cs.Gate)
            if (cs.World.MyShipId is { } me && cs.World.Ships.TryGetValue(me, out var e))
                return e.SectorId;
        return -1;
    }

    private static Ship? ServerShipById(Srv server, short id)
    {
        foreach (var kv in server.Ships)
            if (kv.Ship.Id == id) return kv.Ship;
        return null;
    }

    private static Sector? ServerSectorById(Srv server, short id)
    {
        foreach (var m in server.Missions)
            foreach (var s in m.Sectors)
                if (s.Id == id) return s;
        return null;
    }

    private static Vector3 Vec3(float[] v) => v is { Length: >= 3 } ? new Vector3(v[0], v[1], v[2]) : Vector3.Zero;

    /// <summary>Mirror of <c>BotManager.ProbeSectorCenter</c>: the centroid of a
    /// sector's warps and stations from the peer's real world (the same source
    /// the arena mirroring uses).</summary>
    private static Vector3 WorldCenter(ConnectionSession cs, short sector)
    {
        var pts = new List<Vector3>();
        lock (cs.Gate)
        {
            foreach (var w in cs.World.Warps.Values)
                if (w.ClusterId == sector && w.Position is { Length: 3 } p) pts.Add(Vec3(p));
            foreach (var st in cs.World.Stations.Values)
                if (st.ClusterId == sector && st.Position is { Length: 3 } p) pts.Add(Vec3(p));
        }
        if (pts.Count == 0) return Vector3.Zero;
        var c = Vector3.Zero;
        foreach (var p in pts) c += p;
        return c / pts.Count;
    }

    /// <summary>Pick a destination sector for the relocate: any real mission
    /// sector other than the ship's home and other than Alderan (1052, the only
    /// asteroid-rich sector on m2xspiderw — "every other sector has 0 asteroids",
    /// P539d) so the relocation lands in open space and never risks a rock kill.</summary>
    private static short PickTargetSector(Srv server, short home)
    {
        foreach (var m in server.Missions)
            foreach (var s in m.Sectors)
                if (s.Id != home && s.Id != 1052)
                    return s.Id;
        throw new InvalidOperationException($"no non-home, non-rocky sector on the map (home={home})");
    }

    private static string RelocateCmd(short shipId, short sector, Vector3 pos) =>
        string.Create(CultureInfo.InvariantCulture, $"relocate {shipId} {sector} {pos.X:0.###} {pos.Y:0.###} {pos.Z:0.###} 0 0 1");

    // ── Failing-first: non-privileged relocate is refused + broadcast as chat ──

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Non_privileged_relocate_is_refused_and_broadcast_as_normal_chat()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config(null));                                   // adminCheats absent → disabled
        using var cs = Seat(server, "RegularJoe");                   // not allow-listed anywhere
        short shipId = cs.World.MyShipId!.Value;
        short home = CsSector(cs);
        short target = PickTargetSector(server, home);
        var pos = new Vector3(4000f, 4000f, 4000f);

        using var observer = Seat(server, "Observer");               // same side; Everyone chat reaches it
        Flush(server);
        int chatsBefore = observer.World.Events.Chats.Count;

        cs.SendChat(RelocateCmd(shipId, target, pos), ChatTarget.Everyone);
        PumpUntil(server, () => observer.World.Events.Chats.Count > chatsBefore, 400);

        // Refused: the ship never left its home sector.
        Assert.Equal(home, ServerShipById(server, shipId)!.Sector!.Id);
        // Broadcast as normal chat: the observer received the raw line.
        Assert.Contains(observer.World.Events.Chats, c => c.Message is { } m && m.StartsWith("relocate ", StringComparison.Ordinal));
    }

    // ── Failing-first: option disabled → refused even for an allow-listed name ──

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Relocate_refused_when_option_disabled_even_for_allowlisted_name()
    {
        Require.Artwork();
        // Configured (section present, name allow-listed) but Disabled — the
        // "never on in a default config" guarantee: a name in AllowNames still
        // cannot cheat when Enabled=false.
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config(new Ac { Enabled = false, AllowNames = ["admin"] }));
        using var cs = Seat(server, "admin");
        short shipId = cs.World.MyShipId!.Value;
        short home = CsSector(cs);
        short target = PickTargetSector(server, home);
        var pos = new Vector3(4000f, 4000f, 4000f);

        cs.SendChat(RelocateCmd(shipId, target, pos), ChatTarget.Everyone);
        PumpUntil(server, () => false, 120);

        Assert.Equal(home, ServerShipById(server, shipId)!.Sector!.Id);   // never moved
        Assert.Equal(home, CsSector(cs));                                  // client world unchanged
    }

    // ── Failing-first: privileged relocate moves the ship, sends SET_CLUSTER,
    //    and the bot's ClientWorld shows the new sector ──────────────────────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Privileged_relocate_moves_ship_sends_SET_CLUSTER_and_client_world_shows_new_sector()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config(new Ac { Enabled = true, AllowNames = ["mcp-admin"] }));
        using var cs = Seat(server, "mcp-admin");
        short shipId = cs.World.MyShipId!.Value;
        short home = CsSector(cs);
        short target = PickTargetSector(server, home);

        // The proven-safe arena spot: the target's warp/station centroid + 650u
        // (the MCP ArenaOffset), the same placement the P539d arena uses.
        PumpUntil(server, () =>
        {
            lock (cs.Gate) return cs.World.Warps.Count > 0 && cs.World.Stations.Count > 0;
        }, 1000);
        var destPos = WorldCenter(cs, target) + new Vector3(0f, 0f, 650f);
        Assert.NotEqual(home, target);                                 // sanity: a real cross-sector move

        cs.SendChat(RelocateCmd(shipId, target, destPos), ChatTarget.Individual, oidRecipient: shipId);
        // Process the cheat (dispatch drain) + wait for SET_CLUSTER to land in the
        // peer's world.
        int n = PumpUntil(server, () => CsSector(cs) == target, 1000);
        Assert.True(n < 1000, $"client world never showed sector {target} after the privileged relocate (still {CsSector(cs)})");

        // Server-side: the ship physically moved via RelocateShipToSector
        // (the same path the network cheat uses).
        var ship = ServerShipById(server, shipId)!;
        Assert.Equal(target, ship.Sector!.Id);
        Assert.True((ship.Position - destPos).Length() < 1f, $"server position {ship.Position} != {destPos}");
        Assert.Equal(Vector3.Zero, ship.Velocity);                     // velocity zeroed, like a warp exit

        // Client-world: the bot's own peer reflected the new sector (SET_CLUSTER).
        Assert.Equal(target, CsSector(cs));
    }

    // ── Failing-first: MCP arena against a refusing server returns an explicit error ──

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Privileged_admin_relocates_an_mcp_harness_bot_it_launched()
    {
        Require.Artwork();
        // The ownership rule's "launched a bot" leg: an allow-listed admin may
        // relocate an MCP-harness (mcp-prefixed) bot — the exact population the
        // probe drives in the networked arena proof.
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        var config = Config(new Ac { Enabled = true, AllowNames = ["admin"] });
        config.LiveSettings!.TestHarness = new global::Allegiance.Server.TestHarnessConfig { Enabled = true, IgnoreTechGate = false };
        server.Boot(config);
        using var admin = Seat(server, "admin");          // allow-listed sender
        using var bot = Seat(server, "mcp-b001-1234");    // an MCP-harness bot
        short botShip = bot.World.MyShipId!.Value;
        short target = PickTargetSector(server, CsSector(bot));
        PumpUntil(server, () =>
        {
            lock (bot.Gate) return bot.World.Warps.Count > 0 && bot.World.Stations.Count > 0;
        }, 1000);
        var destPos = WorldCenter(bot, target) + new Vector3(0f, 0f, 650f);

        // The admin sends the relocate naming the bot's shipId.
        admin.SendChat(RelocateCmd(botShip, target, destPos), ChatTarget.Individual, oidRecipient: botShip);
        int n = PumpUntil(server, () => CsSector(bot) == target, 1000);
        Assert.True(n < 1000, $"bot world never showed sector {target} after the admin relocated it (still {CsSector(bot)})");
        Assert.Equal(target, ServerShipById(server, botShip)!.Sector!.Id);
        Assert.Equal(target, CsSector(bot));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Privileged_admin_cannot_relocate_a_non_harness_human_ship()
    {
        Require.Artwork();
        // The ownership rule's safety leg: an allow-listed admin may NOT relocate
        // an arbitrary non-harness (human) ship — only own ship or MCP bots.
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config(new Ac { Enabled = true, AllowNames = ["admin"] }));
        using var admin = Seat(server, "admin");          // allow-listed sender
        using var human = Seat(server, "CaptainKirk");    // a non-harness human ship
        short humanShip = human.World.MyShipId!.Value;
        short home = CsSector(human);
        short target = PickTargetSector(server, home);

        admin.SendChat(RelocateCmd(humanShip, target, new Vector3(4000f, 4000f, 4000f)),
            ChatTarget.Individual, oidRecipient: humanShip);
        PumpUntil(server, () => false, 120);

        Assert.Equal(home, ServerShipById(server, humanShip)!.Sector!.Id);   // never moved
        Assert.Equal(home, CsSector(human));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Mcp_arena_against_a_refusing_server_returns_an_explicit_error()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        var config = Config(null);
        // H-06: buy in the real debug hangar with an explicit test-harness
        // tech grant, so this test reaches the intended relocation refusal.
        config.LiveSettings.TestHarness = new global::Allegiance.Server.TestHarnessConfig
            { Enabled = true, Money = 1_000_000, IgnoreTechGate = true };
        server.Boot(config);                                           // adminCheats disabled → server refuses
        var ticker = new Ticker(server);
        try
        {
            using var mgr = new BotManager("127.0.0.1", server.BoundPort);
            Assert.True(mgr.ServerReady, "probe must validate");

            // Drive the arena spawn of ONE combat bot; the networked relocator
            // (installed by default) sends the 'relocate' cheat via the bot's own
            // connection, the server refuses (adminCheats disabled), and the
            // arena path must surface a CLEAR error — never a silent staging.
            var hull = Catalog.DefaultFighterHull();
            var parts = Catalog.NativePreferredLoadout(hull.Id, hull.Name, 0).Parts;
            var ex = Record.Exception(() => mgr.SpawnBot(hull.Id, hull.Name, mgr.ValidSides()[0], parts, "combat",
                targetSector: 1052, arena: true, arenaPosition: new Vector3(0, 0, 650), arenaFacing: new Vector3(0, 0, 1)));
            Assert.NotNull(ex);
            Assert.Contains("REFUSED", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("adminCheats", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ticker.Dispose();
        }
    }

    // ── P602: shared secret — trusted only when the line presents it ───────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Relocate_without_the_secret_is_refused_and_broadcast_as_chat()
    {
        Require.Artwork();
        // Allow-listed name BUT a shared secret is configured: name alone no
        // longer confers trust — the line must present the secret first.
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config(new Ac { Enabled = true, AllowNames = ["mcp-admin"], Secret = "s3cr3t" }));
        using var cs = Seat(server, "mcp-admin");
        short shipId = cs.World.MyShipId!.Value;
        short home = CsSector(cs);
        short target = PickTargetSector(server, home);
        var pos = new Vector3(4000f, 4000f, 4000f);

        using var observer = Seat(server, "Observer");
        Flush(server);
        int chatsBefore = observer.World.Events.Chats.Count;

        // No secret prefix → ordinary chat, never a cheat.
        cs.SendChat(RelocateCmd(shipId, target, pos), ChatTarget.Everyone);
        PumpUntil(server, () => observer.World.Events.Chats.Count > chatsBefore, 400);

        Assert.Equal(home, ServerShipById(server, shipId)!.Sector!.Id);   // never moved
        Assert.Contains(observer.World.Events.Chats, c => c.Message is { } m && m.StartsWith("relocate ", StringComparison.Ordinal));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Relocate_with_the_secret_moves_the_ship()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config(new Ac { Enabled = true, AllowNames = ["mcp-admin"], Secret = "s3cr3t" }));
        using var cs = Seat(server, "mcp-admin");
        short shipId = cs.World.MyShipId!.Value;
        short home = CsSector(cs);
        short target = PickTargetSector(server, home);
        PumpUntil(server, () =>
        {
            lock (cs.Gate) return cs.World.Warps.Count > 0 && cs.World.Stations.Count > 0;
        }, 1000);
        var destPos = WorldCenter(cs, target) + new Vector3(0f, 0f, 650f);
        Assert.NotEqual(home, target);

        // Present the secret as the leading token: "<secret> relocate …".
        string cmd = $"s3cr3t {RelocateCmd(shipId, target, destPos)}";
        cs.SendChat(cmd, ChatTarget.Individual, oidRecipient: shipId);
        int n = PumpUntil(server, () => CsSector(cs) == target, 1000);
        Assert.True(n < 1000, $"client world never showed sector {target} after the secret-bearing relocate");

        var ship = ServerShipById(server, shipId)!;
        Assert.Equal(target, ship.Sector!.Id);
        Assert.True((ship.Position - destPos).Length() < 1f, $"server position {ship.Position} != {destPos}");
    }

    private sealed class Ticker : IDisposable
    {
        private readonly Srv _server;
        private readonly CancellationTokenSource _cts = new();
        private readonly Thread _thread;
        public Ticker(Srv server)
        {
            _server = server;
            _thread = new Thread(Loop) { IsBackground = true, Name = "p554-ticker" };
            _thread.Start();
        }
        private void Loop()
        {
            while (!_cts.IsCancellationRequested)
            {
                try { _server.TickSim(); } catch { }
                Thread.Sleep(4);
            }
        }
        public void Dispose() { _cts.Cancel(); _thread.Join(2000); }
    }
}
