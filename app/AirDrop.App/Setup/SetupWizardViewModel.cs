using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using AirDrop.App.Localization;
using AirDrop.App.ViewModels;

namespace AirDrop.App.Setup;

public enum WizardPage { Compatibility, Consent, BitLocker, SecureBoot, TestSigning, Install, Done, Rollback, RollbackDone }

public sealed class StepDot(Func<string> title) : Observable
{
    private bool _done, _current;
    public string Title => title();
    public bool IsDone { get => _done; set => Set(ref _done, value); }
    public bool IsCurrent { get => _current; set => Set(ref _current, value); }
}

public sealed record ConsentItem(string Glyph, string Title, string Body);

/// <summary>
/// "Advanced Setup" wizard. Detection on open, one page per step, every system change behind an explicit
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

        Dots = new ObservableCollection<StepDot>(SetupFlow.Steps.Select(s => new StepDot(() => StepTitle(s))));
        Primary = new RelayCommand(_ => Run(PrimaryAsync));
        Secondary = new RelayCommand(_ => Run(SecondaryAsync));
        CheckBitLocker = new RelayCommand(_ => Run(CheckBitLockerAsync));
        SaveRecoveryKey = new RelayCommand(_ => SaveKey(print: false));
        PrintRecoveryKey = new RelayCommand(_ => SaveKey(print: true));
        OpenMyRecoveryKey = new RelayCommand(_ => Shell("https://aka.ms/myrecoverykey"));
        Recheck = new RelayCommand(_ => Run(async () => { await RefreshAsync(); GoNextIncomplete(); }));
        StartRollback = new RelayCommand(_ => Page = WizardPage.Rollback);
        OpenLog = new RelayCommand(_ => Shell(SetupPaths.LogFile));
        PickDriver = new RelayCommand(_ => ImportDriver());
        _page = rollbackMode ? WizardPage.Rollback : WizardPage.Compatibility;
        SetupLog.Line += OnLogLine;
    }

    // ---------------- view hooks ----------------

    /// <summary>(title, body, ok button text) → confirmed.</summary>
    public Func<string, string, string, bool>? Confirm { get; set; }
    /// <summary>Suggested file name → chosen path (or null).</summary>
    public Func<string, string?>? PickSaveFile { get; set; }
    /// <summary>Open dialog for the driver package (zip or airdropwifi.inf) → chosen path (or null).</summary>
    public Func<string?>? PickDriverPackage { get; set; }
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
            SetupLog.Write($"page: {value}");
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
        WizardPage.Compatibility => IsSupported ? SetupStrings.Setup_Title_Compat : SetupStrings.Setup_Title_Unsupported,
        WizardPage.Consent => SetupStrings.Setup_Title_Consent,
        WizardPage.BitLocker => BitLockerNeedsBackup ? SetupStrings.Setup_Title_BitLockerBackup : "BitLocker",
        WizardPage.SecureBoot => _snap.SecureBootOn switch
        {
            true => SetupStrings.Setup_Title_SecureBootOn,
            false => SetupStrings.Setup_Title_SecureBootOff,
            null => SetupStrings.Setup_Title_SecureBootNone,
        },
        WizardPage.TestSigning => _snap.TestSigningActive ? SetupStrings.Setup_Title_TestModeOn
            : SetupFlow.TestSigningPendingReboot(_snap, _state) ? SetupStrings.Setup_Title_RebootNeeded
            : SetupStrings.Setup_Title_TestModeEnable,
        WizardPage.Install => SetupStrings.Setup_Title_Install,
        WizardPage.Done => SetupStrings.Setup_Title_Done,
        WizardPage.Rollback => SetupStrings.Setup_Title_Rollback,
        _ => !_rollbackOk ? SetupStrings.Setup_Title_RollbackFailed
            : _snap.SecureBootOn == true ? SetupStrings.Setup_Title_RollbackRestored : SetupStrings.Setup_Title_RollbackAlmost,
    };

    public string Subtitle => Page switch
    {
        WizardPage.Compatibility => IsSupported ? SetupStrings.Setup_Sub_Compat : SetupStrings.Setup_Sub_Unsupported,
        WizardPage.Consent => SetupStrings.Setup_Sub_Consent,
        WizardPage.BitLocker => _snap.BitLocker switch
        {
            BitLockerQuickState.On or BitLockerQuickState.Suspended or BitLockerQuickState.InProgress => SetupStrings.Setup_Sub_BitLockerOn,
            BitLockerQuickState.Unknown => SetupStrings.Setup_Sub_BitLockerUnknown,
            _ => SetupStrings.Setup_Sub_BitLockerNone,
        },
        WizardPage.SecureBoot => _snap.SecureBootOn switch
        {
            true => SetupStrings.Setup_Sub_SecureBootOn(_snap.Machine.Describe()),
            false => SetupStrings.Setup_Sub_SecureBootOff,
            null => SetupStrings.Setup_Sub_SecureBootNone,
        },
        WizardPage.TestSigning => _snap.TestSigningActive ? SetupStrings.Setup_Sub_TestModeOn
            : SetupFlow.TestSigningPendingReboot(_snap, _state) ? SetupStrings.Setup_Sub_TestModePending
            : SetupStrings.Setup_Sub_TestModeEnable,
        WizardPage.Install => SetupStrings.Setup_Sub_Install,
        WizardPage.Done => SetupStrings.Setup_Sub_Done,
        WizardPage.Rollback => SetupStrings.Setup_Sub_Rollback,
        _ => !_rollbackOk ? SetupStrings.Setup_Sub_RollbackFailed
            : _snap.SecureBootOn == true ? SetupStrings.Setup_Sub_RollbackRestored
            : SetupStrings.Setup_Sub_RollbackAlmost,
    };

    // ---------------- page data ----------------

    public bool IsSupported => _snap.Device is not null;
    public string CardText => _snap.Device is { } d ? SetupStrings.Setup_CardFound(d.Card.Name) : SetupStrings.Setup_NoCard;
    public string MachineText => _snap.Machine.Describe();

    public IReadOnlyList<ConsentItem> ConsentItems => new[]
    {
        new ConsentItem("", SetupStrings.Setup_Consent1_Title, SetupStrings.Setup_Consent1_Body),
        new ConsentItem("", SetupStrings.Setup_Consent2_Title, SetupStrings.Setup_Consent2_Body),
        new ConsentItem("", SetupStrings.Setup_Consent3_Title, SetupStrings.Setup_Consent3_Body),
        new ConsentItem("", SetupStrings.Setup_Consent4_Title, SetupStrings.Setup_Consent4_Body),
        new ConsentItem("", SetupStrings.Setup_Consent5_Title, SetupStrings.Setup_Consent5_Body),
    };

    public bool ConsentChecked { get => _consentChecked; set { if (Set(ref _consentChecked, value)) RaiseFooter(); } }

    public bool BitLockerNeedsBackup => _snap.BitLocker is not (BitLockerQuickState.NotEncrypted or BitLockerQuickState.Off);
    public string BitLockerStateText => _snap.BitLocker switch
    {
        BitLockerQuickState.On => SetupStrings.Setup_BitLocker_On,
        BitLockerQuickState.Suspended => SetupStrings.Setup_BitLocker_Suspended,
        BitLockerQuickState.InProgress => SetupStrings.Setup_BitLocker_InProgress,
        BitLockerQuickState.Off => SetupStrings.Setup_BitLocker_Off,
        BitLockerQuickState.NotEncrypted => SetupStrings.Setup_BitLocker_NotUsed,
        _ => SetupStrings.Setup_BitLocker_Unknown,
    };
    public bool HasRecoveryDetails => _bitLockerDetails is { Recovery.Count: > 0 };
    public string RecoveryKeyIds => _bitLockerDetails is null ? ""
        : _bitLockerDetails.Recovery.Count == 0 ? SetupStrings.Setup_NoRecoveryProtector
        : SetupStrings.Setup_KeyId(string.Join(", ", _bitLockerDetails.Recovery.Select(k => k.Id.Trim('{', '}'))));
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
    public string GuideKeys => SetupStrings.Setup_GuideKeys(Guide.BiosKeys);
    public bool ShowGuide => (Page == WizardPage.SecureBoot && _snap.SecureBootOn == true)
                             || (Page == WizardPage.RollbackDone && _rollbackOk && _snap.SecureBootOn != true);
    public bool SecureBootStillOn => _snap.SecureBootOn == true;

    public bool FirmwareReady { get => _firmwareReady; private set { if (Set(ref _firmwareReady, value)) RaiseFooter(); } }
    public string LicenseText { get => _licenseText; private set => Set(ref _licenseText, value); }
    public bool LicenseAccepted { get => _licenseAccepted; set { if (Set(ref _licenseAccepted, value)) RaiseFooter(); } }
    public ObservableCollection<string> LogLines { get; } = new();

    /// <summary>Folder with inf/sys/cat (next to the app in developer builds, else the imported driver zip), or null.</summary>
    public string? DriverDir => _driverDirOverride is null ? DriverSource.Find() : _driverDirOverride.Length == 0 ? null : _driverDirOverride;
    private string? _driverDirOverride;   // previews: null = detect, "" = no driver package
    public bool DriverAvailable => DriverDir is not null;
    /// <summary>Install page, before the firmware step: the driver zip still has to be picked.</summary>
    public bool ShowDriverPicker => Page == WizardPage.Install && !FirmwareReady;
    public string DriverStateText => DriverDir is { } d ? SetupStrings.Setup_DriverPkgFound(d) : SetupStrings.Setup_DriverPkgMissing;

    public IReadOnlyList<string> RollbackItems => new[]
    {
        SetupStrings.Setup_RollbackItem1,
        SetupStrings.Setup_RollbackItem2,
        SetupStrings.Setup_RollbackItem3,
        SetupStrings.Setup_RollbackItem4,
        SetupStrings.Setup_RollbackItem5,
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
    public RelayCommand PickDriver { get; }

    public string PrimaryText => Page switch
    {
        WizardPage.Compatibility => IsSupported ? SetupStrings.Setup_Btn_Start : CommonStrings.Common_Close,
        WizardPage.Consent => SetupStrings.Setup_Btn_Continue,
        WizardPage.BitLocker => SetupStrings.Setup_Btn_Continue,
        WizardPage.SecureBoot => _snap.SecureBootOn == true ? SetupStrings.Setup_Btn_RebootToBios : SetupStrings.Setup_Btn_Continue,
        WizardPage.TestSigning => _snap.TestSigningActive ? SetupStrings.Setup_Btn_Continue
            : SetupFlow.TestSigningPendingReboot(_snap, _state) ? SetupStrings.Setup_Btn_Restart : SetupStrings.Setup_Btn_EnableTestMode,
        WizardPage.Install => FirmwareReady ? SetupStrings.Setup_Btn_InstallDriver : SetupStrings.Setup_Btn_DownloadFirmware,
        WizardPage.Done => CommonStrings.Common_Done,
        WizardPage.Rollback => SetupStrings.Setup_Title_Rollback,
        _ => _rollbackOk && ShowGuide ? SetupStrings.Setup_Btn_RebootToBios
            : _rollbackOk ? SetupStrings.Setup_Btn_Restart : SetupStrings.Setup_Btn_Retry,
    };

    public bool PrimaryEnabled => !IsBusy && Page switch
    {
        WizardPage.Consent => ConsentChecked,
        WizardPage.BitLocker => !BitLockerNeedsBackup || BackupConfirmed,
        WizardPage.Install => FirmwareReady ? LicenseAccepted : DriverAvailable,
        _ => true,
    };

    public string SecondaryText => Page switch
    {
        WizardPage.Compatibility => CommonStrings.Common_Cancel,
        WizardPage.Done => "",
        WizardPage.Rollback => CommonStrings.Common_Cancel,
        WizardPage.RollbackDone => SetupStrings.Setup_Btn_Later,
        _ => CommonStrings.Common_Back,
    };
    public bool SecondaryVisible => SecondaryText.Length > 0 && (Page != WizardPage.Compatibility || IsSupported);
    public bool SecondaryEnabled => !IsBusy;

    // ---------------- lifecycle ----------------

    public async Task InitializeAsync()
    {
        IsBusy = true;
        Status = SetupStrings.Setup_Status_Checking;
        try
        {
            await RefreshAsync();
            SetupLog.Write($"wizard opened{(IsDryRun ? " (dry-run)" : "")}: card={_snap.Device?.Card.Name ?? "none"} " +
                           $"secureboot={_snap.SecureBootOn?.ToString() ?? "?"} testsigning={_snap.TestSigningActive} " +
                           $"bitlocker={_snap.BitLocker} bound={_snap.OurDriverBound} machine={_snap.Machine.Describe()}");
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
        string license = "", bool rollbackOk = true, string? driverDir = null)
    {
        _driverDirOverride = driverDir;
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
        SetupStep.Compatibility => SetupStrings.Setup_Step_Compat,
        SetupStep.Consent => SetupStrings.Setup_Step_Consent,
        SetupStep.BitLocker => "BitLocker",
        SetupStep.SecureBoot => "Secure Boot",
        SetupStep.TestSigning => SetupStrings.Setup_Step_TestMode,
        _ => SetupStrings.Setup_Step_Install,
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
            SetupLog.Write($"error: {ex}");
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
        SetupLog.Write("user accepted the changes");
        await NextAsync();
    }

    private async Task CheckBitLockerAsync()
    {
        Status = SetupStrings.Setup_Status_ReadingBitLocker;
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
        var path = PickSaveFile?.Invoke(SetupStrings.Setup_RecoveryFileName(Environment.MachineName));
        if (path is null) return;
        try
        {
            File.WriteAllText(path, d.FormatBackup(Environment.MachineName, DateTime.Now));
            SetupLog.Write("recovery key saved to the user's chosen file (the key is never logged)");
            Status = SetupStrings.Setup_Status_Saved(path);
            if (print) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "print" });
        }
        catch (Exception ex) { Error = SetupStrings.Setup_Error_SaveFailed(ex.Message); }
    }

    private async Task RebootToFirmwareAsync()
    {
        if (Confirm?.Invoke(SetupStrings.Setup_ConfirmBiosTitle, SetupStrings.Setup_ConfirmBiosBody,
                SetupStrings.Setup_Btn_Restart) != true)
            return;
        bool suspend = _state.SuspendBitLockerOnReboot && _snap.BitLocker is BitLockerQuickState.On or BitLockerQuickState.Unknown;
        SaveState();
        ResumeRegistration.Register();
        Status = SetupStrings.Setup_Status_Restarting;
        var r = await ElevatedHelper.RunAsync(new HelperRequest { Action = HelperAction.RebootToFirmware, SuspendBitLocker = suspend, Drive = SystemDrive });
        if (r.BitLockerSuspended) _state.BitLockerSuspendedByUs = true;
        if (r.Ok) _state.FirmwareRebootRequested = true;
        SaveState();
        if (!r.Ok) { ResumeRegistration.Unregister(); Status = ""; Error = r.Message; return; }
        Status = r.DryRun ? SetupStrings.Setup_DryRun_Bios : r.Message;
    }

    private async Task TestSigningAsync()
    {
        Status = SetupStrings.Setup_Status_EnablingTestMode;
        bool suspend = _state.SuspendBitLockerOnReboot && _snap.BitLocker is BitLockerQuickState.On or BitLockerQuickState.Unknown;
        var r = await ElevatedHelper.RunAsync(new HelperRequest { Action = HelperAction.TestSigningOn, SuspendBitLocker = suspend, Drive = SystemDrive });
        Status = "";
        if (r.BitLockerSuspended) _state.BitLockerSuspendedByUs = true;
        if (!r.Ok) { SaveState(); Error = r.Message; return; }
        _state.TestSigningEnabledByUs = true;
        _state.TestSigningSetUtc = DateTime.UtcNow;
        SaveState();
        Status = r.DryRun ? SetupStrings.Setup_DryRun_Bcdedit : r.Message;
        RaiseAll();
    }

    private Task RebootAsync()
    {
        if (Confirm?.Invoke(SetupStrings.Setup_ConfirmRestartTitle, SetupStrings.Setup_ConfirmRestartBody,
                SetupStrings.Setup_Btn_Restart) != true)
            return Task.CompletedTask;
        SaveState();
        if (Page != WizardPage.RollbackDone) ResumeRegistration.Register();
        ICommandRunner runner = IsDryRun ? new DryRunCommandRunner() : new ProcessCommandRunner();
        var res = runner.Run(SetupCommands.Reboot(5));   // a plain restart needs no admin
        if (res.ExitCode != 0) { ResumeRegistration.Unregister(); Error = SetupStrings.Setup_Error_RestartFailed; }
        else Status = res.DryRun ? SetupStrings.Setup_DryRun_Restart : SetupStrings.Setup_Status_Restarting;
        return Task.CompletedTask;
    }

    /// <summary>"Sürücü paketini seç…": the separately downloaded NitroDrop-driver-&lt;ver&gt;.zip (or its extracted inf).</summary>
    private void ImportDriver()
    {
        if (PickDriverPackage?.Invoke() is not { } path) return;
        Error = "";
        try
        {
            var missing = DriverSource.Import(path);
            if (missing.Count > 0) Error = SetupStrings.Setup_Error_DriverFileMissing(string.Join(", ", missing));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            Error = SetupStrings.Setup_Error_DriverImport(ex.Message);
        }
        RaiseAll();
    }

    private async Task PrepareFirmwareAsync()
    {
        var dir = SetupPaths.PackageCache;
        var driverDir = DriverDir ?? throw new InvalidOperationException(SetupStrings.Setup_Error_DriverFileMissing(DriverPackage.SysName));
        Directory.CreateDirectory(dir);
        foreach (var f in DriverPackage.BundledFiles)
        {
            var src = Path.Combine(driverDir, f);
            if (!File.Exists(src))
                throw new InvalidOperationException(SetupStrings.Setup_Error_DriverFileMissing(f));
            File.Copy(src, Path.Combine(dir, f), overwrite: true);
        }
        var fetcher = new FirmwareFetcher(Http);
        var progress = new Progress<string>(s => Status = s);
        var local = LocalFirmwareDirs().ToList();
        var lic = await fetcher.EnsureAsync(FirmwareSpec.License, dir, local, progress);
        await fetcher.EnsureAsync(FirmwareSpec.Firmware, dir, local, progress);
        LicenseText = File.ReadAllText(lic);
        Status = SetupStrings.Setup_Status_FirmwareReady;
        FirmwareReady = true;
        RaiseAll();
    }

    /// <summary>Developer builds: reuse a verified copy from the repo's driver\firmware instead of downloading.</summary>
    private static IEnumerable<string> LocalFirmwareDirs()
    {
        foreach (var d0 in DriverSource.Candidates()) yield return d0;
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && d is not null; i++, d = d.Parent)
        {
            var fw = Path.Combine(d.FullName, "driver", "firmware");
            if (Directory.Exists(fw)) { yield return fw; yield break; }
        }
    }

    private async Task InstallAsync()
    {
        if (_snap.Device is not { } dev) { Error = SetupStrings.Setup_Error_NoCard; return; }
        var dir = SetupPaths.PackageCache;
        Status = SetupStrings.Setup_Status_Verifying;
        var bad = await Task.Run(() => CodeSigner.VerifyPackageAgainstCatalog(dir));
        if (bad.Count > 0) { Status = ""; Error = SetupStrings.Setup_Error_CatalogMismatch(string.Join(", ", bad)); return; }
        _state.FirmwareLicenseAccepted = true;
        SaveState();
        LogLines.Clear();
        Status = SetupStrings.Setup_Status_Installing;
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
        Status = r.DryRun ? SetupStrings.Setup_DryRun_Install : r.Message;
    }

    private async Task RollbackAsync()
    {
        if (Confirm?.Invoke(SetupStrings.Setup_ConfirmRollbackTitle, SetupStrings.Setup_ConfirmRollbackBody,
                SetupStrings.Setup_Btn_Uninstall) != true)
            return;
        Status = SetupStrings.Setup_Status_Removing;
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
        else Status = r.DryRun ? SetupStrings.Setup_DryRun_Rollback : r.Message;
    }

    // ---------------- helpers ----------------

    private static string SystemDrive => Environment.GetEnvironmentVariable("SystemDrive") is { Length: 2 } d ? d : "C:";

    private void SaveState()
    {
        if (_statePath is null) return;
        try { _state.Save(_statePath); }
        catch (Exception ex) { SetupLog.Write($"couldn't write setup.json: {ex.Message}"); }
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
                     nameof(DriverDir), nameof(DriverAvailable), nameof(ShowDriverPicker), nameof(DriverStateText),
                 })
            Raise(n);
        RaiseFooter();
    }

    private static void Shell(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { SetupLog.Write($"couldn't open {target}: {ex.Message}"); }
    }
}
