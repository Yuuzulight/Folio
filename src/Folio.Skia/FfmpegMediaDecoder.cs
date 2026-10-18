using System.Diagnostics;
using System.Globalization;
using Folio;
using Folio.Imaging;

namespace Folio.Skia;

/// <summary>Decodes common audio and video formats through an explicitly host-selected FFmpeg executable.</summary>
public sealed class FfmpegMediaDecoder : IMediaDecoder
{
    private readonly string _ffmpegPath;
    private readonly string _ffprobePath;
    private readonly TimeSpan _timeout;

    public FfmpegMediaDecoder(string ffmpegPath = "ffmpeg", string? ffprobePath = null, TimeSpan? timeout = null)
    {
        _ffmpegPath = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath;
        _ffprobePath = string.IsNullOrWhiteSpace(ffprobePath) ? "ffprobe" : ffprobePath;
        _timeout = timeout is { } value && value > TimeSpan.Zero ? value : TimeSpan.FromSeconds(10);
    }

    public MediaDecodeResult? Decode(ReadOnlyMemory<byte> data, string? contentType, bool video, int maxOutputBytes)
    {
        if (data.IsEmpty || maxOutputBytes <= 0) return null;
        var duration = ProbeDuration(data);
        if (video)
        {
            var png = Run(_ffmpegPath, "-hide_banner -loglevel error -i pipe:0 -map 0:v:0 -frames:v 1 -f image2pipe -vcodec png pipe:1", data, Math.Min(maxOutputBytes, 16 * 1024 * 1024));
            return png is null || ImageLoader.Decode(png) is not { } image ? null : new MediaDecodeResult(duration, image, null);
        }

        var pcm = Run(_ffmpegPath, "-hide_banner -loglevel error -i pipe:0 -map 0:a:0 -f f32le -ac 2 -ar 48000 pipe:1", data, Math.Min((long)maxOutputBytes * 2, 64L * 1024 * 1024));
        if (pcm is null || pcm.Length < sizeof(float) || pcm.Length % sizeof(float) != 0) return null;
        var samples = new float[pcm.Length / sizeof(float)];
        Buffer.BlockCopy(pcm, 0, samples, 0, pcm.Length);
        duration = duration > 0 ? duration : (double)samples.Length / (48000 * 2);
        return new MediaDecodeResult(duration, null, new MediaAudioFrame(48000, 2, samples));
    }

    private double ProbeDuration(ReadOnlyMemory<byte> data)
    {
        var output = Run(_ffprobePath, "-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 -i pipe:0", data, 256);
        return output is null || !double.TryParse(System.Text.Encoding.UTF8.GetString(output).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) ? 0 : Math.Max(0, value);
    }

    private byte[]? Run(string executable, string arguments, ReadOnlyMemory<byte> input, long maxOutputBytes)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }
        };
        try
        {
            if (!process.Start()) return null;
            var outputTask = Task.Run(() => ReadBounded(process.StandardOutput.BaseStream, maxOutputBytes, () => { try { process.Kill(entireProcessTree: true); } catch { } }));
            var errorTask = process.StandardError.ReadToEndAsync();
            process.StandardInput.BaseStream.Write(input.Span);
            process.StandardInput.Close();
            if (!process.WaitForExit((int)Math.Min(int.MaxValue, _timeout.TotalMilliseconds)))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            var output = outputTask.GetAwaiter().GetResult();
            _ = errorTask.GetAwaiter().GetResult();
            return output;
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            return null;
        }
    }

    private static byte[]? ReadBounded(Stream stream, long maxBytes, Action abort)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) return output.ToArray();
            if (output.Length + read > maxBytes) { abort(); return null; }
            output.Write(buffer, 0, read);
        }
    }
}
