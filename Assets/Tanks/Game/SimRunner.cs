using System;
using Tanks.Net;
using Tanks.Sim;
using UnityEngine;

namespace Tanks.Game
{
    /// <summary>
    /// One peer of a 1v1 LOCKSTEP session. Samples only its assigned player, exchanges
    /// inputs over the wire, and advances the deterministic sim only when both players'
    /// inputs for the next tick are known — so the two peers compute bit-identical states.
    ///
    /// This is the same protocol proven headless in SimTests~/LockstepTests.cs. If the
    /// game desyncs but that test is green, suspect the wiring here, not the protocol.
    /// </summary>
    public sealed class SimRunner : MonoBehaviour
    {
        public int TickRate = SimConfig.TickRate;

        /// <summary>Which player this peer owns (0 or 1). Set by Bootstrap before the first Update.</summary>
        public int LocalPlayer;

        /// <summary>This peer's end of the wire (peer 0 ↔ EndpointA, peer 1 ↔ EndpointB). Set by Bootstrap.</summary>
        public ITransport Transport;

        public GameState State { get; private set; }
        public Arena Arena { get; private set; }
        public ulong LastHash { get; private set; }
        public StateHistory History { get; private set; }

        // ============================== LOCKSTEP CONVENTION ==============================
        //   State.Tick  = tick of the CURRENT state (already simulated)
        //   next        = State.Tick + 1      — the tick we want to simulate now
        //   scheduled   = next + InputDelay   — the tick a freshly sampled input is for
        // We may simulate `next` only when BOTH players' inputs for `next` are in the ring;
        // otherwise we stall this frame and the view keeps rendering the last good state.
        // Local input therefore takes effect InputDelay ticks (~50 ms) after you press it —
        // and when the wire is slower than that headroom, the whole sim visibly slows down.
        // That tradeoff (sync is absolute, speed is not) is exactly what rollback (M3) fixes.
        // =================================================================================
        public const int InputDelay = 3;

        private readonly InputRing _ring = new InputRing();
        private uint _lastScheduledTick;

        /// <summary>Total packets drained from the transport (HUD: proves the wire is live).</summary>
        public int PacketsReceived { get; private set; }
        /// <summary>Tick stamped on the most recently received packet.</summary>
        public uint LastRxTick { get; private set; }
        /// <summary>Input carried by the most recently received packet.</summary>
        public PlayerInput LastRxInput { get; private set; }
        /// <summary>Fixed steps skipped because the remote input hadn't arrived yet.</summary>
        public int Stalls { get; private set; }

        private readonly PlayerInput[] _inputs = new PlayerInput[SimConfig.PlayerCount];
        private double _accumulator;

        private const int HistoryCapacity = 256;
        private const int MaxStepsPerFrame = 5;            // clamp to avoid a death spiral after a hitch
        private const double MaxAccumulatedSeconds = 0.25; // don't bank seconds of fast-forward during a stall

        private void Awake()
        {
            Arena = Arena.CreateDefault();
            History = new StateHistory(HistoryCapacity);
            ResetMatch();
        }

        private void Update()
        {
            if (InputSampler.IsResetRequested())
                ResetMatch();

            DrainNetwork(); // the pump (execution order -100) has already polled this frame

            double step = 1.0 / TickRate;
            _accumulator += Time.deltaTime;
            if (_accumulator > MaxAccumulatedSeconds) _accumulator = MaxAccumulatedSeconds;

            int steps = 0;
            while (_accumulator >= step && steps < MaxStepsPerFrame)
            {
                if (!TryStepOnce())
                    break; // stall: missing remote input — retry next frame, render last good state
                _accumulator -= step;
                steps++;
            }
        }

        private void DrainNetwork()
        {
            if (Transport == null) return; // not wired by Bootstrap yet
            while (Transport.TryReceive(out var pkt))
            {
                InputCodec.Read(pkt, out uint tick, out int player, out PlayerInput input);
                PacketsReceived++;
                LastRxTick = tick;
                LastRxInput = input;

                if (player == LocalPlayer) continue;             // our own input never rides the wire back
                if (tick > State.Tick + InputDelay + 64) continue; // stale pre-reset traffic: a packet from
                                                                   // before a match reset carries a huge tick;
                                                                   // recording it would detonate as a desync
                                                                   // minutes later when the sim reaches it
                _ring.Record(tick, player, input);
            }
        }

        /// <summary>Advance one tick if both inputs are known. Returns false on a stall.</summary>
        private bool TryStepOnce()
        {
            uint next = State.Tick + 1;

            // Schedule-once: keep our inputs scheduled through next+InputDelay, sampling each
            // tick index EXACTLY ONCE — re-sampling a stalled tick could send two different
            // values for the same tick, and the peers might consume different versions.
            // (The while normally runs 0 or 1 times; it exists so the invariant holds no
            // matter how the surrounding loop evolves — gaps here deadlock both peers.)
            uint targetScheduled = next + InputDelay;
            if (_lastScheduledTick < targetScheduled)
            {
                PlayerInput local = LocalPlayer == 0 ? InputSampler.SampleP1() : InputSampler.SampleP2();
                while (_lastScheduledTick < targetScheduled)
                {
                    _lastScheduledTick++;
                    _ring.Record(_lastScheduledTick, LocalPlayer, local);
                }
            }

            SendRecentInputs();

            if (!_ring.Has(next, 0) || !_ring.Has(next, 1))
            {
                Stalls++;
                return false;
            }

            _inputs[0] = _ring.Get(next, 0);
            _inputs[1] = _ring.Get(next, 1);
            Simulation.Tick(State, Arena, _inputs);

            LastHash = State.Hash();
            History.Record(State);
            return true;
        }

        private void SendRecentInputs()
        {
            if (Transport == null) return;

            // Re-send every scheduled tick the peer could still need. Peers can't drift more
            // than InputDelay+1 ticks apart (to simulate a tick you need an input the peer
            // scheduled InputDelay ticks before playing it), so this window always covers a
            // dropped packet while it still matters: pure redundancy instead of an ack
            // protocol, ~8 small packets per step.
            uint next = State.Tick + 1;
            uint windowStart = next > InputDelay + 1 ? next - InputDelay - 1 : 1u;
            Span<byte> buf = stackalloc byte[InputCodec.MessageSize];
            for (uint t = windowStart; t <= _lastScheduledTick; t++)
            {
                InputCodec.Write(buf, t, LocalPlayer, _ring.Get(t, LocalPlayer));
                Transport.Send(buf);
            }
        }

        public void ResetMatch()
        {
            State = GameState.CreateInitial();
            InputSampler.Reset();   // realign stored turret angles with the new spawn orientations
            LastHash = State.Hash();
            History.Clear();
            History.Record(State);
            _accumulator = 0;

            // Pre-seed the delay window: both peers agree by convention that ticks
            // 1..InputDelay are None for everyone. Without this, both peers deadlock at
            // tick 1 waiting for inputs nobody ever scheduled.
            _ring.Clear();
            for (uint t = 1; t <= InputDelay; t++)
            {
                _ring.Record(t, 0, PlayerInput.None);
                _ring.Record(t, 1, PlayerInput.None);
            }
            _lastScheduledTick = InputDelay;
            Stalls = 0;
        }

        /// <summary>The input most recently applied for a player (for the HUD).</summary>
        public PlayerInput InputOf(int player) => _inputs[player];
    }
}
