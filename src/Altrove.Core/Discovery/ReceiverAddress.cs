using System.Diagnostics.CodeAnalysis;
using System.Net;
using Altrove.Core.Protocol;

namespace Altrove.Core.Discovery;

public static class ReceiverAddress
{
    /// <summary>Parses what the user types: "192.168.1.20", "192.168.1.20:47800", "tablet.local", "[fe80::1]:47800".</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out string? host, out int port)
    {
        host = null;
        port = AltroveProtocol.DefaultPort;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
            return false;
        if (text.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
            text = text[5..].TrimEnd('/');

        if (IPAddress.TryParse(text, out var bare))
        {
            host = bare.ToString();
            return true;
        }
        if (IPEndPoint.TryParse(text, out var endPoint) && text.Contains(':'))
        {
            if (endPoint.Port is <= 0 or > 65535)
                return false;
            host = endPoint.Address.ToString();
            port = endPoint.Port;
            return true;
        }

        var colon = text.LastIndexOf(':');
        var name = colon >= 0 ? text[..colon] : text;
        if (colon >= 0 && (!int.TryParse(text[(colon + 1)..], out port) || port is <= 0 or > 65535))
            return false;
        if (Uri.CheckHostName(name) != UriHostNameType.Dns)
            return false;
        host = name;
        return true;
    }

    public static Uri ToUri(string host, int port) =>
        new($"ws://{(host.Contains(':') ? $"[{host}]" : host)}:{port}/");
}
