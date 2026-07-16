using System.Net;
using Tanks.Net;
using Xunit;

namespace Tests;

public sealed class NetAddressTests
{
    [Fact]
    public void IPv4_Roundtrip()
    {
        var ep = new IPEndPoint(IPAddress.Parse("192.168.1.42"), 7777);
        NetAddress addr = NetAddress.FromIPEndPoint(ep);

        Assert.Equal(NetAddressFamily.IPv4, addr.Family);
        Assert.Equal(7777, addr.Port);
        Assert.Equal(ep, addr.ToIPEndPoint());
        Assert.Equal("192.168.1.42:7777", addr.ToString());
    }

    [Fact]
    public void IPv6_Roundtrip()
    {
        var ep = new IPEndPoint(IPAddress.Parse("2001:db8::1234"), 55555);
        NetAddress addr = NetAddress.FromIPEndPoint(ep);

        Assert.Equal(NetAddressFamily.IPv6, addr.Family);
        Assert.Equal(55555, addr.Port);
        Assert.Equal(ep, addr.ToIPEndPoint());
    }

    [Fact]
    public void V4Mapped_CanonicalizesToIPv4()
    {
        var mapped = new IPEndPoint(IPAddress.Parse("::ffff:10.0.0.5"), 1200);
        var plain = new IPEndPoint(IPAddress.Parse("10.0.0.5"), 1200);

        NetAddress a = NetAddress.FromIPEndPoint(mapped);
        NetAddress b = NetAddress.FromIPEndPoint(plain);

        Assert.Equal(NetAddressFamily.IPv4, a.Family);
        Assert.True(a.Equals(b));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Equality_DiscriminatesPortAndAddress()
    {
        NetAddress a = NetAddress.FromIPEndPoint(new IPEndPoint(IPAddress.Parse("1.2.3.4"), 1000));
        NetAddress samePort = NetAddress.FromIPEndPoint(new IPEndPoint(IPAddress.Parse("1.2.3.5"), 1000));
        NetAddress sameAddr = NetAddress.FromIPEndPoint(new IPEndPoint(IPAddress.Parse("1.2.3.4"), 1001));

        Assert.False(a.Equals(samePort));
        Assert.False(a.Equals(sameAddr));
        Assert.True(a.Equals(NetAddress.FromIPEndPoint(new IPEndPoint(IPAddress.Parse("1.2.3.4"), 1000))));
    }

    [Fact]
    public void Default_IsNone()
    {
        NetAddress addr = default;
        Assert.Equal(NetAddressFamily.None, addr.Family);
        Assert.Equal("(none)", addr.ToString());
    }
}
