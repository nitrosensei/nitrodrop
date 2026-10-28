using System.Collections.Concurrent;
using AirDrop.App.Services;
using AirDrop.Core.Net;
using AirDrop.Core.Security;
using Nitro.Awdl.Ble;
using Nitro.Awdl.Ipc;

namespace AirDrop.Tests;

/// <summary>
/// CoreAirDropService's iPhone-link decisions with a fake radio host: driver absent → LAN, UAC declined → LAN + status,
/// radio failure → status, ready → a second node bound to the Wintun interface name, BLE advert policy.
/// </summary>
public class RadioServiceTests
{
    private static readonly System.Security.Cryptography.X509Certificates.X509Certificate2 Cert = CertificateStore.CreateEphemeral();
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private sealed class FakeConnection : IRadioHostConnection
    {
        public readonly ConcurrentQueue<RadioMessage> Sent = new();
        public RadioStatus Status { get; private set; } = RadioStatus.Stopped();
        public bool IsConnected { get; private set; } = true;
        public bool Disposed;
        public event Action<RadioStatus>? StatusChanged;
        public event Action<string>? Log;
        public event Action? Disconnected;

        /// <summary>What the host does when it gets "start" (null = nothing yet).</summary>
        public Func<RadioStatus>? OnStart;

        public Task SendAsync(RadioMessage command, CancellationToken ct = default)
        {
            Sent.Enqueue(command);
            if (command.Type == RadioPipe.StartCmd && OnStart is { } f) _ = Task.Run(() => Push(f()));
            return Task.CompletedTask;
        }

        public void Push(RadioStatus s)
        {
            Status = s;
            Log?.Invoke($"status {s.State}");
            StatusChanged?.Invoke(s);
        }

        public void Drop()
        {
            IsConnected = false;
            Disconnected?.Invoke();
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeLauncher(Func<IRadioHostConnection> launch) : IRadioHostLauncher
    {
        public int Launches;
        public Task<IRadioHostConnection> LaunchAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref Launches);
            return Task.Run(launch, ct);
        }
    }

    private sealed class FakeBle : IAirDropBleAdvertiser
    {
        public bool On;
        public int Starts;
        public void Start(AirDropBleAdvert? advert = null) { On = true; Starts++; }
        public void Stop() => On = false;
        public void Dispose() => On = false;
    }

    private sealed class Rig : IAsyncDisposable
    {
        public required CoreAirDropService Svc;
        public required FakeLauncher Launcher;
        public readonly FakeBle Ble = new();
        public readonly ConcurrentQueue<string> Resolved = new();
        public readonly TempDir Dir = new();
        public int StatusEvents;

        public async ValueTask DisposeAsync()
        {
            await Svc.DisposeAsync();
            Dir.Dispose();
        }
    }

    private static Rig NewRig(int mdnsPort, Func<IRadioHostConnection> launch, bool driver = true, bool enabled = true,
        WintunProvisioning? wintun = null)
    {
        var launcher = new FakeLauncher(launch);
        Rig? rig = null;
        var svc = new CoreAirDropService(new CoreAirDropServiceSettings
        {
            Interface = "loopback",
            Port = 0,
            MdnsPort = mdnsPort,
            Certificate = Cert,
            Radio = new RadioIntegration
            {
                Launcher = launcher,
                DriverInstalled = () => driver,
                Enabled = enabled,
                CreateBle = () => rig!.Ble,
                // No Wintun adapter in tests: record the name the service asks for and bind loopback instead.
                ResolveBinding = name => { rig!.Resolved.Enqueue(name); return NetworkBinding.Loopback; },
                ReadyTimeout = TimeSpan.FromSeconds(5),
                Wintun = wintun,
            },
        });
        rig = new Rig { Svc = svc, Launcher = launcher };
        svc.HardwareStatusChanged += (_, _) => Interlocked.Increment(ref rig.StatusEvents);
        return rig;
    }

    private static Task Start(Rig r, ReceiveMode mode = ReceiveMode.Everyone) =>
        r.Svc.StartAsync(new ServiceOptions { DeviceName = "Radyo PC", ReceiveFolder = r.Dir.Path }, mode);

