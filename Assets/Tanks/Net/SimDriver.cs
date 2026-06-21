using Tanks.Sim;

namespace Tanks.Net
{
    /// <summary>
    /// The pure, Unity-free netcode driver. Owns the live <see cref="GameState"/>, a rollback-
    /// ready <see cref="StateHistory"/>, and the fixed-tick loop that advances the deterministic
    /// simulation independently of frame rate. Everything it needs is injected (arena, input
    /// sources, tick rate) — no statics, no UnityEngine, no wall-clock. The Unity shell
    /// (SimRunner) just forwards <see cref="Advance"/>; tests drive it directly.
    ///
    /// Today it samples every player locally (couch-coop). The netcode work in the session
    /// (send/receive over an <see cref="ITransport"/>, lockstep, rollback) layers onto this.
    /// </summary>
    public sealed class SimDriver
    {
        public GameState State { get; private set; } = null!; // set by ResetMatch in the ctor
        public Arena Arena { get; }
        public ulong LastHash { get; private set; }
        public StateHistory History { get; }

        public SimConfig Config { get; }

        private readonly Simulation _simulation;
        private readonly IInputSource[] _sources;
        private readonly PlayerInput[] _inputs;
        private double _accumulator;

        private const int MaxStepsPerFrame = 5; // clamp to avoid a death spiral after a hitch

        public SimDriver(SimConfig config, Simulation simulation, Arena arena, IInputSource[] sources, int historyCapacity)
        {
            Config = config;
            _simulation = simulation;
            Arena = arena;
            _sources = sources;
            _inputs = new PlayerInput[sources.Length];
            History = new StateHistory(historyCapacity);
            ResetMatch();
        }

        /// <summary>
        /// Accumulate real elapsed time and run as many fixed sim ticks as are now due. This is
        /// the frame-rate-independent bridge: the only place wall-clock time enters the system.
        /// </summary>
        public void Advance(double deltaTime)
        {
            double step = 1.0 / Config.TickRate;
            _accumulator += deltaTime;

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
            for (int i = 0; i < _sources.Length; i++)
                _inputs[i] = _sources[i].Sample();

            _simulation.Tick(State, Arena, _inputs);

            LastHash = State.Hash();
            History.Record(State);
        }

        public void ResetMatch()
        {
            State = GameState.CreateInitial(Config);
            for (int i = 0; i < _sources.Length; i++)
                _sources[i].Reset();
            LastHash = State.Hash();
            History.Clear();
            History.Record(State);
            _accumulator = 0;
        }

        /// <summary>The input most recently applied for a player (for the HUD).</summary>
        public PlayerInput InputOf(int player) => _inputs[player];
    }
}
