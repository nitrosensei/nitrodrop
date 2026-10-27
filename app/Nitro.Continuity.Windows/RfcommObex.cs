using System.Text;
using Nitro.Continuity.Map;
using Nitro.Continuity.Obex;
using Nitro.Continuity.Runtime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Networking.Sockets;

namespace Nitro.Continuity.Platform;

public sealed class RfcommServiceMissingException(ushort shortId, string detail)
    : Exception($"RFCOMM service 0x{shortId:X4} not available: {detail}")
{
    public ushort ShortId { get; } = shortId;
}

/// <summary>An OBEX session over an RFCOMM StreamSocket to one of the iPhone's services (MAS 0x1132, PSE 0x112F).</summary>
public sealed class RfcommObexSession : IAsyncDisposable
{
    private StreamSocket? _socket;

    public ObexClient Obex { get; private set; } = null!;

    public static async Task<RfcommObexSession> OpenAsync(BluetoothDevice device, ushort shortId, Guid target, CancellationToken ct = default)
    {
        var r = await device.GetRfcommServicesForIdAsync(RfcommServiceId.FromShortId(shortId), BluetoothCacheMode.Uncached).AsTask(ct);
        if (r.Error != BluetoothError.Success || r.Services.Count == 0)
            throw new RfcommServiceMissingException(shortId, r.Error == BluetoothError.Success ? "not advertised" : r.Error.ToString());
        var svc = r.Services[0];
        var access = await svc.RequestAccessAsync().AsTask(ct);
        if (access != Windows.Devices.Enumeration.DeviceAccessStatus.Allowed)
            throw new RfcommServiceMissingException(shortId, $"access {access}");

        var s = new RfcommObexSession { _socket = new StreamSocket() };
        try
        {
            await s._socket.ConnectAsync(svc.ConnectionHostName, svc.ConnectionServiceName,
                SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication).AsTask(ct);
            s.Obex = new ObexClient(s._socket.InputStream.AsStreamForRead(0), s._socket.OutputStream.AsStreamForWrite(0));
            await s.Obex.ConnectAsync(target, ct);
            return s;
        }
        catch
        {
            await s.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Obex is not null)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await Obex.DisconnectAsync(cts.Token); } catch (Exception) { }
            await Obex.DisposeAsync();
        }
        _socket?.Dispose();
        _socket = null;
    }
}

/// <summary>MAP client (MCE) to the iPhone's MAS: folders, listings, GetMessage, PushMessage, notification registration.</summary>
public sealed class MapClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RfcommObexSession? _session;

    public bool IsConnected => _session is not null;

    /// <summary>Throws <see cref="ObexException"/> (ServiceUnavailable = busy with another MCE, e.g. Phone Link) or
    /// <see cref="RfcommServiceMissingException"/> ("Bildirimleri Göster" off / not paired).</summary>
    public async Task ConnectAsync(BluetoothDevice device, CancellationToken ct = default)
    {
        await DisconnectAsync();
        _session = await RfcommObexSession.OpenAsync(device, Bluetooth.BluetoothUuids.MapMas, MapUuids.MasTarget, ct);
    }

    private ObexClient Obex => _session?.Obex ?? throw new InvalidOperationException("MAP not connected.");
    private uint Conn => Obex.ConnectionId ?? 0;

    private async Task<T> Locked<T>(Func<Task<T>> f)
    {
        await _gate.WaitAsync();
        try { return await f(); }
        finally { _gate.Release(); }
    }

    public Task<IReadOnlyList<string>> ListFoldersAsync(string path = "telecom/msg", CancellationToken ct = default) => Locked(async () =>
    {
        await Obex.SetFolderAsync(path, ct);
        var (_, body) = await Obex.GetAsync(MapFolders.GetFolderListing(Conn), ct);
        return MapFolders.ParseFolderListing(Encoding.UTF8.GetString(body));
    });

    public Task<IReadOnlyList<MapMessageListItem>> GetMessagesListingAsync(string folder = "inbox", ushort max = 50, CancellationToken ct = default) => Locked(async () =>
    {
        await Obex.SetFolderAsync("telecom/msg", ct);
        var (_, body) = await Obex.GetAsync(MapRequests.GetMessagesListing(Conn, folder, max), ct);
        return MapXml.ParseMessageListing(Encoding.UTF8.GetString(body));
    });

    public Task<BMessage> GetMessageAsync(string handle, CancellationToken ct = default) => Locked(async () =>
    {
        var (_, body) = await Obex.GetAsync(MapRequests.GetMessage(Conn, handle), ct);
        return BMessage.Parse(Encoding.UTF8.GetString(body));
    });

    /// <summary>Sends via "outbox". Returns the MAP handle from the response Name header (may be null).</summary>
    public Task<string?> PushMessageAsync(string tel, string text, string? name = null, CancellationToken ct = default) => Locked(async () =>
    {
        await Obex.SetFolderAsync("telecom/msg", ct);
        var resp = await Obex.PutAsync(MapRequests.PushMessage(Conn, BMessage.Outgoing(tel, text, name).ToBytes()), ct);
        if (resp.Response is not (ObexResponseCode.Success or ObexResponseCode.Created or ObexResponseCode.Accepted))
            throw new ObexException($"PushMessage failed: {resp.Response}", resp.Response);
        return resp.Find(ObexHeaderId.Name)?.Text;
    });

    public Task<bool> SetNotificationRegistrationAsync(bool on, CancellationToken ct = default) => Locked(async () =>
    {
        var r = await Obex.PutAsync(MapRequests.SetNotificationRegistration(Conn, on), ct);
        return r.Response == ObexResponseCode.Success;
    });

    public async Task DisconnectAsync()
    {
        if (_session is null) return;
        await _session.DisposeAsync();
        _session = null;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _gate.Dispose();
    }
}

