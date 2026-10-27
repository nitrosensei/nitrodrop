using System;
using System.Threading.Tasks;
using AirDrop.App.ViewModels;

namespace AirDrop.App.Setup;

/// <summary>Wi-Fi driver state for the main window banner and the "Wi-Fi Sürücüsü" settings section.</summary>
public sealed class DriverSetupStatus : Observable
{
    private readonly ISystemProbe _probe;
    private SystemSnapshot _snap = new();
    private SetupState _state = new();

    public DriverSetupStatus(ISystemProbe probe)
    {
        _probe = probe;
        OpenSetup = new RelayCommand(_ => OpenWizard?.Invoke(false));
        OpenRollback = new RelayCommand(_ => OpenWizard?.Invoke(true), _ => CanRollback);
        _ = RefreshAsync();
    }

    /// <summary>Set by App: opens the wizard (true = rollback mode).</summary>
    public Action<bool>? OpenWizard { get; set; }

    public RelayCommand OpenSetup { get; }
    public RelayCommand OpenRollback { get; }

    public bool IsSupported => _snap.Device is not null;
    public bool IsInstalled => _snap.OurDriverBound;
    public bool NeedsSetup => IsSupported && !IsInstalled;
    public bool CanRollback => IsInstalled || _state.HasChanges;
    public bool CanInstall => IsSupported && !IsInstalled;

    public string StateText =>
        IsInstalled ? "Kurulu — Wi-Fi kartı NitroDrop sürücüsünde"
        : !IsSupported ? "Desteklenen Intel Wi-Fi kartı bulunamadı"
        : _state.HasChanges || _state.ConsentGiven ? "Kurulum yarım kaldı"
        : "Kurulu değil";

    public string DetailText =>
        IsInstalled ? "iPhone, iPad ve Mac'lerle doğrudan (AirDrop) bağlantı için hazır. Wi-Fi kartı bu sürücüdeyken internete başka bir bağlantıyla çıkılır."
        : !IsSupported ? "Şu an aynı ağdaki cihazlarla paylaşım (LAN modu) çalışır. USB Wi-Fi adaptörü desteği planlanıyor."
        : $"{_snap.Device!.Card.Name} bulundu. iPhone'larla doğrudan bağlantı için Gelişmiş Kurulum gerekli.";

    public Task RefreshAsync() => Task.Run(() =>
    {
        try
        {
            var snap = _probe.Read(includeBitLocker: false);
            var state = SetupState.Load(SetupPaths.StateFile);
            System.Windows.Application.Current?.Dispatcher.Invoke(() => Apply(snap, state));
        }
        catch (Exception ex) { SetupLog.Write($"sürücü durumu okunamadı: {ex.Message}"); }
    });

    private void Apply(SystemSnapshot snap, SetupState state)
    {
        _snap = snap;
        _state = state;
        foreach (var p in new[] { nameof(IsSupported), nameof(IsInstalled), nameof(NeedsSetup), nameof(CanRollback),
                                  nameof(CanInstall), nameof(StateText), nameof(DetailText) })
            Raise(p);
    }
}
