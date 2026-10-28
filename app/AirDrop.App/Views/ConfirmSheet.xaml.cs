using System;
using System.Media;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using AirDrop.App.Interop;
using Nitro.Sync;
using Nitro.Sync.Security;
using AirDrop.App.Localization;

namespace AirDrop.App.Views;

/// <summary>
/// Pairing code ("Diğer bilgisayarda da aynı kodu görüyor musunuz?") and "X wants to open Y here" confirmations.
/// Like ReceiveWindow: never steals focus and the accept button is not the default button.
/// </summary>
public partial class ConfirmSheet : Window
{
    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenRegistration _ctReg;

    private ConfirmSheet()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            WindowEffects.ApplyThemed(this, transient: true);
        };
        Closed += (_, _) =>
        {
            _ctReg.Dispose();
            _result.TrySetResult(false);
        };
    }

    /// <summary>"482 913" — both PCs show the same code; a man in the middle would change it.</summary>
    internal static string FormatCode(string code) => code.Length == 6 ? code[..3] + " " + code[3..] : code;

    internal static ConfirmSheet ForPairing(PairingPrompt p)
    {
        var w = new ConfirmSheet();
        w.Who.Text = p.PeerName;
        w.Eyebrow.Text = DevicesStrings.Pair_Eyebrow;
        w.Badge.Text = "";   // link
        w.Message.Text = p.IsInitiator ? DevicesStrings.Pair_MessageInitiator : DevicesStrings.Pair_MessageResponder;
        w.Code.Text = FormatCode(p.Code);
        w.CodeBox.Visibility = Visibility.Visible;
        w.Question.Text = DevicesStrings.Pair_Question;
        w.Question.Visibility = Visibility.Visible;
        w.AcceptButton.Content = DevicesStrings.Pair_Confirm;
        w.CancelButton.Content = DevicesStrings.Pair_Cancel;
        w.Footnote.Text = DevicesStrings.Pair_Footnote;
        return w;
    }

    internal static ConfirmSheet ForLaunch(AppLaunchRequest r)
    {
        var w = new ConfirmSheet();
        w.Who.Text = r.FromName;
        w.Eyebrow.Text = DevicesStrings.Launch_Eyebrow;
        w.Badge.Text = "";   // open in new window
        w.Message.Text = DevicesStrings.Launch_Message(r.AppName);
        w.AcceptButton.Content = CommonStrings.Common_Open;
        w.CancelButton.Content = CommonStrings.Common_Decline;
        w.Footnote.Text = DevicesStrings.Launch_Footnote;
        return w;
    }

    public static Task<bool> PairAsync(Window? owner, PairingPrompt p, CancellationToken ct) => ShowAsync(ForPairing(p), owner, ct);

    public static Task<bool> LaunchAsync(Window? owner, AppLaunchRequest r, CancellationToken ct) => ShowAsync(ForLaunch(r), owner, ct);

    private static Task<bool> ShowAsync(ConfirmSheet w, Window? owner, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromResult(false);
        if (owner is { IsVisible: true, WindowState: not WindowState.Minimized })
        {
            w.Owner = owner;
            w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            var wa = SystemParameters.WorkArea;
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = wa.Right - w.Width - 16;
            w.Top = wa.Bottom - 520;
        }
        // Never steal keyboard focus: a stray Enter/Space while typing elsewhere must not confirm.
        w.ShowActivated = false;
        // The other side gave up (90 s pairing window) → close the sheet.
        w._ctReg = ct.Register(() => w.Dispatcher.BeginInvoke(() => w.Finish(false)));
        SystemSounds.Asterisk.Play();
        w.Show();
        return w._result.Task;
    }

    private void Finish(bool ok)
    {
        _result.TrySetResult(ok);
        if (IsLoaded) Close();
    }

    private void Accept_Click(object sender, RoutedEventArgs e) => Finish(true);
    private void Cancel_Click(object sender, RoutedEventArgs e) => Finish(false);
}
