using Tanks.Net;
using Xunit;

namespace Tests;

public sealed class ReplayWindowTests
{
    [Theory]
    [InlineData(0ul)]
    [InlineData(1ul)]
    [InlineData(ulong.MaxValue)]
    public void FirstPacket_AnySeq_Accepted(ulong seq)
    {
        var window = new ReplayWindow();
        Assert.True(window.TryAccept(seq));
        Assert.False(window.TryAccept(seq)); // immediate replay
    }

    [Fact]
    public void InOrder_Accepted_DuplicatesRejected()
    {
        var window = new ReplayWindow();
        for (ulong seq = 0; seq < 200; seq++)
            Assert.True(window.TryAccept(seq));
        for (ulong seq = 200 - 64; seq < 200; seq++)
            Assert.False(window.TryAccept(seq)); // everything in the window is a replay
    }

    [Fact]
    public void OutOfOrder_WithinWindow_AcceptedExactlyOnce()
    {
        var window = new ReplayWindow();
        Assert.True(window.TryAccept(100));
        Assert.True(window.TryAccept(98));  // late arrival
        Assert.True(window.TryAccept(99));  // later arrival
        Assert.False(window.TryAccept(98)); // replayed late arrival
        Assert.False(window.TryAccept(100));
    }

    [Fact]
    public void TooOld_Rejected_EvenIfNeverSeen()
    {
        var window = new ReplayWindow();
        Assert.True(window.TryAccept(100));
        Assert.True(window.TryAccept(100 - 63));  // oldest trackable slot
        Assert.False(window.TryAccept(100 - 64)); // one past the window
        Assert.False(window.TryAccept(0));
    }

    [Fact]
    public void LargeJump_SlidesWindowCompletely()
    {
        var window = new ReplayWindow();
        Assert.True(window.TryAccept(5));
        Assert.True(window.TryAccept(5000)); // advance > 64: full slide
        Assert.False(window.TryAccept(5000));
        Assert.False(window.TryAccept(5));          // far outside the window now
        Assert.True(window.TryAccept(5000 - 63));   // trackable again
        Assert.True(window.TryAccept(4999));
    }

    [Fact]
    public void Advance_ExactlyWindowSize_ClearsOldBits()
    {
        var window = new ReplayWindow();
        Assert.True(window.TryAccept(10));
        Assert.True(window.TryAccept(10 + 64)); // advance == 64: the shift-mask edge
        Assert.True(window.TryAccept(10 + 63)); // behind == 1, never seen, must be accepted
        Assert.False(window.TryAccept(10 + 63));
    }
}
