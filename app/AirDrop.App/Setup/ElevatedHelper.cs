using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AirDrop.App.Setup;

/// <summary>
/// The single elevation point. The UI never runs elevated: each step button starts
/// <c>NitroDrop.exe --setup-helper --pipe … --request …</c> with the "runas" verb (one UAC prompt), the helper does
/// exactly one <see cref="HelperAction"/> and writes a <see cref="HelperResult"/> back over a named pipe that only
/// the current user and Administrators can open. In dry-run mode nothing is elevated: the action runs in-process
/// with a <see cref="DryRunCommandRunner"/>.
/// </summary>
public static class ElevatedHelper
{
    public const string DryRunFlag = "--setup-dry-run";

    public static bool DryRun { get; set; }

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMinutes(10);   // install can take a while

    public static async Task<HelperResult> RunAsync(HelperRequest request, CancellationToken ct = default)
    {
        request.DryRun |= DryRun;
        if (request.DryRun)
        {
            SetupLog.Write($"[dry-run] yönetici izni istenmeden çalıştırılıyor: {request.Action}");
            return await Task.Run(() => new HelperActions(new DryRunCommandRunner()).Execute(request), ct);
        }

        string pipeName = "NitroDrop.Setup." + Guid.NewGuid().ToString("N");
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));

        using var server = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, 0, security);

        var psi = new ProcessStartInfo(Environment.ProcessPath!, HelperProtocol.BuildArguments(pipeName, request))
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        SetupLog.Write($"yönetici izni isteniyor: {request.Action}");
        Process? proc;
        try { proc = Process.Start(psi); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)   // ERROR_CANCELLED: UAC "No"
        {
            SetupLog.Write("kullanıcı yönetici iznini vermedi");
            return new HelperResult { Ok = false, Cancelled = true, Message = "Yönetici izni verilmedi. Hazır olduğunuzda tekrar deneyin." };
        }
        if (proc is null) return HelperResult.Fail("Yardımcı başlatılamadı.");

        using (proc)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(ConnectTimeout);
            var connect = server.WaitForConnectionAsync(timeout.Token);
            var exited = proc.WaitForExitAsync(timeout.Token);
            try
            {
                if (await Task.WhenAny(connect, exited) != connect || !server.IsConnected)
                {
                    await Task.WhenAny(connect, Task.Delay(500, CancellationToken.None));
                    if (!server.IsConnected)
                        return HelperResult.Fail($"Yardımcı sonuç döndürmeden kapandı (kod {(proc.HasExited ? proc.ExitCode : -1)}).");
                }
                using var reader = new StreamReader(server);
                var json = await reader.ReadToEndAsync(timeout.Token);
                var result = JsonSerializer.Deserialize<HelperResult>(json, HelperProtocol.Json);
                return result ?? HelperResult.Fail("Yardımcıdan boş yanıt geldi.");
            }
            catch (OperationCanceledException)
            {
                return HelperResult.Fail("Yardımcı zaman aşımına uğradı.");
            }
            catch (JsonException ex)
            {
                return HelperResult.Fail("Yardımcının yanıtı okunamadı: " + ex.Message);
            }
        }
    }

    /// <summary>Entry point of the elevated process (called from App.OnStartup before anything else).</summary>
    public static int HostMain(string[] args)
    {
        SetupLog.Prefix = "[yönetici] ";
        if (!HelperProtocol.TryParse(args, out var pipeName, out var request) || request is null)
        {
            SetupLog.Write("geçersiz yardımcı çağrısı");
            return 2;
        }
        ICommandRunner runner = request.DryRun ? new DryRunCommandRunner() : new ProcessCommandRunner();
        var result = new HelperActions(runner).Execute(request);
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            client.Connect(10_000);
            JsonSerializer.Serialize(client, result, HelperProtocol.Json);
            client.Flush();
        }
        catch (Exception ex)
        {
            SetupLog.Write($"sonuç arayüze iletilemedi: {ex.Message}");
            return 3;
        }
        return result.Ok ? 0 : 1;
    }
}
