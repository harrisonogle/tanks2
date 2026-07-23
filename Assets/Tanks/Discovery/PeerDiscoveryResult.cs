using System.Net;

namespace Tanks.Net;

public readonly struct PeerDiscoveryResult
{
    public PeerDiscoveryResult(PeerId peerId, IPEndPoint endPoint)
    {
        ThrowHelper.ThrowIfNull(endPoint);
        PeerId = peerId;
        EndPoint = endPoint;
    }
    public readonly PeerId PeerId;
    public readonly IPEndPoint EndPoint;
}