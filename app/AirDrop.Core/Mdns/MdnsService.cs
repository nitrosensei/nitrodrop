using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using AirDrop.Core.Net;

namespace AirDrop.Core.Mdns;

/// <summary>A DNS-SD service instance we publish.</summary>
public sealed class ServiceRegistration
{
    public required string InstanceName { get; init; }          // e.g. "1a2b3c4d5e6f"
    public required string ServiceType { get; init; }           // e.g. "_airdrop._tcp.local."
    public required string HostName { get; init; }              // e.g. "MYPC.local."
    public required ushort Port { get; init; }
    public IReadOnlyList<string> Txt { get; init; } = [];
    public string FullName => $"{InstanceName}.{ServiceType}";
}

/// <summary>A discovered DNS-SD instance.</summary>
public sealed record ServiceInstance(string InstanceName, string FullName, string HostName, ushort Port,
    IReadOnlyDictionary<string, string> Txt, IReadOnlyList<IPAddress> Addresses);

/// <summary>
/// Minimal mDNS responder + browser (RFC 6762/6763). Listens on 224.0.0.251 and ff02::fb.
/// No probing/conflict resolution: instance names are random 48-bit hex, collisions are unlikely.
/// </summary>
public sealed class MdnsService : IAsyncDisposable
{
    public const int DefaultPort = 5353;
    public static readonly IPAddress GroupV4 = IPAddress.Parse("224.0.0.251");
    public static readonly IPAddress GroupV6 = IPAddress.Parse("ff02::fb");
    private const uint HostTtl = 120, OtherTtl = 4500;

    private readonly NetworkBinding _binding;
    private readonly int _port;
    private readonly bool _useV4, _useV6;
    private readonly List<(Socket socket, IPAddress group)> _sockets = [];
    private readonly ConcurrentDictionary<string, ServiceRegistration> _registrations = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _loops = [];
    private readonly Cache _cache = new();

    /// <summary>Raised for every received response (after cache update).</summary>
    public event Action? CacheChanged;
    public Action<string>? Log { get; set; }

    public MdnsService(NetworkBinding binding, int port = DefaultPort, bool ipv4 = true, bool ipv6 = true)
    {
        _binding = binding;
        _port = port;
        _useV4 = ipv4;
        _useV6 = ipv6;
    }

    public void Start()
    {
        var nics = _binding.MulticastInterfaces();
        if (_useV4 && Socket.OSSupportsIPv4) TryOpen(AddressFamily.InterNetwork, nics);
        if (_useV6 && Socket.OSSupportsIPv6) TryOpen(AddressFamily.InterNetworkV6, nics);
        if (_sockets.Count == 0) throw new InvalidOperationException($"Could not open any mDNS socket on port {_port}");
        foreach (var (s, g) in _sockets) _loops.Add(Task.Run(() => ReceiveLoop(s, g)));
    }

    private void TryOpen(AddressFamily af, IReadOnlyList<NetworkInterface> nics)
    {
        var s = new Socket(af, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            s.ExclusiveAddressUse = false;
            s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            bool v6 = af == AddressFamily.InterNetworkV6;
            s.Bind(new IPEndPoint(v6 ? IPAddress.IPv6Any : IPAddress.Any, _port));
            var level = v6 ? SocketOptionLevel.IPv6 : SocketOptionLevel.IP;
            s.SetSocketOption(level, SocketOptionName.PacketInformation, true);
            s.SetSocketOption(level, SocketOptionName.MulticastTimeToLive, 255);
            s.SetSocketOption(level, SocketOptionName.MulticastLoopback, true);
            int joined = 0;
            foreach (var nic in nics)
            {
                try
                {
                    if (v6)
                    {
                        var idx = NetworkBinding.IndexOf(nic, AddressFamily.InterNetworkV6);
                        if (idx is null) continue;
                        s.SetSocketOption(level, SocketOptionName.AddMembership, new IPv6MulticastOption(GroupV6, idx.Value));
                    }
                    else
                    {
                        var addr = IPv4Of(nic);
                        if (addr is null) continue;
                        s.SetSocketOption(level, SocketOptionName.AddMembership, new MulticastOption(GroupV4, addr));
                    }
                    joined++;
                }
                catch (SocketException e) { Log?.Invoke($"mDNS join failed on {nic.Name} ({af}): {e.Message}"); }
            }
            if (joined == 0) { s.Dispose(); Log?.Invoke($"mDNS: no interface joined for {af}"); return; }
            _sockets.Add((s, v6 ? GroupV6 : GroupV4));
        }
        catch (SocketException e)
        {
            Log?.Invoke($"mDNS socket ({af}) failed: {e.Message}");
            s.Dispose();
        }
    }

