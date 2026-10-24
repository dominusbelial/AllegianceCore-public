using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P627 — the authoritative client-side receive-dispatch coverage test.
///
/// The fedmsg parity score for the "network messages" area is only trustworthy
/// if we can *prove* that every server→client message the port claims to
/// support is actually consumed by the real client's dispatch
/// (<see cref="ConnectionSession.OnReceive"/> → <see cref="Allegiance.ClientCore.State.ClientWorld.Apply"/>),
/// and is not silently falling into an "unknown fmid" sink. This test does that:
///
///  1. It encodes every server-sent message the real client is supposed to
///     consume (the <see cref="ExpectedConsumedServerSent"/> registry — all 91
///     native S/CS messages the client dispatches, plus the 14 port-added
///     server-sent messages), feeds the real bytes through the real dispatch
///     (no sockets — <see cref="ITransportSite.OnReceive"/> is invoked
///     directly), and asserts the fmid was consumed, i.e. did NOT land in
///     <see cref="ConnectionSession.UnknownFmids"/> and did not throw during
///     decode.
///  2. It proves the "unknown fmid" detector itself works (negative control:
///     a junk fmid IS recorded as unknown), so a pass genuinely means "the
///     dispatch has a case for this fmid".
///  3. It enforces completeness: every native S/CS message that has a wire
///     codec (the 102 "supported" set) must be in exactly one of
///     <see cref="ExpectedConsumedServerSent"/> (the client consumes it) or
///     <see cref="KnownUnconsumedServerSent"/> (documented, with a reason, as a
///     real candidate task). Adding a new supported S-message codec without
///     wiring it into the client AND documenting it fails this test.
///
/// The two registries are the executable form of docs/parity/fedmsg-wiring.csv
/// and must be kept in step with it (the CSV is the human-readable audit; this
/// test is the machine check). The known-unconsumed set is intentionally kept
/// OUT of the pass-feed list: those are real gaps (ranked candidate tasks), not
/// "done".
/// </summary>
public class FedMsgDispatchCoverageTests
{
    /// <summary>
    /// The server-sent messages the REAL MonoGame client must consume. Native
    /// fmid + the <c>Allegiance.Core.Wire</c> codec class that owns that fmid.
    /// This is exactly the set <see cref="ConnectionSession.OnReceive"/>
    /// dispatches, including the P627 addition PLAYER_RESCUED(16). (The P627
    /// addition — the kill-bonus update, fmid 212 — was removed again by P773; the
    /// client derives its own kill bonus from PLAYERINFO/KILL_SHIP experience.)
    /// </summary>
    private static readonly (ushort Fmid, string Type)[] ExpectedConsumedServerSent =
    {
        (87, "FireExpendable"), (156, "GainFlag"), (164, "AddPart"),
        (182, "RequestMoney"), (192, "RelaunchShip"), (194, "PingData"),
        (51, "ChangeTeamCiv"), (86, "AsteroidDestroyed"), (92, "ProbesUpdate"),
        (124, "QuitMission"), (126, "QuitSide"), (128, "EnterGame"), (155, "GameState"),
        (6, "ObjectSpotted"), (93, "OrderChange"), (95, "JoinedMission"), (106, "SetWingId"),
        (2, "LogonAck"), (5, "BuyLoadoutAck"), (7, "SetCluster"), (8, "DevCompleted"),
        (9, "SetBriefingText"), (10, "PlayerInfo"), (12, "ProjectileInstance"), (13, "RankInfo"), (14, "SetStartTime"),
        (16, "PlayerRescued"), (17, "Ballot"), (20, "ShipDelete"), (23, "ChatMessage"),
        (26, "ExportShip"), (28, "TreasureSpawn"), (30, "DestroyTreasure"), (33, "DropPart"), (34, "SwapPart"), (35, "Ping"),
        (37, "MissionStageMessage"), (38, "MissionDef"), (45, "PlayerReady"), (46, "TeamReady"),
        (47, "ForceTeamReady"), (57, "StationsUpdate"), (58, "StationCapture"), (59, "StationDestroyed"),
        (60, "MoneyChange"), (61, "DockedNotify"), (62, "GameOver"), (63, "GameOverPlayers"),
        (65, "BucketStatus"), (68, "SideInactive"), (70, "FireMissile"), (75, "SideTechChange"),
        (76, "SideAttributeChange"), (77, "Eject"), (78, "SingleShipUpdate"), (83, "MissileDestroyed"),
        (84, "MineDestroyed"), (85, "ProbeDestroyed"), (90, "ShipStatusMessage"),
        (94, "LeaveShip"), (96, "LoadoutChange"), (102, "TeleportAck"), (105, "KillShip"),
        (108, "ChatBuoy"), (109, "CreateChaff"), (110, "MissileSpoofed"), (111, "EndSpoofing"),
        (114, "Payday"), (115, "SetMoney"),
        (116, "AutoDonateUpdate"), (121, "SetTeamLeader"), (123, "SetMissionOwner"), (125, "JoinSide"),
        (129, "Reload"), (166, "EnterLifepod"), (175, "LightShipsUpdate"), (176, "HeavyShipsUpdate"),
        (177, "ViewClusterReply"), (178, "BoardNack"), (189, "Promote"), (199, "HighlightCluster"), (197, "ChangeAlliances"), (179, "AsteroidsUpdate"),
        (181, "BuildingEffectDestroyed"), (183, "ShipReset"), (185, "RipcordActivate"), (186, "RipcordDenied"),
        (187, "RipcordAborted"), (191, "CreateBuckets"), (198, "AsteroidMined"), (200, "ClusterInfo"),
        // P758 — the treasure-pickup notice and the warp-bomb notice are now
        // consumed by the real client (appmsg.cpp:262-308 / 850-868).
        (162, "AcquireTreasure"), (188, "WarpBomb"),
        (18, "CancelBallot"), (40, "PositionReqBroadcast"), (42, "DelPositionReq"),
        (43, "LockLobby"), (44, "LockSides"), (52, "AutoAccept"),
        // port-added server-sent messages the real client also dispatches:
        (204, "MissileLockUpdate"), (205, "MissileUpdate"), (206, "OwnShipMiningUpdate"),
        (207, "ExplosionEvent"), (208, "MissileTrackWarningUpdate"),
        // P774 — the port-only score roster (209/219) was retired; the client
        // derives the roster from PLAYERINFO(10) + KILL_SHIP(105) as native.
        (211, "SideRoster"),
        (213, "ProjectileHullHit"), (214, "ClusterWarningUpdate"), (215, "QueuedCommandUpdate"),
        (216, "CollisionEvent"), (217, "VictimDamaged"), (853, "MineStrength"), (991, "TimescaleUpdate"),
    };

