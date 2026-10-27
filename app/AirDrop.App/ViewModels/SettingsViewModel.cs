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

public sealed class SettingsViewModel : Observable
{
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
