using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using AirDrop.App.ViewModels;

namespace AirDrop.App.Setup;

public enum WizardPage { Compatibility, Consent, BitLocker, SecureBoot, TestSigning, Install, Done, Rollback, RollbackDone }

public sealed class StepDot(string title) : Observable
{
    private bool _done, _current;
    public string Title { get; } = title;
    public bool IsDone { get => _done; set => Set(ref _done, value); }
    public bool IsCurrent { get => _current; set => Set(ref _current, value); }
}

public sealed record ConsentItem(string Glyph, string Title, string Body);

/// <summary>
/// "Gelişmiş Kurulum" wizard. Detection on open, one page per step, every system change behind an explicit
/// button (and a confirmation for reboots/rollback), all elevated work through <see cref="ElevatedHelper"/>.
/// </summary>
public sealed class SetupWizardViewModel : Observable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

    private readonly ISystemProbe _probe;
    private readonly string? _statePath;
    private readonly bool _rollbackMode;
    private SetupState _state;
    private SystemSnapshot _snap = new();
    private WizardPage _page;
    private bool _busy;
    private string _status = "", _error = "";
    private bool _consentChecked;
    private BitLockerInfo? _bitLockerDetails;
    private bool _firmwareReady;
    private string _licenseText = "";
    private bool _licenseAccepted;
    private bool _rollbackOk;

    /// <param name="statePath">null = in-memory only (snapshots / previews).</param>
    public SetupWizardViewModel(ISystemProbe probe, string? statePath, bool rollbackMode)
    {
        _probe = probe;
        _statePath = statePath;
        _rollbackMode = rollbackMode;
        _state = statePath is null ? new SetupState() : SetupState.Load(statePath);
        _consentChecked = _state.ConsentGiven;

        Dots = new ObservableCollection<StepDot>(SetupFlow.Steps.Select(s => new StepDot(StepTitle(s))));
        Primary = new RelayCommand(_ => Run(PrimaryAsync));
        Secondary = new RelayCommand(_ => Run(SecondaryAsync));
        CheckBitLocker = new RelayCommand(_ => Run(CheckBitLockerAsync));
        SaveRecoveryKey = new RelayCommand(_ => SaveKey(print: false));
        PrintRecoveryKey = new RelayCommand(_ => SaveKey(print: true));
        OpenMyRecoveryKey = new RelayCommand(_ => Shell("https://aka.ms/myrecoverykey"));
        Recheck = new RelayCommand(_ => Run(async () => { await RefreshAsync(); GoNextIncomplete(); }));
        StartRollback = new RelayCommand(_ => Page = WizardPage.Rollback);
        OpenLog = new RelayCommand(_ => Shell(SetupPaths.LogFile));
        _page = rollbackMode ? WizardPage.Rollback : WizardPage.Compatibility;
        SetupLog.Line += OnLogLine;
    }

    // ---------------- view hooks ----------------

    /// <summary>(title, body, ok button text) → confirmed.</summary>
    public Func<string, string, string, bool>? Confirm { get; set; }
    /// <summary>Suggested file name → chosen path (or null).</summary>
    public Func<string, string?>? PickSaveFile { get; set; }
    public Action? CloseWindow { get; set; }

    // ---------------- common state ----------------

    public bool IsDryRun => ElevatedHelper.DryRun;
    public ObservableCollection<StepDot> Dots { get; }
    public bool ShowDots => !_rollbackMode && Page is not (WizardPage.Rollback or WizardPage.RollbackDone);

    public WizardPage Page
    {
        get => _page;
        set
        {
            if (!Set(ref _page, value)) return;
            Error = "";
            Status = "";
            SetupLog.Write($"sayfa: {value}");
            RaiseAll();
        }
    }

    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) RaiseFooter(); } }
    public string Status { get => _status; private set { if (Set(ref _status, value)) Raise(nameof(HasStatus)); } }
    public bool HasStatus => Status.Length > 0;
    public string Error { get => _error; private set { if (Set(ref _error, value)) Raise(nameof(HasError)); } }
    public bool HasError => Error.Length > 0;

    public bool IsCompatibility => Page == WizardPage.Compatibility;
    public bool IsConsent => Page == WizardPage.Consent;
    public bool IsBitLocker => Page == WizardPage.BitLocker;
    public bool IsSecureBoot => Page == WizardPage.SecureBoot;
    public bool IsTestSigning => Page == WizardPage.TestSigning;
    public bool IsInstall => Page == WizardPage.Install;
    public bool IsDone => Page == WizardPage.Done;
    public bool IsRollback => Page == WizardPage.Rollback;
    public bool IsRollbackDone => Page == WizardPage.RollbackDone;

    public string Glyph => Page switch
    {
        WizardPage.Compatibility => IsSupported ? "" : "",
        WizardPage.Consent => "",
        WizardPage.BitLocker => "",
        WizardPage.SecureBoot => "",
        WizardPage.TestSigning => "",
        WizardPage.Install => "",
        WizardPage.Done => "",
        WizardPage.Rollback => "",
        _ => _rollbackOk ? "" : "",
    };

    /// <summary>Green circle for finished/satisfied pages, accent otherwise.</summary>
    public bool GlyphPositive => Page switch
    {
        WizardPage.Done => true,
        WizardPage.SecureBoot => _snap.SecureBootOn != true,
        WizardPage.TestSigning => _snap.TestSigningActive,
        WizardPage.BitLocker => !BitLockerNeedsBackup,
        WizardPage.RollbackDone => _rollbackOk,
        _ => false,
    };

    public string Title => Page switch
    {
        WizardPage.Compatibility => IsSupported ? "iPhone'larla doğrudan bağlantı" : "Bu bilgisayarda henüz desteklenmiyor",
        WizardPage.Consent => "Neler değişecek?",
        WizardPage.BitLocker => BitLockerNeedsBackup ? "Kurtarma anahtarınızı yedekleyin" : "BitLocker",
        WizardPage.SecureBoot => _snap.SecureBootOn switch
        {
            true => "Secure Boot'u kapatın",
            false => "Secure Boot kapalı",
            null => "Secure Boot kullanılmıyor",
        },
        WizardPage.TestSigning => _snap.TestSigningActive ? "Test modu açık"
            : SetupFlow.TestSigningPendingReboot(_snap, _state) ? "Yeniden başlatma gerekiyor"
            : "Windows test modunu açın",
        WizardPage.Install => "Sürücüyü kurun",
        WizardPage.Done => "Hazır!",
        WizardPage.Rollback => "Kaldır ve eski haline getir",
        _ => !_rollbackOk ? "Bazı adımlar tamamlanamadı"
            : _snap.SecureBootOn == true ? "Eski haline getirildi" : "Neredeyse bitti",
    };

    public string Subtitle => Page switch
    {
        WizardPage.Compatibility => IsSupported
            ? "NitroDrop, Wi-Fi kartınızı AirDrop'un kullandığı doğrudan bağlantıya hazırlayabilir. Birkaç adım ve iki yeniden başlatma sürer. Her adımda ne olacağını önce anlatırız; siz onaylamadan hiçbir şey değişmez."
            : "NitroDrop'un Wi-Fi sürücüsü şimdilik yalnızca Intel Wi-Fi 6 AX203 kartıyla çalışıyor ve bu bilgisayarda bu kart bulunamadı. Aynı ağdaki cihazlarla paylaşım yine çalışır. Kurulum gerektirmeyen USB Wi-Fi adaptörü desteği üzerinde çalışıyoruz.",
        WizardPage.Consent => "Hepsi geçicidir; Ayarlar → Wi-Fi Sürücüsü → \"Kaldır\" ile tek tıkla geri alırsınız.",
        WizardPage.BitLocker => _snap.BitLocker switch
        {
            BitLockerQuickState.On or BitLockerQuickState.Suspended or BitLockerQuickState.InProgress =>
                "Diskiniz BitLocker ile şifreli. Secure Boot ayarını değiştirmek, açılışta 48 haneli kurtarma anahtarının sorulmasına yol açabilir. Anahtar elinizde değilse dosyalarınıza ulaşamazsınız — devam etmeden önce yedekleyin.",
            BitLockerQuickState.Unknown =>
                "BitLocker durumu okunamadı. Emin olmak için ayrıntılı kontrol yapın (yönetici izni istenir).",
            _ => "Diskiniz BitLocker ile korunmuyor; bu adımda yapmanız gereken bir şey yok.",
        },
        WizardPage.SecureBoot => _snap.SecureBootOn switch
        {
            true => $"{_snap.Machine.Describe()} için adımlar aşağıda. \"BIOS'a yeniden başlat\"a bastığınızda bilgisayar doğrudan BIOS ayarlarını açar; adımları telefonla fotoğraflamanız işinizi kolaylaştırır. Windows açılınca kurulum kaldığı yerden devam eder.",
            false => "Bu adım tamam.",
            null => "Bilgisayar eski (Legacy/CSM) modda açılıyor olabilir; bu durumda Secure Boot zaten devre dışıdır.",
        },
        WizardPage.TestSigning => _snap.TestSigningActive ? "Windows kendi imzaladığımız sürücüleri yükleyebiliyor."
            : SetupFlow.TestSigningPendingReboot(_snap, _state)
                ? "Test modu açıldı; etkin olması için bilgisayarı yeniden başlatın. Windows açılınca kurulum kaldığı yerden sürer."
                : "Test modu, Windows'un bu bilgisayarda imzaladığımız sürücüyü yüklemesine izin verir. Bir kez yönetici izni istenir, ardından yeniden başlatılır.",
        WizardPage.Install => "Intel'in Wi-Fi firmware'i (linux-firmware deposundan) indirilip SHA-256 ile doğrulanır, sürücü yalnızca bu bilgisayara özel bir sertifikayla imzalanır ve Wi-Fi kartına kurulur.",
        WizardPage.Done => "Wi-Fi kartınız artık NitroDrop sürücüsünde. Yakındaki iPhone, iPad ve Mac'ler göründüğünde doğrudan paylaşabilirsiniz. Bu sürücü deneyseldir; sorun yaşarsanız tek tıkla geri alabilirsiniz.",
        WizardPage.Rollback => "NitroDrop sürücüsünü kaldırır ve her şeyi kurulumdan önceki haline döndürür. Bir kez yönetici izni istenir.",
        _ => !_rollbackOk ? "Ayrıntılar kurulum günlüğünde. Tekrar deneyebilir ya da Aygıt Yöneticisi'nden Intel sürücüsünü elle seçebilirsiniz."
            : _snap.SecureBootOn == true ? "Sürücü kaldırıldı, test modu kapatıldı. Filigranın kaybolması için bilgisayarı yeniden başlatın."
            : "Son adım: Secure Boot'u yeniden açın. \"BIOS'a yeniden başlat\"a basın ve aşağıdaki adımları izleyin.",
    };

    // ---------------- page data ----------------

    public bool IsSupported => _snap.Device is not null;
    public string CardText => _snap.Device is { } d ? $"{d.Card.Name} bulundu" : "Desteklenen Wi-Fi kartı yok";
    public string MachineText => _snap.Machine.Describe();

    public IReadOnlyList<ConsentItem> ConsentItems { get; } = new[]
    {
        new ConsentItem("", "Secure Boot kapanır",
            "Sürücümüz henüz Microsoft imzalı değil; BIOS'tan kapatmanız gerekiyor. Adım adım göstereceğiz."),
        new ConsentItem("", "Ekranda \"Test Modu\" yazısı",
            "Windows sağ alt köşede filigran gösterir. Zararsızdır; geri alınca kaybolur."),
        new ConsentItem("", "Bazı oyunlar açılmaz",
            "Valorant (Vanguard), FACEIT ve benzeri hile korumalı oyunlar çalışmaz. Oynamak için kurulumu geri alıp Secure Boot'u açın."),
        new ConsentItem("", "Koruma bir miktar azalır",
            "Secure Boot'un açılıştaki zararlı yazılım koruması devre dışı kalır. Windows Güvenliği çalışmaya devam eder."),
        new ConsentItem("", "Intel Wi-Fi kartı NitroDrop'a geçer",
            "Sürücü etkinken kart iPhone bağlantısına ayrılır; internete Ethernet, telefon paylaşımı ya da ikinci bir Wi-Fi adaptörüyle çıkılır."),
    };

    public bool ConsentChecked { get => _consentChecked; set { if (Set(ref _consentChecked, value)) RaiseFooter(); } }

    public bool BitLockerNeedsBackup => _snap.BitLocker is not (BitLockerQuickState.NotEncrypted or BitLockerQuickState.Off);
    public string BitLockerStateText => _snap.BitLocker switch
    {
        BitLockerQuickState.On => "BitLocker: açık",
        BitLockerQuickState.Suspended => "BitLocker: askıda (disk şifreli)",
        BitLockerQuickState.InProgress => "BitLocker: şifreleme sürüyor",
        BitLockerQuickState.Off => "BitLocker: kapalı",
        BitLockerQuickState.NotEncrypted => "BitLocker: kullanılmıyor",
        _ => "BitLocker: bilinmiyor",
    };
    public bool HasRecoveryDetails => _bitLockerDetails is { Recovery.Count: > 0 };
    public string RecoveryKeyIds => _bitLockerDetails is null ? ""
        : _bitLockerDetails.Recovery.Count == 0 ? "Bu diskte kurtarma parolası koruyucusu bulunamadı. Microsoft hesabınızı kontrol edin."
        : "Anahtar kimliği: " + string.Join(", ", _bitLockerDetails.Recovery.Select(k => k.Id.Trim('{', '}')));
    public bool HasRecoveryText => RecoveryKeyIds.Length > 0;

    public bool BackupConfirmed
    {
        get => _state.BitLockerBackupConfirmed;
        set { _state.BitLockerBackupConfirmed = value; SaveState(); Raise(); RaiseFooter(); }
    }

    public bool SuspendOnReboot
    {
        get => _state.SuspendBitLockerOnReboot;
        set { _state.SuspendBitLockerOnReboot = value; SaveState(); Raise(); }
    }

    public VendorGuide Guide => VendorGuides.Find(_snap.Machine);
    public IReadOnlyList<string> GuideSteps => (Page == WizardPage.RollbackDone ? Guide.EnableSteps : Guide.DisableSteps)
        .Select((s, i) => $"{i + 1}.  {s}").ToList();
    public string GuideKeys => $"BIOS kendiliğinden açılmazsa: {Guide.BiosKeys}.";
    public bool ShowGuide => (Page == WizardPage.SecureBoot && _snap.SecureBootOn == true)
                             || (Page == WizardPage.RollbackDone && _rollbackOk && _snap.SecureBootOn != true);
    public bool SecureBootStillOn => _snap.SecureBootOn == true;

    public bool FirmwareReady { get => _firmwareReady; private set { if (Set(ref _firmwareReady, value)) RaiseFooter(); } }
    public string LicenseText { get => _licenseText; private set => Set(ref _licenseText, value); }
    public bool LicenseAccepted { get => _licenseAccepted; set { if (Set(ref _licenseAccepted, value)) RaiseFooter(); } }
    public ObservableCollection<string> LogLines { get; } = new();

    public IReadOnlyList<string> RollbackItems => new[]
    {
        "NitroDrop Wi-Fi sürücüsü kaldırılır, Intel sürücüsü geri gelir",
        "Bu bilgisayara özel imza sertifikası silinir",
        "Windows test modu kapatılır (filigran yeniden başlatınca kaybolur)",
        "BitLocker koruması askıdaysa sürdürülür",
        "Secure Boot'u yeniden açmanız için adımları gösteririz",
    };

    // ---------------- footer ----------------

    public RelayCommand Primary { get; }
    public RelayCommand Secondary { get; }
    public RelayCommand CheckBitLocker { get; }
    public RelayCommand SaveRecoveryKey { get; }
    public RelayCommand PrintRecoveryKey { get; }
    public RelayCommand OpenMyRecoveryKey { get; }
    public RelayCommand Recheck { get; }
    public RelayCommand StartRollback { get; }
    public RelayCommand OpenLog { get; }

    public string PrimaryText => Page switch
    {
        WizardPage.Compatibility => IsSupported ? "Başla" : "Kapat",
        WizardPage.Consent => "Devam",
        WizardPage.BitLocker => "Devam",
        WizardPage.SecureBoot => _snap.SecureBootOn == true ? "BIOS'a yeniden başlat" : "Devam",
        WizardPage.TestSigning => _snap.TestSigningActive ? "Devam"
            : SetupFlow.TestSigningPendingReboot(_snap, _state) ? "Yeniden başlat" : "Test modunu aç",
        WizardPage.Install => FirmwareReady ? "Sürücüyü kur" : "Firmware'i indir",
        WizardPage.Done => "Bitti",
        WizardPage.Rollback => "Kaldır ve eski haline getir",
        _ => _rollbackOk && ShowGuide ? "BIOS'a yeniden başlat" : _rollbackOk ? "Yeniden başlat" : "Tekrar dene",
    };

    public bool PrimaryEnabled => !IsBusy && Page switch
    {
        WizardPage.Consent => ConsentChecked,
        WizardPage.BitLocker => !BitLockerNeedsBackup || BackupConfirmed,
        WizardPage.Install => !FirmwareReady || LicenseAccepted,
        _ => true,
    };

    public string SecondaryText => Page switch
    {
        WizardPage.Compatibility => "Vazgeç",
        WizardPage.Done => "",
        WizardPage.Rollback => "Vazgeç",
        WizardPage.RollbackDone => "Daha sonra",
        _ => "Geri",
    };
    public bool SecondaryVisible => SecondaryText.Length > 0 && (Page != WizardPage.Compatibility || IsSupported);
    public bool SecondaryEnabled => !IsBusy;

    // ---------------- lifecycle ----------------

    public async Task InitializeAsync()
    {
        IsBusy = true;
        Status = "Bilgisayarınız kontrol ediliyor…";
        try
        {
            await RefreshAsync();
            SetupLog.Write($"sihirbaz açıldı{(IsDryRun ? " (dry-run)" : "")}: kart={_snap.Device?.Card.Name ?? "yok"} " +
                           $"secureboot={_snap.SecureBootOn?.ToString() ?? "?"} testsigning={_snap.TestSigningActive} " +
                           $"bitlocker={_snap.BitLocker} bağlı={_snap.OurDriverBound} makine={_snap.Machine.Describe()}");
            if (!_rollbackMode) Page = PageFor(SetupFlow.Start(_snap, _state));
            ResumeRegistration.Unregister();
        }
        finally
        {
            IsBusy = false;
            Status = "";
            RaiseAll();
        }
    }

    /// <summary>Snapshots/previews: show a page with given data, no detection, no persistence.</summary>
    internal void Preview(WizardPage page, SystemSnapshot snap, BitLockerInfo? details = null, bool firmwareReady = false,
        string license = "", bool rollbackOk = true)
    {
        _snap = snap;
        _bitLockerDetails = details;
        _firmwareReady = firmwareReady;
        _licenseText = license;
        _rollbackOk = rollbackOk;
        _page = page;
        UpdateDots();
        RaiseAll();
    }

    public void Detach() => SetupLog.Line -= OnLogLine;

    private async Task RefreshAsync()
    {
        _snap = await Task.Run(() => _probe.Read(includeBitLocker: true));
        UpdateDots();
        RaiseAll();
    }

    private static WizardPage PageFor(SetupStep s) => s switch
    {
        SetupStep.Compatibility => WizardPage.Compatibility,
        SetupStep.Consent => WizardPage.Consent,
        SetupStep.BitLocker => WizardPage.BitLocker,
        SetupStep.SecureBoot => WizardPage.SecureBoot,
        SetupStep.TestSigning => WizardPage.TestSigning,
        SetupStep.Install => WizardPage.Install,
        _ => WizardPage.Done,
    };

    private static string StepTitle(SetupStep s) => s switch
    {
        SetupStep.Compatibility => "Uyumluluk",
        SetupStep.Consent => "Onay",
        SetupStep.BitLocker => "BitLocker",
        SetupStep.SecureBoot => "Secure Boot",
        SetupStep.TestSigning => "Test modu",
        _ => "Kurulum",
    };

    private void GoNextIncomplete() => Page = PageFor(SetupFlow.Next(_snap, _state));

    private async void Run(Func<Task> action)
    {
        if (IsBusy) return;
        Error = "";
        IsBusy = true;
        try { await action(); }
        catch (Exception ex)
        {
            SetupLog.Write($"hata: {ex}");
            Status = "";
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
            RaiseFooter();
        }
    }

    private Task PrimaryAsync() => Page switch
    {
        WizardPage.Compatibility => StartAsync(),
        WizardPage.Consent => ConsentAsync(),
        WizardPage.BitLocker => NextAsync(),
        WizardPage.SecureBoot => _snap.SecureBootOn == true ? RebootToFirmwareAsync() : NextAsync(),
        WizardPage.TestSigning => _snap.TestSigningActive ? NextAsync()
            : SetupFlow.TestSigningPendingReboot(_snap, _state) ? RebootAsync() : TestSigningAsync(),
        WizardPage.Install => FirmwareReady ? InstallAsync() : PrepareFirmwareAsync(),
        WizardPage.Done => CloseAsync(),
        WizardPage.Rollback => RollbackAsync(),
        _ => _rollbackOk && ShowGuide ? RebootToFirmwareAsync() : _rollbackOk ? RebootAsync() : RollbackAsync(),
    };

    private Task SecondaryAsync()
    {
        switch (Page)
        {
            case WizardPage.Compatibility:
            case WizardPage.Rollback:
            case WizardPage.RollbackDone:
                CloseWindow?.Invoke();
                break;
            default:
                int i = Array.IndexOf(SetupFlow.Steps, StepOf(Page));
                Page = i > 0 ? PageFor(SetupFlow.Steps[i - 1]) : WizardPage.Compatibility;
                break;
        }
        return Task.CompletedTask;
    }

    private Task StartAsync()
    {
        if (IsSupported) Page = _state.ConsentGiven ? PageFor(SetupFlow.Next(_snap, _state)) : WizardPage.Consent;
        else CloseWindow?.Invoke();
        return Task.CompletedTask;
    }

    private Task CloseAsync()
    {
        CloseWindow?.Invoke();
        return Task.CompletedTask;
    }

    private static SetupStep StepOf(WizardPage p) => p switch
    {
        WizardPage.Consent => SetupStep.Consent,
        WizardPage.BitLocker => SetupStep.BitLocker,
        WizardPage.SecureBoot => SetupStep.SecureBoot,
        WizardPage.TestSigning => SetupStep.TestSigning,
        WizardPage.Install => SetupStep.Install,
        WizardPage.Done => SetupStep.Done,
        _ => SetupStep.Compatibility,
    };

    private async Task NextAsync()
    {
        await RefreshAsync();
        GoNextIncomplete();
    }

    // ---------------- steps ----------------

    private async Task ConsentAsync()
    {
        _state.ConsentGiven = true;
        _state.ConsentAtUtc = DateTime.UtcNow;
        SaveState();
        SetupLog.Write("kullanıcı değişiklikleri onayladı");
        await NextAsync();
    }

    private async Task CheckBitLockerAsync()
    {
        Status = "BitLocker ayrıntıları okunuyor (yönetici izni)…";
        var r = await ElevatedHelper.RunAsync(new HelperRequest { Action = HelperAction.BitLockerStatus, Drive = SystemDrive });
        Status = "";
        if (r.Cancelled || !r.Ok) { Error = r.Message; return; }
        if (r.BitLocker is null) { Status = r.Message; return; }
        _bitLockerDetails = r.BitLocker;
        _snap = _snap with { BitLocker = r.BitLocker.ToQuick() };
        RaiseAll();
    }

    private void SaveKey(bool print)
    {
        if (_bitLockerDetails is not { Recovery.Count: > 0 } d) return;
        var path = PickSaveFile?.Invoke($"BitLocker Kurtarma Anahtarı {Environment.MachineName}.txt");
        if (path is null) return;
        try
        {
            File.WriteAllText(path, d.FormatBackup(Environment.MachineName, DateTime.Now));
            SetupLog.Write("kurtarma anahtarı kullanıcının seçtiği dosyaya kaydedildi (anahtar günlüğe yazılmaz)");
            Status = $"Kaydedildi: {path}. Bu dosyayı bilgisayarın dışında (USB bellek, telefon) saklayın.";
            if (print) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "print" });
        }
        catch (Exception ex) { Error = "Kaydedilemedi: " + ex.Message; }
    }

    private async Task RebootToFirmwareAsync()
    {
        if (Confirm?.Invoke("BIOS'a yeniden başlatılsın mı?",
                "Bilgisayar 5 saniye içinde yeniden başlayıp BIOS ayarlarını açacak. Açık dosyalarınızı kaydedin. " +
                "BIOS'ta işiniz bitince kaydedip çıkın; Windows açılınca NitroDrop kurulumu kaldığı yerden sürdürür.",
                "Yeniden başlat") != true)
            return;
        bool suspend = _state.SuspendBitLockerOnReboot && _snap.BitLocker is BitLockerQuickState.On or BitLockerQuickState.Unknown;
        SaveState();
        ResumeRegistration.Register();
        Status = "Yeniden başlatılıyor…";
        var r = await ElevatedHelper.RunAsync(new HelperRequest { Action = HelperAction.RebootToFirmware, SuspendBitLocker = suspend, Drive = SystemDrive });
        if (r.BitLockerSuspended) _state.BitLockerSuspendedByUs = true;
        if (r.Ok) _state.FirmwareRebootRequested = true;
        SaveState();
        if (!r.Ok) { ResumeRegistration.Unregister(); Status = ""; Error = r.Message; return; }
        Status = r.DryRun ? "[dry-run] Gerçekte bilgisayar şimdi BIOS'a yeniden başlardı." : r.Message;
    }

    private async Task TestSigningAsync()
    {
        Status = "Test modu açılıyor (yönetici izni)…";
        bool suspend = _state.SuspendBitLockerOnReboot && _snap.BitLocker is BitLockerQuickState.On or BitLockerQuickState.Unknown;
        var r = await ElevatedHelper.RunAsync(new HelperRequest { Action = HelperAction.TestSigningOn, SuspendBitLocker = suspend, Drive = SystemDrive });
        Status = "";
        if (r.BitLockerSuspended) _state.BitLockerSuspendedByUs = true;
        if (!r.Ok) { SaveState(); Error = r.Message; return; }
        _state.TestSigningEnabledByUs = true;
        _state.TestSigningSetUtc = DateTime.UtcNow;
        SaveState();
        Status = r.DryRun ? "[dry-run] bcdedit çalıştırılmadı. Gerçekte şimdi yeniden başlatmanız istenirdi." : r.Message;
        RaiseAll();
    }

    private Task RebootAsync()
    {
        if (Confirm?.Invoke("Yeniden başlatılsın mı?",
                "Bilgisayar 5 saniye içinde yeniden başlayacak. Açık dosyalarınızı kaydedin.", "Yeniden başlat") != true)
            return Task.CompletedTask;
        SaveState();
        if (Page != WizardPage.RollbackDone) ResumeRegistration.Register();
        ICommandRunner runner = IsDryRun ? new DryRunCommandRunner() : new ProcessCommandRunner();
        var res = runner.Run(SetupCommands.Reboot(5));   // a plain restart needs no admin
        if (res.ExitCode != 0) { ResumeRegistration.Unregister(); Error = "Yeniden başlatılamadı. Başlat menüsünden yeniden başlatın."; }
        else Status = res.DryRun ? "[dry-run] Gerçekte bilgisayar şimdi yeniden başlardı." : "Yeniden başlatılıyor…";
        return Task.CompletedTask;
    }

    private async Task PrepareFirmwareAsync()
    {
        var dir = SetupPaths.PackageCache;
        Directory.CreateDirectory(dir);
        foreach (var f in DriverPackage.BundledFiles)
        {
            var src = Path.Combine(SetupPaths.BundledDriverDir, f);
            if (!File.Exists(src))
                throw new InvalidOperationException($"Sürücü dosyası bu NitroDrop sürümünde yok: {f}. (Geliştirici derlemesi: driver\\build.ps1 ve driver\\package.ps1 çalıştırın.)");
            File.Copy(src, Path.Combine(dir, f), overwrite: true);
        }
        var fetcher = new FirmwareFetcher(Http);
        var progress = new Progress<string>(s => Status = s);
        var local = LocalFirmwareDirs().ToList();
        var lic = await fetcher.EnsureAsync(FirmwareSpec.License, dir, local, progress);
        await fetcher.EnsureAsync(FirmwareSpec.Firmware, dir, local, progress);
        LicenseText = File.ReadAllText(lic);
        Status = "Firmware indirildi ve doğrulandı. Lisansı okuyup kabul edin.";
        FirmwareReady = true;
        RaiseAll();
    }

    /// <summary>Developer builds: reuse a verified copy from the repo's driver\firmware instead of downloading.</summary>
    private static IEnumerable<string> LocalFirmwareDirs()
    {
        yield return SetupPaths.BundledDriverDir;
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && d is not null; i++, d = d.Parent)
        {
            var fw = Path.Combine(d.FullName, "driver", "firmware");
            if (Directory.Exists(fw)) { yield return fw; yield break; }
        }
    }

    private async Task InstallAsync()
    {
        if (_snap.Device is not { } dev) { Error = "Wi-Fi kartı bulunamadı."; return; }
        var dir = SetupPaths.PackageCache;
        Status = "Paket doğrulanıyor…";
        var bad = await Task.Run(() => CodeSigner.VerifyPackageAgainstCatalog(dir));
        if (bad.Count > 0) { Status = ""; Error = "Sürücü paketi kataloğuyla uyuşmuyor: " + string.Join(", ", bad); return; }
        _state.FirmwareLicenseAccepted = true;
        SaveState();
        LogLines.Clear();
        Status = "Sürücü imzalanıp kuruluyor (yönetici izni)…";
        var r = await ElevatedHelper.RunAsync(new HelperRequest { Action = HelperAction.InstallDriver, PackageDir = dir, HardwareId = dev.Card.HardwareId });
        Status = "";
        if (r.CertThumbprint is { } t && !r.DryRun) _state.CertThumbprint = t;
        if (r.PublishedInfs.Count > 0) _state.PublishedInfs = r.PublishedInfs;
        if (r.Ok)
        {
            _state.DriverInstalled = !r.DryRun;
            _state.InstalledUtc = DateTime.UtcNow;
        }
        SaveState();
        if (!r.Ok) { Error = r.Message; return; }
        await RefreshAsync();
        Page = WizardPage.Done;
        Status = r.DryRun ? "[dry-run] Hiçbir şey kurulmadı; komutlar günlüğe yazıldı." : r.Message;
    }

    private async Task RollbackAsync()
    {
        if (Confirm?.Invoke("Eski haline getirilsin mi?",
                "NitroDrop sürücüsü kaldırılacak, Intel sürücüsü geri gelecek, imza sertifikası silinecek ve test modu kapatılacak.",
                "Kaldır") != true)
            return;
        Status = "Kaldırılıyor (yönetici izni)…";
        var r = await ElevatedHelper.RunAsync(new HelperRequest
        {
            Action = HelperAction.UninstallDriver,
            CertThumbprint = _state.CertThumbprint,
            DisableTestSigning = true,
            ResumeBitLocker = _state.BitLockerSuspendedByUs || _snap.BitLocker == BitLockerQuickState.Suspended,
            Drive = SystemDrive,
        });
        Status = "";
        if (r.Cancelled) { Error = r.Message; return; }
        _rollbackOk = r.Ok;
        if (r.Ok && !r.DryRun)
        {
            _state.ResetAfterRollback();
            _state.ConsentGiven = false;
            _state.BitLockerBackupConfirmed = false;
            SaveState();
        }
        await RefreshAsync();
        Page = WizardPage.RollbackDone;
        if (!r.Ok) Error = r.Message;
        else Status = r.DryRun ? "[dry-run] Hiçbir şey kaldırılmadı; komutlar günlüğe yazıldı." : r.Message;
    }

    // ---------------- helpers ----------------

    private static string SystemDrive => Environment.GetEnvironmentVariable("SystemDrive") is { Length: 2 } d ? d : "C:";

    private void SaveState()
    {
        if (_statePath is null) return;
        try { _state.Save(_statePath); }
        catch (Exception ex) { SetupLog.Write($"setup.json yazılamadı: {ex.Message}"); }
    }

    private void OnLogLine(string line)
    {
        if (!IsBusy || Page != WizardPage.Install) return;
        var app = Application.Current;
        if (app is null) return;
        app.Dispatcher.BeginInvoke(() =>
        {
            LogLines.Add(line.Length > 19 ? line[20..] : line);
            while (LogLines.Count > 6) LogLines.RemoveAt(0);
        });
    }

    private void UpdateDots()
    {
        var current = StepOf(Page);
        int ci = Array.IndexOf(SetupFlow.Steps, current);
        for (int i = 0; i < Dots.Count; i++)
        {
            var step = SetupFlow.Steps[i];
            Dots[i].IsCurrent = i == ci && Page != WizardPage.Done;
            Dots[i].IsDone = Page == WizardPage.Done || (i != ci && (i < ci || SetupFlow.IsSatisfied(step, _snap, _state)));
        }
    }

    private void RaiseFooter()
    {
        Raise(nameof(PrimaryText)); Raise(nameof(PrimaryEnabled));
        Raise(nameof(SecondaryText)); Raise(nameof(SecondaryVisible)); Raise(nameof(SecondaryEnabled));
    }

    private void RaiseAll()
    {
        UpdateDots();
        foreach (var n in new[]
                 {
                     nameof(IsCompatibility), nameof(IsConsent), nameof(IsBitLocker), nameof(IsSecureBoot), nameof(IsTestSigning),
                     nameof(IsInstall), nameof(IsDone), nameof(IsRollback), nameof(IsRollbackDone), nameof(ShowDots),
                     nameof(Glyph), nameof(GlyphPositive), nameof(Title), nameof(Subtitle), nameof(IsSupported), nameof(CardText),
                     nameof(MachineText), nameof(BitLockerNeedsBackup), nameof(BitLockerStateText), nameof(HasRecoveryDetails),
                     nameof(RecoveryKeyIds), nameof(HasRecoveryText), nameof(BackupConfirmed), nameof(SuspendOnReboot),
                     nameof(Guide), nameof(GuideSteps), nameof(GuideKeys), nameof(ShowGuide), nameof(SecureBootStillOn),
                     nameof(FirmwareReady), nameof(LicenseText), nameof(LicenseAccepted), nameof(ConsentChecked),
                 })
            Raise(n);
        RaiseFooter();
    }

    private static void Shell(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { SetupLog.Write($"açılamadı: {target}: {ex.Message}"); }
    }
}
