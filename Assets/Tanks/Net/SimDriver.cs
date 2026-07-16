using Tanks.Sim;

namespace Tanks.Net
{
    /// <summary>
    /// The couch-coop driver: every player sampled from a local input source, no network.
    /// Pure and Unity-free like its networked sibling <see cref="RollbackDriver"/>; the
    /// Unity shell just forwards <see cref="Advance"/>, tests drive it directly.
    /// </summary>
    public sealed class SimDriver : IMatchDriver
    {
        public GameState State => _state;
        public Sim.Arena Arena => _arena;
        public ulong LastHash => _lastHash;
        public StateHistory History => _history;
        public SimConfig Config => _config;

        private readonly Simulation _simulation;
        private readonly IInputSource[] _sources;
        private readonly PlayerInput[] _inputs;
        private double _accumulator;

        private GameState _state;
        private Arena _arena;
        private ulong _lastHash;
        private StateHistory _history;
        private SimConfig _config;
        
        private const int MaxStepsPerFrame = 5; // clamp to avoid a death spiral after a hitch
        
        public SimDriver(SimConfig config, Simulation simulation, Sim.Arena arena, IInputSource[] sources)
        {
            _config = config;
            _simulation = simulation;
            _arena = arena;
            _sources = sources;
            _inputs = new PlayerInput[sources.Length];
            _history = new StateHistory(256);
            ResetMatch();
        }

        public bool AllowsLocalReset => true;

        public void ResetMatch()
        {
            _state = GameState.CreateInitial(Config);
            for (int i = 0; i < _sources.Length; i++)
                _sources[i].Reset();
            _history.Clear();
            _lastHash = _history.Record(State);
            _accumulator = 0;
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

            _lastHash = _history.Record(State);
        }
    }
}
