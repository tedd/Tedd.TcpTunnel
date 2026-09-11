using System.Net;
using System.Net.Sockets;

namespace Tedd.TcpTunnel;

internal sealed class IpAccessControl
{
    private readonly IpNetworkRule[] _allow;
    private readonly IpNetworkRule[] _deny;

    private IpAccessControl(IpNetworkRule[] allow, IpNetworkRule[] deny)
    { _allow = allow; _deny = deny; }

    public static IpAccessControl Create(AccessControlOptions options) => new(
        options.Allow.Select(IpNetworkRule.Parse).ToArray(),
        options.Deny.Select(IpNetworkRule.Parse).ToArray());

    public bool IsAllowed(IPAddress address)
    {
        address = Normalize(address);
        if (_deny.Any(rule => rule.Contains(address))) return false;
        return _allow.Length == 0 || _allow.Any(rule => rule.Contains(address));
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private sealed class IpNetworkRule
    {
        private readonly byte[] _network;
        private readonly int _prefixLength;
        private readonly AddressFamily _family;

        private IpNetworkRule(IPAddress address, int prefixLength)
        {
            address = Normalize(address);
            _network = address.GetAddressBytes();
            _prefixLength = prefixLength;
            _family = address.AddressFamily;
            MaskHostBits(_network, prefixLength);
        }

        public static IpNetworkRule Parse(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("ACL entries cannot be empty.");
            var parts = value.Split('/', 2, StringSplitOptions.TrimEntries);
            if (!IPAddress.TryParse(parts[0], out var address)) throw new ArgumentException($"Invalid ACL address or subnet: {value}");
            address = Normalize(address);
            var maximum = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            var prefix = maximum;
            if (parts.Length == 2 && (!int.TryParse(parts[1], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out prefix) || prefix < 0 || prefix > maximum))
                throw new ArgumentException($"Invalid ACL prefix length: {value}");
            return new(address, prefix);
        }

        public bool Contains(IPAddress address)
        {
            address = Normalize(address);
            if (address.AddressFamily != _family) return false;
            var candidate = address.GetAddressBytes();
            var fullBytes = _prefixLength / 8;
            for (var i = 0; i < fullBytes; i++)
                if (candidate[i] != _network[i]) return false;
            var remaining = _prefixLength % 8;
            if (remaining == 0) return true;
            var mask = (byte)(0xff << (8 - remaining));
            return (candidate[fullBytes] & mask) == (_network[fullBytes] & mask);
        }

        private static void MaskHostBits(byte[] bytes, int prefixLength)
        {
            var fullBytes = prefixLength / 8;
            var remaining = prefixLength % 8;
            if (remaining != 0) bytes[fullBytes++] &= (byte)(0xff << (8 - remaining));
            Array.Clear(bytes, fullBytes, bytes.Length - fullBytes);
        }
    }
}
