using System.Text.RegularExpressions;

namespace AirDrop.Core.Devices;

/// <summary>Raw SMBIOS strings (HKLM\HARDWARE\DESCRIPTION\System\BIOS / Win32_ComputerSystem + Win32_BaseBoard).</summary>
public sealed record SmbiosStrings(
    string? SystemManufacturer,
    string? SystemProductName,
    string? SystemFamily = null,
    string? SystemVersion = null,
    string? BaseBoardManufacturer = null,
    string? BaseBoardProduct = null);

/// <summary>
/// Turns SMBIOS strings into a friendly manufacturer + model: "Acer" + "Nitro V 15", "Lenovo" + "ThinkPad X1 Carbon Gen 11",
/// dropping OEM placeholders ("To Be Filled By O.E.M.", "System Product Name", "Default string"). Vendor rules:
/// Lenovo puts the marketing name in SystemVersion (product = "21HM002QTX"), Acer in SystemFamily, HP's family carries a
/// board id prefix ("103C_5335KV HP EliteBook") so its product is used, ASUS repeats the code ("…G513QM_G513QM").
/// Custom-built desktops fall back to the motherboard ("ASUS ROG STRIX B550-F GAMING").
/// </summary>
public static partial class PcModelNames
{
    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "To Be Filled By O.E.M.", "To be filled by O.E.M", "TBD by OEM", "System Product Name", "System manufacturer",
        "System Manufacturer", "System Version", "System Family", "System SKU", "Default string", "Default", "Not Applicable",
        "Not Specified", "Not Available", "N/A", "NA", "None", "Null", "OEM", "O.E.M.", "OEM Chassis", "Type1ProductConfigId",
        "Type1Family", "Type1Sku0", "Type2 - Board Product Name1", "Undefined", "Unknown", "INVALID", "x.x", "XX", "0",
        "1.0", "Rev 1.xx", "Base Board Product Name", "Product Name", "Chassis Manufacture", "Desktop", "Notebook", "Laptop",
        "PC", "All Series", "Computer", "To be filled by OEM",
    };

    private static readonly (string Needle, string Name)[] Vendors =
    [
        ("lenovo", "Lenovo"), ("hewlett", "HP"), ("alienware", "Alienware"), ("dell", "Dell"), ("asustek", "ASUS"),
        ("asus", "ASUS"), ("micro-star", "MSI"), ("acer", "Acer"), ("microsoft", "Microsoft"), ("apple", "Apple"),
        ("gigabyte", "Gigabyte"), ("samsung", "Samsung"), ("huawei", "Huawei"), ("honor", "Honor"), ("xiaomi", "Xiaomi"),
        ("timi", "Xiaomi"), ("toshiba", "Toshiba"), ("dynabook", "Dynabook"), ("fujitsu", "Fujitsu"), ("lg electronics", "LG"),
        ("razer", "Razer"), ("medion", "Medion"), ("casper", "Casper"), ("monster", "Monster"), ("framework", "Framework"),
        ("vaio", "VAIO"), ("sony", "Sony"), ("asrock", "ASRock"), ("intel", "Intel"), ("google", "Google"),
        ("system76", "System76"), ("clevo", "Clevo"), ("tongfang", "Tongfang"), ("chuwi", "Chuwi"), ("valve", "Valve"),
        ("panasonic", "Panasonic"), ("getac", "Getac"), ("vmware", "VMware"), ("innotek", "VirtualBox"), ("qemu", "QEMU"),
        ("biostar", "Biostar"), ("zotac", "Zotac"), ("beelink", "Beelink"), ("minisforum", "Minisforum"),
        ("gpd", "GPD"), ("ayaneo", "AYANEO"), ("nvidia", "NVIDIA"), ("evga", "EVGA"),
    ];

    /// <summary>Cleaned (manufacturer, model). Either can be null; never throws.</summary>
    public static (string? Manufacturer, string? Model) Clean(SmbiosStrings s)
    {
        string? mf = Manufacturer(s.SystemManufacturer);
        string? product = Meaningful(s.SystemProductName);
        string? family = Meaningful(s.SystemFamily);
        string? version = Meaningful(s.SystemVersion);
        if (version is not null && (VersionLike().IsMatch(version) || version.All(c => char.IsDigit(c) || c == '.'))) version = null;
        if (family is not null && family.All(c => char.IsDigit(c) || c == '_' || c == '-')) family = null;

        string? model = null;
        if (mf == "Apple" && product is not null && AppleModels.Find(product) is { } apple)
            model = apple.Name;                                   // Boot Camp: "MacBookPro16,1"
        else if (mf == "Lenovo")
            model = Strip(version, mf) is { } v && !LooksLikeCode(v) ? v : Strip(family, mf) ?? product;
        else if (mf == "Acer")
            model = Strip(family, mf) ?? product;
        else if (mf == "HP")
            model = product;
        else if (mf == "MSI" && product is not null && MsiBoard().IsMatch(product))
            model = null;                                         // "MS-7C91": an MSI desktop board id → use the baseboard
        else
            model = product ?? Strip(family, mf);

        if (model is null && mf is null)
        {
            // Custom-built desktop: SMBIOS system strings are all placeholders, the motherboard is the best name we have.
            mf = Manufacturer(s.BaseBoardManufacturer);
            model = Meaningful(s.BaseBoardProduct);
        }
        else if (model is null && Meaningful(s.BaseBoardProduct) is { } board
                 && Manufacturer(s.BaseBoardManufacturer) is { } bmf && (mf is null || bmf == mf))
        {
            mf ??= bmf;
            model = board;
        }

        model = Tidy(Strip(model, mf));
        return (mf, model);
    }

    /// <summary>Cleaned display string ("Acer Nitro V 15"), or null.</summary>
    public static string? DisplayName(SmbiosStrings s)
    {
        var (mf, model) = Clean(s);
        return new HostDevice { Manufacturer = mf, Model = model }.DisplayName;
    }

    public static string? Manufacturer(string? raw)
    {
        var s = Meaningful(raw);
        if (s is null) return null;
        foreach (var (needle, name) in Vendors)
            if (s.Contains(needle, StringComparison.OrdinalIgnoreCase)) return name;
        if (s.Equals("HP", StringComparison.OrdinalIgnoreCase) || s.StartsWith("HP ", StringComparison.OrdinalIgnoreCase)) return "HP";
        if (s.Equals("MSI", StringComparison.OrdinalIgnoreCase)) return "MSI";
        s = CorporateSuffix().Replace(s, "").Trim().TrimEnd(',', '.').Trim();
        if (s.Length == 0) return null;
        // "EXCALIBUR" → "Excalibur"; mixed case is kept as the vendor wrote it.
        if (s.Length > 3 && s.All(c => !char.IsLetter(c) || char.IsUpper(c)))
            s = string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.Length <= 3 ? w : w[0] + w[1..].ToLowerInvariant()));
        return s;
    }

    /// <summary>Null for empty / placeholder / all-zero strings, otherwise the trimmed, single-spaced string.</summary>
    public static string? Meaningful(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = Spaces().Replace(raw.Trim().Trim('\0'), " ");
        if (s.Length == 0 || Placeholders.Contains(s)) return null;
        if (s.All(c => c is '0' or ' ' or '-' or '_' or 'x' or 'X' or 'F' or 'f')) return null;   // 00000000, FFFFFFFF, xxxx
        if (s.StartsWith("To be filled", StringComparison.OrdinalIgnoreCase) || s.StartsWith("Default string", StringComparison.OrdinalIgnoreCase))
            return null;
        return s;
    }

    private static string? Strip(string? model, string? mf)
    {
        if (model is null) return null;
        var m = model.Trim();
        if (mf is not null && m.StartsWith(mf + " ", StringComparison.OrdinalIgnoreCase)) m = m[(mf.Length + 1)..].Trim();
        if (mf == "HP" && m.StartsWith("Hewlett-Packard ", StringComparison.OrdinalIgnoreCase)) m = m["Hewlett-Packard ".Length..];
        return m.Length == 0 ? null : m;
    }

    private static string? Tidy(string? model)
    {
        if (model is null) return null;
        var m = model;
        m = DuplicateSuffix().Replace(m, "$1");          // "ROG Strix G513QM_G513QM" → "ROG Strix G513QM"
        m = MsiBoardSuffix().Replace(m, "");             // "MAG B550 TOMAHAWK (MS-7C91)" → "MAG B550 TOMAHAWK"
        m = Spaces().Replace(m, " ").Trim();
        return Meaningful(m);
    }

    /// <summary>"21HM002QTX", "20XW0055GE": Lenovo machine-type codes, not names.</summary>
    private static bool LooksLikeCode(string s) => !s.Contains(' ') && s.Any(char.IsDigit) && s.Count(char.IsLetter) <= 5 && s.Length >= 8;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^(.*?\b(\w+))_\2$")]
    private static partial Regex DuplicateSuffix();

    [GeneratedRegex(@"^MS-\w{4}$", RegexOptions.IgnoreCase)]
    private static partial Regex MsiBoard();

    [GeneratedRegex(@"\s*\(MS-\w{4}\)$", RegexOptions.IgnoreCase)]
    private static partial Regex MsiBoardSuffix();

    [GeneratedRegex(@"^(V|Rev\.?\s*|Ver\.?\s*)?\d+(\.\w+)*$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionLike();

    [GeneratedRegex(@"\b(Inc\.?|Incorporated|Corporation|Corp\.?|Co\.,?\s*Ltd\.?|Co\.?|Ltd\.?|Limited|LLC|GmbH|S\.A\.|AG|COMPUTER|Computers?|Technology|Technologies|International|Electronics|Bilgisayar Sistemleri|A\.Ş\.|San\. ve Tic\.)(?=\s|,|$)", RegexOptions.IgnoreCase)]
    private static partial Regex CorporateSuffix();
}
