using Tanks.Net;
using Tanks.Sim;
using UnityEngine;

namespace Tanks.Game
{
    /// <summary>
    /// Drives the deterministic simulation at a fixed tick rate, independent of frame rate.
    /// Holds the live <see cref="GameState"/>, a rollback-ready history buffer, and the
    /// (not-yet-wired) network seam.
    ///
    /// Today it samples BOTH players locally — a couch-coop sandbox to prove the sim is fun
    /// and correct. The netcode work in your session replaces the marked seam below.
    /// </summary>
    public sealed class SimRunner : MonoBehaviour
    {
        public int TickRate = SimConfig.TickRate;

        public GameState State { get; private set; }
        public Arena Arena { get; private set; }
        public ulong LastHash { get; private set; }
        public StateHistory History { get; private set; }

        // ===================== NETCODE SEAM (wire up during the session) =====================
        // The fake network is created and ready. It is NOT yet driving the simulation.
        //
        // To go online you'll:
        //   1. Sample only the LOCAL player here; send that PlayerInput over Network (InputCodec).
        //   2. Receive the remote player's input; buffer inputs by tick.
        //   3. LOCKSTEP: advance the sim only when both players' inputs for a tick are known
        //      (with a small input delay). Drive Network.Poll(tick) each tick.
        //   4. ROLLBACK: predict the missing remote input, Tick immediately, and when the real
        //      input arrives and differs, restore History.Get(t) and re-Tick forward to now.
        public InProcessNetwork Network { get; private set; }
        // =====================================================================================

        private readonly PlayerInput[] _inputs = new PlayerInput[SimConfig.PlayerCount];
        private double _accumulator;

        private const int HistoryCapacity = 256;
        private const int MaxStepsPerFrame = 5; // clamp to avoid a death spiral after a hitch

        private void Awake()
        {
            Arena = Arena.CreateDefault();
            Network = new InProcessNetwork(); // ready for the session; harmless while unused
            History = new StateHistory(HistoryCapacity);
            ResetMatch();
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.R))
                ResetMatch();

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

        private void StepOnce()
        {
            // LOCAL sandbox: both players sampled on this machine.
            _inputs[0] = InputSampler.SampleP1();
            _inputs[1] = InputSampler.SampleP2();

            Simulation.Tick(State, Arena, _inputs);

            LastHash = State.Hash();
            History.Record(State);
        }

        public void ResetMatch()
        {
            State = GameState.CreateInitial();
            LastHash = State.Hash();
            History.Clear();
            History.Record(State);
            _accumulator = 0;
        }

        /// <summary>The input most recently applied for a player (for the HUD).</summary>
        public PlayerInput InputOf(int player) => _inputs[player];
    }
}
