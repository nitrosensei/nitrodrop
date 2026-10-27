using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AirDrop.Core.Contacts;

public sealed record ContactEntry(string Id, string DisplayName, IReadOnlyList<string> Phones, IReadOnlyList<string> Emails, byte[]? Photo)
{
    /// <summary>"image/jpeg", "image/png", … from the photo's magic bytes; null if no/unknown photo.</summary>
    public string? PhotoMimeType => ContactBook.DetectImageType(Photo).mime;
}

/// <summary>
/// The user's own address book imported from vCard files, used only to put names/photos on peers whose
/// Apple validation record hashes match one of these contacts (macOS-style avatars). Unknown hashes are
/// never reversed — matching is a lookup in the hashes of imported contacts only.
///
/// Persisted under %LOCALAPPDATA%\airdrop-win\contacts\: index.json + photos\&lt;id&gt;.jpg|png.
/// Thread-safe.
/// </summary>
public sealed class ContactBook
{
    private readonly object _lock = new();
    private readonly List<ContactEntry> _contacts = [];
    private Dictionary<string, ContactEntry> _byHash = new(StringComparer.Ordinal);
    private PhoneRegion _region;

    public string? StorageDirectory { get; }

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "airdrop-win", "contacts");

    /// <param name="storageDirectory">Where to persist; null = in-memory only.</param>
    public ContactBook(string? storageDirectory, PhoneRegion? region = null)
    {
        StorageDirectory = storageDirectory;
        _region = region ?? PhoneRegion.Turkey;
    }

    /// <summary>Opens the default persisted book and loads it.</summary>
    public static ContactBook OpenDefault(PhoneRegion? region = null)
    {
        var book = new ContactBook(DefaultDirectory, region);
        book.Load();
        return book;
    }

    /// <summary>Region used to expand local numbers ("0555…" → "90555…"). Changing it rebuilds the index.</summary>
    public PhoneRegion Region
    {
        get { lock (_lock) return _region; }
        set { lock (_lock) { _region = value; RebuildIndex(); } }
    }

    public int Count { get { lock (_lock) return _contacts.Count; } }
    public IReadOnlyList<ContactEntry> Contacts { get { lock (_lock) return _contacts.ToList(); } }

    /// <summary>Imports a .vcf (one or many contacts). Returns how many were added or updated. Persists.</summary>
    public int Import(string vcfPath) => ImportContacts(VCard.ParseFile(vcfPath));

    public Task<int> ImportAsync(string vcfPath, CancellationToken ct = default) =>
        Task.Run(() => Import(vcfPath), ct);

    public int ImportText(string vcardText) => ImportContacts(VCard.Parse(vcardText));

    public int ImportContacts(IEnumerable<VCardContact> cards)
    {
        int n = 0;
        lock (_lock)
        {
            foreach (var c in cards)
            {
                if (c.Phones.Count == 0 && c.Emails.Count == 0) continue; // nothing to match on
                var id = StableId(c);
                var existing = _contacts.FindIndex(x => x.Id == id);
                var entry = new ContactEntry(id, c.DisplayName, c.Phones, c.Emails,
                    c.Photo ?? (existing >= 0 ? _contacts[existing].Photo : null));
                if (existing >= 0) _contacts[existing] = entry; else _contacts.Add(entry);
                n++;
            }
            RebuildIndex();
            SaveLocked();
        }
        return n;
    }

    /// <summary>First contact whose identifier hashes intersect <paramref name="hashes"/> (lowercase/uppercase hex both fine).</summary>
    public ContactEntry? Match(IEnumerable<string>? hashes)
    {
        if (hashes == null) return null;
        lock (_lock)
        {
            foreach (var h in hashes)
                if (h != null && _byHash.TryGetValue(h.Trim().ToLowerInvariant(), out var c)) return c;
        }
        return null;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _contacts.Clear();
            _byHash.Clear();
            if (StorageDirectory != null && Directory.Exists(StorageDirectory))
            {
                try { Directory.Delete(StorageDirectory, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>Hashes we would accept for a contact (exposed for tests/diagnostics).</summary>
    public IReadOnlyList<string> HashesFor(ContactEntry c)
    {
        lock (_lock) return HashesLocked(c).Distinct().ToList();
    }

    private IEnumerable<string> HashesLocked(ContactEntry c) =>
        c.Emails.SelectMany(ContactHashing.EmailHashes).Concat(c.Phones.SelectMany(p => ContactHashing.PhoneHashes(p, _region)));

    private void RebuildIndex()
    {
        var map = new Dictionary<string, ContactEntry>(StringComparer.Ordinal);
        foreach (var c in _contacts)
            foreach (var h in HashesLocked(c))
                map.TryAdd(h, c);
        _byHash = map;
    }

    private static string StableId(VCardContact c)
    {
        var key = c.DisplayName.Trim().ToLowerInvariant() + "|" +
                  string.Join(",", c.Emails.Select(e => e.Trim().ToLowerInvariant()).Order()) + "|" +
                  string.Join(",", c.Phones.Select(p => new string(p.Where(char.IsAsciiDigit).ToArray())).Order());
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
    }

    internal static (string? mime, string ext) DetectImageType(byte[]? b)
    {
        if (b == null || b.Length < 4) return (null, ".bin");
        if (b[0] == 0xFF && b[1] == 0xD8) return ("image/jpeg", ".jpg");
        if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return ("image/png", ".png");
        if (b[0] == 'G' && b[1] == 'I' && b[2] == 'F') return ("image/gif", ".gif");
        if (b.Length >= 12 && b[4] == 'f' && b[5] == 't' && b[6] == 'y' && b[7] == 'p') return ("image/heic", ".heic");
        return (null, ".bin");
    }

    // ---------------- persistence ----------------

    private sealed record StoredContact(string Id, string Name, List<string> Phones, List<string> Emails, string? Photo);
    private sealed record StoredBook(int Version, List<StoredContact> Contacts);

    public void Load()
    {
        if (StorageDirectory == null) return;
        var index = Path.Combine(StorageDirectory, "index.json");
        lock (_lock)
        {
            _contacts.Clear();
            if (File.Exists(index))
            {
                try
                {
                    var book = JsonSerializer.Deserialize<StoredBook>(File.ReadAllText(index, Encoding.UTF8));
                    foreach (var s in book?.Contacts ?? [])
                    {
                        byte[]? photo = null;
                        if (s.Photo != null && Path.GetFileName(s.Photo) == s.Photo) // no path tricks
                        {
                            var p = Path.Combine(StorageDirectory, "photos", s.Photo);
                            if (File.Exists(p)) photo = File.ReadAllBytes(p);
                        }
                        _contacts.Add(new ContactEntry(s.Id, s.Name, s.Phones, s.Emails, photo));
                    }
                }
                catch (JsonException) { /* corrupt index: start empty */ }
            }
            RebuildIndex();
        }
    }

    private void SaveLocked()
    {
        if (StorageDirectory == null) return;
        var photos = Path.Combine(StorageDirectory, "photos");
        Directory.CreateDirectory(photos);
        var stored = new List<StoredContact>();
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in _contacts)
        {
            string? file = null;
            if (c.Photo != null)
            {
                file = c.Id + DetectImageType(c.Photo).ext;
                File.WriteAllBytes(Path.Combine(photos, file), c.Photo);
                keep.Add(file);
            }
            stored.Add(new StoredContact(c.Id, c.DisplayName, c.Phones.ToList(), c.Emails.ToList(), file));
        }
        foreach (var f in Directory.EnumerateFiles(photos))
            if (!keep.Contains(Path.GetFileName(f))) File.Delete(f);
        var tmp = Path.Combine(StorageDirectory, "index.json.tmp");
        File.WriteAllText(tmp, JsonSerializer.Serialize(new StoredBook(1, stored), new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        File.Move(tmp, Path.Combine(StorageDirectory, "index.json"), overwrite: true);
    }
}
