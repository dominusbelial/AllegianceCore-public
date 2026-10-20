using System.Reflection;
using Allegiance.Core;
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
/// P316 — ship/object id allocator hardening. Two halves:
/// <list type="bullet">
/// <item>A live (in-process server + real bot) proof that an unauthenticated
/// flood of <b>invalid</b> LOGONREQs no longer advances <c>_nextShipId</c>
/// (the id is now allocated only <em>after</em> the validation check), and
/// that the allocator skips an id still held by a live ship rather than
/// colliding with it.</item>
/// <item>A focused unit test that the mission object-id allocator
/// (<see cref="Mission.NextObjectId"/>) skips live ids <em>across</em> the
/// 16-bit wraparound boundary — reachable in a handful of allocations by
/// parking the counter at the boundary instead of doing 65,536 real ones.</item>
/// </list>
/// </summary>
public class ShipIdAllocationLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        SideSelectAutoSeatSeconds = 30,   // P763: these clients never send POSITIONREQ
        Games =
        [
            new Gm { Name = "IdAlloc", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Bios", "IronCo"] },
        ],
    };

    private static LogonRequest ValidRequest(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 888u, DwCookie = 0xabc,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 8810000000000000000ul, DrmHash = "ok",
    };

    /// <summary>A LOGONREQ with a wrong client version (FedsrvVer != MsgVer),
    /// so <see cref="LogonRequest.VersionMatches"/> is false — the unauthenticated
    /// / invalid case that previously still burned a ship id.</summary>
    private static LogonRequest InvalidRequest(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = 707, Time = 889u, DwCookie = 0xabc,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 7710000000000000000ul, DrmHash = "bad",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static int GetNextShipId(Srv server) =>
        (int)typeof(Srv).GetField("_nextShipId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;

    private static void SetNextShipId(Srv server, int value) =>
        typeof(Srv).GetField("_nextShipId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(server, value);

    /// <summary>REQUIRED live proof (P316 scope 2, part 1): an unauthenticated
    /// flood of invalid LOGONREQs does not advance <c>_nextShipId</c> — the
    /// id is allocated only after validation, so a raw request cannot burn ids
    /// toward the 65,536 wrap.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Invalid_logon_flood_does_not_advance_nextShipId()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());

        var baseline = GetNextShipId(server);

        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "FLOOD");
        var invalid = InvalidRequest("FLOOD");
        for (int i = 0; i < 300; i++)
            bot.SendLogonRequest(invalid);

        // Pump until at least one invalid logon round-tripped, then keep pumping
        // so the whole queued flood is drained (we intentionally overshoot).
        PumpUntil(server, () => bot.Ack is { FValidated: false }, 20000);
        for (int i = 0; i < 500; i++) server.TickSim();

        Assert.Equal(baseline, GetNextShipId(server));   // no ids burned by the flood
    }

    /// <summary>Positive control for the above: a VALID logon still allocates
    /// (and thus advances) the counter — proving the reflection read/write is
    /// sound and the fix didn't freeze allocation entirely.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Valid_logon_advances_nextShipId()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());

        var baseline = GetNextShipId(server);

        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "VALD");
        bot.SendLogonRequest(ValidRequest("VALD"));

        // P647 — pump deadline was 1000 iterations; under full-suite load the
        // connect → logon → dispatch → ack round-trip can exceed ~1 s (the
        // transport/drain threads are starved by the parallel unit tests), so
        // the pump gave up before the ack arrived and the assertion failed
        // because _nextShipId had not yet advanced. No product race: Boot()
        // assembles missions synchronously (Server.Lifecycle.cs:167-168), so a
        // validated logon ALWAYS calls AllocateShipId() before its ack. Widened
        // to a generous 20000-iteration (~20 s) deadline; the assertion is
        // unchanged.
        PumpUntil(server, () => bot.Ack is { FValidated: true }, 20000);

        Assert.True(baseline < GetNextShipId(server));   // a valid logon consumed an id
    }

    /// <summary>P316 scope 2 (option b) on the server allocator: with the
    /// counter parked on an id that another peer already holds, a newly
    /// validated logon is handed the NEXT id instead of colliding (which would
    /// mis-direct POSITIONACK/ORDER_CHANGE/boarding and stale-hit the
    /// teammate-export cache). Uses the pending-side-select reservation window:
    /// a pending peer's reserved id (<see cref="PendingSideSelect.ShipId"/>,
    /// in <c>_pendingSideSelect</c>) is already "in use" even before its ship
    /// object exists, so the allocator must not hand it to a second logon.
    /// This is deterministic (needs only the fast logon ack, not the slower
    /// full seating handshake) and exercises the real skip loop's reservation
    /// branch.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Allocated_ship_id_skips_an_id_already_reserved_by_another_peer()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());

        // Bot A logs on validly but stays PENDING (never sends POSITIONREQ):
        // its reserved id X is held in _pendingSideSelect.
        using var botA = new FltBot();
        botA.AutoSelectSideAfterLogon = false;
        botA.Connect(server.BoundPort, "RESVA");
        botA.SendLogonRequest(ValidRequest("RESVA"));
        PumpUntil(server, () => botA.Ack is { FValidated: true }, 20000);
        Assert.NotNull(botA.Ack);
        short x = botA.Ack!.ShipID;
        Assert.NotEqual((short)0, x);

        // Park the allocator exactly on x — the next candidate WOULD collide.
        SetNextShipId(server, x);

        // Bot B logs on validly (also pending — we only need its assigned id).
        using var botB = new FltBot();
        botB.AutoSelectSideAfterLogon = false;
        botB.Connect(server.BoundPort, "RESVB");
        botB.SendLogonRequest(ValidRequest("RESVB"));
        PumpUntil(server, () => botB.Ack is { FValidated: true }, 20000);

        short y = botB.Ack!.ShipID;
        Assert.NotEqual(x, y);                       // never collides with the reserved id
        Assert.Equal((short)(x + 1), y);             // skipped forward exactly past it
    }
}

