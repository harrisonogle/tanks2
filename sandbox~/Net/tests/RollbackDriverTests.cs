using Tanks.Net;
using Tanks.Sim;
using Xunit;

namespace Tests;

// Deterministic rollback tests: no network, no threads. The test plays the role of the
// wire, delivering the remote's input windows / acks / hashes to the Session with
// whatever delay pattern the scenario wants, and asserts the driver's confirmed states
// are IDENTICAL to a serial reference run of the same inputs — the whole point of
// rollback netcode in one invariant.
public sealed class RollbackDriverTests
{
    private const int RemotePlayer = 1; // local is 0 in these tests

    private static RollbackDriver NewDriver(SimConfig config, Session session)
    {
        return new RollbackDriver(
            config, new Simulation(config), Arena.CreateDefault(config),
            new ScriptedInputSource(Scripts.A), localPlayer: 0, session,
            remote: null, NullLog.Instance);
    }

    /// <summary>
    /// Run the driver for enough frames to reach <paramref name="ticks"/>, delivering
    /// remote inputs with a per-tick arrival delay (in ticks). delay 0 = the input for
    /// tick T is available before T executes (pure lockstep, no prediction).
    /// </summary>
    private static RollbackDriver Run(uint ticks, Func<uint, uint> delayOf, ulong[] referenceHashes, out Session session)
    {
        var config = new SimConfig();
        session = new Session(RollbackDriver.RingCapacity);
        RollbackDriver driver = NewDriver(config, session);
        double step = 1.0 / config.TickRate;

        // Frame f corresponds to executing tick ~f+1. Deliver every remote tick whose
        // arrival frame has come, newest last (frontier must be monotone per OnInput).
        uint delivered = 0;
        for (uint frame = 0; driver.ConfirmedTick < ticks; frame++)
        {
            Assert.True(frame < ticks * 4 + 1000, $"no progress: frame {frame}, confirmed {driver.ConfirmedTick}/{ticks}");

            // Frame f executes tick f+1, so tick T with arrival delay d becomes
            // visible just before the Advance of frame (T-1)+d.
            while (delivered < ticks + Input.Count)
            {
                uint next = delivered + 1;
                if (next + delayOf(next) > frame + 1) break;
                TestHelpers.DeliverInputs(session, next, Scripts.B);
                if (next <= ticks)
                {
                    var sh = new StateHash { Tick = next, Hash = referenceHashes[next] };
                    session.OnStateHash(ref sh);
                }
                delivered = next;
            }

            // The remote acks everything we've executed (out of band here; over the wire
            // it rides the Advantage message).
            var adv = new Advantage { CurrentTick = delivered, LastTickRecvd = driver.CurrentTick };
            session.OnAdvantage(ref adv);

            driver.Advance(step);
        }
        return driver;
    }

    private static void AssertConfirmedMatchesReference(RollbackDriver driver, ulong[] reference, uint upTo)
    {
        uint from = upTo > 200 ? upTo - 200 : 1; // stay inside the history ring
        for (uint t = from; t <= upTo; t++)
        {
            Assert.True(driver.History.TryGet(t, out StateHistorySlot slot), $"missing history for tick {t}");
            Assert.True(reference[t] == slot.Hash, $"tick {t}: rollback state diverged from reference");
        }
    }

    [Fact]
    public void ZeroDelay_PureLockstep_NoRollbacks()
    {
        const uint Ticks = 400;
        var config = new SimConfig();
        ulong[] reference = TestHelpers.ReferenceHashes(config, Ticks + Input.Count, Scripts.A, Scripts.B);

        RollbackDriver driver = Run(Ticks, _ => 0u, reference, out _);

        Assert.Equal(0, driver.RollbackCount);
        Assert.False(driver.DesyncDetected);
        AssertConfirmedMatchesReference(driver, reference, Ticks);
    }

    [Fact]
    public void ConstantDelay_RollsBackAndConverges()
    {
        const uint Ticks = 400;
        var config = new SimConfig();
        ulong[] reference = TestHelpers.ReferenceHashes(config, Ticks + Input.Count, Scripts.A, Scripts.B);

        RollbackDriver driver = Run(Ticks, _ => 3u, reference, out _);

        Assert.True(driver.RollbackCount > 0, "a 3-tick delay with changing inputs must cause rollbacks");
        Assert.False(driver.DesyncDetected, $"desync at tick {driver.DesyncTick}");
        AssertConfirmedMatchesReference(driver, reference, Ticks);
    }

    [Fact]
    public void JitteryDelay_BurstArrivals_Converge()
    {
        const uint Ticks = 600;
        var config = new SimConfig();
        ulong[] reference = TestHelpers.ReferenceHashes(config, Ticks + Input.Count, Scripts.A, Scripts.B);

        // Delay swings 0..6 ticks; slow ticks hold back fast successors, so several
        // ticks become visible on the same frame (burst) and none on others.
        RollbackDriver driver = Run(Ticks, t => (t * 5 + 3) % 7, reference, out _);

        Assert.True(driver.RollbackCount > 0);
        Assert.False(driver.DesyncDetected, $"desync at tick {driver.DesyncTick}");
        AssertConfirmedMatchesReference(driver, reference, Ticks);
    }

    [Fact]
    public void DeepDelay_StallsAtPredictionWindow_ThenCatchesUp()
    {
        var config = new SimConfig();
        var session = new Session(RollbackDriver.RingCapacity);
        RollbackDriver driver = NewDriver(config, session);
        double step = 1.0 / config.TickRate;

        // Never deliver anything: the driver may predict PredictionWindow ticks, then stall.
        for (int frame = 0; frame < 100; frame++)
            driver.Advance(step);

        Assert.Equal((uint)driver.PredictionWindow, driver.CurrentTick);
        Assert.True(driver.StalledSteps > 0);

        // Deliver the remote's first 4 ticks and ack what we've executed: execution resumes.
        TestHelpers.DeliverInputs(session, 4, Scripts.B);
        var adv = new Advantage { CurrentTick = 4, LastTickRecvd = driver.CurrentTick };
        session.OnAdvantage(ref adv);
        for (int frame = 0; frame < 8; frame++)
            driver.Advance(step);

        Assert.Equal(4u, driver.ConfirmedTick);
        Assert.Equal(4u + (uint)driver.PredictionWindow, driver.CurrentTick);
    }

    [Fact]
    public void NoAcks_StallsAtAckBound()
    {
        var config = new SimConfig();
        var session = new Session(RollbackDriver.RingCapacity);
        RollbackDriver driver = NewDriver(config, session);
        double step = 1.0 / config.TickRate;

        // Remote input flows freely, but the remote never acks OUR ticks: the ack
        // bound must stop us at Input.Count so our packets always cover their holes.
        for (uint frame = 1; frame <= 100; frame++)
            TestHelpers.DeliverInputs(session, frame, Scripts.B);
        for (int frame = 0; frame < 50; frame++)
            driver.Advance(step);

        Assert.Equal((uint)Input.Count, driver.CurrentTick);
        Assert.True(driver.StalledSteps > 0);
    }

    [Fact]
    public void CorruptedRemoteHash_SetsDesyncFlag()
    {
        const uint Ticks = 50;
        var config = new SimConfig();
        ulong[] reference = TestHelpers.ReferenceHashes(config, Ticks + Input.Count, Scripts.A, Scripts.B);
        reference[20] ^= 0xDEAD; // sabotage one "remote" hash

        RollbackDriver driver = Run(Ticks, _ => 0u, reference, out _);

        Assert.True(driver.DesyncDetected);
        Assert.Equal(20u, driver.DesyncTick);
    }
}
