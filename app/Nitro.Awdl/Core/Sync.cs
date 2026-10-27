// Nitro.Awdl: C# port of owly-awdl (https://github.com/t4t5/owly, crates/owly-awdl, commit 9a676e1).
// Copyright (c) 2026 T4T5 (Owly, MIT License). Port: Copyright (c) 2026 Nitrobyte. See LICENSE-OWLY.txt.
// Source: crates/owly-awdl/src/sync.rs: availability windows (SPEC §3.3, AWDL-7, AWDL-16).

namespace Nitro.Awdl;

/// <summary>AW schedule constants.</summary>
public static class AwdlSchedule
{
    /// <summary>TU per availability window.</summary>
    public const ushort AwPeriodTu = 16;
    /// <summary>Availability windows per slot (the presence mode, §3.2).</summary>
    public const byte PresenceMode = 4;
    /// <summary>TU per slot (one extended availability window).</summary>
    public const ulong SlotTu = AwPeriodTu * PresenceMode;
    /// <summary>Microseconds per slot (65 536 µs).</summary>
    public const ulong SlotUs = SlotTu * Awdl.TU;
    /// <summary>A full 16-slot channel sequence period (≈1.05 s).</summary>
    public const ulong PeriodUs = SlotUs * ChannelSequence.Slots;
    /// <summary>AWDL-16: how far from a slot's edges unicast keeps (3 TU).</summary>
    public const ulong UnicastGuardUs = 3 * Awdl.TU;
    /// <summary>AWDL-16: how far from a slot's edges multicast keeps (16 TU).</summary>
    public const ulong MulticastGuardUs = 16 * Awdl.TU;
}

/// <summary>
/// Where this node is in the AW schedule: the AW count at a slot boundary, and when that boundary was. A node that
/// isn't the sync master aligns count and phase to the master's Synchronization Parameters TLV (AWDL-7).
/// </summary>
public struct AwdlClock
{
    private AwdlTime _last;   // when the slot starting with AW _awCounter began
    private ushort _awCounter;

    /// <summary>A schedule of our own, starting a slot at <paramref name="now"/>.</summary>
    public AwdlClock(AwdlTime now)
    {
        _last = now;
        _awCounter = 0;
    }

    private readonly ulong SinceLast(AwdlTime now) => now.Since(_last);

    /// <summary>Microseconds to the next slot boundary, in 1..=SlotUs.</summary>
    public readonly ulong NextSlotUs(AwdlTime now) => AwdlSchedule.SlotUs - SinceLast(now) % AwdlSchedule.SlotUs;

    /// <summary>TU to the next slot boundary, as the Sync Parameters TLV announces it (its TX down-counter).</summary>
    public readonly ushort NextSlotTu(AwdlTime now) =>
        (ushort)(AwdlSchedule.SlotTu - SinceLast(now) / Awdl.TU % AwdlSchedule.SlotTu);

    /// <summary>The AW count now, as announced in the TLV's next AW sequence number.</summary>
    public readonly ushort CurrentAw(AwdlTime now)
    {
        ulong tu = SinceLast(now) / Awdl.TU;
        ulong aw = _awCounter + tu % AwdlSchedule.SlotTu / AwdlSchedule.AwPeriodTu
                   + AwdlSchedule.PresenceMode * (tu / AwdlSchedule.SlotTu);
        return (ushort)aw;
    }

    /// <summary>The slot count now: one per four AWs.</summary>
    public readonly ushort CurrentEaw(AwdlTime now) => (ushort)(CurrentAw(now) / AwdlSchedule.PresenceMode);

    /// <summary>The slot of the channel sequence now.</summary>
    public readonly int Slot(AwdlTime now) => CurrentEaw(now) % ChannelSequence.Slots;

    /// <summary>
    /// How far, in TU, a schedule announced at <paramref name="now"/> as <paramref name="nextTu"/> TU to the next
    /// boundary and AW count <paramref name="awCounter"/> is ahead of ours (negative: behind). Slot counts are compared
    /// modulo their wrap.
    /// </summary>
    public readonly long ErrorTu(AwdlTime now, ushort nextTu, ushort awCounter)
    {
        const int eawWrap = 1 << 14; // u16 AWs, four a slot
        int theirs = awCounter / AwdlSchedule.PresenceMode;
        int ours = CurrentEaw(now);
        int d = theirs - ours + eawWrap / 2;
        int slots = ((d % eawWrap) + eawWrap) % eawWrap - eawWrap / 2;
        return (long)slots * (long)AwdlSchedule.SlotTu - (nextTu - (long)NextSlotTu(now));
    }

    /// <summary>Adopts a schedule announced at <paramref name="now"/> (AWDL-7).</summary>
    public void Update(AwdlTime now, ushort nextTu, ushort awCounter)
    {
        ulong next = Math.Min(nextTu, AwdlSchedule.SlotTu);
        _last = now.Minus((AwdlSchedule.SlotTu - next) * Awdl.TU);
        _awCounter = (ushort)(awCounter & ~(AwdlSchedule.PresenceMode - 1));
    }

    /// <summary>Whether <paramref name="now"/> is within <paramref name="guard"/> of the end of a slot.</summary>
    public readonly bool InEndGuard(AwdlTime now, ulong guard) => NextSlotUs(now) < guard;

    /// <summary>Whether <paramref name="now"/> is within <paramref name="guard"/> of the start of a slot.</summary>
    public readonly bool InStartGuard(AwdlTime now, ulong guard) => AwdlSchedule.SlotUs - NextSlotUs(now) < guard;

    /// <summary>How long until <paramref name="now"/> is clear of <paramref name="guard"/> at both slot edges; 0 if it is.</summary>
    public readonly ulong EdgeWait(AwdlTime now, ulong guard)
    {
        ulong next = NextSlotUs(now);
        if (next < guard) return next + guard;
        ulong into = AwdlSchedule.SlotUs - next;
        return guard > into ? guard - into : 0;
    }
}
