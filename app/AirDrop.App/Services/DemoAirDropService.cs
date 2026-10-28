using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AirDrop.App.Services;

/// <summary>Fake peers and transfers so the UI can be exercised without the driver.</summary>
public sealed class DemoAirDropService : IAirDropService
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Random _rng = new();
    private ReceiveMode _mode;
    private ServiceOptions? _opt;

    public bool IsHardwareReady => false;
    public string HardwareStatus => "Wi-Fi sürücüsü henüz kurulu değil · demo modu";
    public event EventHandler? HardwareStatusChanged { add { } remove { } }

    public event EventHandler<PeerInfo>? PeerFound;
    public event EventHandler<string>? PeerLost;
    public event EventHandler<TransferProgress>? ReceiveProgress;
    public event EventHandler<ReceivedBatch>? Received;
    public Func<IncomingRequest, Task<bool>>? AskHandler { get; set; }

    public Task StartAsync(ServiceOptions options, ReceiveMode mode, CancellationToken ct = default)
    {
        _opt = options;
        _mode = mode;
        _ = RunAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public Task SetModeAsync(ReceiveMode mode) { _mode = mode; return Task.CompletedTask; }
    public Task UpdateOptionsAsync(ServiceOptions options) { _opt = options; return Task.CompletedTask; }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(1200, ct);
            PeerFound?.Invoke(this, new PeerInfo("p1", "Emre'nin iPhone'u", DeviceKind.iPhone, "iPhone16,1",
                new[] { "demo-hash-emre" }));
            await Task.Delay(1600, ct);
            PeerFound?.Invoke(this, new PeerInfo("p2", "Ayşe'nin MacBook Air'i", DeviceKind.Mac, "Mac14,2"));
            await Task.Delay(2200, ct);
            PeerFound?.Invoke(this, new PeerInfo("p3", "Ofis iPad", DeviceKind.iPad, "iPad13,18"));
            await Task.Delay(8000, ct);

            if (_mode != ReceiveMode.Off && AskHandler is { } ask)
            {
                var files = new[] { new IncomingFile("Sunum.pdf", 2_400_000, "com.adobe.pdf"),
                                    new IncomingFile("IMG_2041.JPG", 3_100_000, "public.jpeg") };
                var req = new IncomingRequest("Ayşe'nin MacBook Air'i", DeviceKind.Mac, "Mac14,2",
                    files, Array.Empty<Uri>(), null);
                if (await ask(req))
                {
                    long total = 5_500_000;
                    for (int i = 0; i <= 40; i++)
                    {
                        ReceiveProgress?.Invoke(this, new TransferProgress(total * i / 40, total, 3_900_000));
                        await Task.Delay(70, ct);
                    }
                    Received?.Invoke(this, new ReceivedBatch(req.SenderName, req.SenderKey,
                        Array.Empty<string>(), Array.Empty<Uri>(), total));
                }

                await Task.Delay(6000, ct);
                var link = new IncomingRequest("Emre'nin iPhone'u", DeviceKind.iPhone, "iPhone16,1",
                    Array.Empty<IncomingFile>(), new[] { new Uri("https://nitrobyte.com.tr") }, null,
                    new[] { "demo-hash-emre" });
                if (await ask(link))
                    Received?.Invoke(this, new ReceivedBatch(link.SenderName, link.SenderKey,
                        Array.Empty<string>(), link.Links, 0));
            }

            await Task.Delay(15000, ct);
            PeerLost?.Invoke(this, "p3");
        }
        catch (OperationCanceledException) { }
    }

    public async Task<SendOutcome> SendAsync(string peerId, IReadOnlyList<string> files,
        IProgress<TransferProgress> progress, CancellationToken ct = default)
    {
        try
        {
            await Task.Delay(1500, ct);
            if (_rng.NextDouble() < 0.15) return SendOutcome.Declined;
            long total = 48_000_000;
            for (int i = 1; i <= 60; i++)
            {
                progress.Report(new TransferProgress(total * i / 60, total, 11_800_000 + _rng.Next(-900_000, 900_000)));
                await Task.Delay(65, ct);
            }
            return SendOutcome.Delivered;
        }
        catch (OperationCanceledException) { return SendOutcome.Cancelled; }
    }

    public async Task<SendOutcome> SendLinkAsync(string peerId, Uri link, CancellationToken ct = default)
    {
        await Task.Delay(1200, ct);
        return SendOutcome.Delivered;
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Demo contact book: one fake contact so the avatar path is visible.</summary>
public sealed class DemoContactResolver : IContactResolver
{
    public int Count => 1;
    public ContactMatch? Match(IReadOnlyList<string>? hashes) =>
        hashes is not null && hashes.Contains("demo-hash-emre") ? new ContactMatch("Emre Can Temur", null) : null;
    public Task<int> ImportAsync(string vcfPath) => Task.FromResult(0);
    public void Clear() { }
}
