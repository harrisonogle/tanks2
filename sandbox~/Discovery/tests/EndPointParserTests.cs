using System.Net;
using Tanks.Net;
using Xunit;

namespace Tests;

public sealed class EndPointParserTests
{
    [Theory]
    [InlineData("169.254.12.34:47777", "169.254.12.34", 47777)]
    [InlineData(" 10.0.0.2:1 ", "10.0.0.2", 1)]
    [InlineData("192.168.0.1:65535", "192.168.0.1", 65535)]
    [InlineData("[2001:db8::1]:47777", "2001:db8::1", 47777)]
    public void TryParse_AcceptsValidEndPoints(string text, string expectedAddress, int expectedPort)
    {
        Assert.True(EndPointParser.TryParse(text, out IPEndPoint? endPoint, out string? error));
        Assert.Null(error);
        Assert.NotNull(endPoint);
        Assert.Equal(IPAddress.Parse(expectedAddress), endPoint!.Address);
        Assert.Equal(expectedPort, endPoint.Port);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("169.254.12.34")] // no port
    [InlineData("169.254.12.34:")] // empty port
    [InlineData(":47777")] // no host
    [InlineData("169.254.12.34:0")] // port zero
    [InlineData("169.254.12.34:65536")] // port overflow
    [InlineData("999.1.1.1:47777")] // bad address
    [InlineData("fe80::1:47777")] // bare IPv6 — ambiguous without brackets
    [InlineData("[fe80::1]47777")] // missing colon after bracket
    public void TryParse_RejectsMalformedInput(string? text)
    {
        Assert.False(EndPointParser.TryParse(text, out IPEndPoint? endPoint, out string? error));
        Assert.Null(endPoint);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("[fe80::1]:47777")] // link-local
    [InlineData("[fe80::1%7]:47777")] // link-local with scope id
    [InlineData("[2001:db8::1%3]:47777")] // scoped, even if not link-local
    public void TryParse_RejectsScopedIPv6_BecauseNetAddressDropsScopeIds(string text)
    {
        Assert.False(EndPointParser.TryParse(text, out IPEndPoint? endPoint, out string? error));
        Assert.Null(endPoint);
        Assert.Contains("scope id", error);
    }
}
