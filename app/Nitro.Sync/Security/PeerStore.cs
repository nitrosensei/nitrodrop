using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nitro.Sync.Security;

/// <summary>A paired NitroDrop PC: TOFU pin of its TLS certificate + the request-signing key from pairing.</summary>
public sealed record PairedPeer
{
    public required string DeviceId { get; init; }
    public required string Name { get; init; }
    /// <summary>SHA-256 of the peer's TLS certificate (DER), lowercase hex. Pinned at pairing time.</summary>
    public required string CertSha256 { get; init; }
    /// <summary>32-byte HMAC key, DPAPI-protected (CurrentUser) and base64 encoded.</summary>
    public required string ProtectedKey { get; init; }
    public DateTimeOffset PairedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>An iPhone/iPad Shortcut allowed to POST links. Only a hash of its token is stored.</summary>
public sealed record IosDevice
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string TokenSha256 { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class PeerStoreData
{
    public int Version { get; set; } = 1;
    public string? DeviceId { get; set; }
    public List<PairedPeer> Peers { get; set; } = [];
    public List<IosDevice> IosDevices { get; set; } = [];
}

/// <summary>%LOCALAPPDATA%\airdrop-win\sync-peers.json — thread-safe, atomic writes.</summary>
public sealed class PeerStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly bool _protectKeys;
    private PeerStoreData _data;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "airdrop-win", "sync-peers.json");

    /// <param name="protectKeys">DPAPI-wrap the pairing keys (Windows). Off only for non-Windows test hosts.</param>
    public PeerStore(string? path = null, bool protectKeys = true)
    {
        _path = path ?? DefaultPath;
        _protectKeys = protectKeys && OperatingSystem.IsWindows();
        _data = Load();
        if (string.IsNullOrEmpty(_data.DeviceId))
        {
            _data.DeviceId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
            Save();
        }
    }

    public string FilePath => _path;
    public string DeviceId => _data.DeviceId!;

    public IReadOnlyList<PairedPeer> Peers { get { lock (_gate) return _data.Peers.ToList(); } }
    public IReadOnlyList<IosDevice> IosDevices { get { lock (_gate) return _data.IosDevices.ToList(); } }

    public PairedPeer? Find(string deviceId)
    {
        lock (_gate) return _data.Peers.FirstOrDefault(p => p.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase));
    }

    public PairedPeer AddOrReplace(string deviceId, string name, string certSha256, byte[] key)
    {
        var peer = new PairedPeer
        {
            DeviceId = deviceId, Name = name, CertSha256 = certSha256.ToLowerInvariant(),
            ProtectedKey = Convert.ToBase64String(Protect(key)),
        };
        lock (_gate)
        {
            _data.Peers.RemoveAll(p => p.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase));
            _data.Peers.Add(peer);
            Save();
        }
        return peer;
    }

    public bool Remove(string deviceId)
    {
        lock (_gate)
        {
            bool removed = _data.Peers.RemoveAll(p => p.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed) Save();
            return removed;
        }
    }

    public byte[] KeyOf(PairedPeer peer) => Unprotect(Convert.FromBase64String(peer.ProtectedKey));

    public void AddIos(IosDevice d)
    {
        lock (_gate) { _data.IosDevices.Add(d); Save(); }
    }

    public bool RemoveIos(string id)
    {
        lock (_gate)
        {
            bool removed = _data.IosDevices.RemoveAll(d => d.Id == id) > 0;
            if (removed) Save();
            return removed;
        }
    }

    private PeerStoreData Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<PeerStoreData>(File.ReadAllBytes(_path), Json) ?? new PeerStoreData();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { }
        return new PeerStoreData();
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(_data, Json));
        File.Move(tmp, _path, overwrite: true);
    }

    private byte[] Protect(byte[] data) => _protectKeys ? Dpapi.Protect(data) : data;
    private byte[] Unprotect(byte[] data) => _protectKeys ? Dpapi.Unprotect(data) : data;
}

/// <summary>CryptProtectData/CryptUnprotectData (CurrentUser scope) without the ProtectedData NuGet package.</summary>
internal static class Dpapi
{
    private static readonly byte[] Entropy = "NitroDrop.Sync.v1"u8.ToArray();

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int cbData; public IntPtr pbData; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    public static byte[] Protect(byte[] data) => Run(data, protect: true);
    public static byte[] Unprotect(byte[] data) => Run(data, protect: false);

    private static unsafe byte[] Run(byte[] data, bool protect)
    {
        fixed (byte* pIn = data)
        fixed (byte* pEnt = Entropy)
        {
            var input = new DataBlob { cbData = data.Length, pbData = (IntPtr)pIn };
            var ent = new DataBlob { cbData = Entropy.Length, pbData = (IntPtr)pEnt };
            var output = default(DataBlob);
            bool ok = protect
                ? CryptProtectData(ref input, "NitroDrop sync key", ref ent, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref ent, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output);
            if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally { LocalFree(output.pbData); }
        }
    }
}
