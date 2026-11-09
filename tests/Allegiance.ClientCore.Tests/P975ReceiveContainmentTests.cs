using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P975 (P971-01) — per-message containment on the client receive path.
///
/// The review's repro: a CRC-valid packet whose message header says
/// <c>SET_CLUSTER(7)</c> but whose body is absent made <see cref="SetCluster.Decode"/>
/// throw <c>ArgumentException</c> straight out of
/// <see cref="ITransportSite.OnReceive"/>, which the TCP read loop treats as an
/// invalid frame (<c>TcpTransport.cs</c> ReadDataLoop → "invalid frame") and turns
/// into session loss. One malformed optional message therefore ended an otherwise
/// usable session.
///
/// Native's receive loop does not do that: it sanity-checks each message's
/// <c>cbmsg</c>/<c>fmid</c> and, when a message fails, logs "HACKER??" and abandons
/// the REST of that packet while keeping the session (_Utility/Messages.cpp:947-994).
/// Containment — not disconnection — is the native intent.
///
/// These tests assert, for every FMID the client dispatches, that a decode/apply
/// failure never escapes <see cref="ITransportSite.OnReceive"/>, never ends the
/// session, and leaves the world untouched; that the one mandatory message
/// (LOGONACK) is deliberately still fatal; and that the session keeps working after
/// a stream of malformed messages.
/// </summary>
public class P975ReceiveContainmentTests
{
    // ── feeding helpers ────────────────────────────────────────────────────

    private static void Feed(ConnectionSession session, byte[] message)
        => ((ITransportSite)session).OnReceive(ConnectionId.Server, FedPacket.Pack(new[] { message }));

    private static byte[] HeaderOnly(ushort fmid)
    {
        var b = new byte[4];
        BitConverter.TryWriteBytes(b.AsSpan(0), (ushort)4);   // cbmsg = header only
        BitConverter.TryWriteBytes(b.AsSpan(2), fmid);
        return b;
    }

    private static byte[] DeclaredOversize(ushort fmid)
    {
        // cbmsg claims 65 535 bytes while the buffer carries 12: every codec that
        // validates cbmsg == length must reject it.
        var b = new byte[12];
        BitConverter.TryWriteBytes(b.AsSpan(0), (ushort)0xFFFF);
        BitConverter.TryWriteBytes(b.AsSpan(2), fmid);
        return b;
    }

    private static byte[] BadVarItem(ushort fmid)
    {
        var b = new byte[12];
        BitConverter.TryWriteBytes(b.AsSpan(0), (ushort)12);
        BitConverter.TryWriteBytes(b.AsSpan(2), fmid);
        b[4] = 0xFF; b[5] = 0xFF;    // ib = 65 535
        b[6] = 0xFF; b[7] = 0xFF;    // cb = 65 535
        return b;
    }

    /// <summary>Cut a real encoding in half and fix the header length so the codec
    /// cannot be saved by the cbmsg check: the failure must be a body failure.</summary>
    private static byte[] TruncatedHalf(byte[] valid)
    {
        int len = Math.Max(4, valid.Length / 2);
        var b = new byte[len];
        Array.Copy(valid, b, len);
        BitConverter.TryWriteBytes(b.AsSpan(0), (ushort)len);
        return b;
    }

    /// <summary>Append a var-item pair whose count claims 65 520 bytes: an
    /// out-of-range var item against a self-consistent header.</summary>
    private static byte[] VarItemOverflow(byte[] valid)
    {
        var b = new byte[valid.Length + 4];
        Array.Copy(valid, b, valid.Length);
        BitConverter.TryWriteBytes(b.AsSpan(valid.Length), (ushort)valid.Length);   // ib
        BitConverter.TryWriteBytes(b.AsSpan(valid.Length + 2), (ushort)0xFFF0);     // cb = 65 520
        BitConverter.TryWriteBytes(b.AsSpan(0), (ushort)b.Length);
        return b;
    }

