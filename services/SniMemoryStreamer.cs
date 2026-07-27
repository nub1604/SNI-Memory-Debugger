using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using System.Collections.Concurrent;

namespace MemoryDebgugger.Services;

public sealed class SniMemoryStreamer : IDisposable
{
    private readonly GrpcChannel _channel;
    private readonly DeviceMemory.DeviceMemoryClient _memoryClient;
    private readonly Devices.DevicesClient _devicesClient;

    // mutable so it can be set from a device dropdown at runtime
    public string? DeviceUri { get; set; }

    public SniMemoryStreamer(string sniAddress = "http://localhost:8191")
    {
        _channel = GrpcChannel.ForAddress(sniAddress);
        _memoryClient = new DeviceMemory.DeviceMemoryClient(_channel);
        _devicesClient = new Devices.DevicesClient(_channel);
    }

    public async Task<IReadOnlyList<(string Uri, string DisplayName)>> ListDevicesAsync(CancellationToken ct = default)
    {
        var response = await _devicesClient.ListDevicesAsync(new DevicesRequest(), cancellationToken: ct);
        return response.Devices
            .Select(d => (d.Uri, DisplayName: $"{d.DisplayName} ({d.Uri})"))
            .ToList();
    }

    public async Task RunWatchLoopAsync(
        Func<IReadOnlyList<(string Key, uint FxPakProAddress, uint Size)>> getWatchedItems,
        Action<string, byte[]> onValue,
        CancellationToken ct)
    {
        using var call = _memoryClient.StreamRead(cancellationToken: ct);

        // maps responses back to the request that triggered them
        var pending = new ConcurrentQueue<IReadOnlyList<(string Key, uint FxPakProAddress, uint Size)>>();

        var readTask = Task.Run(async () =>
        {
            await foreach (var response in call.ResponseStream.ReadAllAsync(ct))
            {
                if (!pending.TryDequeue(out var items))
                    continue;

                for (var i = 0; i < response.Responses.Count && i < items.Count; i++)
                {
                    onValue(items[i].Key, response.Responses[i].Data.ToByteArray());
                }
            }
        }, ct);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (string.IsNullOrEmpty(DeviceUri))
                {
                    // no device selected yet, just wait
                    await Task.Delay(200, ct);
                    continue;
                }

                var items = getWatchedItems();
                if (items.Count > 0)
                {
                    var request = new MultiReadMemoryRequest { Uri = DeviceUri };
                    foreach (var item in items)
                    {
                        request.Requests.Add(new ReadMemoryRequest
                        {
                            RequestAddress = item.FxPakProAddress,
                            RequestAddressSpace = AddressSpace.FxPakPro,
                            Size = item.Size
                        });
                    }

                    pending.Enqueue(items);
                    await call.RequestStream.WriteAsync(request);
                }

                await Task.Delay(100, ct); // 10 Hz is enough for UI display
            }
        }
        catch (OperationCanceledException)
        {
            // expected when stopping
        }
        finally
        {
            try { await call.RequestStream.CompleteAsync(); } catch { /* ignore */ }
            await readTask;
        }
    }

    public async Task WriteAsync(uint fxPakProAddress, byte[] data, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(DeviceUri))
            throw new InvalidOperationException("No device selected.");

        await _memoryClient.SingleWriteAsync(new SingleWriteMemoryRequest
        {
            Uri = DeviceUri,
            Request = new WriteMemoryRequest
            {
                RequestAddress = fxPakProAddress,
                RequestAddressSpace = AddressSpace.FxPakPro,
                Data = ByteString.CopyFrom(data)
            }
        }, cancellationToken: ct);
    }

    public void Dispose() => _channel.Dispose();
}