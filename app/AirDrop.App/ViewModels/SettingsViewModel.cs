using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using AirDrop.App.Localization;
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

/// <summary>
/// Entry of Ayarlar → Dil: <see cref="Code"/> null = follow Windows ("Windows diliyle aynı (English)"). Native names are
/// never translated; the auto entry's label re-reads on language changes (Observable).
/// </summary>
public sealed class LanguageOption(string? code) : Observable
{
    public string? Code { get; } = code;

    public string Label => Code is null
        ? SettingsStrings.Settings_LanguageAutoWith(CommonStrings.Language_Auto, NativeName(Loc.ResolveDefault()))
        : NativeName(Code);

    private static string NativeName(string code) => Loc.Languages.First(l => l.Code == code).NativeName;
}

public sealed class SettingsViewModel : Observable
{
    public const string WebsiteUrl = "https://nitrobyte.com.tr";
    // Placeholder until the public repository exists.
    public const string RepoUrl = "https://github.com/nitrosensei/nitrodrop";

    private readonly AppSettings _s;
    private readonly IContactResolver _contacts;
    private readonly Func<Task> _applied;
    // Contact status as a text factory, so a language switch re-renders it (OnLanguageChanged → Raise("")).
    private Func<string> _contactStatus = () => "";

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
        Languages = new[] { new LanguageOption(null) }.Concat(Loc.Languages.Select(l => new LanguageOption(l.Code))).ToArray();
    }

    // ---- Dil ----

    /// <summary>"Windows diliyle aynı" + the six UI languages. Stable instances, so the selection survives a switch.</summary>
    public LanguageOption[] Languages { get; }
    private string? _pendingLanguage;   // "" = auto chosen, switch not applied yet

    public LanguageOption SelectedLanguage
    {
        get => Languages.FirstOrDefault(o => o.Code is not null && o.Code == (_pendingLanguage ?? _s.Language)) ?? Languages[0];
        set
        {
            if (value is null || value == SelectedLanguage) return;
            var code = value.Code;
            // Answer the binding's re-read with the new choice right away (the switch itself is deferred below).
            _pendingLanguage = code ?? "";
            // After the ComboBox finishes its selection change: the switch re-raises every binding on this page.
            void Apply()
            {
                UiLanguage.Change(code, _s);
                _pendingLanguage = null;
                // The classic Explorer verb / SendTo description are written in the UI language: rewrite them.
                try { ExplorerMenuSvc.RefreshLanguage(); } catch (Exception) { /* registry unavailable */ }
            }
            if (System.Windows.Application.Current?.Dispatcher is { } d) d.BeginInvoke(Apply);
            else Apply();
        }
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
            return SettingsStrings.About_Version(plus > 0 ? v[..plus] : v);
        }
    }

    public string RepositoryUrl => RepoUrl;

    public LicenseItem[] Licenses => new LicenseItem[]
    {
        new("Owly", "AWDL / OWL", "MIT"),
        new("iwx", SettingsStrings.About_UseIwx, "ISC"),
        new("QRCoder", SettingsStrings.About_UseQr, "MIT"),
        new("Golos Text · Schibsted Grotesk · JetBrains Mono", SettingsStrings.About_UseFonts, "OFL 1.1"),
        new("Intel Wi-Fi firmware", SettingsStrings.About_UseFirmware, "INTEL"),
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
            var v = string.IsNullOrWhiteSpace(value) ? LocalPcName.Value : value.Trim();
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

    /// <summary>Set by the window when Ctrl+Alt+A could not be registered (another app owns it).</summary>
    public bool HotkeyInUse
    {
        get => _hotkeyInUse;
        set { _hotkeyInUse = value; Raise(); Raise(nameof(HotkeyStatus)); }
    }
    private bool _hotkeyInUse;

    /// <summary>
    /// Key chip text ("Strg + Alt + A" in German). Kept settable for the window: any assignment means the hotkey is
    /// taken (use <see cref="HotkeyInUse"/>); the assigned text itself is ignored so it stays localized.
    /// </summary>
    public string HotkeyStatus
    {
        get => HotkeyInUse ? SettingsStrings.Settings_HotkeyInUse(SettingsStrings.Settings_Hotkey) : SettingsStrings.Settings_Hotkey;
        set => HotkeyInUse = true;
    }

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

    public string ExplorerMenuCaption => (_explorerMenuError is { } err ? UiStrings.ExplorerMenuFailed(err) : null)
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
            catch (Exception ex) { _explorerMenuError = ex.Message; }
            Raise();
            Raise(nameof(ExplorerMenuCaption));
        }
    }

    public string ContactStatus => _contactStatus();

    private void SetContactStatus(Func<string> text)
    {
        _contactStatus = text;
        Raise(nameof(ContactStatus));
    }

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

    private string ContactCountText() => _contacts.Count == 0
        ? SettingsStrings.Settings_ContactsNone
        : SettingsStrings.Settings_ContactsImported_Plural(_contacts.Count);

    private void RefreshContacts() => SetContactStatus(ContactCountText);

    private void PickFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = SettingsStrings.Settings_PickFolderTitle, InitialDirectory = _s.ReceiveFolder };
        if (dlg.ShowDialog() != true) return;
        _s.ReceiveFolder = dlg.FolderName;
        Commit();
        Raise(nameof(ReceiveFolder));
    }

    private async Task PickContactsAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = SettingsStrings.Settings_PickContactsTitle,
            Filter = $"vCard (*.vcf)|*.vcf|{SettingsStrings.Settings_AllFiles}|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog() != true) return;
        SetContactStatus(() => SettingsStrings.Settings_ContactsImporting);
        int added = 0;
        try
        {
            foreach (var f in dlg.FileNames) added += await _contacts.ImportAsync(f);
            RefreshContacts();
            if (added == 0) SetContactStatus(() => SettingsStrings.Settings_ContactsNoneFound(ContactCountText()));
        }
        catch (Exception ex)
        {
            var msg = ex.Message;
            SetContactStatus(() => SettingsStrings.Settings_ContactsImportFailed(msg));
        }
        await _applied();
    }

    private void Commit()
    {
        _s.Save();
        _ = _applied();
    }
}
