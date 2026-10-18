using System.Net;

namespace EqLink.Core;

public static class OverlayGuard
{
    public static bool Overlaps(string overlayCidr, IEnumerable<(IPAddress Address, int PrefixLength)> localAddresses, string? ownVirtualIp = null)
    {
        var parts = overlayCidr.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var network) || !int.TryParse(parts[1], out var overlayPrefix) ||
            network.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || overlayPrefix is < 1 or > 32)
            throw new ArgumentException("Invalid overlay CIDR.");
        var target = ToUInt32(network);
        foreach (var (address, prefix) in localAddresses)
        {
            if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || prefix is < 1 or > 32) continue;
            if (address.ToString() == ownVirtualIp) continue;
            var bits = Math.Min(prefix, overlayPrefix);
            var mask = uint.MaxValue << (32 - bits);
            if ((ToUInt32(address) & mask) == (target & mask)) return true;
        }
        return false;
    }

    private static uint ToUInt32(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }
}
