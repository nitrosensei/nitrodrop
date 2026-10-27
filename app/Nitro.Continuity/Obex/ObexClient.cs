namespace Nitro.Continuity.Obex;

public sealed class ObexException(string message, ObexResponseCode code) : Exception(message)
{
    public ObexResponseCode Code { get; } = code;
}

/// <summary>
/// OBEX client over a byte stream pair (RFCOMM socket in Nitro.Continuity.Windows, a pipe in tests). Requests are
/// serialized. GET follows Continue responses; PUT splits the body to the negotiated max packet length.
/// </summary>
public sealed class ObexClient : IAsyncDisposable
{
    private readonly Stream _in;
    private readonly Stream _out;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ObexClient(Stream input, Stream output) { _in = input; _out = output; }

    public uint? ConnectionId { get; private set; }
    /// <summary>Peer's max packet length (255 until Connect negotiates a bigger one).</summary>
    public int MaxPacketLength { get; private set; } = ObexPacket.MinimumMaxPacketLength;

    /// <summary>Connect with a Target UUID; stores ConnectionId and max packet length. Throws <see cref="ObexException"/> on refusal.</summary>
    public async Task<ObexPacket> ConnectAsync(Guid? target, CancellationToken ct = default)
    {
        var resp = await RequestAsync(ObexPacket.Connect(target), isConnect: true, ct).ConfigureAwait(false);
        if (resp.Response != ObexResponseCode.Success)
            throw new ObexException($"OBEX Connect refused: {resp.Response}", resp.Response);
        MaxPacketLength = Math.Max(ObexPacket.MinimumMaxPacketLength, (int)resp.MaxPacketLength);
        ConnectionId = resp.Find(ObexHeaderId.ConnectionId)?.Value;
        return resp;
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        var p = new ObexPacket { Code = (byte)ObexOpcode.Disconnect };
        if (ConnectionId is { } id) p.Add(ObexHeader.ConnectionId(id));
        try { await RequestAsync(p, false, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or ObjectDisposedException or EndOfStreamException) { }
    }

    /// <summary>One request → one response packet.</summary>
    public async Task<ObexPacket> RequestAsync(ObexPacket request, bool isConnect = false, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await RoundTripAsync(request, isConnect, ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<ObexPacket> RoundTripAsync(ObexPacket request, bool isConnect, CancellationToken ct)
    {
        var bytes = request.Encode();
        await _out.WriteAsync(bytes, ct).ConfigureAwait(false);
        await _out.FlushAsync(ct).ConfigureAwait(false);
        var raw = await ObexPacket.ReadRawAsync(_in, ct).ConfigureAwait(false);
        return ObexPacket.Decode(raw, isConnectResponse: isConnect);
    }

    public async Task SetPathAsync(string? name, bool backup = false, CancellationToken ct = default)
    {
        var p = ObexPacket.SetPath(name, backup);
        if (ConnectionId is { } id) p.Headers.Insert(0, ObexHeader.ConnectionId(id));
        var r = await RequestAsync(p, false, ct).ConfigureAwait(false);
        if (r.Response != ObexResponseCode.Success)
            throw new ObexException($"SetPath '{name}' failed: {r.Response}", r.Response);
    }

    /// <summary>SetPath to root, then each component of <paramref name="path"/> ("telecom/msg").</summary>
    public async Task SetFolderAsync(string path, CancellationToken ct = default)
    {
        await SetPathAsync("", false, ct).ConfigureAwait(false);
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            await SetPathAsync(part, false, ct).ConfigureAwait(false);
    }

    /// <summary>GET: sends the request, then GetFinal (with ConnectionId) for every Continue, concatenating Body/EndOfBody.</summary>
    public async Task<(ObexPacket Final, byte[] Body)> GetAsync(ObexPacket request, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var body = new MemoryStream();
            var resp = await RoundTripAsync(request, false, ct).ConfigureAwait(false);
            for (int guard = 0; ; guard++)
            {
                foreach (var h in resp.Headers)
                    if (h.Id is ObexHeaderId.Body or ObexHeaderId.EndOfBody && h.Bytes is { } b) body.Write(b);
                if (resp.Response != ObexResponseCode.Continue) break;
                if (guard > 100_000) throw new InvalidDataException("OBEX GET never finished.");
                var next = new ObexPacket { Code = (byte)ObexOpcode.GetFinal };
                if (ConnectionId is { } id) next.Add(ObexHeader.ConnectionId(id));
                resp = await RoundTripAsync(next, false, ct).ConfigureAwait(false);
            }
            if (resp.Response != ObexResponseCode.Success)
                throw new ObexException($"GET failed: {resp.Response}", resp.Response);
            return (resp, body.ToArray());
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// PUT: the request's non-body headers go in the first packet; its Body/EndOfBody bytes are split so every packet fits
    /// <see cref="MaxPacketLength"/>. Intermediate packets are Put (expect Continue), the last PutFinal with EndOfBody.
    /// </summary>
    public async Task<ObexPacket> PutAsync(ObexPacket request, CancellationToken ct = default)
    {
        var headers = request.Headers.Where(h => h.Id is not (ObexHeaderId.Body or ObexHeaderId.EndOfBody)).ToList();
        var body = request.Headers.Where(h => h.Id is ObexHeaderId.Body or ObexHeaderId.EndOfBody)
                                  .SelectMany(h => h.Bytes ?? []).ToArray();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int offset = 0;
            bool first = true;
            while (true)
            {
                var p = new ObexPacket();
                if (first) p.Headers.AddRange(headers);
                else if (ConnectionId is { } id) p.Add(ObexHeader.ConnectionId(id));
                int overhead = 3 + p.Headers.Sum(h => h.EncodedLength) + 3;
                int room = Math.Max(1, MaxPacketLength - overhead);
                int n = Math.Min(room, body.Length - offset);
                bool last = offset + n >= body.Length;
                p.Code = (byte)(last ? ObexOpcode.PutFinal : ObexOpcode.Put);
                p.Add(ObexHeader.ByteSeq(last ? ObexHeaderId.EndOfBody : ObexHeaderId.Body, body.AsSpan(offset, n).ToArray()));
                var resp = await RoundTripAsync(p, false, ct).ConfigureAwait(false);
                offset += n;
                first = false;
                if (last) return resp;
                if (resp.Response != ObexResponseCode.Continue)
                    throw new ObexException($"PUT interrupted: {resp.Response}", resp.Response);
            }
        }
        finally { _gate.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