    private static IPAddress? IPv4Of(NetworkInterface nic) =>
        nic.GetIPProperties().UnicastAddresses.Select(u => u.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);

    // ---------------- responder ----------------

    public async Task RegisterAsync(ServiceRegistration reg, CancellationToken ct = default)
    {
        _registrations[reg.FullName] = reg;
        // RFC 6762 §8.3: announce at least twice, one second apart.
        for (int i = 0; i < 2; i++)
        {
            await AnnounceAsync(reg, goodbye: false);
            if (i == 0) await Task.Delay(1000, ct);
        }
    }

    public async Task UnregisterAsync(ServiceRegistration reg)
    {
        if (_registrations.TryRemove(reg.FullName, out _)) await AnnounceAsync(reg, goodbye: true);
    }

    private async Task AnnounceAsync(ServiceRegistration reg, bool goodbye)
    {
        foreach (var (socket, group) in _sockets)
        foreach (var nic in _binding.MulticastInterfaces())
        {
            var msg = new DnsMessage { IsResponse = true, Authoritative = true };
            msg.Answers.AddRange(ServiceRecords(reg));
            msg.Answers.AddRange(AddressRecords(reg, IndexOf(nic, socket.AddressFamily)));
            if (goodbye)
            {
                var zero = msg.Answers.Select(r => r.WithTtl(0)).ToList();
                msg.Answers.Clear();
                msg.Answers.AddRange(zero);
            }
            await SendMulticastAsync(socket, group, nic, msg.Encode());
        }
    }

    private static int? IndexOf(NetworkInterface nic, AddressFamily af) => NetworkBinding.IndexOf(nic, af);

    private static IEnumerable<DnsRecord> ServiceRecords(ServiceRegistration r)
    {
        yield return new DnsRecord { Name = r.ServiceType, Type = DnsType.PTR, Ttl = OtherTtl, Target = r.FullName };
        yield return new DnsRecord { Name = r.FullName, Type = DnsType.SRV, Ttl = HostTtl, CacheFlush = true, Port = r.Port, Target = r.HostName };
        yield return new DnsRecord { Name = r.FullName, Type = DnsType.TXT, Ttl = OtherTtl, CacheFlush = true, Text = r.Txt };
    }

    private IEnumerable<DnsRecord> AddressRecords(ServiceRegistration r, int? interfaceIndex)
    {
        foreach (var a in AddressesFor(interfaceIndex))
        {
            var bare = a.AddressFamily == AddressFamily.InterNetworkV6 ? new IPAddress(a.GetAddressBytes()) : a;
            yield return new DnsRecord
            {
                Name = r.HostName, Type = a.AddressFamily == AddressFamily.InterNetworkV6 ? DnsType.AAAA : DnsType.A,
                Ttl = HostTtl, CacheFlush = true, Address = bare,
            };
        }
    }

