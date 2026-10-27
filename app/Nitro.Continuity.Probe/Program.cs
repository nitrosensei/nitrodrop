using Nitro.Continuity.Ancs;
using Nitro.Continuity.Bluetooth;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

// nitro-continuity-probe — READ-ONLY. Lists paired Bluetooth devices and, for one (or all), the GATT services
// (looking for ANCS / AMS) and RFCOMM services (MAP / PBAP / HFP). Never initiates pairing, never writes,
// never subscribes. SDP/GATT discovery may briefly open a baseband/LE link to the device.
//
//   nitro-continuity-probe                 adapter info + paired device list
//   nitro-continuity-probe <n|name>        inspect device #n from the list, or the first whose name contains <name>
//   nitro-continuity-probe --all           inspect every paired device
//   --cached                               use Windows' cache only (no radio traffic)

Console.OutputEncoding = System.Text.Encoding.UTF8;
bool cached = args.Contains("--cached");
var mode = cached ? BluetoothCacheMode.Cached : BluetoothCacheMode.Uncached;
var target = args.FirstOrDefault(a => !a.StartsWith("--"));
bool all = args.Contains("--all");

await PrintAdapter();
var devices = await ListPaired();

if (target is null && !all)
{
    Console.WriteLine();
    Console.WriteLine("Inspect one: nitro-continuity-probe <n|name> [--cached]   (or --all)");
    return 0;
}

var chosen = all ? devices : devices.Where((d, i) =>
    (int.TryParse(target, out var n) && n == i) ||
    d.Info.Name.Contains(target!, StringComparison.OrdinalIgnoreCase)).Take(1).ToList();
if (chosen.Count == 0) { Console.WriteLine($"No paired device matches '{target}'."); return 1; }

foreach (var d in chosen)
{
    Console.WriteLine();
    Console.WriteLine(new string('=', 78));
    Console.WriteLine($"{d.Kind}  {d.Info.Name}");
    try
    {
        if (d.Kind == "Classic") await InspectClassic(d.Info, mode);
        else await InspectLe(d.Info.Id, mode);
    }
    catch (Exception e) { Console.WriteLine($"  ! {e.GetType().Name}: {e.Message}"); }
}
return 0;

static async Task PrintAdapter()
{
    var a = await BluetoothAdapter.GetDefaultAsync();
    if (a is null) { Console.WriteLine("No Bluetooth adapter."); return; }
    Console.WriteLine($"Adapter: {Addr(a.BluetoothAddress)}  Classic={a.IsClassicSupported} LE={a.IsLowEnergySupported} " +
                      $"Central={a.IsCentralRoleSupported} Peripheral={a.IsPeripheralRoleSupported}");
    Console.WriteLine($"         AdvOffload={a.IsAdvertisementOffloadSupported} ExtendedAdv={a.IsExtendedAdvertisingSupported} " +
                      $"MaxAdvData={a.MaxAdvertisementDataLength}");
}

static async Task<List<(string Kind, DeviceInformation Info)>> ListPaired()
{
    string[] props = ["System.Devices.Aep.DeviceAddress", "System.Devices.Aep.IsConnected"];
    var classic = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true), props);
    var le = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), props);
    var list = classic.Select(i => ("Classic", i)).Concat(le.Select(i => ("LE", i))).ToList();
    Console.WriteLine();
    Console.WriteLine($"Paired devices: {classic.Count} classic, {le.Count} LE");
    for (int i = 0; i < list.Count; i++)
    {
        var (kind, info) = list[i];
        info.Properties.TryGetValue("System.Devices.Aep.DeviceAddress", out var addr);
        info.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var conn);
        Console.WriteLine($"  [{i,2}] {kind,-7} {info.Name,-34} {addr,-17} connected={conn}");
    }
    return list;
}

