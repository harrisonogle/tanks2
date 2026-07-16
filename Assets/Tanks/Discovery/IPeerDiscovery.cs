using System.Collections.Generic;

namespace Tanks.Net;

public interface IPeerDiscovery
{
    IEnumerable<PeerDiscoveryResult> GetPeers();
}