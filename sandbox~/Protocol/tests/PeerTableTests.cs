using Tanks.Net;
using Xunit;

namespace Tests;

public sealed class PeerTableTests
{
    private static SessionId Sid(int n) => new((uint)n, 1);

    [Fact]
    public void AddGetRemove_Roundtrip()
    {
        var table = new PeerTable();
        var rng = new Random(1);
        PeerId peer = TestHelpers.RandomPeerId(rng);

        Assert.True(table.TryAdd(in peer, Sid(7)));
        Assert.True(table.TryGetValue(in peer, out SessionId sid));
        Assert.Equal(Sid(7), sid);
        Assert.True(table.TryRemove(in peer, out sid));
        Assert.Equal(Sid(7), sid);
        Assert.False(table.TryGetValue(in peer, out _));
        Assert.Equal(0, table.Count);
    }

    [Fact]
    public void TryAdd_IsIdempotent_ButRejectsRemap()
    {
        var table = new PeerTable();
        var rng = new Random(2);
        PeerId peer = TestHelpers.RandomPeerId(rng);

        Assert.True(table.TryAdd(in peer, Sid(1)));
        Assert.True(table.TryAdd(in peer, Sid(1)));  // same mapping: ok
        Assert.False(table.TryAdd(in peer, Sid(2))); // different sid: rejected
        Assert.Equal(1, table.Count);
    }

    [Fact]
    public void TrySet_Overwrites()
    {
        var table = new PeerTable();
        var rng = new Random(3);
        PeerId peer = TestHelpers.RandomPeerId(rng);

        Assert.True(table.TrySet(in peer, Sid(1)));
        Assert.True(table.TrySet(in peer, Sid(2)));
        Assert.True(table.TryGetValue(in peer, out SessionId sid));
        Assert.Equal(Sid(2), sid);
        Assert.Equal(1, table.Count);
    }

    [Fact]
    public void ManyPeers_GrowRehashAndBackshift_PreserveAllMappings()
    {
        // Exercises growth past the initial 256-entry load threshold and the
        // backshift deletion path under whatever collisions the hash produces.
        var table = new PeerTable();
        var rng = new Random(4);

        var peers = new List<PeerId>();
        var sids = new Dictionary<int, SessionId>();
        for (int i = 0; i < 1000; i++)
        {
            PeerId peer = TestHelpers.RandomPeerId(rng);
            peers.Add(peer);
            sids[i] = Sid(i);
            Assert.True(table.TryAdd(in peer, sids[i]));
        }
        Assert.Equal(1000, table.Count);

        // Remove a random half.
        var removed = new HashSet<int>();
        while (removed.Count < 500)
        {
            int i = rng.Next(1000);
            if (!removed.Add(i)) continue;
            PeerId peer = peers[i];
            Assert.True(table.TryRemove(in peer, out SessionId sid));
            Assert.Equal(sids[i], sid);
        }

        // Every survivor must still resolve; every removed peer must not.
        for (int i = 0; i < 1000; i++)
        {
            PeerId peer = peers[i];
            bool found = table.TryGetValue(in peer, out SessionId sid);
            if (removed.Contains(i))
            {
                Assert.False(found);
            }
            else
            {
                Assert.True(found);
                Assert.Equal(sids[i], sid);
            }
        }
        Assert.Equal(500, table.Count);
    }
}
