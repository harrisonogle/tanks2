using Tanks.Net;
using Tanks.Sim;
using UnityEngine;

namespace Tanks.Game
{
    /// <summary>
    /// Represents a single match between peers, plus the per-frame pump of the protocol
    /// pipe. The pump runs every frame — even with no match — because session lifecycle
    /// events (the thing that STARTS a match) arrive through it. Ordering per frame is
    /// deliberate and load-bearing: Poll (network events settle) then Advance (sim ticks).
    /// </summary>
    public sealed class SimRunner : MonoBehaviour
    {
        // A match is in progress iff this is not null.
        // Transition from null to non-null effectively starts the match.
        private IMatchDriver _driver;
        private RemoteState _remote;

        /// <summary>Injected by Bootstrap immediately after AddComponent, before the first Update.</summary>
        public RemoteState Remote { set => _remote = value; }

        /// <summary>The running match driver, if any (DebugHud peeks for netcode stats).</summary>
        public IMatchDriver Driver => _driver;

        public void StartMatch(IMatchDriver driver)
        {
            if (_driver != null)
            {
                Debug.Log("Match already in progress.");
                return;
            }

            Debug.Log("Starting match.");
            _driver = driver;
        }

        public void EndMatch()
        {
            if (_driver == null) return;
            Debug.Log("Match ended.");
            _driver = null;
        }

        // Read-only surface consumed by GameView / DebugHud.
        public GameState State => _driver?.State;
        public Arena Arena => _driver?.Arena;
        public SimConfig Config => _driver?.Config;
        public ulong LastHash => _driver?.LastHash ?? 0UL;

        private void Update()
        {
            _remote?.Poll();

            if (_driver == null) return;

            // R resets couch-coop only; resetting one side of a networked match = desync.
            if (_driver.AllowsLocalReset && InputSampler.IsResetRequested())
                _driver.ResetMatch();

            _driver.Advance(Time.deltaTime);
        }
    }
}
