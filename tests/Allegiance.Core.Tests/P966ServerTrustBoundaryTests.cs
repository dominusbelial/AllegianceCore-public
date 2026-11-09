using System.Text;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

namespace Allegiance.Core.Tests;

/// <summary>
/// P966 — server trust boundaries (independent server review P963 findings
/// 02/03/05, each re-verified against master before the fix):
/// <list type="bullet">
/// <item><b>P963-02</b> — a peer pending side selection could re-send LOGONREQ
/// without limit; every copy logged a line, sent a guaranteed LOGONACK plus a
/// guaranteed SIDE_ROSTER (rebuilt by scanning <c>_peerShips</c> per side).
/// Native has no such flow at all — it asserts an existing player never sends
/// LOGONREQ (<c>FedSrv/FedSrv.CPP:4830-4832</c>). The re-ack stays idempotent
/// but is now bounded by a per-peer retry interval, with no log line per excess
/// duplicate and cached roster bytes until the roster changes.</item>
/// <item><b>P963-03</b> — a player-claimed map went straight into
/// <c>Path.Combine(artPath, claim)</c>, so a <c>..</c>/rooted claim escaped the
/// artwork root and hosted any readable file while skipping the catalog. Native
/// checks a user map against the static-map catalog — exact file and team count
/// (<c>VerifyUserMap</c>, <c>FedSrv/FedSrv.CPP:234-245</c>).</item>
/// </list>
/// The reconnect-grace half of P966 (P963-05: wrong-token impostor, name
/// collision, token redaction) lives in
/// <see cref="P813cReconnectGraceTests"/> next to the rest of the grace suite.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P966ServerTrustBoundaryTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P966", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 966u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 9660000000000000000ul, DrmHash = "p966",
    };

    /// <summary>In-process server driven through the production dispatch path
    /// (TestProcessDatagram), with every egress frame captured — the P655/P869
    /// harness shape. No transport, so 256 dispatch calls run in one sim tick.</summary>
    private sealed class World : IDisposable
    {
        public readonly Srv Server = new() { SpawnInitialMinersAtBoot = false };
        public readonly List<(int Conn, byte[] Message)> Output = [];
        public World()
        {
            Require.Artwork();
            Server.Boot(Config());
            Server._testSendCapture = (c, p) => Output.AddRange(FedPacket.Unpack(p).Select(m => ((int)c.Value, m)));
        }
        public void Send(int conn, IFedMessage message) =>
            Server.TestProcessDatagram(new ConnectionId((uint)conn), FedPacket.Pack([message.Encode()]));
        public List<byte[]> SentTo(int conn, ushort fmid) =>
            Output.Where(o => o.Conn == conn && FedMessageHeader.Read(o.Message).Fmid == fmid).Select(o => o.Message).ToList();
        public LogonAck Logon(int conn, string name)
        {
            Send(conn, Request(name));
            var acks = SentTo(conn, LogonAck.Fmid);
            return LogonAck.Decode(acks[^1]);
        }
        public object? Call(string method, params object?[] args) =>
            typeof(Srv).GetMethod(method, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(Server, args);
        public void Dispose() => Server.Dispose();
    }

    private static string CaptureConsole(Action run)
    {
        var sw = new StringWriter();
        var orig = Console.Out;
        Console.SetOut(sw);
        try { run(); }
        finally { Console.SetOut(orig); }
        return sw.ToString();
    }

    // ── P963-02: bounded re-LOGONREQ re-ack ─────────────────────────────────

    /// <summary>⭐ A pending peer flooding duplicate LOGONREQs gets NO reply per
    /// copy: inside the per-peer re-ack retry interval every duplicate is
    /// dropped (rate-limited diagnostics only, no per-copy log line), while a
    /// legitimate retry after the interval still receives the SAME idempotent
    /// ack (same ship id, still exactly one pending entry, no second ship —
    /// P646's one-logon-per-connection contract, FedSrv.CPP:4832).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Duplicate_LOGONREQ_flood_from_a_pending_peer_is_bounded()
    {
        Require.Artwork();
        using var w = new World();
        var ack = w.Logon(1, "FLOOD");
        Assert.True(ack.FValidated);
        Assert.Equal(1, w.Server.ConnectionScopedCacheCounts().PendingSideSelect);
        Assert.Single(w.SentTo(1, LogonAck.Fmid));        // the original logon ack
        Assert.Single(w.SentTo(1, SideRoster.Fmid));      // the original side roster

        // 256 duplicates in one tick (the per-peer fair-share inbound limit).
        // All inside the retry interval anchored at the original ack: dropped,
        // no LOGONACK, no SIDE_ROSTER, no per-copy log line.
        string floodLog = CaptureConsole(() =>
        {
            for (int i = 0; i < 256; i++) w.Send(1, Request("FLOOD"));
        });
        Assert.Single(w.SentTo(1, LogonAck.Fmid));
        Assert.Single(w.SentTo(1, SideRoster.Fmid));
        int logLines = floodLog.Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(l => l.Contains("LOGONREQ"));
        Assert.True(logLines <= 8, $"256 duplicates produced {logLines} log lines (rate-limited gate must bound them)");
        Assert.True(logLines >= 1, "the flood should leave at least one rate-limited diagnostic");

        // A legitimate client retry — never faster than the interval — is served
        // the same ack (same ship id) and a fresh roster frame.
        for (int i = 0; i < 25; i++) w.Server.TickSim();   // 25 ticks @ 20 ups = 1.25 s >= the 1 s interval
        w.Send(1, Request("FLOOD"));
        var reacks = w.SentTo(1, LogonAck.Fmid);
        Assert.Equal(2, reacks.Count);
        var reack = LogonAck.Decode(reacks[^1]);
        Assert.True(reack.FValidated);
        Assert.Equal(ack.ShipID, reack.ShipID);            // the SAME reserved id
        Assert.Equal(2, w.SentTo(1, SideRoster.Fmid).Count);

        // The bound re-arms immediately: a burst right after a served re-ack is
        // again all dropped.
        for (int i = 0; i < 50; i++) w.Send(1, Request("FLOOD"));
        Assert.Equal(2, w.SentTo(1, LogonAck.Fmid).Count);
        Assert.Equal(2, w.SentTo(1, SideRoster.Fmid).Count);
        Assert.Equal(1, w.Server.ConnectionScopedCacheCounts().PendingSideSelect);
        Assert.Empty(w.Server.Ships);                       // P646: no second ship, no orphaned id
        Console.WriteLine($"P966-FLOOD: 306 duplicates -> 1 served re-ack, {logLines} log lines, 1 pending entry, 0 ships");
    }

    /// <summary>⭐ The re-acked SIDE_ROSTER bytes are cached per mission until the
    /// roster changes: three pending peers re-acking in the same roster epoch
    /// trigger exactly ONE roster build (the pre-fix cost was one full per-side
    /// _peerShips scan per duplicate), and a real roster change (a peer seating)
    /// invalidates the cache so the next re-ack carries the fresh counts.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Re_acked_roster_bytes_are_cached_until_the_roster_changes()
    {
        Require.Artwork();
        using var w = new World();
        Assert.True(w.Logon(1, "CACHE-A").FValidated);
        Assert.True(w.Logon(2, "CACHE-B").FValidated);
        Assert.True(w.Logon(3, "CACHE-C").FValidated);
        // Nothing roster-visible changed between the three logons (pending peers
        // are not mission players), so all three SIDE_ROSTER sends share one build.
        Assert.Equal(1, w.Server.SideRosterBuilds);
        Assert.Single(w.SentTo(1, SideRoster.Fmid));
        Assert.Single(w.SentTo(2, SideRoster.Fmid));
        Assert.Single(w.SentTo(3, SideRoster.Fmid));

        // Three re-acks in the same epoch: still exactly one build.
        for (int i = 0; i < 25; i++) w.Server.TickSim();   // past the re-ack interval
        w.Send(1, Request("CACHE-A"));
        w.Send(2, Request("CACHE-B"));
        w.Send(3, Request("CACHE-C"));
        Assert.Equal(2, w.SentTo(1, SideRoster.Fmid).Count);
        Assert.Equal(2, w.SentTo(2, SideRoster.Fmid).Count);
        Assert.Equal(2, w.SentTo(3, SideRoster.Fmid).Count);
        Assert.Equal(1, w.Server.SideRosterBuilds);

        // Seat peer 1 on side 0. The seat-time refresh of the other pending
        // peers runs BEFORE the seat is registered in _peerShips (the
        // pre-existing order in CompleteSideSelection), so the roster inputs
        // have not changed yet and the refresh legitimately re-uses the cached
        // bytes; the registration then bumps the epoch.
        w.Send(1, new PositionReq { SideId = 0 });
        w.Call("AdvanceSideSelection", w.Server.SimNow);
        Assert.Equal(1, w.Server.SideRosterBuilds);
        Assert.Single(w.Server.Ships);

        // Peer 2's next re-ack is AFTER the epoch bump: the cache was
        // invalidated, so it rebuilds exactly once and carries the FRESH count
        // (side 0 now has the seated player) — never the pre-seat bytes.
        for (int i = 0; i < 25; i++) w.Server.TickSim();
        w.Send(2, Request("CACHE-B"));
        // Four rosters to peer 2: logon, re-ack, the seat-time refresh (a cache
        // hit — the seat was not yet registered when it ran), and this re-ack.
        Assert.Equal(4, w.SentTo(2, SideRoster.Fmid).Count);
        Assert.Equal(2, w.Server.SideRosterBuilds);
        var roster = SideRoster.Decode(w.SentTo(2, SideRoster.Fmid)[^1]);
        Assert.Equal(1, roster.Sides.Single(s => s.SideId == 0).PlayerCount);
        Assert.Equal(0, roster.Sides.Single(s => s.SideId == 1).PlayerCount);
        Console.WriteLine("P966-ROSTERCACHE: 7 roster sends across an epoch change -> 2 builds; fresh counts after seating");
    }

    // ── P963-03: claimed map must be an exact catalog entry ─────────────────

    /// <summary>⭐ A player-claimed map is only ever a catalog KEY: an exact
    /// maps.txt match (file + team count, native <c>VerifyUserMap</c>,
    /// FedSrv.CPP:234-245) whose canonical path stays under the artwork root.
    /// Parent-directory traversal, rooted/absolute paths, wrong-team claims and
    /// existing-but-non-catalog files (the core itself) are all refused —
    /// closing the artPath-escape and the file-existence/parse oracle — and the
    /// refusal names no server path.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Claimed_map_traversal_rooted_wrong_team_and_non_catalog_are_refused()
    {
        Require.Artwork();
        using var w = new World();
        // The real corpus catalog: "2,25AnniSimple,XXV_simple" (maps.txt), and
        // PCore001.igc exists under artPath but is a CORE, not a catalog map.
        var parameters = new MissionParamsMsg
        {
            StrGameName = "P966 claimed", SzIgcStaticFile = "PCore001.igc", NTems = 2,
            NTotalMaxPlayersPerGame = 32, NMaxPlayersPerTeam = 12,
            ILives = MissionDef.UnlimitedLives, IGoalConquestPercentage = 100, IGoalTerritoryPercentage = 100,
        };
        string wire = Convert.ToBase64String(parameters.Encode());
        parameters.NTems = 3;                             // a wrong team count for a 2-team catalog map
        string wire3 = Convert.ToBase64String(parameters.Encode());
        parameters.NTems = 2;

        // Control: an exact catalog match (file name with the wizard's .igc form).
        Assert.NotNull(w.Server.ProvisionPlayerGame(new ClaimedGame("ok", "s", "PCore001.igc",
            "XXV_simple.igc", "P966 claimed", wire, "Claimed")));
        // Control: the catalog's bare file name and friendly name are keys too.
        Assert.NotNull(w.Server.ProvisionPlayerGame(new ClaimedGame("bare", "s", "PCore001.igc",
            "XXV_simple", "P966 bare", wire, "Claimed")));
        Assert.NotNull(w.Server.ProvisionPlayerGame(new ClaimedGame("friendly", "s", "PCore001.igc",
            "25AnniSimple", "P966 friendly", wire, "Claimed")));

        // Parent-directory traversal — must be refused without touching the file system.
        string refuse = CaptureConsole(() =>
        {
            Assert.Null(w.Server.ProvisionPlayerGame(new ClaimedGame("dotdot", "s", "PCore001.igc",
                "../XXV_simple.igc", "P966 claimed", wire, "Claimed")));
            Assert.Null(w.Server.ProvisionPlayerGame(new ClaimedGame("dotdot2", "s", "PCore001.igc",
                "../../Artwork/XXV_simple.igc", "P966 claimed", wire, "Claimed")));
            // Rooted / absolute / drive-letter forms.
            Assert.Null(w.Server.ProvisionPlayerGame(new ClaimedGame("rooted", "s", "PCore001.igc",
                "/etc/passwd", "P966 claimed", wire, "Claimed")));
            Assert.Null(w.Server.ProvisionPlayerGame(new ClaimedGame("rooted2", "s", "PCore001.igc",
                "C:\\Artwork\\XXV_simple.igc", "P966 claimed", wire, "Claimed")));
            // Wrong team count for a catalog map (native requires nNumTeams to match).
            Assert.Null(w.Server.ProvisionPlayerGame(new ClaimedGame("teams", "s", "PCore001.igc",
                "XXV_simple.igc", "P966 claimed", wire3, "Claimed")));
            // A file that EXISTS under artPath but is not in the catalog (the
            // core): the existence/parse oracle is closed.
            Assert.Null(w.Server.ProvisionPlayerGame(new ClaimedGame("core", "s", "PCore001.igc",
                "PCore001.igc", "P966 claimed", wire, "Claimed")));
        });
        // The refusals echo no server path details (the sanitized claim string
        // itself may appear; the artwork root never does).
        Assert.DoesNotContain(ArtRoot, refuse, StringComparison.Ordinal);
        // Exactly the three control missions exist (plus the configured world).
        Assert.Equal(4, w.Server.Missions.Count);
        Assert.All(w.Server.Missions.Skip(1), m => Assert.True(m.PlayerCreated));
        Console.WriteLine("P966-MAPCLAIM: traversal/rooted/wrong-team/non-catalog claims refused; catalog matches provisioned");
    }
}
