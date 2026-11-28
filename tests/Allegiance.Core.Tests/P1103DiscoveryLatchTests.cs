using Allegiance.Core.Transport;
using Allegiance.Server;

namespace Allegiance.Core.Tests;

public sealed class P1103DiscoveryLatchTests
{
    [Fact]
    public void Membership_and_invalidation_match_tuple_set_through_slot_and_id_reuse()
    {
        var old = new SweepLatchSet<(ConnectionId Peer, byte Kind, short Id)>();
        var latch = new DiscoveryLatch();
        var random = new Random(1103);
        var peers = Enumerable.Range(0, 196).Select(i => new ConnectionId(uint.MaxValue - (uint)i * 65537)).ToArray();

        void Check()
        {
            Assert.Equal(old.Count, latch.Count);
            Assert.Equal(old.RemovalVersion, latch.RemovalVersion);
        }
        void See(ConnectionId peer, byte kind, short id)
        {
            Assert.Equal(old.Add((peer, kind, id)), latch.Add((peer, kind, id)));
            Check();
        }
        void Leave(ConnectionId peer)
        {
            old.RemoveWhere(e => e.Peer == peer);
            latch.RemovePeer(peer);
            Check();
        }
        void Migrate(HashSet<ConnectionId> leaving)
        {
            old.RemoveWhere(e => leaving.Contains(e.Peer));
            latch.RemovePeers(leaving);
            latch.TrimExcessIfWorthwhile();
            Check();
        }

        // Cross three word boundaries. Destroying/recreating an object with
        // the same kind and short ID is indistinguishable to the original set.
        foreach (var peer in peers)
        {
            See(peer, 4, short.MinValue);
            See(peer, 4, short.MinValue);
            See(peer, 7, short.MinValue);
        }
        Leave(peers[64]);
        Leave(peers[64]);
        See(ConnectionId.Me, 4, short.MinValue);
        See(peers[64], 7, short.MinValue);
        Migrate(peers.Where((_, i) => i % 3 == 0).ToHashSet());
        for (int i = 0; i < 8000; i++)
        {
            var peer = peers[random.Next(peers.Length)];
            if (i % 31 == 0) Leave(peer);
            else if (i % 401 == 0) Migrate(peers.Where(_ => random.Next(7) == 0).ToHashSet());
            else See(peer, (byte)random.Next(3), (short)random.Next(-5, 6));
        }
        // Probe every surviving answer, including kinds that share an ID.
        foreach (var peer in peers)
        for (byte kind = 0; kind < 3; kind++)
        for (short id = -5; id <= 5; id++)
            See(peer, kind, id);
        old.Clear();
        latch.Clear();
        latch.TrimExcessIfWorthwhile();
        Check();
        See(peers[^1], 4, short.MinValue);
    }
}
