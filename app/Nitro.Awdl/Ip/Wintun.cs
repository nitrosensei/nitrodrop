// Nitro.Awdl: awdl0-like interface on Windows with Wintun (https://www.wintun.net, WireGuard LLC).
// Copyright (c) 2026 Nitrobyte. Uses wintun.dll only through the API in wintun.h (the "Permitted API" of its
// Prebuilt Binaries License); the dll is fetched by tools/fetch-wintun.ps1 (version + SHA-256 pinned), not committed.

using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Nitro.Awdl.Ip;

/// <summary>P/Invoke surface of wintun.h (0.14.1).</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class WintunNative
{
    public const string Dll = "wintun.dll";
    public const int ERROR_NO_MORE_ITEMS = 259, ERROR_BUFFER_OVERFLOW = 111, ERROR_HANDLE_EOF = 38;
    public const uint MinRingCapacity = 0x20000, MaxRingCapacity = 0x4000000;

    static WintunNative()
    {
        try { NativeLibrary.SetDllImportResolver(typeof(WintunNative).Assembly, Resolve); }
        catch (InvalidOperationException) { /* a resolver is already set for this assembly */ }
    }

    /// <summary>wintun\{amd64|arm64}\wintun.dll next to the assembly (as the csproj copies it), else the default search.</summary>
    private static nint Resolve(string name, Assembly asm, DllImportSearchPath? path)
    {
        if (name != Dll) return 0;
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "amd64",
        };
        foreach (var candidate in new[] { Path.Combine(AppContext.BaseDirectory, "wintun", arch, Dll), Path.Combine(AppContext.BaseDirectory, Dll) })
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var h)) return h;
        return 0;
    }

    [LibraryImport(Dll, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint WintunCreateAdapter(string name, string tunnelType, in Guid requestedGuid);

    [LibraryImport(Dll, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint WintunOpenAdapter(string name);

    [LibraryImport(Dll)]
    public static partial void WintunCloseAdapter(nint adapter);

    [LibraryImport(Dll)]
    public static partial void WintunGetAdapterLUID(nint adapter, out ulong luid);

    [LibraryImport(Dll, SetLastError = true)]
    public static partial uint WintunGetRunningDriverVersion();

    [LibraryImport(Dll, SetLastError = true)]
    public static partial nint WintunStartSession(nint adapter, uint capacity);

    [LibraryImport(Dll)]
    public static partial void WintunEndSession(nint session);

    [LibraryImport(Dll)]
    public static partial nint WintunGetReadWaitEvent(nint session);

    [LibraryImport(Dll, SetLastError = true)]
    public static partial byte* WintunReceivePacket(nint session, out uint size);

    [LibraryImport(Dll)]
    public static partial void WintunReleaseReceivePacket(nint session, byte* packet);

    [LibraryImport(Dll, SetLastError = true)]
    public static partial byte* WintunAllocateSendPacket(nint session, uint size);

    [LibraryImport(Dll)]
    public static partial void WintunSendPacket(nint session, byte* packet);

    [LibraryImport("iphlpapi.dll")]
    public static partial uint ConvertInterfaceLuidToIndex(in ulong luid, out uint index);
}

/// <summary>
/// A Wintun adapter used as the AWDL interface. Creating one installs a network adapter (admin, a system change);
/// <see cref="Open"/> attaches to one made earlier (e.g. by the setup helper). Nothing here runs implicitly.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WintunAdapter : IDisposable
{
    /// <summary>Pinned build (tools/fetch-wintun.ps1).</summary>
    public const string PinnedVersion = "0.14.1";
    public const string PinnedZipSha256 = "07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51";
    /// <summary>Default adapter name: shows up as the interface name AirDrop.Core binds to.</summary>
    public const string DefaultName = Ipc.RadioPipe.InterfaceName;
    public const string TunnelType = "NitroDrop";
    /// <summary>Stable adapter GUID so Windows keeps the same interface (and its settings) across sessions.</summary>
    public static readonly Guid DefaultGuid = new("5f0c3a1e-6b7d-4e2a-9c41-0a3d2b1c4e7f");
    /// <summary>awdl0's MTU in Owly (native.rs TAP_MTU).</summary>
    public const int Mtu = 1450;

    private nint _handle;

    private WintunAdapter(nint handle, string name)
    {
        _handle = handle;
        Name = name;
        WintunNative.WintunGetAdapterLUID(handle, out var luid);
        Luid = luid;
        WintunNative.ConvertInterfaceLuidToIndex(in luid, out var index);
        InterfaceIndex = (int)index;
    }

    public string Name { get; }
    public ulong Luid { get; }
    /// <summary>The IPv6 interface index: the scope id of fe80:: addresses on this adapter.</summary>
    public int InterfaceIndex { get; }

    /// <summary>Creates (or re-creates) the adapter. Requires administrator rights; installs the Wintun driver if needed.</summary>
    public static WintunAdapter Create(string name = DefaultName, Guid? guid = null)
    {
        var h = WintunNative.WintunCreateAdapter(name, TunnelType, guid ?? DefaultGuid);
        if (h == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), $"WintunCreateAdapter({name}) failed (administrator rights needed)");
        return new WintunAdapter(h, name);
    }

    /// <summary>Opens an existing adapter by name.</summary>
    public static WintunAdapter Open(string name = DefaultName)
    {
        var h = WintunNative.WintunOpenAdapter(name);
        if (h == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), $"WintunOpenAdapter({name}) failed: adapter not created yet?");
        return new WintunAdapter(h, name);
    }

    /// <summary>The loaded Wintun driver's version, or null when not loaded / wintun.dll missing.</summary>
    public static Version? RunningDriverVersion()
    {
        try
        {
            uint v = WintunNative.WintunGetRunningDriverVersion();
            return v == 0 ? null : new Version((int)(v >> 16), (int)(v & 0xffff));
        }
        catch (DllNotFoundException) { return null; }
    }

    /// <summary>
    /// The commands that turn the adapter into awdl0 (NET-1, NET-2, Owly native.rs): our EUI-64 link-local without DAD,
    /// MTU 1450, no router discovery / advertising, and the OS's own auto link-local kept from being picked as source.
    /// All need administrator rights. <paramref name="otherLinkLocals"/> are the adapter's current fe80 addresses besides ours.
    /// </summary>
    public static IReadOnlyList<string> ConfigurationCommands(int interfaceIndex, MacAddr awdlAddr, IEnumerable<IPAddress>? otherLinkLocals = null)
    {
        var ll = awdlAddr.LinkLocal();
        var cmds = new List<string>
        {
            $"netsh interface ipv6 set interface interface={interfaceIndex} mtu={Mtu} dadtransmissions=0 routerdiscovery=disabled advertise=disabled managedaddress=disabled otherstateful=disabled store=active",
            $"netsh interface ipv6 add address interface={interfaceIndex} address={ll}/64 type=unicast store=active",
        };
        foreach (var other in otherLinkLocals ?? [])
            if (!other.Equals(ll))
                cmds.Add($"netsh interface ipv6 set address interface={interfaceIndex} address={new IPAddress(other.GetAddressBytes())} skipassource=true store=active");
        return cmds;
    }

    /// <summary>Runs <see cref="ConfigurationCommands"/> (administrator rights; changes this adapter's IP settings only).</summary>
    public void ApplyConfiguration(MacAddr awdlAddr, Action<string>? log = null)
    {
        var nic = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => n.GetIPProperties().GetIPv6Properties()?.Index == InterfaceIndex);
        var others = nic?.GetIPProperties().UnicastAddresses.Select(u => u.Address).Where(a => a.IsIPv6LinkLocal) ?? [];
        foreach (var cmd in ConfigurationCommands(InterfaceIndex, awdlAddr, others))
        {
            log?.Invoke(cmd);
            var parts = cmd.Split(' ', 2);
            using var p = Process.Start(new ProcessStartInfo(parts[0], parts[1]) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException($"'{cmd}' failed ({p.ExitCode}): {p.StandardOutput.ReadToEnd()}{p.StandardError.ReadToEnd()}");
        }
    }

    internal nint Handle => _handle != 0 ? _handle : throw new ObjectDisposedException(nameof(WintunAdapter));

    /// <summary>Closes the handle. The adapter stays (Wintun removes adapters it created when the last handle closes).</summary>
    public void Dispose()
    {
        if (_handle != 0) WintunNative.WintunCloseAdapter(_handle);
        _handle = 0;
    }
}

/// <summary>
/// <see cref="IPacketSink"/> over a Wintun session: IPv6 packets the OS routes to the adapter (fe80::…%awdl) go to
/// AWDL; packets from peers are written into the adapter. Wintun is L3 (no link-layer header, no neighbour cache), so
/// peer up/down needs no neighbour entries: link-layer addresses are derived from the IPv6 ones (NET-2).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WintunPacketSink : IPacketSink
{
    private sealed class BorrowedEvent : WaitHandle
    {
        public BorrowedEvent(nint h) => SafeWaitHandle = new SafeWaitHandle(h, ownsHandle: false);
    }

    private readonly WintunAdapter _adapter;
    private nint _session;
    private readonly BorrowedEvent _readEvent;
    private long _deliverDropped;

    public WintunPacketSink(WintunAdapter adapter, uint ringCapacity = 0x400000)
    {
        _adapter = adapter;
        ringCapacity = Math.Clamp(ringCapacity, WintunNative.MinRingCapacity, WintunNative.MaxRingCapacity);
        _session = WintunNative.WintunStartSession(adapter.Handle, ringCapacity);
        if (_session == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "WintunStartSession failed");
        _readEvent = new BorrowedEvent(WintunNative.WintunGetReadWaitEvent(_session));
    }

    public WaitHandle ReadWaitHandle => _readEvent;

    /// <summary>Packets from peers the adapter had no room for.</summary>
    public long DeliverDropped => Interlocked.Read(ref _deliverDropped);

    public bool TryRead(out byte[] packet)
    {
        packet = [];
        if (_session == 0) return false;
        byte* p = WintunNative.WintunReceivePacket(_session, out uint size);
        if (p == null)
        {
            int err = Marshal.GetLastPInvokeError();
            if (err is WintunNative.ERROR_NO_MORE_ITEMS) return false;
            throw new Win32Exception(err, "WintunReceivePacket failed");
        }
        try
        {
            packet = new ReadOnlySpan<byte>(p, (int)size).ToArray();
        }
        finally
        {
            WintunNative.WintunReleaseReceivePacket(_session, p);
        }
        return true;
    }

    public void Deliver(ReadOnlySpan<byte> packet)
    {
        if (_session == 0 || packet.IsEmpty) return;
        byte* p = WintunNative.WintunAllocateSendPacket(_session, (uint)packet.Length);
        if (p == null)
        {
            Interlocked.Increment(ref _deliverDropped);
            return;
        }
        packet.CopyTo(new Span<byte>(p, packet.Length));
        WintunNative.WintunSendPacket(_session, p);
    }

    public void PeerUp(PeerInfo peer) { }

    public void PeerDown(MacAddr mac) { }

    public void Dispose()
    {
        if (_session != 0) WintunNative.WintunEndSession(_session);
        _session = 0;
        _readEvent.Dispose();
    }
}
