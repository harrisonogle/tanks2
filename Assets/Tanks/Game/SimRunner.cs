using Tanks.Net;
using Tanks.Sim;
using UnityEngine;

namespace Tanks.Game
{
    /// <summary>
    /// Thin Unity shell over the pure <see cref="SimDriver"/>. It holds no gameplay logic: it
    /// forwards the per-frame tick to the driver (marshaling Unity's frame time across), routes
    /// the reset control, and re-exposes the driver's read-only state for the view and HUD.
    ///
    /// The driver is built and injected by <see cref="Bootstrap"/> (the composition root) right
    /// after this component is added, before the first frame. The netcode work in the session
    /// (transport, discovery, lockstep, rollback) lives in the driver, not here.
    /// </summary>
    public sealed class SimRunner : MonoBehaviour
    {
        private SimDriver _driver;

        /// <summary>Injected by Bootstrap immediately after AddComponent, before the first Update.</summary>
        public SimDriver Driver { set => _driver = value; }

        // Read-only surface consumed by GameView / DebugHud.
        public GameState State => _driver?.State;
        public Arena Arena => _driver?.Arena;
        public SimConfig Config => _driver?.Config;
        public ulong LastHash => _driver != null ? _driver.LastHash : 0UL;
        public StateHistory History => _driver?.History;

        private void Update()
        {
            if (_driver == null) return;

            if (InputSampler.IsResetRequested())
                _driver.ResetMatch();

            _driver.Advance(Time.deltaTime);
        }
    }
}