    /// <summary>
    /// Every native S/CS message that HAS a port wire codec but is NOT consumed
    /// by the real client — the genuinely-unconsumed set. Each is a real
    /// candidate task (ranked in docs/reports/P627_FEDMSG_WIRING_AUDIT.md);
    /// keeping them here means the completeness assertion below still passes
    /// while the codec exists. When a future P-task wires one of these into the
    /// client, move it to <see cref="ExpectedConsumedServerSent"/>.
    /// </summary>
    private static readonly (ushort Fmid, string Name, string Why)[] KnownUnconsumedServerSent =
    {
        (3, "LOGOFF", "server echo of client LOGOFF; quit handled client-side, echo benign"),
        (31, "NEW_SHIP_INSTANCE", "inactive native definition; active object creation uses EXPORT (P740 n/a)"),
        (89, "TREASURE_SETS", "native WinTrek.cpp:11090-11094 explicitly NYI (P740 n/a); port legacy producer retained"),
        // P758 moved 162 ACQUIRE_TREASURE and 188 WARP_BOMB to the consumed
        // set: the real client now decodes both and answers/offers the native
        // behaviour (treasure ack / destabilized-aleph state + alert line).
    };

    // ── helpers ────────────────────────────────────────────────────────────

    /// <summary>Set a property or field by name (reflection handles both plain
    /// and init-only setters, and public fields).</summary>
    private static void Set(object o, string name, object value)
    {
        var t = o.GetType();
        var p = t.GetProperty(name);
        if (p is not null && p.CanWrite) { p.SetValue(o, value); return; }
        var f = t.GetField(name);
        if (f is not null) { f.SetValue(o, value); return; }
        throw new InvalidOperationException($"no settable member '{name}' on {t.Name}");
    }

    /// <summary>A valid server ship-export (hull 0 id is the "unknown", so a
    /// sane non-zero id + healthy fractions keeps every world Apply happy).</summary>
    private static ServerShipUpdate MakeShip(short id) => ServerShipUpdate.FromValues(
        id, 0, default, 1u, Vector3.Zero, new Orientation(), Vector3.Zero,
        new float[3], 1f, 1f, 1f, 1f, 1f, 1f, 1f);

