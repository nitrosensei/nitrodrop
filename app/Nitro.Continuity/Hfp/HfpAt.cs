using System.Globalization;
using System.Text;

namespace Nitro.Continuity.Hfp;

// Hands-Free Profile 1.8 (iOS AG: https://support.apple.com/HT204387), HF side. AT commands are CR-terminated;
// AG results are framed as <CR><LF>result<CR><LF>.

[Flags]
public enum HfFeatures : uint
{
    None = 0,
    EcNr = 1 << 0,
    ThreeWayCalling = 1 << 1,
    CallerIdPresentation = 1 << 2,
    VoiceRecognition = 1 << 3,
    RemoteVolumeControl = 1 << 4,
    EnhancedCallStatus = 1 << 5,
    EnhancedCallControl = 1 << 6,
    CodecNegotiation = 1 << 7,
    HfIndicators = 1 << 8,
    EscoS4 = 1 << 9,
}

[Flags]
public enum AgFeatures : uint
{
    None = 0,
    ThreeWayCalling = 1 << 0,
    EcNr = 1 << 1,
    VoiceRecognition = 1 << 2,
    InBandRingTone = 1 << 3,
    VoiceTag = 1 << 4,
    RejectCall = 1 << 5,
    EnhancedCallStatus = 1 << 6,
    EnhancedCallControl = 1 << 7,
    ExtendedErrorCodes = 1 << 8,
    CodecNegotiation = 1 << 9,
    HfIndicators = 1 << 10,
    EscoS4 = 1 << 11,
}

public static class HfpCommands
{
    public static string Brsf(HfFeatures f) => $"AT+BRSF={(uint)f}\r";
    public const string CindTest = "AT+CIND=?\r";
    public const string CindRead = "AT+CIND?\r";
    /// <summary>Enable indicator event reporting (+CIEV).</summary>
    public const string CmerEnable = "AT+CMER=3,0,0,1\r";
    public const string ClipEnable = "AT+CLIP=1\r";
    public const string Answer = "ATA\r";
    public const string HangUp = "AT+CHUP\r";
    public const string ListCalls = "AT+CLCC\r";
    public const string Redial = "AT+BLDN\r";
    public const string ChldTest = "AT+CHLD=?\r";
    public const string CopsFormat = "AT+COPS=3,0\r";
    public const string CopsRead = "AT+COPS?\r";
    public static string Dial(string number) => $"ATD{number};\r";
    public static string Dtmf(char c) => $"AT+VTS={c}\r";
    public static string SpeakerGain(int g) => $"AT+VGS={Math.Clamp(g, 0, 15)}\r";
    /// <summary>Apple accessory battery/dock state (HFP Apple extensions; UNVERIFIED that iOS uses it for PCs).</summary>
    public static string AppleAccessory(int vendorId, int productId, string version, int features) =>
        $"AT+XAPL={vendorId:X4}-{productId:X4}-{version},{features}\r";
}

public abstract record AtResult;
public sealed record AtOk : AtResult;
public sealed record AtError(int? CmeCode = null) : AtResult;
public sealed record AtRing : AtResult;
public sealed record AtBrsf(AgFeatures Features) : AtResult;
/// <summary>"+CIND: (\"service\",(0,1)),(\"call\",(0,1)),..." — indicator names in order (index = position + 1).</summary>
public sealed record AtCindTest(IReadOnlyList<string> Names) : AtResult;
public sealed record AtCindValues(IReadOnlyList<int> Values) : AtResult;
/// <summary>"+CIEV: &lt;index&gt;,&lt;value&gt;" (1-based index into the +CIND list).</summary>
public sealed record AtCiev(int Index, int Value) : AtResult;
public sealed record AtClip(string Number, int Type, string? Name) : AtResult;
public sealed record AtClcc(int Index, int Direction, int Status, int Mode, bool Multiparty, string? Number, int? Type) : AtResult;
public sealed record AtUnsolicited(string Command, string Args) : AtResult;
public sealed record AtUnknown(string Line) : AtResult;

/// <summary>Splits the AG byte stream into lines and parses each result.</summary>
public sealed class AtResultParser
{
    private readonly StringBuilder _buf = new();

