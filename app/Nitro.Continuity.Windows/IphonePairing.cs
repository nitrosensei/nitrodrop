using Nitro.Continuity.Runtime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;

namespace Nitro.Continuity.Platform;

/// <summary>
/// Finding and pairing the iPhone (docs/continuity-plan.md §1.6). Procedure A = classic pairing (+ CTKD gives the LE
/// keys); B = LE pairing to the strongest Apple advertiser; C = HID-over-GATT bait (<see cref="HidBait"/>).
/// Nothing here runs unless the UI asks for it.
/// </summary>
public static class IphonePairing
{
    private const string AddressProp = "System.Devices.Aep.DeviceAddress";
    private const string ConnectedProp = "System.Devices.Aep.IsConnected";
    private const string CodProp = "System.Devices.Aep.Bluetooth.Cod";
    private const int MajorClassPhone = 0x02;

    public static async Task<(bool Present, bool On)> RadioStateAsync()
    {
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter is null) return (false, false);
            var radio = await adapter.GetRadioAsync();
            return (true, radio is null || radio.State == RadioState.On);
        }
        catch (Exception) { return (false, false); }
    }

    /// <summary>Paired classic devices that look like phones (Class of Device major = Phone, or the name says iPhone).</summary>
    public static async Task<IReadOnlyList<PhoneDevice>> GetPairedPhonesAsync(CancellationToken ct = default)
    {
        var list = new List<PhoneDevice>();
        var infos = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true), [AddressProp, ConnectedProp]).AsTask(ct);
        foreach (var info in infos)
        {
            try
            {
                using var dev = await BluetoothDevice.FromIdAsync(info.Id).AsTask(ct);
                if (dev is null) continue;
                bool phone = dev.ClassOfDevice.MajorClass == BluetoothMajorClass.Phone || LooksLikeIphone(dev.Name);
                if (!phone) continue;
                list.Add(new PhoneDevice(dev.DeviceId, dev.Name, dev.BluetoothAddress, true,
                    dev.ConnectionStatus == BluetoothConnectionStatus.Connected));
            }
            catch (Exception e) when (e is not OperationCanceledException) { /* device vanished / access denied: skip */ }
        }
        return list;
    }

    /// <summary>
    /// Classic inquiry (AssociationEndpoint watcher) for unpaired phones. The iPhone is discoverable while
    /// Ayarlar → Bluetooth is open on it.
    /// </summary>
    public static async Task<IReadOnlyList<PhoneDevice>> DiscoverAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var found = new Dictionary<string, PhoneDevice>();
        var watcher = DeviceInformation.CreateWatcher(BluetoothDevice.GetDeviceSelectorFromPairingState(false),
            [AddressProp, CodProp, ConnectedProp], DeviceInformationKind.AssociationEndpoint);
        void Consider(DeviceInformation d)
        {
            uint cod = d.Properties.TryGetValue(CodProp, out var c) && c is uint u ? u : 0;
            bool phone = ((cod >> 8) & 0x1F) == MajorClassPhone || LooksLikeIphone(d.Name);
            if (!phone || string.IsNullOrWhiteSpace(d.Name)) return;
            ulong addr = d.Properties.TryGetValue(AddressProp, out var a) && a is string s ? ParseAddress(s) : 0;
            lock (found) found[d.Id] = new PhoneDevice(d.Id, d.Name, addr, false);
        }
        watcher.Added += (_, d) => Consider(d);
        watcher.Updated += (_, u) => { /* names can arrive late; a new Added will follow for renamed AEPs */ };
        watcher.Start();
        try { await Task.Delay(timeout, ct); }
        catch (OperationCanceledException) { }
        finally
        {
            if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) watcher.Stop();
        }
        lock (found) return found.Values.OrderBy(p => p.Name).ToList();
    }

    /// <summary>Procedure A: classic pairing (6-digit numeric comparison on both screens).</summary>
    public static async Task<PairingResult> PairClassicAsync(PhoneDevice device, IPairingPrompt prompt, CancellationToken ct = default)
    {
        DeviceInformation info;
        try { info = await DeviceInformation.CreateFromIdAsync(device.Id).AsTask(ct); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new(PairingOutcome.NotFound, $"iPhone bulunamadı ({e.Message.Trim()}). Ayarlar → Bluetooth ekranı açık mı?");
        }
        return await PairAsync(info, device.Name, prompt, ct);
    }

    internal static async Task<PairingResult> PairAsync(DeviceInformation info, string name, IPairingPrompt prompt, CancellationToken ct)
    {
        if (info.Pairing.IsPaired) return new(PairingOutcome.AlreadyPaired, $"{name} zaten eşleşmiş.");
        if (!info.Pairing.CanPair) return new(PairingOutcome.Failed, "Windows bu cihazla eşleşmeye izin vermiyor.");

        var custom = info.Pairing.Custom;
        async void OnRequested(DeviceInformationCustomPairing sender, DevicePairingRequestedEventArgs args)
        {
            using var deferral = args.GetDeferral();
            try
            {
                switch (args.PairingKind)
                {
                    case DevicePairingKinds.ConfirmOnly:
                        if (await prompt.ConfirmAsync(PairingKind.ConfirmOnly, name, null, ct)) args.Accept();
                        break;
                    case DevicePairingKinds.ConfirmPinMatch:
                        if (await prompt.ConfirmAsync(PairingKind.ConfirmPinMatch, name, args.Pin, ct)) args.Accept();
                        break;
                    case DevicePairingKinds.DisplayPin:
                        // We show the code; the user types it on the iPhone. Accept so Windows keeps waiting.
                        args.Accept();
                        _ = prompt.ConfirmAsync(PairingKind.DisplayPin, name, args.Pin, ct);
                        break;
                    case DevicePairingKinds.ProvidePin:
                        if (await prompt.ProvidePinAsync(name, ct) is { Length: > 0 } pin) args.Accept(pin);
                        break;
                }
            }
            catch (Exception) { /* not accepting = rejected by handler */ }
        }

        custom.PairingRequested += OnRequested;
        try
        {
            var kinds = DevicePairingKinds.ConfirmOnly | DevicePairingKinds.ConfirmPinMatch | DevicePairingKinds.DisplayPin | DevicePairingKinds.ProvidePin;
            var r = await custom.PairAsync(kinds, DevicePairingProtectionLevel.Encryption).AsTask(ct);
            return Map(r.Status, name);
        }
        catch (OperationCanceledException) { return new(PairingOutcome.Canceled, "Eşleşme iptal edildi."); }
        catch (Exception e) { return new(PairingOutcome.Failed, $"Eşleşme başarısız: {e.Message.Trim()}"); }
        finally { custom.PairingRequested -= OnRequested; }
    }

    internal static PairingResult Map(DevicePairingResultStatus s, string name) => s switch
    {
        DevicePairingResultStatus.Paired => new(PairingOutcome.Paired, $"{name} eşleşti."),
        DevicePairingResultStatus.AlreadyPaired => new(PairingOutcome.AlreadyPaired, $"{name} zaten eşleşmiş."),
        DevicePairingResultStatus.PairingCanceled => new(PairingOutcome.Canceled, "Eşleşme iptal edildi."),
        DevicePairingResultStatus.RejectedByHandler or DevicePairingResultStatus.AuthenticationFailure
            or DevicePairingResultStatus.ConnectionRejected or DevicePairingResultStatus.Failed =>
            new(PairingOutcome.Rejected, "Eşleşme reddedildi. iPhone'da \"Eşleştir\"e dokunup kodların aynı olduğunu doğrulayın."),
        DevicePairingResultStatus.AuthenticationTimeout or DevicePairingResultStatus.NotReadyToPair or DevicePairingResultStatus.NoSupportedProfiles =>
            new(PairingOutcome.Failed, "iPhone yanıt vermedi. Ayarlar → Bluetooth ekranını açık tutup tekrar deneyin."),
        _ => new(PairingOutcome.Failed, $"Eşleşme başarısız ({s})."),
    };

    /// <summary>
    /// Procedure A, LE half: after classic pairing iOS derives LE keys (CTKD, UNVERIFIED for PCs), so the LE identity
    /// address equals the classic public address. Also checks paired LE devices with the same name.
    /// </summary>
    public static async Task<BluetoothLEDevice?> FindLeDeviceAsync(PhoneDevice phone, CancellationToken ct = default)
    {
        try
        {
            if (phone.Address != 0 && await BluetoothLEDevice.FromBluetoothAddressAsync(phone.Address).AsTask(ct) is { } le)
                return le;
        }
        catch (Exception e) when (e is not OperationCanceledException) { }
        var paired = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)).AsTask(ct);
        foreach (var d in paired.Where(d => string.Equals(d.Name, phone.Name, StringComparison.OrdinalIgnoreCase)))
        {
            try { if (await BluetoothLEDevice.FromIdAsync(d.Id).AsTask(ct) is { } le) return le; }
            catch (Exception e) when (e is not OperationCanceledException) { }
        }
        return null;
    }

    /// <summary>
    /// Procedure B: listen for Apple (0x004C) advertisements, take the strongest (the iPhone held next to the PC),
    /// open it and LE-pair (iOS shows "Bluetooth Eşleme İsteği"). UNVERIFIED that iOS accepts an unsolicited PC central.
    /// </summary>
    public static async Task<(BluetoothLEDevice? Device, PairingResult Result)> PairLeNearestAppleAsync(
        string name, IPairingPrompt prompt, TimeSpan scan, CancellationToken ct = default)
    {
        var best = new Dictionary<ulong, (short Rssi, BluetoothAddressType Type)>();
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += (_, a) =>
        {
            if (!a.Advertisement.ManufacturerData.Any(m => m.CompanyId == 0x004C)) return;
            lock (best)
                if (!best.TryGetValue(a.BluetoothAddress, out var cur) || a.RawSignalStrengthInDBm > cur.Rssi)
                    best[a.BluetoothAddress] = (a.RawSignalStrengthInDBm, a.BluetoothAddressType);
        };
        watcher.Start();
        try { await Task.Delay(scan, ct); }
        catch (OperationCanceledException) { }
        finally { watcher.Stop(); }

        KeyValuePair<ulong, (short Rssi, BluetoothAddressType Type)>[] candidates;
        lock (best) candidates = best.Where(kv => kv.Value.Rssi >= -65).OrderByDescending(kv => kv.Value.Rssi).Take(3).ToArray();
        if (candidates.Length == 0)
            return (null, new(PairingOutcome.NotFound, "Yakında bir Apple cihazı bulunamadı. iPhone'u bilgisayara yaklaştırıp ekranını açın."));

        foreach (var c in candidates)
        {
            BluetoothLEDevice? le = null;
            try { le = await BluetoothLEDevice.FromBluetoothAddressAsync(c.Key, c.Value.Type).AsTask(ct); }
            catch (Exception e) when (e is not OperationCanceledException) { }
            if (le is null) continue;
            var r = await PairAsync(le.DeviceInformation, name, prompt, ct);
            if (r.Success) return (le, r);
            le.Dispose();
        }
        return (null, new(PairingOutcome.Failed, "iPhone LE eşleşmesini kabul etmedi."));
    }

    public static bool LooksLikeIphone(string? name) =>
        name is not null && (name.Contains("iPhone", StringComparison.OrdinalIgnoreCase) || name.Contains("iPad", StringComparison.OrdinalIgnoreCase));

    internal static ulong ParseAddress(string s)
    {
        var hex = new string(s.Where(Uri.IsHexDigit).ToArray());
        return hex.Length == 12 ? Convert.ToUInt64(hex, 16) : 0;
    }
}
