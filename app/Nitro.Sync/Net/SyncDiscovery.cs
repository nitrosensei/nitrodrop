using System.Net;
using System.Net.Sockets;
using AirDrop.Core.Mdns;
using AirDrop.Core.Net;

namespace Nitro.Sync.Net;

public sealed record DiscoveredPeer(string DeviceId, string Name, ushort Port, IReadOnlyList<IPAddress> Addresses)
{
    /// <summary>LAN IPv4 first (no scope-id trouble), then global/ULA IPv6, then link-local.</summary>
    public IPEndPoint EndPoint
    {
        get
        {
            var a = Addresses.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(x))
                    ?? Addresses.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork)
                    ?? Addresses.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetworkV6 && !x.IsIPv6LinkLocal)
                    ?? Addresses.First();
            return new IPEndPoint(a, Port);
        }
    }
}

/// <summary>
/// Separate DNS-SD service <c>_nitrosync._tcp</c> on its own <see cref="MdnsService"/> (port 5353 is shared
/// with SO_REUSEADDR, like AirDrop.Core does next to Windows' own responder). Instance name = device id.
/// </summary>
internal sealed class SyncDiscovery : IAsyncDisposable
{
    private readonly MdnsService _mdns;
    private readonly string _selfId;
    private readonly CancellationTokenSource _cts = new();
    private ServiceRegistration? _reg;
    private Task? _loop;
    private Dictionary<string, DiscoveredPeer> _last = new(StringComparer.OrdinalIgnoreCase);

    public event Action<IReadOnlyList<DiscoveredPeer>>? Changed;

    public SyncDiscovery(NetworkBinding binding, int mdnsPort, string selfId, Action<string>? log)
    {
        _selfId = selfId;
        _mdns = new MdnsService(binding, mdnsPort) { Log = log };
    }

    public IReadOnlyList<DiscoveredPeer> Current { get { lock (this) return _last.Values.ToList(); } }

    public async Task StartAsync(string name, ushort port, string hostName)
    {
        _mdns.Start();
        _reg = new ServiceRegistration
        {
            InstanceName = _selfId,
            ServiceType = SyncProtocol.ServiceType,
            HostName = hostName,
            Port = port,
            Txt = [$"v={SyncProtocol.Version}", $"id={_selfId}", $"name={Truncate(name, 60)}"],
        };
        _ = _mdns.RegisterAsync(_reg, _cts.Token).ContinueWith(_ => { }, TaskScheduler.Default);
        _loop = Task.Run(() => BrowseLoop(_cts.Token));
        await Task.CompletedTask;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private async Task BrowseLoop(CancellationToken ct)
    {
        var signal = new SemaphoreSlim(0);
        void OnChange() { if (signal.CurrentCount == 0) signal.Release(); }
        _mdns.CacheChanged += OnChange;
        try
        {
            var interval = TimeSpan.FromSeconds(1);
            var next = DateTime.UtcNow;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (DateTime.UtcNow >= next)
                    {
                        var qs = new List<DnsQuestion> { new(SyncProtocol.ServiceType, DnsType.PTR) };
                        qs.AddRange(_mdns.PendingResolutions(SyncProtocol.ServiceType));
                        await _mdns.QueryAsync(qs);
                        next = DateTime.UtcNow + interval;
                        interval = TimeSpan.FromSeconds(Math.Min(interval.TotalSeconds * 2, 15));
                    }
                    else
                    {
                        var pending = _mdns.PendingResolutions(SyncProtocol.ServiceType).ToList();
                        if (pending.Count > 0) await _mdns.QueryAsync(pending);
                    }
                    Refresh();
                }
                catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
                try { await signal.WaitAsync(TimeSpan.FromMilliseconds(700), ct); }
                catch (OperationCanceledException) { return; }
            }
        }
        finally { _mdns.CacheChanged -= OnChange; }
    }

    private void Refresh()
    {
        var live = new HashSet<string>(_mdns.GetInstanceNames(SyncProtocol.ServiceType), StringComparer.OrdinalIgnoreCase);
        var now = new Dictionary<string, DiscoveredPeer>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in _mdns.GetInstances(SyncProtocol.ServiceType))
        {
            var id = i.Txt.TryGetValue("id", out var tid) && tid.Length > 0 ? tid : i.InstanceName;
            if (id.Equals(_selfId, StringComparison.OrdinalIgnoreCase) || !live.Contains(i.InstanceName)) continue;
            var name = i.Txt.TryGetValue("name", out var n) && n.Length > 0 ? n : i.HostName.TrimEnd('.');
            now[id] = new DiscoveredPeer(id, name, i.Port, i.Addresses);
        }
        bool changed;
        lock (this)
        {
            changed = now.Count != _last.Count || now.Any(kv => !_last.TryGetValue(kv.Key, out var o)
                || o.Port != kv.Value.Port || o.Name != kv.Value.Name || !o.Addresses.SequenceEqual(kv.Value.Addresses));
            if (changed) _last = now;
        }
        if (changed) Changed?.Invoke(now.Values.ToList());
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_reg != null) { try { await _mdns.UnregisterAsync(_reg); } catch (Exception e) when (e is SocketException or ObjectDisposedException) { } }
        if (_loop != null) { try { await _loop; } catch (OperationCanceledException) { } }
        await _mdns.DisposeAsync();
        _cts.Dispose();
    }
}