    public IReadOnlyList<AtResult> Feed(string chunk)
    {
        _buf.Append(chunk);
        var results = new List<AtResult>();
        while (true)
        {
            var s = _buf.ToString();
            int i = s.IndexOfAny(['\r', '\n']);
            if (i < 0) break;
            var line = s[..i].Trim();
            int j = i;
            while (j < s.Length && (s[j] == '\r' || s[j] == '\n')) j++;
            _buf.Remove(0, j);
            if (line.Length > 0) results.Add(ParseLine(line));
        }
        return results;
    }

    public IReadOnlyList<AtResult> Feed(ReadOnlySpan<byte> bytes) => Feed(Encoding.UTF8.GetString(bytes));

    public static AtResult ParseLine(string line)
    {
        if (line == "OK") return new AtOk();
        if (line == "ERROR") return new AtError();
        if (line == "RING") return new AtRing();
        if (!line.StartsWith('+')) return new AtUnknown(line);
        int c = line.IndexOf(':');
        if (c < 0) return new AtUnknown(line);
        var cmd = line[1..c].Trim().ToUpperInvariant();
        var args = line[(c + 1)..].Trim();
        try
        {
            switch (cmd)
            {
                case "CME ERROR": return new AtError(int.Parse(args, CultureInfo.InvariantCulture));
                case "BRSF": return new AtBrsf((AgFeatures)uint.Parse(args, CultureInfo.InvariantCulture));
                case "CIEV":
                {
                    var p = args.Split(',');
                    return new AtCiev(int.Parse(p[0], CultureInfo.InvariantCulture), int.Parse(p[1], CultureInfo.InvariantCulture));
                }
                case "CIND":
                    if (args.StartsWith('('))
                    {
                        var names = new List<string>();
                        int pos = 0;
                        while ((pos = args.IndexOf("(\"", pos, StringComparison.Ordinal)) >= 0)
                        {
                            int end = args.IndexOf('"', pos + 2);
                            names.Add(args[(pos + 2)..end]);
                            pos = end;
                        }
                        return new AtCindTest(names);
                    }
                    return new AtCindValues(args.Split(',').Select(v => int.Parse(v.Trim(), CultureInfo.InvariantCulture)).ToList());
                case "CLIP":
                {
                    var p = SplitArgs(args);
                    return new AtClip(p[0] ?? "", p.Count > 1 && int.TryParse(p[1], out var t) ? t : 129,
                        p.Count > 4 && !string.IsNullOrEmpty(p[4]) ? p[4] : null);
                }
                case "CLCC":
                {
                    var p = SplitArgs(args);
                    int I(int k) => int.Parse(p[k]!, CultureInfo.InvariantCulture);
                    return new AtClcc(I(0), I(1), I(2), I(3), I(4) == 1,
                        p.Count > 5 ? p[5] : null, p.Count > 6 && int.TryParse(p[6], out var ty) ? ty : null);
                }
            }
        }
        catch (Exception e) when (e is FormatException or IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
        {
            return new AtUnknown(line);
        }
        return new AtUnsolicited(cmd, args);
    }

    /// <summary>Comma split honouring double quotes; quotes removed, empty fields → "".</summary>
    internal static List<string?> SplitArgs(string args)
    {
        var res = new List<string?>();
        var cur = new StringBuilder();
        bool q = false;
        foreach (char ch in args)
        {
            if (ch == '"') { q = !q; continue; }
            if (ch == ',' && !q) { res.Add(cur.ToString().Trim()); cur.Clear(); continue; }
            cur.Append(ch);
        }
        res.Add(cur.ToString().Trim());
        return res;
    }
}

public enum PhoneCallState { Idle, Incoming, Outgoing, Active, Held }

/// <summary>
/// Service Level Connection driver + call-state tracker. Feed AG results in; send the commands it returns.
/// SLC order (HFP 1.8 §4.2): BRSF → (BAC if codec negotiation) → CIND=? → CIND? → CMER → (CHLD=? if 3-way) → done; then CLIP=1.
/// We don't advertise codec negotiation or 3-way calling, so BAC/CHLD are skipped.
/// </summary>
public sealed class HfpSession
{
    private enum Step { NotStarted, Brsf, CindTest, CindRead, Cmer, Clip, Connected }

