using Tanks.Net;
using Tanks.Sim;
using UnityEngine;

namespace Tanks.Game
{
    /// <summary>
    /// Represents a single match between peers.
    /// </summary>
    public sealed class SimRunner : MonoBehaviour
    {
        // A match is in progress iff this is not null
        // Transition from null to non-null effectively starts the match
        private SimDriver? _driver;

        /// <summary>Injected by Bootstrap immediately after AddComponent, before the first Update.</summary>
        public SimDriver Driver { set => _driver = value; }

        public void StartMatch(SimDriver driver)
        {
            if (_driver is not null)
            {
                Debug.Log("Match already in progress.");
                return;
            }

            Debug.Log("Starting match.");
            _driver = driver;
        }

        // Read-only surface consumed by GameView / DebugHud.
        public GameState? State => _driver?.State;
        public Arena? Arena => _driver?.Arena;
        public SimConfig? Config => _driver?.Config;
        public ulong LastHash => _driver?.LastHash ?? 0UL;

        private void Update()
        {
            if (_driver == null) return;

            if (InputSampler.IsResetRequested())
                _driver.ResetMatch();

            _driver.Advance(Time.deltaTime);
        }
    }
}
