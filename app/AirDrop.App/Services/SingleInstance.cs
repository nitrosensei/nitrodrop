using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AirDrop.App.Services;

/// <summary>
/// One running instance per user. A second launch (e.g. Explorer "NitroDrop ile gönder")
/// forwards its command line to the first instance over a named pipe and exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private static readonly string Name = $"Nitrobyte.NitroDrop.{Environment.UserName}";
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();

    public bool IsFirst { get; }
    public event Action<string[]>? ArgsReceived;

    public SingleInstance()
    {
        _mutex = new Mutex(true, @"Local\" + Name, out bool first);
        IsFirst = first;
        if (first) _ = ListenAsync(_cts.Token);
    }

    public static bool Forward(string[] args)
    {
        try
        {
            using var c = new NamedPipeClientStream(".", Name, PipeDirection.Out);
            c.Connect(5000);   // a big Explorer selection starts many forwarders at once; the server takes one at a time
            using var w = new StreamWriter(c, Encoding.UTF8);
            w.Write(string.Join('\n', args));
            return true;
        }
        catch (Exception) { return false; }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var s = new NamedPipeServerStream(Name, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await s.WaitForConnectionAsync(ct);
                using var r = new StreamReader(s, Encoding.UTF8);
                var text = await r.ReadToEndAsync(ct);
                ArgsReceived?.Invoke(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) { await Task.Delay(200, CancellationToken.None); }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (IsFirst) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
