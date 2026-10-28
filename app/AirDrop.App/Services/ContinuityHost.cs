using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using AirDrop.App.ViewModels;
using AirDrop.App.Views;
using AirDrop.Core.Contacts;
using Nitro.Continuity.Platform;
using Nitro.Continuity.Runtime;
using Nitro.Sync;

namespace AirDrop.App.Services;

/// <summary>
/// Composition for the "Telefon" tab: picks the real runtime (Nitro.Continuity.Windows) or the demo, builds the
/// <see cref="PhoneViewModel"/>, and owns the call banner + notification toasts (top-right, stacked, never focused).
/// App.xaml.cs creates one at startup (docs/ui-integration-continuity.md).
/// </summary>
public sealed class ContinuityHost : IAsyncDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly List<Window> _banners = new();
    private readonly DispatcherTimer _tick;

    private ContinuityHost(IContinuityService service, IPhoneContacts contacts, Dispatcher dispatcher, PhoneSettings settings)
    {
        _dispatcher = dispatcher;
        Service = service;
        ViewModel = new PhoneViewModel(service, contacts, a => Post(a), new ShellUrlOpener(), settings);
        ViewModel.IncomingCall += OnIncomingCall;
        ViewModel.ToastRequested += OnToast;
        _tick = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (_, _) => ViewModel.Tick(), dispatcher);
    }

    public IContinuityService Service { get; }
    public PhoneViewModel ViewModel { get; }

    /// <summary>Brings the main window forward on the "Telefon" tab (set by App.xaml.cs).</summary>
    public Action? ShowPhoneTab { get; set; }

    /// <summary>When false (snapshots) no banner/toast window is ever opened.</summary>
    public bool ShowWindows { get; set; } = true;

    public static string StateFile { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "airdrop-win", "phone.json");

    /// <param name="demo">--demo / --snapshot: fake iPhone (autoplay rings once in --demo).</param>
    /// <param name="book">The user's contact book (PBAP imports go here); null in demo.</param>
    public static ContinuityHost Create(bool demo, ContactBook? book, Dispatcher dispatcher, bool autoplay = true)
    {
        var settings = PhoneSettings.Load();
        if (demo)
            return new ContinuityHost(new DemoContinuityService(autoplay), new DemoPhoneContacts(), dispatcher, settings);
        IPhoneContacts contacts = book is null ? new DemoPhoneContacts() : new ContactBookPhoneContacts(book);
        var runtime = new ContinuityRuntime(StateFile, contacts.ImportVCards);
        return new ContinuityHost(runtime, contacts, dispatcher, settings);
    }

    /// <summary>For tests/snapshots: wrap an existing service.</summary>
    public static ContinuityHost For(IContinuityService service, IPhoneContacts contacts, Dispatcher dispatcher) =>
        new(service, contacts, dispatcher, new PhoneSettings()) { ShowWindows = false };

    public async Task StartAsync()
    {
        _tick.Start();
        try { await ViewModel.StartAsync(); }
        catch (Exception) { /* Bluetooth stack errors surface through Status; never crash startup */ }
    }

    private void Post(Action a)
    {
        if (_dispatcher.CheckAccess()) a();
        else _dispatcher.BeginInvoke(a);
    }

    private void OnIncomingCall(CallViewModel call)
    {
        if (!ShowWindows) return;
        var w = IncomingCallWindow.ShowFor(call, ViewModel.Settings.PlaySounds);
        Track(w, first: true);
    }

    private void OnToast(NotificationItem item)
    {
        if (!ShowWindows) return;
        // At most 3 toasts; the oldest goes first.
        var toasts = _banners.OfType<NotificationToast>().ToList();
        if (toasts.Count >= 3) toasts[0].Close();
        var t = new NotificationToast(item, ViewModel, ViewModel.Settings.ShowPreviews) { ShowMain = ShowPhoneTab };
        if (ViewModel.Settings.PlaySounds) System.Media.SystemSounds.Asterisk.Play();
        Track(t, first: false);
    }

    private void Track(Window w, bool first)
    {
        if (first) _banners.Insert(0, w); else _banners.Add(w);
        w.Closed += (_, _) => { _banners.Remove(w); Layout(); };
        w.SizeChanged += (_, _) => Layout();
        if (!w.IsVisible) w.Show();
        Layout();
    }

    /// <summary>Stack banners from the top-right corner: calls first, then toasts (8 px apart).</summary>
    private void Layout()
    {
        var wa = SystemParameters.WorkArea;
        double top = wa.Top + 12;
        foreach (var w in _banners.OrderBy(b => b is IncomingCallWindow ? 0 : 1))
        {
            w.Left = wa.Right - w.Width - 12;
            w.Top = top;
            top += (w.ActualHeight > 0 ? w.ActualHeight : 120) + 8;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _tick.Stop();
        foreach (var w in _banners.ToList()) w.Close();
        await ViewModel.DisposeAsync();
    }
}
