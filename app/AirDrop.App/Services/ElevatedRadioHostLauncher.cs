using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Nitro.Awdl.Ipc;

namespace AirDrop.App.Services;

/// <summary>
/// Starts NitroDrop.Radio.exe (next to NitroDrop.exe) with the "runas" verb — one UAC prompt per session — and connects
/// to its "NitroDrop.Radio.&lt;user SID&gt;" pipe, accepting it only when served by the process we started. The host exits
/// when this connection closes, so closing NitroDrop releases the Wi-Fi card.
/// </summary>
public sealed class ElevatedRadioHostLauncher : IRadioHostLauncher
{
    public const string ExeName = "NitroDrop.Radio.exe";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    public string ExePath { get; init; } = Path.Combine(AppContext.BaseDirectory, ExeName);

    public async Task<IRadioHostConnection> LaunchAsync(CancellationToken ct = default)
    {
        if (!File.Exists(ExePath)) throw new FileNotFoundException($"{ExeName} bulunamadı", ExePath);
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Kullanıcı SID'i okunamadı");
        var psi = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = $"--user-sid {sid} --parent-pid {Environment.ProcessId}",
        };
        Process? proc;
        try { proc = Process.Start(psi); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)   // ERROR_CANCELLED: UAC "No"
        {
            throw new RadioLaunchCancelledException();
        }
        if (proc is null) throw new InvalidOperationException($"{ExeName} başlatılamadı");

        using (proc)
        {
            var connect = RadioHostClient.ConnectAsync(RadioPipe.NameFor(sid), proc.Id, ConnectTimeout, ct);
            var exited = proc.WaitForExitAsync(ct);
            if (await Task.WhenAny(connect, exited) == connect) return await connect;
            // The host quit before serving the pipe (e.g. pipe name taken, parent check failed).
            await Task.WhenAny(connect, Task.Delay(500, CancellationToken.None));
            if (connect.IsCompletedSuccessfully) return connect.Result;
            _ = connect.ContinueWith(t => t.Exception, TaskScheduler.Default);   // observe
            throw new InvalidOperationException($"{ExeName} kapandı (kod {(proc.HasExited ? proc.ExitCode : -1)})");
        }
    }
}