    private static RadioStatus Ready(int peers = 1) => new()
    {
        State = RadioState.Ready, Channel = 6, Interface = RadioPipe.InterfaceName, InterfaceIndex = 42,
        Peers = Enumerable.Range(0, peers).Select(i => new RadioPeer($"86:08:04:8a:2b:f{i}", $"iPhone {i}")).ToList(),
    };

    private static async Task WaitUntil(Func<bool> cond)
    {
        var until = DateTime.UtcNow + Timeout;
        while (!cond())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("condition not met");
            await Task.Delay(25);
        }
    }

    [Fact]
    public async Task DriverAbsent_StaysLan_NoUacPrompt()
    {
        await using var r = NewRig(53711, () => throw new InvalidOperationException("must not launch"), driver: false);
        await Start(r);
        await r.Svc.RadioStartup.WaitAsync(Timeout);
        Assert.Equal(RadioPhase.NoDriver, r.Svc.Phase);
        Assert.Equal(0, r.Launcher.Launches);
        Assert.False(r.Svc.IsHardwareReady);
        Assert.Equal(CoreAirDropService.LanOnlyText, r.Svc.HardwareStatus);
        Assert.True(r.Svc.Port > 0);                       // LAN node is up regardless
        Assert.Null(r.Svc.AwdlBinding);
    }

    [Fact]
    public async Task UacCancelled_StaysLan_WithClearStatus_RetryOnlyOnToggle()
    {
        await using var r = NewRig(53712, () => throw new RadioLaunchCancelledException());
        await Start(r);
        await r.Svc.RadioStartup.WaitAsync(Timeout);
        Assert.Equal(RadioPhase.Cancelled, r.Svc.Phase);
        Assert.Contains("Yönetici izni verilmedi", r.Svc.HardwareStatus);
        Assert.Contains("yerel ağ", r.Svc.HardwareStatus);
        Assert.False(r.Svc.IsHardwareReady);
        Assert.True(r.StatusEvents > 0);

        await r.Svc.SetRadioEnabledAsync(true);           // other settings saved: no new prompt
        Assert.Equal(1, r.Launcher.Launches);
        await r.Svc.SetRadioEnabledAsync(false);
        Assert.Equal(RadioPhase.Disabled, r.Svc.Phase);
        await r.Svc.SetRadioEnabledAsync(true);           // switched back on: asks again
        await r.Svc.RadioStartup.WaitAsync(Timeout);
        Assert.Equal(2, r.Launcher.Launches);
    }

    [Fact]
    public async Task LauncherError_ShowsHelperFailure()
    {
        await using var r = NewRig(53713, () => throw new FileNotFoundException("NitroDrop.Radio.exe bulunamadı"));
        await Start(r);
        await r.Svc.RadioStartup.WaitAsync(Timeout);
        Assert.Equal(RadioPhase.LaunchFailed, r.Svc.Phase);
        Assert.Contains("NitroDrop.Radio.exe bulunamadı", r.Svc.HardwareStatus);
    }

    [Fact]
    public async Task RadioFailure_ShowsTurkishReason_NoAwdlNode()
    {
        var conn = new FakeConnection { OnStart = () => RadioStatus.Failed(RadioFailureKind.RfKill, "Wi-Fi hardware radio switch is off (RF kill)") };
        await using var r = NewRig(53714, () => conn);
        await Start(r);
        await r.Svc.RadioStartup.WaitAsync(Timeout);
        Assert.Equal(RadioPhase.Failed, r.Svc.Phase);
        Assert.Equal("iPhone bağlantısı kurulamadı: Wi-Fi kapalı (uçak modu ya da donanım anahtarı) · yerel ağ modu", r.Svc.HardwareStatus);
        Assert.False(r.Svc.IsHardwareReady);
        Assert.Empty(r.Resolved);
        Assert.False(r.Ble.On);
        Assert.Contains(conn.Sent, m => m.Type == RadioPipe.StartCmd && m.Name == "Radyo PC");

        foreach (var kind in Enum.GetValues<RadioFailureKind>())
            Assert.False(string.IsNullOrWhiteSpace(CoreAirDropService.DescribeFailure(kind)));
    }

    [Fact]
    public async Task NoAnswer_TimesOutToFailure()
    {
        var conn = new FakeConnection();                   // never reports Ready/Failed
        await using var r = NewRig(53715, () => conn);
        await Start(r);
        await r.Svc.RadioStartup.WaitAsync(Timeout);
        Assert.Equal(RadioPhase.Failed, r.Svc.Phase);
        Assert.Contains("beklenmeyen hata", r.Svc.HardwareStatus);
    }

    [Fact]
    public async Task Ready_BindsWintunInterface_UpdatesPeers_BlePolicy_AndQuitsHostOnDispose()
    {
        var conn = new FakeConnection { OnStart = () => Ready() };
        var r = NewRig(53716, () => conn);
        try
        {
            await Start(r);
            await r.Svc.RadioStartup.WaitAsync(Timeout);
            Assert.Equal(RadioPhase.Ready, r.Svc.Phase);
            Assert.True(r.Svc.IsHardwareReady);
            Assert.Equal(RadioPipe.InterfaceName, Assert.Single(r.Resolved));   // = WintunAdapter.DefaultName
            Assert.Equal("NitroDrop AWDL", RadioPipe.InterfaceName);
            Assert.NotNull(r.Svc.AwdlBinding);
            Assert.Equal("iPhone bağlantısı hazır · kanal 6 · 1 cihaz", r.Svc.HardwareStatus);

            // Everyone mode → BLE wake-up advert on; Off → off; a send turns it on for its duration.
            await WaitUntil(() => r.Ble.On);
            await r.Svc.SetModeAsync(ReceiveMode.Off);
            Assert.False(r.Ble.On);
            Assert.False(r.Svc.BleAdvertising);

            conn.Push(Ready(peers: 3));
            await WaitUntil(() => r.Svc.HardwareStatus.EndsWith("3 cihaz"));

            var sendStarts = r.Ble.Starts;
            var outcome = await r.Svc.SendLinkAsync("no-such-peer", new Uri("https://nitrobyte.com.tr"));
            Assert.Equal(SendOutcome.Failed, outcome);     // unknown peer, but the advert ran meanwhile
            Assert.True(r.Ble.Starts > sendStarts);
            Assert.False(r.Ble.On);
        }
        finally
        {
            await r.DisposeAsync();
        }
        Assert.Contains(conn.Sent, m => m.Type == RadioPipe.QuitCmd);
        Assert.True(conn.Disposed);
        Assert.False(r.Ble.On);
    }

    [Fact]
    public async Task HostExitAfterReady_FallsBackToLan()
    {
        var conn = new FakeConnection { OnStart = () => Ready() };
        await using var r = NewRig(53717, () => conn);
        await Start(r);
        await r.Svc.RadioStartup.WaitAsync(Timeout);
        Assert.True(r.Svc.IsHardwareReady);

        conn.Drop();
        await WaitUntil(() => r.Svc.Phase == RadioPhase.HostExited);
        Assert.False(r.Svc.IsHardwareReady);
        Assert.Null(r.Svc.AwdlBinding);
        Assert.Equal("Wi-Fi yardımcısı kapandı · yerel ağ modu", r.Svc.HardwareStatus);
        await WaitUntil(() => !r.Ble.On);
        Assert.True(r.Svc.Port > 0);
    }

    [Fact]
    public async Task DisabledInSettings_NoLaunch_UntilSwitchedOn()
    {
        var conn = new FakeConnection { OnStart = () => Ready(0) };
        await using var r = NewRig(53718, () => conn, enabled: false);
        await Start(r);
        await r.Svc.RadioStartup.WaitAsync(Timeout);
        Assert.Equal(RadioPhase.Disabled, r.Svc.Phase);
        Assert.Equal(0, r.Launcher.Launches);
        Assert.Contains("kapalı", r.Svc.HardwareStatus);

        await r.Svc.SetRadioEnabledAsync(true);
        await r.Svc.RadioStartup.WaitAsync(Timeout);
        Assert.Equal(RadioPhase.Ready, r.Svc.Phase);
        Assert.Equal("iPhone bağlantısı hazır · kanal 6 · 0 cihaz", r.Svc.HardwareStatus);

        await r.Svc.SetRadioEnabledAsync(false);
        Assert.Equal(RadioPhase.Disabled, r.Svc.Phase);
        Assert.Contains(conn.Sent, m => m.Type == RadioPipe.QuitCmd);
        Assert.Null(r.Svc.AwdlBinding);
    }

    [Fact]
    public void LanBinding_AnyExcept_LeavesAwdlAdapterOut()
    {
        var b = NetworkBinding.AnyExcept(RadioPipe.InterfaceName);
        Assert.True(b.IsAny);
        Assert.DoesNotContain(b.MulticastInterfaces(), n => n.Name == RadioPipe.InterfaceName);
        Assert.Contains("NitroDrop AWDL", b.DisplayName);
        var lo = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up && n.SupportsMulticast);
        if (lo != null) Assert.DoesNotContain(NetworkBinding.AnyExcept(lo.Name).MulticastInterfaces(), n => n.Name == lo.Name);
    }

    // ---------------- on-demand Wintun (not shipped: non-OSI licence) ----------------

    private sealed class FakeWintun
    {
        public bool Installed, Agree, Fail;
        public int Prompts, Installs;
        public WintunProvisioning Provisioning => new()
        {
            IsInstalled = () => Installed,
            Confirm = _ => { Prompts++; return Task.FromResult(Agree); },
            Install = _ =>
            {
                Installs++;
                if (Fail) throw new InvalidDataException("wintun zip SHA-256 mismatch");
                Installed = true;
                return Task.CompletedTask;
            },
        };
    }

    [Fact]
    public async Task WintunMissing_Declined_StaysLan_NoDownload_NoUac()
    {
        var w = new FakeWintun { Agree = false };
        await using var r = NewRig(53721, () => throw new InvalidOperationException("must not launch"), wintun: w.Provisioning);
        await Start(r);
        await r.Svc.RadioStartup.WaitAsync(Timeout);
        Assert.Equal(RadioPhase.WintunDeclined, r.Svc.Phase);
        Assert.Equal(1, w.Prompts);
        Assert.Equal(0, w.Installs);
        Assert.Equal(0, r.Launcher.Launches);
        Assert.Contains("Wi-Fi bileşeni indirilmedi", r.Svc.HardwareStatus);
    }

    [Fact]
    public async Task WintunMissing_Agreed_DownloadsThenLaunches()
    {
        var w = new FakeWintun { Agree = true };
        await using var r = NewRig(53722, () => throw new RadioLaunchCancelledException(), wintun: w.Provisioning);
        await Start(r);
        await r.Svc.RadioStartup.WaitAsync(Timeout);
        Assert.Equal(1, w.Installs);
        Assert.Equal(1, r.Launcher.Launches);
        Assert.Equal(RadioPhase.Cancelled, r.Svc.Phase);
    }

    [Fact]
    public async Task WintunInstalled_NoPrompt()
    {
        var w = new FakeWintun { Installed = true };
        await using var r = NewRig(53723, () => throw new RadioLaunchCancelledException(), wintun: w.Provisioning);
        await Start(r);
        await r.Svc.RadioStartup.WaitAsync(Timeout);
        Assert.Equal(0, w.Prompts);
        Assert.Equal(1, r.Launcher.Launches);
    }

    [Fact]
    public async Task WintunDownloadFails_ShowsReason_NoUac()
    {
        var w = new FakeWintun { Agree = true, Fail = true };
        await using var r = NewRig(53724, () => throw new InvalidOperationException("must not launch"), wintun: w.Provisioning);
        await Start(r);
        await r.Svc.RadioStartup.WaitAsync(Timeout);
        Assert.Equal(RadioPhase.WintunFailed, r.Svc.Phase);
        Assert.Equal(0, r.Launcher.Launches);
        Assert.Contains("Wi-Fi bileşeni indirilemedi", r.Svc.HardwareStatus);
        Assert.Contains("SHA-256", r.Svc.HardwareStatus);
    }
}
