using Tanks.Net;
using Xunit;

namespace Tests;

public sealed unsafe class SessionTableTests
{
    // One shared SessionProtocol: the table only stores the reference, and
    // RSA keygen is too slow to repeat 100k times. The umem is held statically
    // so its finalizer can't free the rings mid-test.
    private static readonly Umem s_umem = new Umem(slotCount: 64, slotSize: NetworkConstants.ApplicationDataSlotSize);
    private static readonly SessionProtocol s_session = CreateSession();

    private static SessionProtocol CreateSession()
    {
        var (transport, _) = InMemoryDatagramTransport.Create();
        SessionContext ctx = TestHelpers.CreateSessionContext();
        return new SessionProtocol(null, 0, ctx, transport, s_umem, NullLog.Instance, new NetworkMetrics());
    }

    [Fact]
    public void PeekThenAdd_Succeeds_AndIsRetrievable()
    {
        var table = new SessionTable();

        Assert.True(table.TryPeek(out SessionId sid));
        Assert.True(table.TryAdd(sid, s_session));
        Assert.True(table.TryGetValue(sid, out SessionProtocol? found));
        Assert.Same(s_session, found);
        Assert.Equal(1, table.Count);
    }

    [Fact]
    public void Add_WithStaleGen_Fails()
    {
        var table = new SessionTable();

        Assert.True(table.TryPeek(out SessionId sid));
        var stale = new SessionId(sid.SlotId, sid.Gen + 1);
        Assert.False(table.TryAdd(stale, s_session));
    }

    [Fact]
    public void Peek_IsInvalidatedByInterveningRemove()
    {
        var table = new SessionTable();

        // Occupy one slot.
        Assert.True(table.TryPeek(out SessionId first));
        Assert.True(table.TryAdd(first, s_session));

        // Peek the next slot, then mutate the table before adding.
        Assert.True(table.TryPeek(out SessionId peeked));
        Assert.True(table.TryRemove(first, out _)); // pushes first's slot on the free stack

        // The stale peek must be rejected (LIFO adjacency violated)...
        Assert.False(table.TryAdd(peeked, s_session));

        // ...and a fresh peek must succeed.
        Assert.True(table.TryPeek(out SessionId repeeked));
        Assert.True(table.TryAdd(repeeked, s_session));
    }

    [Fact]
    public void SlotChurn_GensIncrease_AndTableSurvives()
    {
        var table = new SessionTable();

        // Churn one slot (the LIFO free list reuses the same slot every time).
        // With 32-bit gens the wrap is out of practical reach, but gen 0 stays
        // the reserved "unset DSID" sentinel and every reuse must be addable.
        uint previousGen = 0;
        for (int i = 0; i < 100_000; i++)
        {
            Assert.True(table.TryPeek(out SessionId sid));
            Assert.NotEqual(0u, sid.Gen);
            Assert.True(sid.Gen > previousGen);
            previousGen = sid.Gen;
            Assert.True(table.TryAdd(sid, s_session));
            Assert.True(table.TryGetValue(sid, out _));
            Assert.True(table.TryRemove(sid, out _));
        }

        Assert.Equal(0, table.Count);
    }

    [Fact]
    public void Get_WithStaleGen_ReturnsNull()
    {
        var table = new SessionTable();

        Assert.True(table.TryPeek(out SessionId sid));
        Assert.True(table.TryAdd(sid, s_session));
        Assert.True(table.TryRemove(sid, out _));

        // Reuse the slot; the old sid must no longer resolve.
        Assert.True(table.TryPeek(out SessionId reused));
        Assert.True(table.TryAdd(reused, s_session));
        Assert.Equal(sid.SlotId, reused.SlotId);
        Assert.Null(table.Get(sid));
        Assert.NotNull(table.Get(reused));
    }

    [Fact]
    public void Iterator_RemoveWhileIterating_VisitsEverySurvivor()
    {
        var table = new SessionTable();
        for (int i = 0; i < 6; i++)
        {
            Assert.True(table.TryPeek(out SessionId sid));
            Assert.True(table.TryAdd(sid, s_session));
        }

        // Reap every other visited entry.
        SessionTable.Iterator it = default;
        Assert.True(table.TryGetIterator(ref it));
        int visited = 0;
        bool more;
        do
        {
            _ = it.Current;
            bool reap = (visited & 1) == 0;
            visited++;
            more = it.Advance(reap);
        }
        while (more);

        Assert.Equal(6, visited);
        Assert.Equal(3, table.Count);
    }
}