    /// <summary>Construct a valid (degenerate-but-decodable) instance of the
    /// named Wire codec. Collection members are initialised to empty; the few
    /// codecs that need a non-empty nested ship / byte-blob / ctor are built
    /// explicitly. Produces real wire bytes that Decode() must accept.</summary>
    private static IFedMessage NewMessage(string typeName, ushort fmid)
    {
        // Read-only record-style codecs built via their From(...) factory:
        switch (fmid)
        {
            case 204: return MissileLockUpdate.From(5, -1, 0f, Vector3.Zero, "test");
            case 214: return ClusterWarningUpdate.From(1, 1, false, false);
        }

        var type = typeof(IFedMessage).Assembly.GetType("Allegiance.Core.Wire." + typeName)
            ?? throw new InvalidOperationException($"no Wire type {typeName}");
        var obj = Activator.CreateInstance(type)!;
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanWrite) continue;
            var pt = prop.PropertyType;
            if (pt == typeof(string) || !typeof(System.Collections.IEnumerable).IsAssignableFrom(pt)) continue;
            if (pt.IsArray)
            {
                prop.SetValue(obj, Array.CreateInstance(pt.GetElementType()!, 0));
            }
            else if (pt.IsGenericType)
            {
                var gd = pt.GetGenericTypeDefinition();
                var elem = pt.GetGenericArguments()[0];
                if (gd == typeof(List<>))
                    prop.SetValue(obj, Activator.CreateInstance(pt));
                else
                    prop.SetValue(obj, Array.CreateInstance(elem, 0)); // arrays impl IReadOnlyList/IList/IEnumerable
            }
        }

        // Hand-held valid data for the codecs whose Encode()/world Apply() need
        // a non-empty nested ship, a full-width byte/float blob, or text.
        switch (fmid)
        {
            case 7:   // SET_CLUSTER — carry a real ship
                Set(obj, "SectorId", (short)1); Set(obj, "Cookie", (byte)1); Set(obj, "Ship", MakeShip(5)); break;
            case 28:  // TREASURE — Data must carry a non-null P0/P1 vec
                Set(obj, "Data", new Allegiance.Core.DataTreasureIGC(new float[3], new float[3], 10f, 0u, 1, 1, 1, 1, default, false)); break;
            case 62:  // GAME_OVER — always carries 6 side records + a message
                Set(obj, "GameoverMessage", "test"); Set(obj, "SideWinner", (short)0);
                Set(obj, "NumSides", (byte)6);
                Set(obj, "Sides", Enumerable.Range(0, 6).Select(_ => new SideEndgameInfo("side", 0, 1f, 1f, 1f, 1f, 0, 0, 0, 0, 0, 0, 0)).ToArray());
                break;
            case 75:  // SIDE_TECH_CHANGE — mask must be exactly MaskBytes
                Set(obj, "TtbmDevelopments", new byte[SideTechChange.MaskBytes]); break;
            case 76:  // SIDE_ATTRIBUTE_CHANGE — exactly GaCount floats
                Set(obj, "GasAttributes", new float[SideAttributeChange.GaCount]); break;
            case 78:  // SINGLE_SHIP_UPDATE — carry a real ship
                Set(obj, "OidTarget", (short)5); Set(obj, "Ship", MakeShip(5)); break;
            case 183: // SHIP_RESET — carry a real ship
                Set(obj, "Ship", MakeShip(5)); Set(obj, "Cookie", (byte)1); break;
            case 197: // CHANGE_ALLIANCES — full signed six-side group vector
                Set(obj, "Allies", Enumerable.Repeat((sbyte)-1, ChangeAlliances.SideAlliesMax).ToArray()); break;
            case 191: // CREATE_BUCKETS — both masks exactly MaskBytes
                Set(obj, "TtbmDevelopments", new byte[CreateBuckets.MaskBytes]);
                Set(obj, "TtbmInitial", new byte[CreateBuckets.MaskBytes]); break;
        }
        return (IFedMessage)obj;
    }

    private static byte[] Encode(IFedMessage m) => m.Encode();

    private static void Feed(ConnectionSession session, byte[] encoded)
    {
        ((ITransportSite)session).OnReceive(ConnectionId.Server, FedPacket.Pack(new[] { encoded }));
    }

    // ── tests ──────────────────────────────────────────────────────────────

    [Fact]
    public void EveryExpectedServerSentMessage_IsConsumed_NotUnknownFmid()
    {
        var failures = new List<string>();
        foreach (var (fmid, typeName) in ExpectedConsumedServerSent)
        {
            var session = new ConnectionSession();
            try
            {
                var msg = NewMessage(typeName, fmid);
                Feed(session, Encode(msg));
                if (session.UnknownFmids.Contains(fmid))
                    failures.Add($"fmid {fmid} ({typeName}) was NOT consumed — fell into the unknown-fmid sink");
            }
            catch (Exception ex)
            {
                failures.Add($"fmid {fmid} ({typeName}) threw during decode/apply: {ex.GetType().Name}: {ex.Message}");
            }
        }
        Assert.True(failures.Count == 0, "Unconsumed / undecodable expected server-sent messages:\n  " +
                                         string.Join("\n  ", failures));
    }

    [Fact]
    public void UnknownFmidDetector_RecordsUnconsumedServerSentMessage()
    {
        // Negative control: feed a real, codec-bearing server-sent message the
        // real client deliberately does not consume — LOGOFF(3), the server echo
        // of the client's own logoff, documented as unconsumed. It must land in
        // UnknownFmids. Proves the detector works, so a pass in the positive
        // test genuinely means "the dispatch consumed it" rather than "nothing
        // was recorded".
        var session = new ConnectionSession();
        Feed(session, Encode(NewMessage("Logoff", 3)));
        Assert.True(session.UnknownFmids.Contains((ushort)3),
            "LOGOFF(3) — a codec-bearing, server-sent message the real client does not consume — should be recorded as unknown");
    }

    [Fact]
    public void SupportedServerSentSet_IsFullyAccountedFor()
    {
        // Completeness: every native S/CS message that has a wire codec must be
        // in exactly one registry — either the real client consumes it, or it is
        // a documented candidate task. Nothing may silently sit in between.
        var consumed = ExpectedConsumedServerSent.Select(x => x.Fmid).ToHashSet();
        var unconsumed = KnownUnconsumedServerSent.Select(x => x.Fmid).ToHashSet();

        // The 102 native S/CS messages that have a port codec:
        int[] supportedNativeServerSent =
        {
            2, 3, 5, 6, 7, 8, 9, 10, 12, 13, 14, 16, 17, 18, 20, 23, 26, 28, 30, 31,
            33, 34, 35, 37, 38, 40, 42, 43, 44, 45, 46, 47, 51, 52, 57, 58, 59, 60,
            61, 62, 63, 65, 68, 70, 75, 76, 77, 78, 83, 84, 85, 86, 87, 89, 90, 92,
            93, 94, 95, 96, 102, 105, 106, 108, 109, 110, 111, 114, 115, 116, 121,
            123, 124, 125, 126, 128, 129, 155, 156, 162, 164, 166, 175, 176, 177,
            178, 179, 181, 182, 183, 185, 186, 187, 188, 189, 191, 192, 194, 197,
            198, 199, 200,
        };
        var overlap = consumed.Intersect(unconsumed).ToList();
        var missing = supportedNativeServerSent.Where(f => !consumed.Contains((ushort)f) && !unconsumed.Contains((ushort)f)).ToList();
        var unexpected = consumed.Where(f => f <= 200 && !supportedNativeServerSent.Contains(f)).ToList();

        Assert.True(overlap.Count == 0, $"Registries overlap: {string.Join(",", overlap)}");
        Assert.True(missing.Count == 0, $"Supported S-messages unaccounted for: {string.Join(",", missing)}");
        Assert.True(unexpected.Count == 0,
            $"Consumed registry lists non-native-S/CS fmids that are not in the supported set: {string.Join(",", unexpected)}");

        // Registry entries must be unique; otherwise a duplicate can mask stale counts.
        Assert.Equal(ExpectedConsumedServerSent.Length, consumed.Count);
        Assert.Equal(KnownUnconsumedServerSent.Length, unconsumed.Count);
        Assert.Equal(99, consumed.Count(f => supportedNativeServerSent.Contains(f)));
        // 13 port-added consumed messages: P266's lead stream (210) retired in
        // P804 (the client solves the lead itself), like P774's 209/219.
        Assert.Equal(13, consumed.Count(f => !supportedNativeServerSent.Contains(f)));

        // Sanity: the executable counts match the audited numbers (112 after
        // P804 retired the port-only lead stream, 210).
        Assert.Equal(112, ExpectedConsumedServerSent.Length);
        Assert.Equal(3, KnownUnconsumedServerSent.Length);
        Assert.Equal(102, supportedNativeServerSent.Length);
    }
}
