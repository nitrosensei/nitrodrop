// Nitro.Awdl: Windows timing primitives for the AWDL host loop.
// Copyright (c) 2026 Nitrobyte.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Nitro.Awdl.Interop;

internal static partial class TimingNative
{
    private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
    private const uint TIMER_ALL_ACCESS = 0x001F0003;

    [LibraryImport("api-ms-win-core-realtime-l1-1-1.dll")]
    [SupportedOSPlatform("windows")]
    internal static partial void QueryInterruptTimePrecise(out ulong lpInterruptTimePrecise);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [SupportedOSPlatform("windows")]
    private static partial SafeWaitHandle CreateWaitableTimerEx(nint attributes, string? name, uint flags, uint access);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SupportedOSPlatform("windows")]
    internal static partial bool SetWaitableTimer(SafeWaitHandle timer, in long dueTime, int period, nint completion, nint arg,
        [MarshalAs(UnmanagedType.Bool)] bool resume);

    [LibraryImport("avrt.dll", EntryPoint = "AvSetMmThreadCharacteristicsW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [SupportedOSPlatform("windows")]
    internal static partial nint AvSetMmThreadCharacteristics(string task, ref uint taskIndex);

    [LibraryImport("avrt.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SupportedOSPlatform("windows")]
    internal static partial bool AvRevertMmThreadCharacteristics(nint handle);

    /// <summary>A high-resolution waitable timer (Windows 10 1803+), or null where unavailable.</summary>
    [SupportedOSPlatform("windows")]
    internal static SafeWaitHandle? TryCreateHighResolutionTimer()
    {
        try
        {
            var h = CreateWaitableTimerEx(0, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
            if (!h.IsInvalid) return h;
            h.Dispose();
        }
        catch (EntryPointNotFoundException) { }
        catch (DllNotFoundException) { }
        return null;
    }
}

/// <summary>
/// The microsecond clock AWDL runs on. On Windows it's the interrupt-time clock (QueryInterruptTimePrecise), the same
/// base our driver stamps RX frames with (KeQueryInterruptTimePrecise), so RX times need no conversion. Elsewhere a
/// Stopwatch.
/// </summary>
public static class HostClock
{
    private static readonly bool UseInterruptTime = OperatingSystem.IsWindows() && Probe();

    private static bool Probe()
    {
        try
        {
            TimingNative.QueryInterruptTimePrecise(out _);
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Microseconds on the host clock.</summary>
    public static ulong NowMicros()
    {
        if (UseInterruptTime && OperatingSystem.IsWindows())
        {
            TimingNative.QueryInterruptTimePrecise(out var t100ns);
            return t100ns / 10;
        }
        return (ulong)(Stopwatch.GetTimestamp() * 1_000_000.0 / Stopwatch.Frequency);
    }

    /// <summary>Converts a driver RX HostTimestamp (KeQueryInterruptTimePrecise, 100 ns units) to this clock.</summary>
    public static ulong FromInterruptTime100ns(ulong t) => t / 10;

    /// <summary>True when <see cref="NowMicros"/> is the interrupt-time clock (driver timestamps are directly comparable).</summary>
    public static bool IsInterruptTime => UseInterruptTime;
}

/// <summary>
/// Waits for any of a set of handles or a deadline, with sub-millisecond deadline precision where Windows offers it
/// (high-resolution waitable timer), optionally spinning the last stretch.
/// </summary>
public sealed class PreciseWaiter : IDisposable
{
    private sealed class TimerWaitHandle : WaitHandle
    {
        public TimerWaitHandle(SafeWaitHandle h) => SafeWaitHandle = h;
    }

    private readonly TimerWaitHandle? _timer;

    /// <summary>Busy-wait the final microseconds before a deadline (0 = never spin). Costs CPU; improves tail latency.</summary>
    public ulong SpinUs { get; init; }

    public PreciseWaiter(bool highResolution = true)
    {
        if (highResolution && OperatingSystem.IsWindows() && TimingNative.TryCreateHighResolutionTimer() is { } h)
            _timer = new TimerWaitHandle(h);
    }

    /// <summary>Whether a high-resolution waitable timer is in use.</summary>
    public bool IsHighResolution => _timer != null;

    /// <summary>
    /// Waits until one of <paramref name="handles"/> is signalled or the clock reaches <paramref name="deadlineUs"/>.
    /// Returns the index of the signalled handle, or -1 on the deadline.
    /// </summary>
    public int WaitUntil(ReadOnlySpan<WaitHandle> handles, ulong deadlineUs, Func<ulong> now)
    {
        ulong t = now();
        if (t >= deadlineUs) return Poll(handles);
        ulong remaining = deadlineUs - t;
        if (SpinUs > 0 && remaining <= SpinUs) return Spin(handles, deadlineUs, now);
        ulong sleepUs = SpinUs > 0 ? remaining - SpinUs : remaining;
        int idx;
        if (_timer != null && OperatingSystem.IsWindows())
        {
            long due = -(long)(sleepUs * 10); // relative, 100 ns
            TimingNative.SetWaitableTimer(_timer.SafeWaitHandle, in due, 0, 0, 0, false);
            var all = new WaitHandle[handles.Length + 1];
            handles.CopyTo(all);
            all[^1] = _timer;
            idx = WaitHandle.WaitAny(all);
            if (idx == handles.Length) idx = -1;
        }
        else
        {
            int ms = (int)Math.Min(int.MaxValue, (sleepUs + 999) / 1000);
            if (handles.Length == 0)
            {
                Thread.Sleep(ms);
                idx = -1;
            }
            else
            {
                idx = WaitHandle.WaitAny(handles.ToArray(), ms);
                if (idx == WaitHandle.WaitTimeout) idx = -1;
            }
        }
        if (idx == -1 && SpinUs > 0) return Spin(handles, deadlineUs, now);
        return idx;
    }

    private static int Poll(ReadOnlySpan<WaitHandle> handles)
    {
        if (handles.Length == 0) return -1;
        int i = WaitHandle.WaitAny(handles.ToArray(), 0);
        return i == WaitHandle.WaitTimeout ? -1 : i;
    }

    private static int Spin(ReadOnlySpan<WaitHandle> handles, ulong deadlineUs, Func<ulong> now)
    {
        var arr = handles.ToArray();
        while (now() < deadlineUs)
        {
            if (arr.Length > 0)
            {
                int i = WaitHandle.WaitAny(arr, 0);
                if (i != WaitHandle.WaitTimeout) return i;
            }
            Thread.SpinWait(20);
        }
        return -1;
    }

    public void Dispose() => _timer?.Dispose();
}
