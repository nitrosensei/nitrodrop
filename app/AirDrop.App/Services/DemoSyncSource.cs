using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nitro.Sync;
using Nitro.Sync.Model;
using Nitro.Sync.Security;

namespace AirDrop.App.Services;

/// <summary>Sample PCs, tabs, apps and an iPhone activity for <c>--demo</c> / <c>--snapshot</c>. Nothing touches the network.</summary>
public sealed class DemoSyncSource : ISyncSource
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, SyncPeerInfo> _peers = new();
    private readonly List<IosDevice> _ios = new() { new IosDevice { Id = "ios-demo", Name = "Emre'nin iPhone'u", TokenSha256 = "" } };
    private readonly object _gate = new();

    public bool IsDemo => true;
    public string? StartError => null;

    public event EventHandler<SyncPeerInfo>? PeerStateChanged;
    public event EventHandler<IncomingLink>? LinkReceived { add { } remove { } }   // demo never receives links
    public event EventHandler<IphoneActivity>? IphoneActivityChanged;

    public Func<PairingPrompt, CancellationToken, Task<bool>>? ConfirmPairing { get; set; }
    public Func<AppLaunchRequest, CancellationToken, Task<bool>>? ConfirmLaunch { get; set; }
    public Func<IReadOnlyList<LinkTarget>>? ExternalLinkTargets { get; set; }
    public Func<string, string, string?, CancellationToken, Task<bool>>? SendLinkToExternal { get; set; }

    public DeviceState? LocalState { get; } = new()
    {
        DeviceId = "demo-self",
        DeviceName = LocalPcName.Value,
        Tabs = new[] { new BrowserTab { Browser = BrowserKind.Chrome, Title = "NitroDrop", Url = "https://nitrobyte.com.tr/" } },
    };

    public IReadOnlyList<IosDevice> IosDevices { get { lock (_gate) return _ios.ToList(); } }

    public Task StartAsync(SyncConfig config)
    {
        _ = RunAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public Task ReconfigureAsync(SyncConfig config) => Task.CompletedTask;

    private static long Ago(TimeSpan t) => DateTimeOffset.UtcNow.Add(-t).ToUnixTimeMilliseconds();

    private static string? Icon(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is null) return null;
            using var bmp = icon.ToBitmap();
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return Convert.ToBase64String(ms.ToArray());
        }
        catch (Exception) { return null; }
    }

    private static DeviceState DesktopState()
    {
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return new DeviceState
        {
            DeviceId = "a1b2c3d4e5f6",
            DeviceName = "Emre-Masaüstü",
            UpdatedAt = Ago(TimeSpan.Zero),
            Hardware = new AirDrop.Core.Devices.HostDevice
            {
                Manufacturer = "MSI", Model = "MAG Infinite S3", Chassis = AirDrop.Core.Devices.HostChassis.Desktop, Platform = "windows",
            },
            Tabs = new[]
            {
                new BrowserTab { Browser = BrowserKind.Chrome, WindowId = 1, TabId = 11, Active = true, LastAccessed = Ago(TimeSpan.FromMinutes(1)),
                    Title = "z3x0fly/nitrodrop: NitroDrop", Url = "https://github.com/z3x0fly/nitrodrop" },
                new BrowserTab { Browser = BrowserKind.Chrome, WindowId = 1, TabId = 12, LastAccessed = Ago(TimeSpan.FromMinutes(4)),
                    Title = "lofi hip hop radio – beats to relax/study to", Url = "https://www.youtube.com/watch?v=jfKfPfyJRdk" },
                new BrowserTab { Browser = BrowserKind.Chrome, WindowId = 1, TabId = 13, LastAccessed = Ago(TimeSpan.FromMinutes(9)),
                    Title = "İstanbul - Vikipedi", Url = "https://tr.wikipedia.org/wiki/%C4%B0stanbul" },
                new BrowserTab { Browser = BrowserKind.Chrome, WindowId = 2, TabId = 21, LastAccessed = Ago(TimeSpan.FromMinutes(22)),
                    Title = "c# - RenderTargetBitmap of a VisualBrush is blank - Stack Overflow", Url = "https://stackoverflow.com/questions/2734810" },
                new BrowserTab { Browser = BrowserKind.Edge, WindowId = 3, TabId = 31, LastAccessed = Ago(TimeSpan.FromHours(1)),
                    Title = "Gelen Kutusu (3) - Gmail", Url = "https://mail.google.com/mail/u/0/#inbox" },
            },
            Apps = new[]
            {
                new AppInfo { AppId = "d1", Name = "Visual Studio Code", Exe = "Code.exe", WindowTitle = "MainWindow.xaml — airdrop-win", Foreground = true },
                new AppInfo { AppId = "d2", Name = "Spotify", Exe = "Spotify.exe", WindowTitle = "Duman — Bu Akşam" },
                new AppInfo { AppId = "d3", Name = "Discord", Exe = "Discord.exe" },
                new AppInfo { AppId = "d4", Name = "Dosya Gezgini", Exe = "explorer.exe", WindowTitle = "İndirilenler",
                    IconPng = Icon(Path.Combine(win, "explorer.exe")) },
                new AppInfo { AppId = "d5", Name = "Not Defteri", Exe = "notepad.exe", WindowTitle = "notlar.txt",
                    IconPng = Icon(Path.Combine(win, "System32", "notepad.exe")) },
            },
        };
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(500, ct);
            Put(new SyncPeerInfo("a1b2c3d4e5f6", "Emre-Masaüstü", true, true, DesktopState(), DateTimeOffset.Now, null));
            Put(new SyncPeerInfo("0f9e8d7c6b5a", "Ofis-Laptop", true, false,
                new DeviceState
                {
                    DeviceId = "0f9e8d7c6b5a", DeviceName = "Ofis-Laptop",
                    Hardware = new AirDrop.Core.Devices.HostDevice
                    {
                        Manufacturer = "Lenovo", Model = "ThinkPad X1 Carbon Gen 11", Chassis = AirDrop.Core.Devices.HostChassis.Laptop, Platform = "windows",
                    },
                },
                DateTimeOffset.Now.AddHours(-3), null));
            await Task.Delay(700, ct);
            Put(new SyncPeerInfo("77aa88bb99cc", "Ayşe-PC", false, true, null, null, null));
            await Task.Delay(1000, ct);
            IphoneActivityChanged?.Invoke(this, new IphoneActivity
            {
                DeviceId = "ios-demo", DeviceName = "Emre'nin iPhone'u", App = "Instagram",
                At = DateTimeOffset.UtcNow.AddSeconds(-20),
                Target = ContinuationMap.Resolve("Instagram", null),
            });
        }
        catch (OperationCanceledException) { }
    }

    private void Put(SyncPeerInfo p)
    {
        lock (_gate) _peers[p.DeviceId] = p;
        PeerStateChanged?.Invoke(this, p);
    }

    public IReadOnlyList<SyncPeerInfo> GetPeers() { lock (_gate) return _peers.Values.ToList(); }

    public async Task<PairResult> PairAsync(string deviceId)
    {
        SyncPeerInfo? p;
        lock (_gate) _peers.TryGetValue(deviceId, out p);
        if (p is null) return new PairResult(false, null, SyncErrors.NotFound);
        await Task.Delay(400);
        const string code = "482913";
        if (ConfirmPairing is not { } confirm ||
            !await confirm(new PairingPrompt(deviceId, p.Name, code, IsInitiator: true), CancellationToken.None))
            return new PairResult(false, code, SyncErrors.Declined);
        Put(p with { IsPaired = true, State = new DeviceState { DeviceId = deviceId, DeviceName = p.Name } });
        return new PairResult(true, code, null);
    }

    public Task UnpairAsync(string deviceId)
    {
        SyncPeerInfo? p;
        lock (_gate) _peers.TryGetValue(deviceId, out p);
        if (p is not null) Put(p with { IsPaired = false, State = null });
        return Task.CompletedTask;
    }

    public Task<bool> ContinueOnPcAsync(IphoneActivity activity) => Task.FromResult(activity.Target is not null);

    public IosSetup AddIosDevice(string name)
    {
        var dev = new IosDevice { Id = "ios-" + Guid.NewGuid().ToString("N")[..8], Name = name, TokenSha256 = "" };
        lock (_gate) _ios.Add(dev);
        const string token = "q3ZkR8vN1sXc0LbT7yWm4PfJ2hGd9EaUoKiQ6nVz5rY";
        return new IosSetup(dev.Id, name, token, $"http://192.168.1.20:8772/ios/v1/activity?t={token}", Array.Empty<string>());
    }

    public bool RemoveIosDevice(string id) { lock (_gate) return _ios.RemoveAll(d => d.Id == id) > 0; }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Debug snapshot: an incoming launch request to render the confirmation sheet.</summary>
    internal static AppLaunchRequest SampleLaunch => new("a1b2c3d4e5f6", "Emre-Masaüstü", "d2", "Spotify");
}
