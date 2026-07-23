using System;
using System.Text;
using Tanks.Net;
using Xunit;

namespace Tests;

public sealed unsafe class PeerIdTests
{
    private static PeerId CreatePeerId(byte seed)
    {
        byte* bytes = stackalloc byte[PeerId.Size];
        for (int i = 0; i < PeerId.Size; i++)
            bytes[i] = (byte)(seed + i * 7);
        return new PeerId(bytes);
    }

    [Fact]
    public void TryParse_RoundTrips_ToVerboseString()
    {
        for (byte seed = 0; seed < 32; seed++)
        {
            PeerId original = CreatePeerId(seed);
            Assert.True(PeerId.TryParse(original.ToVerboseString(), out PeerId parsed));
            Assert.True(original.Equals(parsed));
        }
    }

    [Fact]
    public void TryParse_RoundTrips_DerivedPeerId()
    {
        // Same path Network uses: derive from a public key, then round-trip
        // through the string a player would share over the shoulder.
        byte[] publicKey = Encoding.UTF8.GetBytes("not a real key, but any bytes will do");
        NetworkHelper.GetPeerId(publicKey, out PeerId original);

        Assert.True(PeerId.TryParse(original.ToVerboseString(), out PeerId parsed));
        Assert.True(original.Equals(parsed));
        Assert.Equal(original.ToVerboseString(), parsed.ToVerboseString());
    }

    [Fact]
    public void TryParse_AcceptsSurroundingWhitespace()
    {
        PeerId original = CreatePeerId(0xA5);
        Assert.True(PeerId.TryParse($"  {original.ToVerboseString()}\n", out PeerId parsed));
        Assert.True(original.Equals(parsed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("12345678")] // short ToString() form is truncated; must not parse
    public void TryParse_RejectsInvalidInput(string? input)
    {
        Assert.False(PeerId.TryParse(input, out PeerId parsed));
        Assert.True(parsed.Equals(default(PeerId)));
    }
}
