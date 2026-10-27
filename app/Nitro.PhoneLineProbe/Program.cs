using System.Runtime.InteropServices;
using System.Text;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Calls;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Security.Authorization.AppCapabilityAccess;

// nitro-phoneline-probe — READ-ONLY probe of Windows.ApplicationModel.Calls (the APIs Phone Link uses for calls).
// Places no call, answers nothing, never pairs. By default it does NOT call RegisterApp/ConnectAsync either
// (RegisterApp can take the HFP registration away from Phone Link); those need an explicit flag.
//
//   nitro-phoneline-probe                 identity, capability status, paired BT, PhoneLineTransportDevices,
//                                         RequestAccessAsync, IsRegistered, PhoneCallStore + PhoneLineWatcher
//   --no-request-access                   skip PhoneLineTransportDevice.RequestAccessAsync()
//   --register                            ALSO call RegisterApp() + ConnectAsync() on each transport (state change!)
//   --log <path>                          log file (default: phoneline-probe.log next to the exe)
//
// Every line goes to the console and to the log file (the exe may be launched via its AUMID with no console).

Console.OutputEncoding = Encoding.UTF8;
var log = new Log(ArgValue(args, "--log") ?? Path.Combine(AppContext.BaseDirectory, "phoneline-probe.log"));
bool requestAccess = !args.Contains("--no-request-access");
bool register = args.Contains("--register");

log.Line($"=== nitro-phoneline-probe {DateTime.Now:yyyy-MM-dd HH:mm:ss}  OS {Environment.OSVersion.Version}  " +
         $"args=[{string.Join(' ', args)}]");

await Step("Package identity", () =>
{
    var len = 0u;
    var rc = GetCurrentPackageFullName(ref len, null);
    if (rc == 15700) { log.Line("  NO package identity (APPMODEL_ERROR_NO_PACKAGE) — running unpackaged"); return Task.CompletedTask; }
    var sb = new StringBuilder((int)len);
    GetCurrentPackageFullName(ref len, sb);
    log.Line($"  PackageFullName = {sb}");
    log.Line($"  FamilyName      = {Package.Current.Id.FamilyName}");
    log.Line($"  SignatureKind   = {Package.Current.SignatureKind}  IsDevelopmentMode={Package.Current.IsDevelopmentMode}");
    return Task.CompletedTask;
});

await Step("Capability access (AppCapability.CheckAccess)", () =>
{
    foreach (var cap in new[] { "phoneLineTransportManagement", "phoneCall", "phoneCallHistory", "phoneCallHistorySystem",
                                "voipCall", "bluetooth" })
    {
        try { log.Line($"  {cap,-30} {AppCapability.Create(cap).CheckAccess()}"); }
        catch (Exception e) { log.Line($"  {cap,-30} ! {Err(e)}"); }
    }
    return Task.CompletedTask;
});

await Step("Paired classic Bluetooth devices", async () =>
{
    string[] props = ["System.Devices.Aep.DeviceAddress", "System.Devices.Aep.IsConnected"];
    var classic = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true), props);
    log.Line($"  {classic.Count} paired classic device(s)");
    foreach (var d in classic)
    {
        d.Properties.TryGetValue("System.Devices.Aep.DeviceAddress", out var addr);
        d.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var conn);
        string hf = "?";
        try
        {
            using var bt = await BluetoothDevice.FromIdAsync(d.Id);
            var svc = bt is null ? null : await bt.GetRfcommServicesAsync(BluetoothCacheMode.Cached);
            if (svc is not null)
                hf = string.Join(",", svc.Services.Select(s => s.ServiceId.Uuid.ToString()[4..8]).Distinct());
        }
        catch (Exception e) { hf = "! " + Err(e); }
        log.Line($"  - {d.Name,-30} {addr,-17} connected={conn}  rfcomm(cached)=[{hf}]  (111f = HFP AG = a phone)");
    }
});

