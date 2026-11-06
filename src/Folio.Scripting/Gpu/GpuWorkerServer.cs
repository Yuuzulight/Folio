namespace Folio.Scripting.Gpu;

/// <summary>Only the --gpu-worker entry point may run this loop in production.</summary>
internal static class GpuWorkerServer
{
    internal static void Run(Stream input, Stream output, Func<long, GpuDeviceResult>? deviceFactory = null)
    {
        IGpuDevice? device = null;
        var surfaces = new Dictionary<int, IGpuSurface>();
        byte[]? snapshot = null;
        int snapshotId = 0, nextId = 0, sequence = 0, nextOffset = 0;
        var request = new byte[GpuWorkerProtocol.RequestBytes];
        try
        {
            while (true)
            {
                var first = input.ReadByte();
                if (first < 0) return;
                request[0] = (byte)first;
                input.ReadExactly(request.AsSpan(1));
                if (GpuWorkerProtocol.Get(request, 0) != GpuWorkerProtocol.Magic || GpuWorkerProtocol.Get(request, 2) != checked(++sequence))
                    throw new InvalidDataException("Invalid GPU worker request sequence.");
                var command = (GpuWorkerCommand)GpuWorkerProtocol.Get(request, 1);
                int id = GpuWorkerProtocol.Get(request, 3), result = 0;
                byte[] payload = [];
                try
                {
                    if (command == GpuWorkerCommand.Open)
                    {
                        if (device is not null || sequence != 1) throw new InvalidDataException("GPU device already opened.");
                        var budget = GpuWorkerProtocol.Get(request, 4);
                        if (budget <= 0 || budget > GpuWorkerProtocol.MaxDeviceBytes) throw new InvalidDataException("Invalid GPU budget.");
                        var created = deviceFactory is null ? FolioGpuBackend.TryCreate(budget) : deviceFactory(budget);
                        device = created.Device ?? throw new IOException("GPU device unavailable.");
                        result = (int)device.RendererKind;
                    }
                    else
                    {
                        if (device is null) throw new InvalidDataException("GPU device not opened.");
                        if (command == GpuWorkerCommand.CreateSurface)
                        {
                            var width = GpuWorkerProtocol.Get(request, 4); var height = GpuWorkerProtocol.Get(request, 5);
                            _ = GpuWorkerProtocol.SurfaceBytes(width, height);
                            if (surfaces.Count >= GpuWorkerProtocol.MaxSurfaces) throw new InvalidDataException("Too many GPU surfaces.");
                            var surface = device.TryCreateSurface(width, height).Surface ?? throw new IOException("GPU allocation failed.");
                            result = checked(++nextId); surfaces.Add(result, surface);
                        }
                        else
                        {
                            if (!surfaces.TryGetValue(id, out var surface)) throw new InvalidDataException("Unknown GPU surface.");
                            if (command == GpuWorkerCommand.ReadPixels)
                            {
                                int offset = GpuWorkerProtocol.Get(request, 4), count = GpuWorkerProtocol.Get(request, 5);
                                int total = GpuWorkerProtocol.SurfaceBytes(surface.Width, surface.Height);
                                if (offset < 0 || count <= 0 || count > GpuWorkerProtocol.MaxPayloadBytes || offset > total - count)
                                    throw new InvalidDataException("Invalid readback range.");
                                if (offset == 0)
                                {
                                    snapshot = new byte[total]; snapshotId = id; nextOffset = 0;
                                    if (!surface.TryReadPixels(snapshot, out _)) throw new IOException("GPU readback failed.");
                                }
                                if (snapshot is null || snapshotId != id || offset != nextOffset) throw new InvalidDataException("Readback snapshot is unavailable.");
                                payload = snapshot.AsSpan(offset, count).ToArray(); nextOffset += count;
                                if (nextOffset == total) snapshot = null;
                            }
                            else
                            {
                                snapshot = null;
                                float R(int i) => BitConverter.Int32BitsToSingle(GpuWorkerProtocol.Get(request, i));
                                if (command is GpuWorkerCommand.Clear or GpuWorkerCommand.ClearRect)
                                {
                                    for (int i = 4; i < 8; i++) if (!float.IsFinite(R(i))) throw new InvalidDataException("Non-finite clear color.");
                                    if (command == GpuWorkerCommand.ClearRect)
                                    {
                                        int x = GpuWorkerProtocol.Get(request, 8), y = GpuWorkerProtocol.Get(request, 9);
                                        int w = GpuWorkerProtocol.Get(request, 10), h = GpuWorkerProtocol.Get(request, 11);
                                        if (x < 0 || y < 0 || w < 0 || h < 0 || w > surface.Width || h > surface.Height || x > surface.Width - w || y > surface.Height - h)
                                            throw new InvalidDataException("Invalid clear rectangle.");
                                    }
                                    bool ok = command == GpuWorkerCommand.Clear
                                        ? surface.ClearColor(R(4), R(5), R(6), R(7), out _)
                                        : surface.ClearColorRect(R(4), R(5), R(6), R(7), GpuWorkerProtocol.Get(request, 8), GpuWorkerProtocol.Get(request, 9), GpuWorkerProtocol.Get(request, 10), GpuWorkerProtocol.Get(request, 11), out _);
                                    if (!ok) throw new IOException("GPU clear failed.");
                                }
                                else if (command == GpuWorkerCommand.Release) { surfaces.Remove(id); surface.Dispose(); }
                                else throw new InvalidDataException("Unsupported GPU command.");
                            }
                        }
                    }
                }
                catch (Exception error) when (error is InvalidDataException or IOException or ArgumentException or OverflowException or InvalidOperationException)
                {
                    WriteReply(output, command, sequence, 1, 0, []);
                    return; // terminal: no ambiguous recovery or partially reused native state
                }
                WriteReply(output, command, sequence, 0, result, payload);
            }
        }
        finally { foreach (var surface in surfaces.Values) surface.Dispose(); device?.Dispose(); }
    }

    private static void WriteReply(Stream output, GpuWorkerCommand command, int sequence, int status, int result, byte[] payload)
    {
        var header = new byte[GpuWorkerProtocol.HeaderBytes];
        GpuWorkerProtocol.Put(header, 0, GpuWorkerProtocol.Magic); GpuWorkerProtocol.Put(header, 1, (int)command | 0x40000000);
        GpuWorkerProtocol.Put(header, 2, sequence); GpuWorkerProtocol.Put(header, 3, status);
        GpuWorkerProtocol.Put(header, 4, payload.Length); GpuWorkerProtocol.Put(header, 5, result);
        output.Write(header); output.Write(payload); output.Flush();
    }
}