/// <summary>Focused unit test (P316 scope 2, option b) on the mission object-id
/// allocator, using a small reachable id space: park <c>_nextObjectId</c> just
/// before the signed-16-bit wrap and place live models on the boundary ids —
/// the allocator must skip them and return the first free id past the wrap,
/// rather than handing out a duplicate.</summary>
public class ShipIdWraparoundUnitTests
{
    private static readonly Lazy<StaticCore> Core = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            var file = IgcReader.ParseFile(path);
            if (file.Wrapper == IgcWrapper.VersionAndDataSize)
                return StaticCore.Load(file);
        }
        throw new InvalidOperationException("no static core in corpus");
    });

    private static readonly Lazy<(string Path, MissionMap Map)> Map = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            var file = IgcReader.ParseFile(path);
            if (file.Wrapper == IgcWrapper.DataSize)
                return (path, MissionMap.Load(file));
        }
        throw new InvalidOperationException("no map in corpus");
    });

    private static IReadOnlyList<SideSpec> TwoSides()
    {
        var civs = Core.Value.Civilizations.Values.OrderBy(c => c.CivilizationId).Take(2).ToList();
        return civs.Count == 1
            ? [new SideSpec(0, "Team 1", civs[0].CivilizationId)]
            : [new SideSpec(0, "Team 1", civs[0].CivilizationId), new SideSpec(1, "Team 2", civs[1].CivilizationId)];
    }

    private static int GetNextObjectId(Mission m) =>
        (int)typeof(Mission).GetField("_nextObjectId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(m)!;

    private static void SetNextObjectId(Mission m, int value) =>
        typeof(Mission).GetField("_nextObjectId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(m, value);

    /// <summary>A minimal live model carrying a chosen id, so the test can
    /// fabricate a precise "already occupied" set in a sector.</summary>
    private sealed class StubModel : global::Allegiance.Core.Sim.Model
    {
        public StubModel(short id) : base(id, default) { }
        protected override void OnUpdate(double now, double dt) { }
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void NextObjectId_skips_live_models_across_the_wraparound_boundary()
    {
        Require.Artwork();
        var mission = Mission.Assemble(Core.Value, Map.Value.Map, TwoSides(), new NullIgcSite());
        var sector = mission.Sectors[0];

        // (short)65534 = -2, (short)65535 = -1, (short)65536 = 0 — three ids
        // straddling the signed-16-bit wrap that are currently "live".
        short[] liveIds = [(short)-2, (short)-1, (short)0];
        foreach (var id in liveIds)
            sector.Add(new StubModel(id));

        // Park the counter so the next allocations walk exactly across the wrap.
        SetNextObjectId(mission, 65534);

        short first = mission.NextObjectId();
        short second = mission.NextObjectId();

        // Skips -2, -1 and 0 (all live) and returns the first free id past the
        // wrap (1), then 2 — a duplicate id is never handed out.
        Assert.Equal((short)1, first);
        Assert.Equal((short)2, second);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void NextObjectId_sequence_is_unchanged_when_nothing_collides()
    {
        Require.Artwork();
        var mission = Mission.Assemble(Core.Value, Map.Value.Map, TwoSides(), new NullIgcSite());
        var start = GetNextObjectId(mission);

        // With an empty live model set the allocator must hand out the exact
        // same sequence the old unchecked counter produced — no behavioral
        // drift for the common (non-wrapped) case.
        short a = mission.NextObjectId();
        short b = mission.NextObjectId();
        Assert.Equal(unchecked((short)start), a);
        Assert.Equal(unchecked((short)(start + 1)), b);
    }
}
