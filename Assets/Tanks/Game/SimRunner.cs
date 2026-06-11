using Tanks.Net;
using Tanks.Sim;
using UnityEngine;

namespace Tanks.Game
{
    /// <summary>
    /// Drives the deterministic simulation at a fixed tick rate, independent of frame rate.
    /// Holds the live <see cref="GameState"/>, a rollback-ready history buffer, and one end
    /// of the wire.
    ///
    /// M1 shape: ONE PEER of a 1v1. It samples only its assigned player, sends that input
    /// across its transport every tick, and counts what arrives from the other peer. The
    /// remote player's input is NOT consumed yet — the remote tank stays frozen until the
    /// lockstep loop (M2) records received inputs into a per-tick buffer and advances on them.
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

        // ===================== NETCODE SEAM (wire up during the session) =====================
        //   1. DONE — sample only the LOCAL player; send that PlayerInput over the transport.
        //   2. DONE — receive the remote player's bytes (just counted for now, see below).
        //   3. LOCKSTEP (M2): buffer inputs by tick; advance the sim only when both players'
        //      inputs for a tick are known (with a small input delay).
        //   4. ROLLBACK (M3): predict the missing remote input, Tick immediately, and when the
        //      real input arrives and differs, restore History.Get(t) and re-Tick forward.
        // =====================================================================================

        /// <summary>Total packets drained from the transport (HUD: proves the wire is live).</summary>
        public int PacketsReceived { get; private set; }
        /// <summary>Tick stamped on the most recently received packet.</summary>
        public uint LastRxTick { get; private set; }
        /// <summary>Input carried by the most recently received packet.</summary>
        public PlayerInput LastRxInput { get; private set; }

        private readonly PlayerInput[] _inputs = new PlayerInput[SimConfig.PlayerCount];
        private double _accumulator;

        private const int HistoryCapacity = 256;
        private const int MaxStepsPerFrame = 5; // clamp to avoid a death spiral after a hitch

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

            DrainNetwork(); // before stepping — the shape the lockstep loop wants

            double step = 1.0 / TickRate;
            _accumulator += Time.deltaTime;

            int steps = 0;
            while (_accumulator >= step && steps < MaxStepsPerFrame)
            {
                StepOnce();
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
                // M2 records (tick, player, input) into a ring here; M1 only proves the wire is live.
            }
        }

        private void StepOnce()
        {
            // Sample ONLY our player; the remote slot stays None until M2 consumes real inputs.
            PlayerInput local = LocalPlayer == 0 ? InputSampler.SampleP1() : InputSampler.SampleP2();
            _inputs[LocalPlayer] = local;
            _inputs[1 - LocalPlayer] = PlayerInput.None;

            // Ship our input to the peer, stamped with the tick it is about to play into.
            if (Transport != null)
                Transport.Send(InputCodec.ToBytes(State.Tick + 1, LocalPlayer, local));

            Simulation.Tick(State, Arena, _inputs);

            LastHash = State.Hash();
            History.Record(State);
        }

        public void ResetMatch()
        {
            State = GameState.CreateInitial();
            InputSampler.Reset();   // realign stored turret angles with the new spawn orientations
            LastHash = State.Hash();
            History.Clear();
            History.Record(State);
            _accumulator = 0;
        }

        /// <summary>The input most recently applied for a player (for the HUD).</summary>
        public PlayerInput InputOf(int player) => _inputs[player];
    }
}
