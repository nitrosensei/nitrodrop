using System.Text;
using Nitro.Continuity.Obex;

namespace Nitro.Continuity.Map;

/// <summary>
/// OBEX server side of the MAP Message Notification Service (what the iPhone connects back to after
/// SetNotificationRegistration). Transport-agnostic: feed each request packet to <see cref="Handle"/> and write the
/// returned response. Accepts Connect with Target = MNS UUID, multi-packet PUT x-bt/MAP-event-report, Disconnect, Abort.
/// </summary>
public sealed class MnsSession
{
    private readonly uint _connectionId;
    private MemoryStream? _body;
    private string? _type;
    private ObexAppParameters? _params;

    public MnsSession(uint connectionId = 1) => _connectionId = connectionId;

    public bool Connected { get; private set; }

    /// <summary>MASInstanceID (from the PUT's application parameters, null if absent) and the parsed events.</summary>
    public event Action<byte?, IReadOnlyList<MapEvent>>? EventReport;

    /// <summary>Raised when an event report body is not valid XML (the response is still Success so the phone moves on).</summary>
    public event Action<Exception>? ParseError;

    public byte[] Handle(ReadOnlySpan<byte> raw)
    {
        ObexPacket req;
        try { req = ObexPacket.Decode(raw); }
        catch (FormatException) { return Respond(ObexResponseCode.BadRequest); }

        switch ((ObexOpcode)req.Code)
        {
            case ObexOpcode.Connect:
            {
                var target = req.Find(ObexHeaderId.Target)?.Bytes;
                // A Connect response always carries version/flags/MTU, even when refused (OBEX 1.5 §3.4.1).
                if (target is null || !target.AsSpan().SequenceEqual(ObexHeader.UuidBytes(MapUuids.MnsTarget)))
                    return new ObexPacket { Code = (byte)ObexResponseCode.NotAcceptable, IsConnect = true, MaxPacketLength = 0x2000 }.Encode();
                Connected = true;
                var r = new ObexPacket
                {
                    Code = (byte)ObexResponseCode.Success, IsConnect = true,
                    MaxPacketLength = Math.Min(req.MaxPacketLength, (ushort)0xFFFF),
                };
                r.Add(ObexHeader.ConnectionId(_connectionId)).Add(ObexHeader.Who(MapUuids.MnsTarget));
                return r.Encode();
            }
            case ObexOpcode.Disconnect:
                Connected = false;
                ResetPut();
                return Respond(ObexResponseCode.Success);
            case ObexOpcode.Abort:
                ResetPut();
                return Respond(ObexResponseCode.Success);
            case ObexOpcode.Put:
            case ObexOpcode.PutFinal:
            {
                if (!Connected) return Respond(ObexResponseCode.Forbidden);
                _body ??= new MemoryStream();
                foreach (var h in req.Headers)
                {
                    switch (h.Id)
                    {
                        case ObexHeaderId.Type: _type = h.TypeString; break;
                        case ObexHeaderId.AppParameters when h.Bytes is { } ap: _params = ObexAppParameters.Decode(ap); break;
                        case ObexHeaderId.Body or ObexHeaderId.EndOfBody when h.Bytes is { } b: _body.Write(b); break;
                    }
                }
                if (req.Code == (byte)ObexOpcode.Put) return Respond(ObexResponseCode.Continue);
                var type = _type;
                var body = _body.ToArray();
                var masId = _params?.GetUInt(MapTag.MasInstanceId) is { } m ? (byte)m : (byte?)null;
                ResetPut();
                if (!string.Equals(type, MapTypes.EventReport, StringComparison.OrdinalIgnoreCase))
                    return Respond(ObexResponseCode.NotImplemented);
                try
                {
                    var events = MapXml.ParseEventReport(Encoding.UTF8.GetString(body));
                    EventReport?.Invoke(masId, events);
                }
                catch (Exception e) when (e is System.Xml.XmlException or NullReferenceException) { ParseError?.Invoke(e); }
                return Respond(ObexResponseCode.Success);
            }
            default:
                return Respond(ObexResponseCode.NotImplemented);
        }
    }

    private void ResetPut() { _body = null; _type = null; _params = null; }

    private static byte[] Respond(ObexResponseCode code) => new ObexPacket { Code = (byte)code }.Encode();

    /// <summary>Serves one MNS connection: read packet → <see cref="Handle"/> → write, until the stream ends or Disconnect.</summary>
    public async Task ServeAsync(Stream input, Stream output, CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            byte[] raw;
            try { raw = await ObexPacket.ReadRawAsync(input, ct).ConfigureAwait(false); }
            catch (Exception e) when (e is EndOfStreamException or IOException or ObjectDisposedException) { return; }
            var resp = Handle(raw);
            await output.WriteAsync(resp, ct).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);
            if (raw[0] == (byte)ObexOpcode.Disconnect) return;
        }
    }
}