    private IReadOnlyList<IPAddress> AddressesFor(int? interfaceIndex)
    {
        if (interfaceIndex is int idx)
        {
            var nic = _binding.MulticastInterfaces().FirstOrDefault(n => IndexOf(n, AddressFamily.InterNetworkV6) == idx || IndexOf(n, AddressFamily.InterNetwork) == idx);
            if (nic != null)
            {
                var list = nic.GetIPProperties().UnicastAddresses.Select(u => u.Address)
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork || a.IsIPv6LinkLocal || IPAddress.IsLoopback(a))
                    .ToList();
                if (list.Count > 0) return list;
            }
        }
        return _binding.AdvertisedAddresses();
    }

    private void HandleQuery(DnsMessage q, IPEndPoint from, int ifIndex, Socket socket, IPAddress group)
    {
        if (_registrations.IsEmpty) return;
        var answers = new List<DnsRecord>();
        var additionals = new List<DnsRecord>();
        foreach (var question in q.Questions)
        {
            foreach (var reg in _registrations.Values)
            {
                bool any = question.Type == DnsType.ANY;
                if (DnsMessage.NameEquals(question.Name, reg.ServiceType) && (any || question.Type == DnsType.PTR))
                {
                    var ptr = ServiceRecords(reg).First();
                    if (!KnownAnswer(q, ptr)) answers.Add(ptr);
                    additionals.AddRange(ServiceRecords(reg).Skip(1));
                    additionals.AddRange(AddressRecords(reg, ifIndex));
                }
                else if (DnsMessage.NameEquals(question.Name, "_services._dns-sd._udp.local") && (any || question.Type == DnsType.PTR))
                {
                    answers.Add(new DnsRecord { Name = "_services._dns-sd._udp.local.", Type = DnsType.PTR, Ttl = OtherTtl, Target = reg.ServiceType });
                }
                else if (DnsMessage.NameEquals(question.Name, reg.FullName))
                {
                    var recs = ServiceRecords(reg).Skip(1).Where(r => any || r.Type == question.Type).ToList();
                    answers.AddRange(recs);
                    if (recs.Any(r => r.Type == DnsType.SRV)) additionals.AddRange(AddressRecords(reg, ifIndex));
                }
                else if (DnsMessage.NameEquals(question.Name, reg.HostName))
                {
                    answers.AddRange(AddressRecords(reg, ifIndex).Where(r => any || r.Type == question.Type));
                }
            }
        }
        if (answers.Count == 0) return;

        bool legacyUnicast = from.Port != _port;
        var resp = new DnsMessage { IsResponse = true, Authoritative = true, Id = legacyUnicast ? q.Id : (ushort)0 };
        if (legacyUnicast) resp.Questions.AddRange(q.Questions);
        resp.Answers.AddRange(Dedup(answers).Select(r => legacyUnicast ? CapTtl(r) : r));
        resp.Additionals.AddRange(Dedup(additionals).Where(a => !resp.Answers.Any(x => Same(x, a))).Select(r => legacyUnicast ? CapTtl(r) : r));
        var bytes = resp.Encode();

        bool unicast = legacyUnicast || q.Questions.All(x => x.UnicastResponse);
        try
        {
            if (unicast) socket.SendTo(bytes, from);
            else
            {
                var nic = _binding.MulticastInterfaces().FirstOrDefault(n => IndexOf(n, socket.AddressFamily) == ifIndex);
                if (nic != null) SendMulticastAsync(socket, group, nic, bytes).GetAwaiter().GetResult();
                else socket.SendTo(bytes, new IPEndPoint(group, _port));
            }
        }
        catch (SocketException e) { Log?.Invoke($"mDNS send failed: {e.Message}"); }
    }

    private static DnsRecord CapTtl(DnsRecord r) => r.Ttl > 10 ? r.WithTtl(10) : r;

    private static bool KnownAnswer(DnsMessage q, DnsRecord r) =>
        q.Answers.Any(a => a.Type == r.Type && DnsMessage.NameEquals(a.Name, r.Name) &&
                           a.Target != null && DnsMessage.NameEquals(a.Target, r.Target!) && a.Ttl >= r.Ttl / 2);

    private static bool Same(DnsRecord a, DnsRecord b) =>
        a.Type == b.Type && DnsMessage.NameEquals(a.Name, b.Name) && Equals(a.Address, b.Address) && a.Target == b.Target && a.Port == b.Port;

    private static IEnumerable<DnsRecord> Dedup(IEnumerable<DnsRecord> records)
    {
        var list = new List<DnsRecord>();
        foreach (var r in records) if (!list.Any(x => Same(x, r))) list.Add(r);
        return list;
    }

    // ---------------- browser ----------------

    /// <summary>Sends a one-shot multicast query on all sockets/interfaces.</summary>
    public async Task QueryAsync(IEnumerable<DnsQuestion> questions)
    {
        var msg = new DnsMessage();
        msg.Questions.AddRange(questions);
        // Known-answer suppression for PTR questions.
        foreach (var q in msg.Questions.Where(q => q.Type == DnsType.PTR))
            msg.Answers.AddRange(_cache.Get(q.Name, DnsType.PTR).Where(r => r.Ttl > 0));
        var bytes = msg.Encode();
        foreach (var (socket, group) in _sockets)
        foreach (var nic in _binding.MulticastInterfaces())
            await SendMulticastAsync(socket, group, nic, bytes);
    }

    /// <summary>Returns fully resolved instances of a service type from the cache.</summary>
    public IReadOnlyList<ServiceInstance> GetInstances(string serviceType)
    {
        var result = new List<ServiceInstance>();
        foreach (var ptr in _cache.Get(serviceType, DnsType.PTR))
        {
            var full = ptr.Target!;
            var srv = _cache.Get(full, DnsType.SRV).FirstOrDefault();
            if (srv == null) continue;
            var addrs = _cache.Get(srv.Target!, DnsType.AAAA).Concat(_cache.Get(srv.Target!, DnsType.A))
                .Select(r => r.Address!).Distinct().ToList();
            if (addrs.Count == 0) continue;
            var txt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in _cache.Get(full, DnsType.TXT).SelectMany(r => r.Text ?? []))
            {
                int eq = t.IndexOf('=');
                if (eq < 0) txt[t] = ""; else txt[t[..eq]] = t[(eq + 1)..];
            }
            var instance = full.EndsWith("." + serviceType, StringComparison.OrdinalIgnoreCase)
                ? full[..^(serviceType.Length + 1)] : full;
            result.Add(new ServiceInstance(instance, full, srv.Target!, srv.Port, txt, addrs));
        }
        return result;
    }

    /// <summary>Instance names with a live PTR record (resolved or not). A goodbye (TTL 0) removes them at once.</summary>
    public IReadOnlyList<string> GetInstanceNames(string serviceType) =>
        _cache.Get(serviceType, DnsType.PTR)
            .Select(p => p.Target!)
            .Select(full => full.EndsWith("." + serviceType, StringComparison.OrdinalIgnoreCase) ? full[..^(serviceType.Length + 1)] : full)
            .ToList();

    /// <summary>Names of PTR targets that are known but not yet resolved (missing SRV or address).</summary>
    public IEnumerable<DnsQuestion> PendingResolutions(string serviceType)
    {
        foreach (var ptr in _cache.Get(serviceType, DnsType.PTR))
        {
            var srv = _cache.Get(ptr.Target!, DnsType.SRV).FirstOrDefault();
            if (srv == null)
            {
                yield return new DnsQuestion(ptr.Target!, DnsType.SRV);
                yield return new DnsQuestion(ptr.Target!, DnsType.TXT);
            }
            else if (!_cache.Get(srv.Target!, DnsType.AAAA).Any() && !_cache.Get(srv.Target!, DnsType.A).Any())
            {
                yield return new DnsQuestion(srv.Target!, DnsType.AAAA);
                yield return new DnsQuestion(srv.Target!, DnsType.A);
            }
        }
    }

    // ---------------- I/O ----------------

    private async Task SendMulticastAsync(Socket socket, IPAddress group, NetworkInterface nic, byte[] bytes)
    {
        try
        {
            if (socket.AddressFamily == AddressFamily.InterNetworkV6)
            {
                var idx = NetworkBinding.IndexOf(nic, AddressFamily.InterNetworkV6);
                if (idx is null) return;
                socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastInterface, idx.Value);
            }
            else
            {
                var addr = IPv4Of(nic);
                if (addr is null) return;
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                    BitConverter.ToInt32(addr.GetAddressBytes(), 0));
            }
            await socket.SendToAsync(bytes, new IPEndPoint(group, _port));
        }
        catch (SocketException e) { Log?.Invoke($"mDNS send on {nic.Name} failed: {e.Message}"); }
    }

    private async Task ReceiveLoop(Socket socket, IPAddress group)
    {
        var buf = new byte[9000];
        EndPoint any = new IPEndPoint(socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        while (!_cts.IsCancellationRequested)
        {
            SocketReceiveMessageFromResult res;
            try { res = await socket.ReceiveMessageFromAsync(buf, SocketFlags.None, any, _cts.Token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize) { continue; }
            catch (SocketException e) { Log?.Invoke($"mDNS receive error: {e.Message}"); continue; }

            DnsMessage msg;
            try { msg = DnsMessage.Decode(buf.AsSpan(0, res.ReceivedBytes)); }
            catch (Exception e) when (e is FormatException or ArgumentException or IndexOutOfRangeException) { continue; }

            var from = (IPEndPoint)res.RemoteEndPoint;
            int ifIndex = res.PacketInformation.Interface;
            if (!_binding.IsAny && ifIndex != (_binding.IPv6Index ?? -1) && ifIndex != (_binding.IPv4Index ?? -1)) continue;
            try
            {
                if (msg.IsResponse)
                {
                    _cache.Add(msg.AllRecords, ifIndex);
                    CacheChanged?.Invoke();
                }
                else HandleQuery(msg, from, ifIndex, socket, group);
            }
            catch (Exception e) { Log?.Invoke($"mDNS handler error: {e.Message}"); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var reg in _registrations.Values.ToList())
        {
            try { await UnregisterAsync(reg); } catch { /* shutting down */ }
        }
        _cts.Cancel();
        foreach (var (s, _) in _sockets) s.Dispose();
        try { await Task.WhenAll(_loops); } catch { /* ignore */ }
        _cts.Dispose();
    }

    /// <summary>TTL-aware record cache keyed by (name, type).</summary>
    private sealed class Cache
    {
        private readonly object _lock = new();
        private readonly Dictionary<(string, DnsType), List<(DnsRecord rec, DateTime expires)>> _map = [];

        public void Add(IEnumerable<DnsRecord> records, int ifIndex)
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                var flushed = new HashSet<(string, DnsType)>();
                foreach (var r0 in records)
                {
                    var r = r0;
                    // Scope link-local addresses to the interface they arrived on.
                    if (r.Address is { IsIPv6LinkLocal: true, ScopeId: 0 } && ifIndex > 0)
                        r = new DnsRecord { Name = r.Name, Type = r.Type, Ttl = r.Ttl, CacheFlush = r.CacheFlush, Address = new IPAddress(r.Address.GetAddressBytes(), ifIndex) };
                    var key = (r.Name.TrimEnd('.').ToLowerInvariant(), r.Type);
                    if (!_map.TryGetValue(key, out var list)) _map[key] = list = [];
                    if (r.CacheFlush && flushed.Add(key)) list.Clear(); // simplified RFC 6762 §10.2 cache flush
                    list.RemoveAll(x => Same(x.rec, r));
                    if (r.Ttl > 0) list.Add((r, now.AddSeconds(r.Ttl)));
                }
            }
        }

        public List<DnsRecord> Get(string name, DnsType type)
        {
            lock (_lock)
            {
                var key = (name.TrimEnd('.').ToLowerInvariant(), type);
                if (!_map.TryGetValue(key, out var list)) return [];
                var now = DateTime.UtcNow;
                list.RemoveAll(x => x.expires <= now);
                return list.Select(x => x.rec.WithTtl((uint)Math.Max(0, (x.expires - now).TotalSeconds))).ToList();
            }
        }
    }
}