static async Task InspectClassic(DeviceInformation info, BluetoothCacheMode mode)
{
    using var dev = await BluetoothDevice.FromIdAsync(info.Id);
    if (dev is null) { Console.WriteLine("  (could not open device)"); return; }
    var cod = dev.ClassOfDevice;
    Console.WriteLine($"  Address {Addr(dev.BluetoothAddress)}  Status={dev.ConnectionStatus}  CoD major={cod.MajorClass} minor={cod.MinorClass} raw=0x{cod.RawValue:X6}");
    Console.WriteLine($"  Pairing: IsPaired={dev.DeviceInformation.Pairing.IsPaired} ProtectionLevel={dev.DeviceInformation.Pairing.ProtectionLevel}");

    var rf = await dev.GetRfcommServicesAsync(mode);
    Console.WriteLine($"  RFCOMM services ({mode}): {rf.Error}, {rf.Services.Count} found");
    foreach (var s in rf.Services)
    {
        var u = s.ServiceId.Uuid;
        string tag = BluetoothUuids.ToShort(u) switch
        {
            BluetoothUuids.MapMas => "   <-- MAP (messages)",
            BluetoothUuids.PbapPse => "   <-- PBAP (contacts)",
            BluetoothUuids.HandsFreeAudioGateway => "   <-- HFP AG (calls)",
            _ => "",
        };
        Console.WriteLine($"    {BluetoothUuids.Describe(u),-32} {s.ConnectionServiceName}{tag}");
        s.Dispose();
    }

    // The LE side of a dual-mode bond (CTKD) often shares the public address; peek without pairing.
    using var le = await BluetoothLEDevice.FromBluetoothAddressAsync(dev.BluetoothAddress);
    if (le is null) Console.WriteLine("  LE: no LE device object for this address (iPhone LE normally uses a resolvable private address).");
    else { Console.WriteLine($"  LE companion: {le.Name} ({le.DeviceId})"); await InspectGatt(le, mode); }
}

static async Task InspectLe(string id, BluetoothCacheMode mode)
{
    using var le = await BluetoothLEDevice.FromIdAsync(id);
    if (le is null) { Console.WriteLine("  (could not open LE device)"); return; }
    Console.WriteLine($"  Address {Addr(le.BluetoothAddress)} ({le.BluetoothAddressType})  Status={le.ConnectionStatus}  " +
                      $"Paired={le.DeviceInformation.Pairing.IsPaired} Protection={le.DeviceInformation.Pairing.ProtectionLevel}");
    await InspectGatt(le, mode);
}

static async Task InspectGatt(BluetoothLEDevice le, BluetoothCacheMode mode)
{
    var r = await le.GetGattServicesAsync(mode);
    bool hasAncs = r.Services.Any(s => s.Uuid == AncsUuids.Service);
    Console.WriteLine($"  GATT services ({mode}): {r.Status}, {r.Services.Count} found");
    foreach (var s in r.Services)
    {
        var u = s.Uuid;
        string name = u == AncsUuids.Service ? "ANCS  <-- notifications" :
                      u == AmsUuids.Service ? "AMS   <-- media" :
                      BluetoothUuids.ToShort(u) is uint sh ? $"0x{sh:X4}" : u.ToString();
        Console.WriteLine($"    {name}");
        if (u == AncsUuids.Service || u == AmsUuids.Service)
        {
            var c = await s.GetCharacteristicsAsync(mode);
            foreach (var ch in c.Characteristics)
                Console.WriteLine($"      char {ch.Uuid} props={ch.CharacteristicProperties} protection={ch.ProtectionLevel}");
        }
        s.Dispose();
    }
    if (!hasAncs)
        Console.WriteLine("    (ANCS not visible — iOS only exposes it to an LE-bonded central; see docs/continuity-plan.md)");
}

static string Addr(ulong a) => string.Join(":", Enumerable.Range(0, 6).Select(i => ((a >> (8 * (5 - i))) & 0xFF).ToString("X2")));