/// <summary>
/// MAP Message Notification Server on the PC: an SDP record for 0x1133 (Windows adds ServiceClassIdList/RFCOMM channel,
/// we add name/profile/features) and a listener; each connection is served by <see cref="MnsSession"/>.
/// </summary>
public sealed class MnsServer : IAsyncDisposable
{
    private RfcommServiceProvider? _provider;
    private StreamSocketListener? _listener;
    private uint _nextConnId = 1;

    public event Action<byte?, IReadOnlyList<MapEvent>>? EventReport;
    public bool IsRunning => _provider is not null;
    public string? LastError { get; private set; }

    public async Task<bool> StartAsync()
    {
        if (_provider is not null) return true;
        try
        {
            _provider = await RfcommServiceProvider.CreateAsync(RfcommServiceId.FromShortId(Bluetooth.BluetoothUuids.MapMns));
            _listener = new StreamSocketListener();
            _listener.ConnectionReceived += OnConnection;
            await _listener.BindServiceNameAsync(_provider.ServiceId.AsString(),
                SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication);
            foreach (var (id, value) in Bluetooth.Sdp.MapMnsAttributes())
            {
                var w = new Windows.Storage.Streams.DataWriter();
                w.WriteBytes(value);
                _provider.SdpRawAttributes[id] = w.DetachBuffer();
            }
            _provider.StartAdvertising(_listener, true);
            return true;
        }
        catch (Exception e)
        {
            LastError = e.Message;
            await DisposeAsync();
            return false;
        }
    }

    private async void OnConnection(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
    {
        using var socket = args.Socket;
        var session = new MnsSession(Interlocked.Increment(ref _nextConnId));
        session.EventReport += (mas, events) => EventReport?.Invoke(mas, events);
        try { await session.ServeAsync(socket.InputStream.AsStreamForRead(0), socket.OutputStream.AsStreamForWrite(0)); }
        catch (Exception e) { LastError = e.Message; }
    }

    public ValueTask DisposeAsync()
    {
        try { _provider?.StopAdvertising(); } catch (Exception) { }
        _provider = null;
        _listener?.Dispose();
        _listener = null;
        return ValueTask.CompletedTask;
    }
}

/// <summary>PBAP client (read-only pull of telecom/pb.vcf). Requires "Kişileri Eşitle" on the iPhone.</summary>
public static class PbapClient
{
    public static async Task<string> PullPhoneBookAsync(BluetoothDevice device, CancellationToken ct = default)
    {
        await using var s = await RfcommObexSession.OpenAsync(device, Bluetooth.BluetoothUuids.PbapPse, PbapUuids.PseTarget, ct);
        var (_, body) = await s.Obex.GetAsync(PbapRequests.PullPhoneBook(s.Obex.ConnectionId ?? 0), ct);
        return Encoding.UTF8.GetString(body);
    }
}
