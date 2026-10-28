using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using AirDrop.App.Services;

namespace AirDrop.App.ViewModels;

public sealed class TrustedDeviceViewModel(string key, DevicePrefs prefs, Action<string> remove)
{
    public string Name => prefs.DisplayName ?? prefs.LastSeenName ?? key.Split('|')[0];
    public string Detail => DeviceModels.Describe(prefs.Kind, key.Split('|').ElementAtOrDefault(1));
    public RelayCommand Remove { get; } = new(_ => remove(key));
}

/// <summary>Row in Ayarlar → Hakkında → Açık kaynak bileşenler.</summary>
public sealed record LicenseItem(string Name, string Use, string License);

public sealed class SettingsViewModel : Observable
{
    public const string WebsiteUrl = "https://nitrobyte.com.tr";
    // Placeholder until the public repository exists.
    public const string RepoUrl = "https://github.com/nitrobyte/nitrodrop";

    private readonly AppSettings _s;
    private readonly IContactResolver _contacts;
    private readonly Func<Task> _applied;
    private string _contactStatus = "";

    public SettingsViewModel(AppSettings s, IContactResolver contacts, Func<Task> applied)
    {
        _s = s;
        _contacts = contacts;
        _applied = applied;
        ChooseFolder = new RelayCommand(_ => PickFolder());
        ImportContacts = new RelayCommand(async _ => await PickContactsAsync());
        ClearContacts = new RelayCommand(_ => { _contacts.Clear(); RefreshContacts(); }, _ => _contacts.Count > 0);
        RefreshContacts();
        RefreshTrusted();
        OpenWebsite = new RelayCommand(_ => OpenShell(WebsiteUrl));
        OpenRepository = new RelayCommand(_ => OpenShell(RepoUrl));
        OpenLicenses = new RelayCommand(_ => OpenShell(System.IO.Path.Combine(AppContext.BaseDirectory, "licenses")));
    }

    // ---- Hakkında ----

    public string VersionText
    {
        get
        {
            var asm = System.Reflection.Assembly.GetEntryAssembly() ?? typeof(SettingsViewModel).Assembly;
            var v = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion
                ?? asm.GetName().Version?.ToString(3) ?? "1.0.0";
            int plus = v.IndexOf('+');
            return $"SÜRÜM {(plus > 0 ? v[..plus] : v)} · NITROBYTE";
        }
    }

    public string RepositoryUrl => RepoUrl;

    public LicenseItem[] Licenses { get; } =
    {
        new("Owly", "AWDL / OWL", "MIT"),
        new("iwx", "Intel Wi-Fi sürücüsü (OpenBSD)", "ISC"),
        new("QRCoder", "QR kod", "MIT"),
        new("Schibsted Grotesk · JetBrains Mono", "yazı tipleri", "OFL 1.1"),
        new("Intel Wi-Fi firmware", "kurulumda gösterilir", "INTEL"),
    };

    public RelayCommand OpenWebsite { get; }
    public RelayCommand OpenRepository { get; }
    public RelayCommand OpenLicenses { get; }

