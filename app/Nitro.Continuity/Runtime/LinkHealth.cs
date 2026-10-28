namespace Nitro.Continuity.Runtime;

/// <summary>Exponential reconnect delays: 1, 2, 4, 8, 16, 30, 60, 60… seconds (capped). <see cref="Reset"/> after a good link.</summary>
public sealed class ReconnectBackoff(TimeSpan? initial = null, TimeSpan? max = null)
{
    private readonly TimeSpan _initial = initial ?? TimeSpan.FromSeconds(1);
    private readonly TimeSpan _max = max ?? TimeSpan.FromSeconds(60);

    public int Attempt { get; private set; }

    public TimeSpan Next()
    {
        var d = TimeSpan.FromTicks(Math.Min(_max.Ticks, _initial.Ticks * (1L << Math.Min(Attempt, 20))));
        Attempt++;
        return d;
    }

    public void Reset() => Attempt = 0;
}

/// <summary>
/// Spots Phone Link (or any second consumer) holding the iPhone. Signals: PhoneLineTransportDevice access
/// DeniedBySystem, the ANCS Data Source stream not matching our requests (another client's GetNotificationAttributes
/// answers land in our assembler — <see cref="Ancs.AncsDataSourceAssembler"/> throws FormatException), and MAP/PBAP
/// Connect answered with ServiceUnavailable/Forbidden. One stray ANCS error can be noise; two within a minute is a conflict.
/// </summary>
public sealed class PhoneLinkConflictDetector(TimeSpan? window = null, int threshold = 2)
{
    private readonly TimeSpan _window = window ?? TimeSpan.FromMinutes(1);
    private readonly Queue<DateTimeOffset> _ancsErrors = new();

    public bool ConflictDetected { get; private set; }
    public string? Evidence { get; private set; }

    public const string Guidance =
        "This iPhone is in use by Phone Link. Open Phone Link → Settings → My devices → ⋯ next to the iPhone → Remove. " +
        "Then press \"Try again\" in NitroDrop.";

    /// <summary>Returns true when this error tips the detector into the conflict state.</summary>
    public bool ReportAncsStreamError(DateTimeOffset now)
    {
        _ancsErrors.Enqueue(now);
        while (_ancsErrors.Count > 0 && now - _ancsErrors.Peek() > _window) _ancsErrors.Dequeue();
        if (_ancsErrors.Count >= threshold && !ConflictDetected)
            return Flag("ANCS Data Source stream does not match our requests (another app is requesting notifications too).");
        return false;
    }

    public bool ReportPhoneLineAccess(PhoneLineAccess access) =>
        access == PhoneLineAccess.DeniedBySystem && !ConflictDetected && Flag("The Windows phone line is registered to another app (DeniedBySystem).");

    /// <summary>OBEX response code of a MAP/PBAP Connect (0xD3 ServiceUnavailable / 0xC3 Forbidden mean "busy with another client").</summary>
    public bool ReportObexConnectRefused(byte responseCode) =>
        (responseCode | 0x80) is 0xD3 && !ConflictDetected && Flag("The iPhone message service is serving another connection (OBEX ServiceUnavailable).");

    public void Reset()
    {
        _ancsErrors.Clear();
        ConflictDetected = false;
        Evidence = null;
    }

    private bool Flag(string evidence)
    {
        ConflictDetected = true;
        Evidence = evidence;
        return true;
    }
}
