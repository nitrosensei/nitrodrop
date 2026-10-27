using System.Net;
using System.Net.Sockets;
using AirDrop.Core.Mdns;
using AirDrop.Core.Net;

namespace AirDrop.Core;

public sealed record AirDropPeer(string InstanceName, string HostName, ushort Port, AirDropReceiverFlags Flags, IReadOnlyList<IPAddress> Addresses)
{
    /// <summary>Preferred endpoint: link-local IPv6 (AWDL), then any IPv6, then IPv4.</summary>
    public IPEndPoint EndPoint
    {
        get
        {
            var addr = Addresses.FirstOrDefault(a => a.IsIPv6LinkLocal)
                       ?? Addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6)
                       ?? Addresses.First();
            return new IPEndPoint(addr, Port);
        }
    }

    public override string ToString() =>
        $"{InstanceName} {HostName}:{Port} flags=0x{(int)Flags:x} [{string.Join(", ", Addresses)}]";
}

/// <summary>Browses _airdrop._tcp via mDNS.</summary>
public sealed class AirDropBrowser : IAsyncDisposable
{
    private readonly MdnsService _mdns;
    private readonly bool _ownsMdns;
    private readonly HashSet<string> _exclude = new(StringComparer.OrdinalIgnoreCase);

    public AirDropBrowser(NetworkBinding binding, int mdnsPort = MdnsService.DefaultPort, Action<string>? log = null)
    {
        _mdns = new MdnsService(binding, mdnsPort) { Log = log };
        _mdns.Start();
        _ownsMdns = true;
    }

    /// <summary>Reuse an already started mDNS service (e.g. the server's).</summary>
    public AirDropBrowser(MdnsService mdns) => _mdns = mdns;

    /// <summary>Hide our own instance from results.</summary>
    public void Exclude(string instanceName) => _exclude.Add(instanceName);

    public IReadOnlyList<AirDropPeer> CurrentPeers() =>
        _mdns.GetInstances(AirDropConstants.ServiceType)
            .Where(i => !_exclude.Contains(i.InstanceName))
            .Select(i => new AirDropPeer(i.InstanceName, i.HostName, i.Port,
                i.Txt.TryGetValue("flags", out var f) && int.TryParse(f, out var fl) ? (AirDropReceiverFlags)fl : AirDropReceiverFlags.None,
                i.Addresses))
            .ToList();

    /// <summary>Queries until cancelled, reporting each newly resolved peer once.</summary>
    public async Task BrowseAsync(Action<AirDropPeer> onPeer, CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var signal = new SemaphoreSlim(0);
        void OnChange() => signal.Release();
        _mdns.CacheChanged += OnChange;
        try
        {
            // RFC 6762 §5.2: intervals grow (1s, 2s, 4s…), capped here at 10s.
            var interval = TimeSpan.FromSeconds(1);
            var nextQuery = DateTime.UtcNow;
            var lastPending = DateTime.MinValue;
            while (!ct.IsCancellationRequested)
            {
                if (DateTime.UtcNow >= nextQuery)
                {
                    var questions = new List<DnsQuestion> { new(AirDropConstants.ServiceType, DnsType.PTR) };
                    questions.AddRange(_mdns.PendingResolutions(AirDropConstants.ServiceType));
                    await _mdns.QueryAsync(questions);
                    nextQuery = DateTime.UtcNow + interval;
                    interval = TimeSpan.FromSeconds(Math.Min(interval.TotalSeconds * 2, 10));
                }
                foreach (var p in CurrentPeers())
                    if (seen.Add(p.InstanceName)) onPeer(p);

                // Ask promptly for missing SRV/addresses of half-resolved peers.
                var pending = _mdns.PendingResolutions(AirDropConstants.ServiceType).ToList();
                if (pending.Count > 0 && DateTime.UtcNow - lastPending > TimeSpan.FromMilliseconds(500))
                {
                    lastPending = DateTime.UtcNow;
                    await _mdns.QueryAsync(pending);
                }

                var wait = nextQuery - DateTime.UtcNow;
                if (wait < TimeSpan.FromMilliseconds(50)) wait = TimeSpan.FromMilliseconds(50);
                try { await signal.WaitAsync(wait, ct); }
                catch (OperationCanceledException) { return; }
            }
        }
        finally { _mdns.CacheChanged -= OnChange; }
    }

    /// <summary>Browses until a peer matches or the timeout expires.</summary>
    public async Task<AirDropPeer?> FindAsync(Func<AirDropPeer, bool> predicate, TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        AirDropPeer? found = null;
        await BrowseAsync(p =>
        {
            if (found == null && predicate(p))
            {
                found = p;
                cts.Cancel();
            }
        }, cts.Token);
        return found;
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsMdns) await _mdns.DisposeAsync();
    }
}