var transports = new List<PhoneLineTransportDevice>();
await Step("PhoneLineTransportDevice enumeration", async () =>
{
    var selector = PhoneLineTransportDevice.GetDeviceSelector();
    log.Line($"  selector: {selector}");
    string[] props = ["System.DeviceInterface.Bluetooth.DeviceAddress", "System.Devices.PhoneLineTransportDevice.Connected"];
    var infos = await DeviceInformation.FindAllAsync(selector, props);
    log.Line($"  {infos.Count} transport device(s)");
    foreach (var i in infos)
    {
        i.Properties.TryGetValue(props[0], out var addr);
        i.Properties.TryGetValue(props[1], out var connected);
        log.Line($"  - {i.Name}  addr={addr} connected={connected}  enabled={i.IsEnabled}");
        log.Line($"    id={i.Id}");
        try
        {
            var t = PhoneLineTransportDevice.FromId(i.Id);
            transports.Add(t);
            log.Line($"    FromId OK  Transport={t.Transport}");
            Try("    IsRegistered()", () => t.IsRegistered());
            Try("    AudioRoutingStatus", () => t.AudioRoutingStatus);
            Try("    InBandRingingEnabled", () => t.InBandRingingEnabled);
        }
        catch (Exception e) { log.Line($"    FromId ! {Err(e)}"); }
    }
});

if (requestAccess)
    foreach (var t in transports)
        await Step($"RequestAccessAsync {t.DeviceId[^20..]}", async () =>
            log.Line($"  DeviceAccessStatus = {await t.RequestAccessAsync()}"));

if (register)
    foreach (var t in transports)
        await Step($"RegisterApp + ConnectAsync {t.DeviceId[^20..]}  (state change, requested by --register)", async () =>
        {
            Try("  IsRegistered() before", () => t.IsRegistered());
            try { t.RegisterApp(); log.Line("  RegisterApp() returned"); }
            catch (Exception e) { log.Line($"  RegisterApp() ! {Err(e)}"); }
            Try("  IsRegistered() after", () => t.IsRegistered());
            try { log.Line($"  ConnectAsync() = {await t.ConnectAsync()}"); }
            catch (Exception e) { log.Line($"  ConnectAsync() ! {Err(e)}"); }
        });

await Step("PhoneCallManager / PhoneCallStore / PhoneLineWatcher", async () =>
{
    Try("  PhoneCallManager.IsCallActive", () => PhoneCallManager.IsCallActive);
    Try("  PhoneCallManager.IsCallIncoming", () => PhoneCallManager.IsCallIncoming);
    var store = await PhoneCallManager.RequestStoreAsync();
    log.Line($"  RequestStoreAsync OK: {store is not null}");
    if (store is null) return;
    try { log.Line($"  GetDefaultLineAsync = {await store.GetDefaultLineAsync()}"); }
    catch (Exception e) { log.Line($"  GetDefaultLineAsync ! {Err(e)}"); }

    var watcher = store.RequestLineWatcher();
    var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var lineIds = new List<Guid>();
    watcher.LineAdded += (_, a) => { lock (lineIds) lineIds.Add(a.LineId); };
    watcher.EnumerationCompleted += (_, _) => done.TrySetResult("EnumerationCompleted");
    watcher.Stopped += (_, _) => done.TrySetResult("Stopped");
    watcher.Start();
    var how = await Task.WhenAny(done.Task, Task.Delay(8000)) == done.Task ? done.Task.Result : "timeout (8 s)";
    log.Line($"  watcher: {how}, status={watcher.Status}, {lineIds.Count} line(s)");
    foreach (var id in lineIds)
    {
        try
        {
            var l = await PhoneLine.FromIdAsync(id);
            log.Line($"  - line {id}: '{l.DisplayName}' transport={l.Transport} canDial={l.CanDial} " +
                     $"network='{l.NetworkName}' state={l.NetworkState} transportDevice={l.TransportDeviceId}");
        }
        catch (Exception e) { log.Line($"  - line {id}: ! {Err(e)}"); }
    }
    try { watcher.Stop(); } catch { }
});

log.Line("=== done");
return 0;

async Task Step(string title, Func<Task> body)
{
    log.Line("");
    log.Line($"[{title}]");
    try { await body(); }
    catch (Exception e) { log.Line($"  ! {Err(e)}"); }
}

void Try(string label, Func<object?> f)
{
    try { log.Line($"{label} = {f()}"); }
    catch (Exception e) { log.Line($"{label} ! {Err(e)}"); }
}

static string Err(Exception e) =>
    $"{e.GetType().Name} 0x{e.HResult:X8}{(e.HResult == unchecked((int)0x80070005) ? " (E_ACCESSDENIED)" : "")}: " +
    e.Message.Replace("\r", " ").Replace("\n", " ").Trim();

static string? ArgValue(string[] a, string name)
{
    var i = Array.IndexOf(a, name);
    return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
}

[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, StringBuilder? packageFullName);

sealed class Log(string path)
{
    public void Line(string s)
    {
        Console.WriteLine(s);
        try { File.AppendAllText(path, s + Environment.NewLine); } catch { /* console still has it */ }
    }
}
