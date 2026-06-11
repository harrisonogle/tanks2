using Tanks.Net;
using Tanks.Sim;
using UnityEngine;

namespace Tanks.Game
{
    /// <summary>
    /// Drives the shared <see cref="InProcessNetwork"/>'s clock. The fake network only moves
    /// packets from "in flight" to an endpoint's inbox during <see cref="InProcessNetwork.Poll"/> —
    /// without this component, nothing is ever delivered.
    ///
    /// The clock here is FREE-RUNNING (its own 60 Hz accumulator), deliberately not either
    /// peer's sim tick: under lockstep (M2) a sim stalls while waiting for remote input, but
    /// the network must keep delivering — real networks don't pause when your game loop does.
    ///
    /// Runs before the SimRunners each frame (execution order -100) so packets due "now" are
    /// already in the inbox when the peers drain.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public sealed class NetworkPump : MonoBehaviour
    {
        public InProcessNetwork Network;

        private double _accumulator;
        private uint _netTick;

        private void Update()
        {
            if (Network == null) return; // not wired by Bootstrap yet

            double step = 1.0 / SimConfig.TickRate;
            _accumulator += Time.deltaTime;
            if (_accumulator > 0.25) _accumulator = 0.25; // cap catch-up after a hitch

            while (_accumulator >= step)
            {
                Network.Poll(++_netTick);
                _accumulator -= step;
            }
        }
    }
}