    private static void OpenShell(string target)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception) { /* no handler / folder missing */ }
    }

    public string DeviceName
    {
        get => _s.DeviceName;
        set
        {
            var v = string.IsNullOrWhiteSpace(value) ? Environment.MachineName : value.Trim();
            if (v == _s.DeviceName) return;
            _s.DeviceName = v;
            Commit();
            Raise();
        }
    }

    public string ReceiveFolder => _s.ReceiveFolder;

    public bool PreferCompatibleFormats
    {
        get => _s.PreferCompatibleFormats;
        set { _s.PreferCompatibleFormats = value; Commit(); Raise(); }
    }

    public bool OpenLinksAutomatically
    {
        get => _s.OpenLinksAutomatically;
        set { _s.OpenLinksAutomatically = value; Commit(); Raise(); }
    }

    /// <summary>"iPhone'lara doğrudan bağlan (Wi-Fi sürücüsü)": radio host on/off, applied without a restart.</summary>
    public bool DirectIphoneConnection
    {
        get => _s.DirectIphoneConnection;
        set { _s.DirectIphoneConnection = value; Commit(); Raise(); }
    }

    public bool CopyIncomingToClipboard
    {
        get => _s.CopyIncomingToClipboard;
        set { _s.CopyIncomingToClipboard = value; Commit(); Raise(); }
    }

    /// <summary>Set by the window once the global hotkey is (or isn't) registered.</summary>
    public string HotkeyStatus { get; set; } = "Ctrl + Alt + A";

    public bool StartWithWindows
    {
        get => _s.StartWithWindows;
        set
        {
            try { StartupRegistration.Set(value); _s.StartWithWindows = value; }
            catch (Exception) { /* registry unavailable — leave as is */ }
            Commit();
            Raise();
        }
    }

    // ---- Gezgin sağ tık menüsü (Services/ExplorerMenu.cs) ----

    private ExplorerMenuService ExplorerMenuSvc => ExplorerMenuIntegration.Default;
    private string? _explorerMenuError;

    public string ExplorerMenuTitle => UiStrings.ExplorerMenuSetting;

    /// <summary>MSIX: always on (manifest IExplorerCommand), the switch is shown checked and disabled.</summary>
    public bool ExplorerMenuToggleable => !ExplorerMenuSvc.ManagedByWindows;

    public string ExplorerMenuCaption => _explorerMenuError
        ?? (ExplorerMenuSvc.ManagedByWindows ? UiStrings.ExplorerMenuCaptionPackaged : UiStrings.ExplorerMenuCaptionPortable);

    /// <summary>Read from the registry each time (the registry is the truth, not settings.json).</summary>
    public bool ExplorerMenuEnabled
    {
        get
        {
            try { return ExplorerMenuSvc.IsEnabled; }
            catch (Exception) { return false; }
        }
        set
        {
            try { ExplorerMenuSvc.SetEnabled(value); _explorerMenuError = null; }
            catch (Exception ex) { _explorerMenuError = UiStrings.ExplorerMenuFailed + ex.Message; }
            Raise();
            Raise(nameof(ExplorerMenuCaption));
        }
    }

    public string ContactStatus { get => _contactStatus; private set => Set(ref _contactStatus, value); }

    public ObservableCollection<TrustedDeviceViewModel> Trusted { get; } = new();
    public bool HasTrusted => Trusted.Count > 0;

    public RelayCommand ChooseFolder { get; }
    public RelayCommand ImportContacts { get; }
    public RelayCommand ClearContacts { get; }

    public void RefreshTrusted()
    {
        Trusted.Clear();
        foreach (var (k, p) in _s.Devices.Where(d => d.Value.Trusted))
            Trusted.Add(new TrustedDeviceViewModel(k, p, key =>
            {
                _s.For(key).Trusted = false;
                Commit();
                RefreshTrusted();
            }));
        Raise(nameof(HasTrusted));
    }

    private void RefreshContacts() =>
        ContactStatus = _contacts.Count == 0
            ? "Rehber içe aktarılmadı. Kişilerin fotoğrafları için iCloud ya da Google'dan dışa aktardığınız .vcf dosyasını seçin."
            : $"{_contacts.Count} kişi içe aktarıldı. Rehberinizdeki kişiler fotoğraflarıyla görünür.";

    private void PickFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Alınan dosyaların kaydedileceği klasör", InitialDirectory = _s.ReceiveFolder };
        if (dlg.ShowDialog() != true) return;
        _s.ReceiveFolder = dlg.FolderName;
        Commit();
        Raise(nameof(ReceiveFolder));
    }

    private async Task PickContactsAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Rehber dosyası (.vcf)",
            Filter = "vCard (*.vcf)|*.vcf|Tüm dosyalar|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog() != true) return;
        ContactStatus = "İçe aktarılıyor…";
        int added = 0;
        try
        {
            foreach (var f in dlg.FileNames) added += await _contacts.ImportAsync(f);
            RefreshContacts();
            if (added == 0) ContactStatus = "Dosyada kişi bulunamadı. " + ContactStatus;
        }
        catch (Exception ex)
        {
            ContactStatus = "İçe aktarılamadı: " + ex.Message;
        }
        await _applied();
    }

    private void Commit()
    {
        _s.Save();
        _ = _applied();
    }
}