    // ── the containment counters (new in this task; reflection keeps these tests
    //    compiling and RUNNING — and failing — on the pre-fix tree) ────────────

    private static long ContainedCount(ConnectionSession session)
    {
        var p = typeof(ConnectionSession).GetProperty("MalformedMessageCount",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (p?.GetValue(session) is long n) return n;
        var f = typeof(ConnectionSession).GetField("_malformedMessages",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (f?.GetValue(session) is long n2) return n2;
        return -1;   // pre-fix: no containment counter exists
    }

    private static int ContainedFmidCount(ConnectionSession session)
    {
        var p = typeof(ConnectionSession).GetProperty("MalformedMessageCounts",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return p?.GetValue(session) is IDictionary d ? d.Count : -1;
    }

    // ── world fingerprint ──────────────────────────────────────────────────

    /// <summary>
    /// A whole-world snapshot: every <see cref="ClientWorld"/> field's scalar value
    /// or collection count. A contained message must leave this string identical —
    /// the machine check for "decode fully before apply, never half-apply".
    /// <para>Excluded on purpose: <c>_lastAppliedTick64</c>, the decode-lag wall stamp
    /// that <see cref="ClientWorld.MarkApplied"/> refreshes once per received batch.
    /// It is session bookkeeping, not world state, and it must advance even when
    /// every message in the batch was dropped.</para>
    /// </summary>
    private static string WorldFingerprint(ClientWorld world)
    {
        var sb = new StringBuilder();
        foreach (var f in typeof(ClientWorld)
                     .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                     .OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            if (f.Name == "_lastAppliedTick64") continue;
            object? v;
            try { v = f.GetValue(world); } catch { sb.Append(f.Name).Append("=?;"); continue; }
            if (v is null) { sb.Append(f.Name).Append("=null;"); continue; }
            var vt = v.GetType();
            if (vt.IsPrimitive || vt.IsEnum || v is string)
            {
                sb.Append(f.Name).Append('=').Append(v).Append(';');
                continue;
            }
            var count = vt.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
            if (count?.GetValue(v) is int n) { sb.Append(f.Name).Append('#').Append(n).Append(';'); continue; }
            sb.Append(f.Name).Append('~');   // opaque (delegate/clock/record): not summarisable
        }
        return sb.ToString();
    }

    // ── the FMIDs the client dispatches ────────────────────────────────────

    /// <summary>Every FMID <see cref="ConnectionSession.OnReceive"/> has a
    /// <c>case</c> for: the P627 coverage registry (the messages the real client is
    /// contracted to consume) plus the dispatched-but-unregistered port messages
    /// (STATIC_MAP_INFO(11), SHIP_WARNING_UPDATE(218), OWN_SHIP_CLOAK_UPDATE(220),
    /// RESEARCH_BRIEFING(992), RESEARCH_PRODUCTION(993), LAST_BASE_NOTICE(995),
    /// MISSION_BRIEFING_INFO(996)) — a table-driven fuzz must cover the switch as
    /// written, not only the audited subset.</summary>
    internal static readonly (ushort Fmid, string Type)[] DispatchedFmids =
    [
        .. FedMsgDispatchCoverageTests.ExpectedConsumedServerSent,
        (11, "StaticMapInfo"), (218, "ShipWarningUpdate"), (220, "OwnShipCloakUpdate"),
        (992, "ResearchBriefing"), (993, "ResearchProduction"),
        (995, "LastBaseNotice"), (996, "MissionBriefingInfo"),
    ];

    /// <summary>The malformed shapes fed to every dispatched FMID.</summary>
    private static IEnumerable<(string Shape, byte[] Body)> Shapes(ushort fmid, byte[]? valid)
    {
        yield return ("header-only", HeaderOnly(fmid));
        yield return ("declared-oversize", DeclaredOversize(fmid));
        yield return ("bad-var-item", BadVarItem(fmid));
        if (valid is null) yield break;
        yield return ("truncated-half", TruncatedHalf(valid));
        yield return ("var-item-overflow", VarItemOverflow(valid));
    }

    private static byte[]? ValidEncoding(string typeName, ushort fmid)
    {
        try { return FedMsgDispatchCoverageTests.NewMessage(typeName, fmid).Encode(); }
        catch { return null; }   // the codec needs hand-held data this registry does not carry
    }

    // ── tests ──────────────────────────────────────────────────────────────

    [Fact]
    public void ReviewerRepro_ShortSetCluster_IsDropped_AndTheSessionSurvives()
    {
        var session = new ConnectionSession();
        var world = session.World;
        int lostRaised = 0;
        session.SessionLost += () => lostRaised++;
        string before = WorldFingerprint(world);

        // The review's exact bytes: cbmsg=4, fmid=SET_CLUSTER(7), no body.
        var repro = new byte[] { 0x04, 0x00, 0x07, 0x00 };
        var escaped = Record.Exception(() => Feed(session, repro));

        Assert.Null(escaped);                       // P971-01: nothing escapes OnReceive
        Assert.False(session.Lost);                 // one bad message is not session loss
        Assert.Equal(0, lostRaised);
        Assert.Equal(1, ContainedCount(session));
        Assert.Equal(1, ContainedFmidCount(session));
        Assert.Equal(before, WorldFingerprint(world));

        // …and the session still works: a real SET_CLUSTER lands normally.
        Feed(session, ValidEncoding("SetCluster", SetCluster.Fmid)!);
        Assert.Single(world.Ships);
        Assert.NotNull(world.MyShipId);
        Assert.False(session.Lost);
    }

    [Fact]
    public void MalformedLogonAck_IsMandatory_AndDeliberatelyDisconnects()
    {
        // The mandatory list (one entry): LOGONACK is the message whose body cannot
        // be skipped — a client that cannot read its own logon answer has no
        // session, and a corrupt one says the peer is not speaking this protocol.
        var mandatory = typeof(ConnectionSession).GetMethod("IsMandatoryFmid",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(mandatory);
        Assert.True((bool)mandatory!.Invoke(null, [(ushort)LogonAck.Fmid])!);
        Assert.False((bool)mandatory.Invoke(null, [(ushort)SetCluster.Fmid])!);

        var session = new ConnectionSession();
        int lostRaised = 0;
        session.SessionLost += () => lostRaised++;
        var escaped = Record.Exception(() => Feed(session, HeaderOnly(LogonAck.Fmid)));

        Assert.Null(escaped);
        Assert.True(session.Lost);
        Assert.Equal(1, lostRaised);
        Assert.True(ContainedCount(session) >= 1);

        // Every other dispatched message is non-mandatory: the same malformed shape
        // must NOT end the session (proved across all of them below).
        Assert.False((bool)mandatory.Invoke(null, [(ushort)GameOverPlayers.Fmid])!);
    }

    [Fact]
    public void MalformedMessages_OverEveryDispatchedFmid_NeverEscape_NeverKillTheSession()
    {
        var failures = new List<string>();
        var noRejection = new List<string>();
        int feeds = 0, rejected = 0, rejectedWithoutMutation = 0;

        var session = new ConnectionSession();
        int lostRaised = 0;
        session.SessionLost += () => lostRaised++;

        foreach (var (fmid, typeName) in DispatchedFmids)
        {
            if (fmid == LogonAck.Fmid) continue;          // its own mandatory test above
            byte[]? valid = ValidEncoding(typeName, fmid);
            bool anyRejected = false;

            foreach (var (shape, body) in Shapes(fmid, valid))
            {
                if (session.Lost)
                {
                    failures.Add($"session was already lost before {fmid}/{shape}: a contained message killed it");
                    session = new ConnectionSession();
                    session.SessionLost += () => lostRaised++;
                }
                string before = WorldFingerprint(session.World);
                long containedBefore = ContainedCount(session);
                feeds++;
                var escaped = Record.Exception(() => Feed(session, body));
                if (escaped is not null)
                {
                    failures.Add($"{fmid} ({typeName}) [{shape}]: escaped as {escaped.GetType().Name}: {escaped.Message}");
                    continue;
                }
                bool contained = containedBefore >= 0 && ContainedCount(session) > containedBefore;
                if (!contained && session.Lost)
                {
                    // A shape that decoded and then legitimately disconnected is not
                    // this test's subject; carry on with a fresh session.
                    session = new ConnectionSession();
                    session.SessionLost += () => lostRaised++;
                    continue;
                }
                if (session.Lost)
                {
                    failures.Add($"{fmid} ({typeName}) [{shape}]: a CONTAINED message terminated the session");
                    session = new ConnectionSession();
                    session.SessionLost += () => lostRaised++;
                    continue;
                }
                if (!contained) continue;   // decoded as a (degenerate) valid message
                anyRejected = true;
                rejected++;
                if (WorldFingerprint(session.World) == before) rejectedWithoutMutation++;
                else failures.Add($"{fmid} ({typeName}) [{shape}]: rejected message left a partial world mutation");
            }
            if (!anyRejected) noRejection.Add($"{fmid} ({typeName})");
        }

        Assert.Equal(0, lostRaised);
        Assert.False(session.Lost);
        Assert.True(feeds >= DispatchedFmids.Length * 3, $"only {feeds} malformed feeds ran");
        // A handful of codecs (measured: 8 of the 121 non-mandatory messages) accept
        // every degenerate shape — they carry no required body field, or deliberately
        // ignore cbmsg, so those shapes are not "bad" input for them at all. They are
        // listed rather than failed: nothing throws, so nothing escapes, and the
        // session checks above still apply.
        Assert.True(noRejection.Count <= 12,
            "unexpectedly many FMIDs accepted every malformed shape:\n  " + string.Join("\n  ", noRejection));
        Assert.True(rejected >= 200,
            $"only {rejected}/{feeds} malformed shapes were rejected — the containment path is not exercised");
        Assert.Equal(rejected, rejectedWithoutMutation);
        Assert.True(failures.Count == 0, "containment failures:\n  " + string.Join("\n  ", failures));

        // After the whole fuzz run the session is still a working session: one valid
        // message still reaches the world.
        Feed(session, new GameOverPlayers { Entries = [new GameOverPlayerEntry("post-fuzz-probe", 0, 0, 1f)] }.Encode());
        Assert.Contains(session.World.GameOverPlayersList, e => e.CharacterName == "post-fuzz-probe");
        Assert.False(session.Lost);
    }

    [Fact]
    public void MalformedCounters_StayBounded_UnderAFlood()
    {
        var session = new ConnectionSession();
        // 16 rounds over every dispatched FMID: ~1 900 contained messages on one
        // session. The per-fmid map must stay bounded (64 entries) and the log must
        // be rate-limited (first 10, then one in 500), not one line per message.
        var writer = new System.IO.StringWriter();
        var original = Console.Out;
        Console.SetOut(writer);
        try
        {
            for (int round = 0; round < 16; round++)
                foreach (var (fmid, _) in DispatchedFmids)
                    if (fmid != LogonAck.Fmid) Feed(session, HeaderOnly(fmid));
        }
        finally { Console.SetOut(original); }

        long count = ContainedCount(session);
        Assert.True(count >= 16 * 100, $"expected ~1 900 contained messages, saw {count}");
        int tracked = ContainedFmidCount(session);
        Assert.True(tracked > 0 && tracked <= 64,
            $"per-fmid counters hold {tracked} entries (must be 1..64 — bounded)");
        // The log is throttled (first 10 + one per 500), never one line per message.
        // Count OUR marker lines: other tests in this run share the process console.
        int logLines = writer.ToString().Split('\n')
            .Count(l => l.Contains("[recv] contained malformed message"));
        Assert.True(logLines >= 1 && logLines <= 20,
            $"{logLines} malformed-message log lines for ~1 900 messages (expected 1..20 — rate-limited)");
        Assert.False(session.Lost);
    }
}
