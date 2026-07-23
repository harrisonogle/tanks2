using System;
using System.Net;
using System.Net.Sockets;

namespace Tanks.Net;

// Parses the "IP:port" text a player types into the connect screen. Pure C# so
// it's testable without Unity; real discovery can reuse it for config/CLI input.
//
// Accepts "a.b.c.d:port" and "[v6]:port". Rejects link-local/scoped IPv6 loudly:
// NetAddress has no scope id field, so such a destination would silently lose its
// scope and every send would fail to route.
public static class EndPointParser
{
    public static bool TryParse(string? text, out IPEndPoint? endPoint, out string? error)
    {
        endPoint = null;
        error = null;
        text = (text ?? "").Trim();

        string host, portText;
        if (text.StartsWith("[", StringComparison.Ordinal))
        {
            int close = text.IndexOf(']');
            if (close < 0 || close + 2 > text.Length || text[close + 1] != ':')
            {
                error = "Expected [address]:port for IPv6.";
                return false;
            }
            host = text.Substring(1, close - 1);
            portText = text.Substring(close + 2);
        }
        else
        {
            int colon = text.LastIndexOf(':');
            if (colon <= 0 || colon == text.Length - 1)
            {
                error = "Expected address:port (e.g. 169.254.12.34:47777).";
                return false;
            }
            if (text.IndexOf(':') != colon)
            {
                error = "IPv6 addresses need brackets: [address]:port.";
                return false;
            }
            host = text.Substring(0, colon);
            portText = text.Substring(colon + 1);
        }

        if (!IPAddress.TryParse(host, out IPAddress? address))
        {
            error = $"'{host}' is not a valid IP address.";
            return false;
        }
        if (!ushort.TryParse(portText, out ushort port) || port == 0)
        {
            error = $"'{portText}' is not a valid port.";
            return false;
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6 &&
            (address.IsIPv6LinkLocal || address.ScopeId != 0))
        {
            error = "IPv6 link-local needs a scope id, which the wire address format doesn't carry yet — share the IPv4 address instead.";
            return false;
        }

        endPoint = new IPEndPoint(address, port);
        return true;
    }
}
