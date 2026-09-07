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
/// THE ONE-COHERENT-SESSION INTEGRATION TEST — everything the client has
/// learned to do, done together in one continuous live session the way a real
/// client would, with no test-only isolation between phases. Two real
/// <see cref="ConnectionSession"/>s (a subject and a same-side observer),
/// seeded onto the same friendly station so both can dock → loadout → launch,
/// are driven through, in order, asserting real <see cref="ClientWorld"/> state
/// after every phase:
///
///   1. join        → both get their SET_CLUSTER assignment and appear in each
///                    other's <c>Ships</c> (the same-side EXPORT), not just
///                    their own.
///   2. ready       → subject's <c>SendPlayerReady</c> relay updates BOTH
///                    peers' <see cref="TeamRoster"/>.
///   3. loadout     → subject buys a real non-hull-only loadout; its
///                    <c>ShipEntity.Loadout</c> carries the real mounted part.
///   4. launch      → the same BUY_LOADOUT with fLaunch=true undocks the ship
///                    (a real client arms AND launches in one click — a second
///                    hull-only relaunch would wipe the just-mounted parts, so
///                    the phases deliberately share one message, mirroring
///                    HangarScreen::OnButtonLaunch's own call shape).
///   5. movement    → subject commands real thrust; the OBSERVER's entity
///                    table streams the teammate's position through ≥3 distinct
///                    observations (the P85 broadcast, composed here with
///                    everything that happened before).
///   6. chat        → subject's <c>SendChat</c> lands in the observer's
///                    <c>EventLog.Chats</c>.
///
/// After every phase we re-assert the earlier invariants still hold — the
/// whole point being that no phase regresses another. Step 7 (a real kill
/// between the two peers) is deliberately NOT forced in: see the note below.
/// </summary>
/// <remarks>
/// <b>Why the full-session test stops at step 6 (the kill).</b> The task asks
/// for a real kill "between the two peers, matching <c>KillShipLiveTests</c>'s
/// own mechanism" — but that mechanism is a <em>cross-side</em> shootout, and
/// steps 1–6 here REQUIRE the two peers to be <em>same-side</em>:
/// a cross-side enemy never even enters the observer's entity table
/// (<c>TeammateMovementLiveTests</c> proves <c>DoesNotContain(enemyId)</c>),
/// so it cannot be the seen-and-moved teammate of phases 1, 5; meanwhile the
/// server's collision pass enforces a friendly-fire gate — a projectile can
/// never damage a same-side target (<c>Projectile.cs</c>,
/// <c>Sector.ResolveCombatCollisions</c>), so a same-side pair can never
/// resolve a real KILL_SHIP. Two fixed same-side peers therefore make a real
/// kill structurally impossible in this session without breaking phases 1-6.
/// A resolvable kill needs a third, cross-side attacker (making sides no
/// longer the fixed "the two peers"), which the task's budget rightly flags as
/// disproportionate here. So the test proves the coherent same-side session
/// (steps 1-6) and leaves the composed cross-side kill as a natural follow-up:
/// the kill itself is already proven in isolation by <c>KillShipLiveTests</c>.
/// </remarks>
[Collection("LiveNetwork")]
public class FullSessionIntegrationTests
{
    private const string ArtRoot = "/home/domingo/Development/Artwork";
    private const string Map = "XXV_simple.igc";

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Full Session", Map = Map, Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 617u, DwCookie = 0x19FCu,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 86000000000000000ul, DrmHash = "fullsession",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    // The ShipSpawner hook is `internal` (friend-gated) — reach via reflection,
    // the same recipe DockLoadoutLiveTests/PlayerReadyLiveTests use. Seeding it
    // places every joining ship on a friendly station's side inside DockRadius,
    // giving us a guaranteed same-side pair that can genuinely dock → load →
    // launch.
    private static readonly FieldInfo ShipSpawnerField =
        typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Allegiance.Server.Server.ShipSpawner field not found");

    private static void SetShipSpawner(Srv server, Func<StaticCore, Mission, short, (Ship Ship, Sector Sector)> spawner) =>
        ShipSpawnerField.SetValue(server, spawner);

    private static (Ship Ship, Sector Sector) SpawnDocked(
        StaticCore core, Mission mission, short shipId, out short stationId)
    {
        var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
        var st = mission.Sectors.SelectMany(s => s.Stations).First(s => s.SideId >= 0);
        var sector = mission.Sectors.First(s => s.Stations.Contains(st));
        var ship = new Ship(shipId, hull, new Vector3(20f, 0f, 0f) + StationPos(st), Vector3.Zero, Orientation.Identity)
        {
            SideId = st.SideId,
        };
        mission.PlaceShip(ship, sector);
        stationId = st.StationId;
        return (ship, sector);
    }

