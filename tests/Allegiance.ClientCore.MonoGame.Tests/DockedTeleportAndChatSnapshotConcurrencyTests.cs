using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P319 (findings H-2 / H-3) — the two unlocked-collection render paths that the
/// P306 ReadyCheckRoster.Gather fix (thread-safe snapshot) left as outliers:
/// <c>ProcessIncomingQuickChatVoices</c>'s indexed watermark walk of
/// <c>ClientWorld.Events.Chats</c>, and <c>BuildTeleportModel</c>'s enumeration of
/// <c>ClientWorld.Ships</c> / <c>ClientWorld.Stations</c>. The receive thread appends
/// (and, past the 500-entry bound, evicts from the front) the chat log and upserts
/// the two entity dictionaries under the session <c>Gate</c>; the render thread must
/// snapshot under that same lock.
///
/// These tests drive a REAL <c>ClientWorld</c> — messages fed through the same
/// <c>ClientWorld.Apply</c> path the wire receive handler uses — and mirror the
/// ReadyCheckRosterTests idiom (a stand-in gate object + Apply-fed world).
/// </summary>
public class DockedTeleportAndChatSnapshotConcurrencyTests
{
    private const short MySide = 4;
    private static readonly object Gate = new();   // stand-in for the session's world-read lock

    private static DataStationIGC Station(short id) =>
        new(
            Position: [0f, 0f, 0f],
            Up: [0f, 1f, 0f],
            Forward: [0f, 0f, -1f],
            Rotation: [0f, 0f, 0f],
            ClusterId: 333,
            SideId: MySide,
            StationId: id,
            StationTypeId: 2,
            BpHull: 0,
            BpShield: 0,
            Name: "st" + id);

    /// <summary>The exact receive-thread mutation stream, all under the session
    /// Gate: append a <see cref="ChatMessage"/> (which also evicts the oldest once
    /// the log passes <see cref="EventLog.Bound"/>) and upsert one ship + one
    /// station — precisely what <c>OnReceive</c> does via <c>ClientWorld.Apply</c>.</summary>
    private static void ReceiveThreadMutate(ClientWorld w, int n)
    {
        lock (Gate)
        {
            w.Apply(new ChatMessage { Message = "hello " + n, VoiceOver = (short)(n % 40) });
            w.Apply(new ExportShip { ShipId = (short)(n % 60), SideId = MySide, HullId = 707 });
            w.Apply(new ExportStation { Station = Station((short)(n % 8)) });
        }
    }

    /// <summary>The exact (fixed) render-thread read of
    /// <c>ProcessIncomingQuickChatVoices</c>: walk the chat log by index from the
    /// watermark under the lock, collecting the new arrivals; the caller already
    /// holds <see cref="Gate"/>. Returns how many new messages were walked.</summary>
    private static int ReadQuickChatUnderGate(ClientWorld w, ref ChatMessage? lastVoice)
    {
        var chats = w.Events.Chats;
        if (chats.Count == 0) return 0;
        int start = 0;
        if (lastVoice is not null)
        {
            bool found = false;
            for (int i = 0; i < chats.Count; i++)
            {
                if (ReferenceEquals(chats[i], lastVoice)) { start = i + 1; found = true; break; }
            }
            if (!found) { lastVoice = chats[^1]; return 0; }   // evicted → re-anchor on newest
        }
        int seen = 0;
        for (int i = start; i < chats.Count; i++) { lastVoice = chats[i]; seen++; }
        return seen;
    }

