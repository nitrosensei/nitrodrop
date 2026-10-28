using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AirDrop.Core.Net;

/// <summary>
/// Which network interface AirDrop runs on. Later this points at the AWDL virtual
/// adapter (the Windows equivalent of awdl0); today it is loopback or a LAN adapter.
/// </summary>
public sealed class NetworkBinding
{
    /// <summary>Null means "all interfaces" (HTTP on [::], mDNS on every multicast-capable adapter).</summary>
    public NetworkInterface? Interface { get; }
    public int? IPv6Index { get; }
    public int? IPv4Index { get; }

    /// <summary>Interface names an "any" binding leaves out (e.g. the AWDL adapter served by its own binding).</summary>
    private readonly IReadOnlyList<string> _excluded = [];

    private NetworkBinding(NetworkInterface? nic, IReadOnlyList<string>? excluded = null)
    {
        Interface = nic;
        _excluded = excluded ?? [];
        if (nic == null) return;
        IPv6Index = IndexOf(nic, AddressFamily.InterNetworkV6);
        IPv4Index = IndexOf(nic, AddressFamily.InterNetwork);
    }

    /// <summary>Interface index for a family, or null when that stack is not bound to the adapter.</summary>
    public static int? IndexOf(NetworkInterface nic, AddressFamily af)
    {
        try
        {
            var p = nic.GetIPProperties();
            if (af == AddressFamily.InterNetworkV6)
                return nic.Supports(NetworkInterfaceComponent.IPv6) ? p.GetIPv6Properties()?.Index : null;
            return nic.Supports(NetworkInterfaceComponent.IPv4) ? p.GetIPv4Properties()?.Index : null;
        }
        catch (NetworkInformationException) { return null; }
    }

    public static NetworkBinding Any { get; } = new(null);

    /// <summary>
    /// "All interfaces" except the named ones: mDNS never joins or advertises them (HTTPS still listens on [::]). Used for
    /// the LAN node while a second node owns the AWDL interface, so each network sees exactly one of them.
    /// </summary>
    public static NetworkBinding AnyExcept(params string[] interfaceNames) => new(null, interfaceNames);

    public IReadOnlyList<string> ExcludedInterfaces => _excluded;

    public static NetworkBinding Loopback =>
        new(NetworkInterface.GetAllNetworkInterfaces().First(n => n.NetworkInterfaceType == NetworkInterfaceType.Loopback));

    /// <summary>Resolve by interface name, description, id/GUID or numeric index; "loopback"/"any" are shortcuts.</summary>
    public static NetworkBinding Resolve(string? nameOrIndex)
    {
        if (string.IsNullOrWhiteSpace(nameOrIndex) || nameOrIndex.Equals("any", StringComparison.OrdinalIgnoreCase))
            return Any;
        if (nameOrIndex.Equals("loopback", StringComparison.OrdinalIgnoreCase) || nameOrIndex == "lo")
            return Loopback;

        var all = NetworkInterface.GetAllNetworkInterfaces();
        if (int.TryParse(nameOrIndex, out int idx))
        {
            var byIndex = all.FirstOrDefault(n => new NetworkBinding(n) is var b && (b.IPv6Index == idx || b.IPv4Index == idx));
            if (byIndex != null) return new NetworkBinding(byIndex);
        }
        var match = all.FirstOrDefault(n => n.Name.Equals(nameOrIndex, StringComparison.OrdinalIgnoreCase))
                    ?? all.FirstOrDefault(n => n.Id.Trim('{', '}').Equals(nameOrIndex.Trim('{', '}'), StringComparison.OrdinalIgnoreCase))
                    ?? all.FirstOrDefault(n => n.Description.Equals(nameOrIndex, StringComparison.OrdinalIgnoreCase));
        return match != null ? new NetworkBinding(match) : throw new ArgumentException($"Network interface not found: {nameOrIndex}");
    }

    public bool IsAny => Interface == null;

    public string DisplayName => Interface == null ? (_excluded.Count > 0 ? $"any except {string.Join(", ", _excluded)}" : "any") : $"{Interface.Name} (#{IPv6Index ?? IPv4Index})";

    /// <summary>Interfaces mDNS should use: the bound one, or all up multicast-capable adapters.</summary>
    public IReadOnlyList<NetworkInterface> MulticastInterfaces()
    {
        if (Interface != null) return [Interface];
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.SupportsMulticast)
            .Where(n => !_excluded.Any(x => x.Equals(n.Name, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>Addresses to advertise in mDNS (link-local IPv6 first, then IPv4).</summary>
    public IReadOnlyList<IPAddress> AdvertisedAddresses()
    {
        var list = new List<IPAddress>();
        foreach (var nic in MulticastInterfaces())
        {
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                var a = ua.Address;
                if (a.AddressFamily == AddressFamily.InterNetworkV6 && (a.IsIPv6LinkLocal || IPAddress.IsLoopback(a) || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback))
                    list.Add(a);
                else if (a.AddressFamily == AddressFamily.InterNetwork)
                    list.Add(a);
            }
        }
        return list.OrderBy(a => a.AddressFamily == AddressFamily.InterNetworkV6 ? 0 : 1).Distinct().ToList();
    }

    /// <summary>The address the HTTPS server listens on.</summary>
    // Dual-mode [::] for now. UNVERIFIED: on the AWDL adapter we may want to bind its
    // link-local address (with scope id) so the server is not reachable from other networks.
    public IPEndPoint ListenEndPoint(int port) => new(IPAddress.IPv6Any, port);

    /// <summary>Pins a socket's outgoing traffic to this interface (IP_UNICAST_IF / IPV6_UNICAST_IF).</summary>
    public void ApplyUnicastInterface(Socket socket)
    {
        if (Interface == null || !OperatingSystem.IsWindows()) return;
        const int IP_UNICAST_IF = 31, IPV6_UNICAST_IF = 31;
        try
        {
            if (socket.AddressFamily == AddressFamily.InterNetworkV6 && IPv6Index is int i6)
                socket.SetSocketOption(SocketOptionLevel.IPv6, (SocketOptionName)IPV6_UNICAST_IF, i6);
            if ((socket.AddressFamily == AddressFamily.InterNetwork || socket.DualMode) && IPv4Index is int i4)
                // IPv4 variant takes the index in network byte order.
                socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)IP_UNICAST_IF, IPAddress.HostToNetworkOrder(i4));
        }
        catch (SocketException) { /* best effort */ }
    }

    /// <summary>Opens a TCP connection to <paramref name="remote"/>, scoping link-local addresses to this interface.</summary>
    public async Task<Socket> ConnectAsync(IPEndPoint remote, CancellationToken ct)
    {
        if (remote.Address.IsIPv6LinkLocal && remote.Address.ScopeId == 0 && IPv6Index is int idx)
            remote = new IPEndPoint(new IPAddress(remote.Address.GetAddressBytes(), idx), remote.Port);
        var socket = new Socket(remote.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            ApplyUnicastInterface(socket);
            await socket.ConnectAsync(remote, ct);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public override string ToString() => DisplayName;
}