    private static Vector3 StationPos(DataStationIGC st) =>
        new(st.Position[0], st.Position[1], st.Position[2]);

    // The representative static core the server boots (PCore001.igc under
    // Artwork), parsed once — used to pick a REAL weapon part id, the same way
    // DockLoadoutLiveTests does (resolvable by the server's ApplyLoadout).
    private static readonly StaticCore RepresentativeCore =
        StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));

    /// <summary>The first real weapon part id in the representative core,
    /// ordered by PartId — always resolvable by the server's ApplyLoadout.</summary>
    private static ExpandedPartData RealWeaponPart() =>
        new(
            RepresentativeCore.PartTypes.Values
                .Where(p => p.EquipmentType == EquipmentType.Weapon)
                .OrderBy(p => p.PartId)
                .First().PartId,
            Amount: 20, MountId: 0, FractionMounted: ExpandedPartData.FullyMounted);

    /// <summary>⭐ THE composed end-to-end proof: one real server, two real
    /// same-side peers, one continuous session through join → ready → real
    /// loadout+launch → teammate movement → chat, with every earlier invariant
    /// re-asserted after every phase.</summary>
    [Fact]
    public void FullSession_Join_Ready_Loadout_Launch_Movement_Chat_Compose()
    {
        using var server = new Srv();
        short friendlyStation = -1;
        SetShipSpawner(server, (core, mission, shipId) =>
        {
            var r = SpawnDocked(core, mission, shipId, out short st);
            friendlyStation = st;
            return r;
        });
        server.Boot(Config());

        using var subject = new ConnectionSession();
        using var observer = new ConnectionSession();
        subject.Connect(server.BoundPort, "fs_subject");
        observer.Connect(server.BoundPort, "fs_observer");
        subject.SendLogonRequest(Request("fs_subject"));
        observer.SendLogonRequest(Request("fs_observer"));

        short subjectId, observerId;
        static short Assigned(ConnectionSession s) => s.World.MyShipId!.Value;

        // ── PHASE 1: join — both assigned, each sees the other ──────────────
        int join = PumpUntil(server, () => subject.World.MyShipId is not null && observer.World.MyShipId is not null, 4000);
        Assert.True(join < 4000, "both peers must be assigned a ship");
        subjectId = Assigned(subject);
        observerId = Assigned(observer);
        Assert.NotEqual(subjectId, observerId);

        // The seeded spawner guarantees same-side — read it from each peer's
        // own EXPORT-derived SideId once the teammate export lands.
        int exported = PumpUntil(server,
            () => subject.World.Ships.ContainsKey(observerId) && observer.World.Ships.ContainsKey(subjectId),
            4000);
        Assert.True(exported < 4000, "same-side peers must learn each other's ship from EXPORT");
        Assert.True(subject.World.Ships.ContainsKey(observerId) && observer.World.Ships.ContainsKey(subjectId),
            "mutual teammate presence after join");
        Assert.False(subject.World.Ships[observerId].IsMine);
        Assert.False(observer.World.Ships[subjectId].IsMine);
        Assert.Equal(subject.World.Ships[observerId].SideId, observer.World.Ships[observerId].SideId);
        // A "reads" observer's logon name from its EXPORT — proof it saw the real teammate.
        Assert.Equal("fs_observer", subject.World.Ships[observerId].CharacterName);
        Assert.Equal("fs_subject", observer.World.Ships[subjectId].CharacterName);

        // ── PHASE 2: ready — subject's toggle lands on BOTH rosters ─────────
        subject.SendPlayerReady(fReady: true, shipId: subjectId);
        int relayed = PumpUntil(server,
            () => subject.World.Roster.ReadyByShipId.TryGetValue(subjectId, out var r1) && r1 &&
                  observer.World.Roster.ReadyByShipId.TryGetValue(subjectId, out var r2) && r2,
            4000);
        Assert.True(relayed < 4000, "ready relay never reached both peers' rosters");
        Assert.True(subject.World.Roster.ReadyByShipId[subjectId]);
        Assert.True(observer.World.Roster.ReadyByShipId[subjectId]);

        // ── PHASES 3+4: real loadout AND launch in one BUY_LOADOUT(fLaunch=true)
        // ────────────────────────────────────────────────────────────────────
        short hull = subject.World.Ships[subjectId].HullId;
        Assert.NotEqual((short)0, hull);

        // Dock it first (both were spawned inside DockRadius) — required before
        // the launch phase can undock anything.
        int docked = PumpUntil(server, () => subject.World.Ships[subjectId].StationId != -1, 8000);
        Assert.True(docked < 8000, $"subject never docked (StationId={subject.World.Ships[subjectId].StationId})");
        Assert.Equal(friendlyStation, subject.World.Ships[subjectId].StationId);
        Assert.Empty(subject.World.Ships[subjectId].Loadout!);   // join-burst adopt-a-hull ACK = hull-only so far

        var requested = RealWeaponPart();
        subject.SendBuyLoadout(hull, fLaunch: true, parts: new[] { requested });

        // (a) the ACK mounts the real part — non-hull-only loadout (phase 3);
        // (b) fLaunch=true undocks — the ship leaves the dock (phase 4).
        int armed = PumpUntil(server,
            () => subject.World.Ships[subjectId].Loadout is { Count: > 0 } &&
                  subject.World.Ships[subjectId].StationId == -1,
            8000);
        Assert.True(armed < 8000, $"loadout+launch never composed (parts={(subject.World.Ships[subjectId].Loadout?.Count)}, stat={subject.World.Ships[subjectId].StationId})");
        Assert.Single(subject.World.Ships[subjectId].Loadout!);
        Assert.Equal(requested, subject.World.Ships[subjectId].Loadout![0]);  // real part, byte-exact
        Assert.Equal(hull, subject.World.Ships[subjectId].HullId);            // hull unchanged
        Assert.Equal(-1, subject.World.Ships[subjectId].StationId);           // undocked/flying

        // Re-assert phases 1-2 (nothing regressed by launch).
        AssertStateConsistent();

        // ── PHASE 5: movement — the observer streams the subject's live position
        // ────────────────────────────────────────────────────────────────────
        subject.CommandControls(new ControlData(0f, 0f, 0f, 0.7f), ShipControlState.ForwardButton);
        var obsWorld = observer.World;
        var streamed = new List<Vector3>();
        int moveTicks = PumpUntil(server, () =>
        {
            if (obsWorld.Ships.TryGetValue(subjectId, out var e) && e.Latest is not null)
            {
                var p = e.Latest.Position;
                if (!streamed.Any(q => (q - p).Length() < 1e-3f)) streamed.Add(p);
            }
            return streamed.Count >= 3;
        }, 7000);
        Assert.True(moveTicks < 7000, $"teammate never streamed ≥3 distinct positions into observer (saw {streamed.Count})");
        Assert.True((streamed[^1] - streamed[0]).Length() > 0.01f, "teammate must actually move under thrust");

        // Re-assert phases 1-4 (still same-side, roster intact, loadout intact).
        AssertStateConsistent();

        // ── PHASE 6: chat — subject's message lands in observer's EventLog ──
        subject.SendChat("FS MOVE TO GRID 9");
        int chatted = PumpUntil(server,
            () => observer.World.Events.Chats.Any(c => c.Message == "FS MOVE TO GRID 9"), 4000);
        Assert.True(chatted < 4000, "subject's chat never reached observer's EventLog");
        var got = observer.World.Events.Chats.Single(c => c.Message == "FS MOVE TO GRID 9");
        Assert.Equal(subjectId, got.SidSender);   // relayed with the real sender id

        // Final: nothing regressed across the WHOLE session.
        AssertStateConsistent();

        // ── LOCAL ────────────────────────────────────────────────────────────
        void AssertStateConsistent()
        {
            // Phase 1: mutual teammate presence survives.
            Assert.True(subject.World.Ships.ContainsKey(observerId));
            Assert.True(observer.World.Ships.ContainsKey(subjectId));
            // Phase 2: subject's ready survives on both rosters.
            Assert.True(subject.World.Roster.ReadyByShipId.TryGetValue(subjectId, out var sr) && sr);
            Assert.True(observer.World.Roster.ReadyByShipId.TryGetValue(subjectId, out var or) && or);
            // Phases 3+4: the subject is flying with a real loadout.
            Assert.Equal(-1, subject.World.Ships[subjectId].StationId);
            Assert.NotEmpty(subject.World.Ships[subjectId].Loadout!);
        }
    }
}
