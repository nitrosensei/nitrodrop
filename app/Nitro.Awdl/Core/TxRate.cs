// Nitro.Awdl: C# port of owly-awdl (https://github.com/t4t5/owly, crates/owly-awdl, commit 9a676e1).
// Copyright (c) 2026 T4T5 (Owly, MIT License). Port: Copyright (c) 2026 Nitrobyte. See LICENSE-OWLY.txt.
// Source: crates/owly-awdl/src/rate.rs: transmit rates for data frames.

namespace Nitro.Awdl;

/// <summary>
/// A rate for one frame: legacy (non-HT, in 500 kbit/s units as radiotap and our driver's
/// <c>AIRDROP_TX_HEADER.Rate</c> count it) or an HT MCS on 20 MHz. A node sends at one configured rate, 12 Mbit/s by
/// default; HT only towards a peer whose HT Capabilities TLV says it receives that MCS.
/// </summary>
public readonly record struct TxRate
{
    public bool IsHt { get; }
    /// <summary>Legacy: rate in 500 kbit/s units. HT: the MCS index.</summary>
    public byte Value { get; }
    /// <summary>HT short guard interval.</summary>
    public bool ShortGi { get; }

    private TxRate(bool ht, byte value, bool sgi)
    {
        IsHt = ht;
        Value = value;
        ShortGi = sgi;
    }

    public static TxRate Legacy(byte halfMbps) => new(false, halfMbps, false);
    public static TxRate Ht(byte mcs, bool sgi = false) => new(true, mcs, sgi);

    /// <summary>12 Mbit/s, the default, for action frames and multicast.</summary>
    public static readonly TxRate Basic = Legacy(24);

    /// <summary>Parses <c>12</c> (Mbit/s: 6, 9, 12, 18, 24, 36, 48, 54), <c>mcs7</c> or <c>mcs7-sgi</c> (MCS 0-15).</summary>
    public static TxRate? Parse(string s)
    {
        if (s.StartsWith("mcs", StringComparison.Ordinal))
        {
            var rest = s[3..];
            bool sgi = rest.EndsWith("-sgi", StringComparison.Ordinal);
            var n = sgi ? rest[..^4] : rest;
            if (n.Length == 0 || n[0] == '+' || !byte.TryParse(n, System.Globalization.NumberStyles.None, null, out var mcs) || mcs > 15)
                return null;
            return Ht(mcs, sgi);
        }
        if (!byte.TryParse(s, System.Globalization.NumberStyles.None, null, out var mbps)) return null;
        return mbps is 6 or 9 or 12 or 18 or 24 or 36 or 48 or 54 ? Legacy((byte)(mbps * 2)) : null;
    }

    /// <summary>The rate to use towards a peer with these HT capabilities (or none).</summary>
    public TxRate ForPeer(HtCapabilities? peer)
    {
        if (!IsHt) return this;
        if (peer is { } ht && Value < 32 && (ht.RxMcs & (1u << Value)) != 0)
            return Ht(Value, ShortGi && ht.Sgi20);
        return Basic;
    }

    public override string ToString() =>
        IsHt ? $"HT MCS {Value}{(ShortGi ? ", short GI" : "")}"
             : Value % 2 == 0 ? $"{Value / 2} Mbit/s" : $"{Value / 2}.5 Mbit/s";
}
