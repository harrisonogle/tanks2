using System.Collections.Generic;
using System.Net;

namespace Tanks.Net;

// Discovery shim for direct-connect sessions: the "scan" is a human reading
// the other player's PeerId + IP:port off their screen and typing it in.
// Set() is that data entry; GetPeers() surfaces the result exactly like a
// real discovery backend would, so the consumer doesn't know the difference.
//
// Single-threaded by design (game thread owns it). The real discovery
// component runs on its own thread behind a single-writer/multi-reader
// table — that concurrency story arrives with it, not here.
public sealed class ManualPeerDiscovery : IPeerDiscovery
{
    private PeerDiscoveryResult _peer;
    private bool _hasPeer;

    public void Set(PeerId peerId, IPEndPoint endPoint)
    {
        _peer = new PeerDiscoveryResult(peerId, endPoint);
        _hasPeer = true;
    }

    public void Clear()
    {
        _peer = default;
        _hasPeer = false;
    }

    public IEnumerable<PeerDiscoveryResult> GetPeers()
    {
        if (_hasPeer)
            yield return _peer;
    }
}