    [Fact]
    public async Task Chat_walk_and_teleport_snapshot_survive_high_concurrency_under_gate()
    {
        // Both fixed render reads hold the Gate while touching the shared world
        // tables; concurrent receive-thread writers (also under the Gate) can thus
        // append/evict chats and upsert ships/stations as fast as they like without
        // ever breaking a read in flight. Run a tight, multi-threaded exchange for a
        // couple of seconds and assert the reads never throw.
        var w = new ClientWorld();
        w.Apply(new ExportShip { ShipId = -1, SideId = MySide });   // own-ship export → MySideId

        const int writers = 2;
        const int readers = 2;
        const double runSeconds = 2.0;
        long chatIterations = 0;
        long teleportIterations = 0;
        Exception? failure = null;
        var stats = new object();

        var writersTasks = new List<Task>();
        for (int wi = 0; wi < writers; wi++)
        {
            int seed = wi;
            writersTasks.Add(Task.Run(() =>
            {
                int n = seed;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < runSeconds * 1000)
                    ReceiveThreadMutate(w, n++);
            }));
        }

        // The render thread alternates between the two fixed reads.
        ChatMessage? lastVoice = null;   // render-thread-only voice watermark (closure cell)
        var readersTasks = new List<Task>();
        for (int ri = 0; ri < readers; ri++)
        {
            readersTasks.Add(Task.Run(() =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < runSeconds * 1000)
                {
                    try
                    {
                        // ProcessIncomingQuickChatVoices snapshot.
                        lock (Gate)
                        {
                            lock (stats) chatIterations += ReadQuickChatUnderGate(w, ref lastVoice);
                        }
                        // BuildTeleportModel snapshot (ships then stations).
                        lock (Gate)
                        {
                            var ships = new List<(short Sid, short Side, short StationId, short Hull)>();
                            foreach (var s in w.Ships.Values)
                                ships.Add((s.ShipId, s.SideId, s.StationId, s.HullId));
                            var stations = new List<(short Sid, short Side, short TypeId, string Name)>();
                            foreach (var s in w.Stations.Values)
                                stations.Add((s.StationId, s.SideId, s.StationTypeId, s.Name));
                            lock (stats) teleportIterations++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Interlocked.CompareExchange(ref failure, ex, null);
                        return;
                    }
                }
            }));
        }

        await Task.WhenAll(readersTasks);
        await Task.WhenAll(writersTasks);

        Assert.Null(failure);                       // no InvalidOperationException across every rep
        Assert.True(chatIterations > 0, "reader must actually walk the growing/evicting chat log");
        Assert.True(teleportIterations > 0, "reader must actually enumerate the world tables");
    }

    [Fact]
    public void Unlocked_foreach_over_world_tables_throws_when_a_peer_arrives_mid_iteration()
    {
        // Deterministic proof that the H-3 hazard is real and that holding the Gate
        // (as BuildTeleportModel now does) is what prevents it: the receive thread
        // ADDING a brand-new ship (a teammate EXPORT(26) arrival) bumps
        // ClientWorld.Ships' version; an unlocked foreach over Ships.Values then
        // throws InvalidOperationException ("Collection was modified"). A barrier
        // places the Add mid-iteration, so there is no reliance on winning a race —
        // it throws every time when unlocked.
        var w = new ClientWorld();
        w.Apply(new ExportShip { ShipId = -1, SideId = MySide });
        for (short id = 1; id <= 2000; id++)            // a non-trivial population to be mid-way through
            w.Apply(new ExportShip { ShipId = id, SideId = MySide, HullId = 703 });

        using var entered = new ManualResetEventSlim(false);
        using var addNow = new ManualResetEventSlim(false);
        Exception? addError = null;
        var adder = new Thread(() =>
        {
            try
            {
                addNow.Wait();
                w.Apply(new ExportShip { ShipId = 8811, SideId = MySide, HullId = 710 }); // new peer EXPORT
            }
            catch (Exception ex) { addError = ex; }
        });
        adder.Start();

        var thrown = Record.Exception(() =>
        {
            foreach (var s in w.Ships.Values)           // UNLOCKED — the pre-fix access
            {
                if (s.ShipId == 888)                    // mid-way through the enumeration
                {
                    entered.Set();
                    addNow.Set();
                    adder.Join(TimeSpan.FromSeconds(5));// let the new peer land before MoveNext
                }
            }
        });

        adder.Join(TimeSpan.FromSeconds(5));
        Assert.Null(addError);
        Assert.IsType<InvalidOperationException>(thrown);
    }
}