    private Step _step = Step.NotStarted;
    private readonly Dictionary<string, int> _indicatorIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, int> _values = new();

    public HfFeatures LocalFeatures { get; }
    public AgFeatures RemoteFeatures { get; private set; }
    public bool IsSlcEstablished => _step == Step.Connected;
    public PhoneCallState State { get; private set; } = PhoneCallState.Idle;
    public string? CallerNumber { get; private set; }
    public string? CallerName { get; private set; }

    public event Action<PhoneCallState>? StateChanged;
    /// <summary>Raised on each RING / +CLIP while incoming (caller may still be null on the first RING).</summary>
    public event Action<string?, string?>? Ringing;

    public HfpSession(HfFeatures features = HfFeatures.CallerIdPresentation | HfFeatures.EnhancedCallStatus | HfFeatures.RemoteVolumeControl)
        => LocalFeatures = features;

    public IReadOnlyDictionary<string, int> IndicatorIndex => _indicatorIndex;

    /// <summary>First command to send right after the RFCOMM channel opens.</summary>
    public string Start() { _step = Step.Brsf; return HfpCommands.Brsf(LocalFeatures); }

    /// <summary>Process one parsed AG result; returns the next command to send, or null.</summary>
    public string? OnResult(AtResult r)
    {
        switch (r)
        {
            case AtBrsf b: RemoteFeatures = b.Features; return null;
            case AtCindTest t:
                _indicatorIndex.Clear();
                for (int i = 0; i < t.Names.Count; i++) _indicatorIndex[t.Names[i]] = i + 1;
                return null;
            case AtCindValues v:
                for (int i = 0; i < v.Values.Count; i++) _values[i + 1] = v.Values[i];
                Recompute();
                return null;
            case AtCiev ev:
                _values[ev.Index] = ev.Value;
                Recompute();
                return null;
            case AtRing:
                if (State == PhoneCallState.Idle) SetState(PhoneCallState.Incoming);
                Ringing?.Invoke(CallerNumber, CallerName);
                return null;
            case AtClip c:
                CallerNumber = c.Number; CallerName = c.Name;
                if (State == PhoneCallState.Idle) SetState(PhoneCallState.Incoming);
                Ringing?.Invoke(CallerNumber, CallerName);
                return null;
            case AtOk:
                return Advance();
            case AtError when _step is not Step.Connected:
                // Optional step failed (e.g. CLIP): keep going; mandatory ones are surfaced by the caller via IsSlcEstablished.
                return _step == Step.Clip ? Advance() : null;
        }
        return null;
    }

    private string? Advance()
    {
        switch (_step)
        {
            case Step.Brsf: _step = Step.CindTest; return HfpCommands.CindTest;
            case Step.CindTest: _step = Step.CindRead; return HfpCommands.CindRead;
            case Step.CindRead: _step = Step.Cmer; return HfpCommands.CmerEnable;
            case Step.Cmer: _step = Step.Clip; return HfpCommands.ClipEnable;
            case Step.Clip: _step = Step.Connected; return null;
            default: return null;
        }
    }

    private int Ind(string name) => _indicatorIndex.TryGetValue(name, out var i) && _values.TryGetValue(i, out var v) ? v : 0;

    private void Recompute()
    {
        int call = Ind("call"), setup = Ind("callsetup"), held = Ind("callheld");
        PhoneCallState s =
            setup == 1 ? PhoneCallState.Incoming :
            setup is 2 or 3 ? PhoneCallState.Outgoing :
            call == 1 ? (held == 2 ? PhoneCallState.Held : PhoneCallState.Active) :
            PhoneCallState.Idle;
        SetState(s);
    }

    private void SetState(PhoneCallState s)
    {
        if (s == State) return;
        State = s;
        if (s == PhoneCallState.Idle) { CallerNumber = null; CallerName = null; }
        StateChanged?.Invoke(s);
    }
}
