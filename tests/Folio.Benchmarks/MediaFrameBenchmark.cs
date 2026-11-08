using System.Diagnostics;
using Folio.Dom;
using Folio.Imaging;
using Folio.Scripting.Dom;
using Folio.Scripting.Native;
using Folio.Scripting;
using Folio.Rendering;
using Folio.Painting;

namespace Folio.Benchmarks;

/// <summary>Measures clock-driven video frame selection through display-list paint and raster output.</summary>
internal static class MediaFrameBenchmark
{
    public static int Run(int frameCount)
    {
        frameCount = Math.Clamp(frameCount, 30, 240);
        using var engine = new NativeJavaScriptEngine();
        var clock = new VirtualEventLoopClock();
        using var loop = new HtmlEventLoop(engine, clock);
        using var document = Document.Parse("<video id='clip' style='width:320px;height:180px' src='data:video/test;base64,AQID'></video>",
            new FolioOptions
            {
                Capabilities = new FeatureCapabilities { Media = true },
                MediaDecoder = new Decoder(frameCount),
                Raster = SoftwareRasterBackend.Instance
            });
        DomBindings.Install(engine, document, eventLoop: loop);
        clock.SetNow(0);
        loop.UpdateTheRendering();
        engine.Evaluate("document.getElementById('clip').play()");

        var samples = new List<double>(frameCount);
        var presented = new HashSet<uint>();
        for (var i = -10; i < frameCount; i++)
        {
            clock.SetNow(Math.Max(0, i) * (1000d / 60));
            var start = Stopwatch.GetTimestamp();
            loop.UpdateTheRendering();
            using var rendered = HeadlessRenderer.Render(document, new RenderRequest(320, 180));
            if (i >= 0)
            {
                samples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                var pixel = rendered.Pixels.Slice((20 * rendered.Width + 20) * 4, 4);
                presented.Add(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(pixel));
            }
        }

        samples.Sort();
        var median = samples[samples.Count / 2];
        var p95 = samples[Math.Min(samples.Count - 1, (int)Math.Ceiling(samples.Count * .95) - 1)];
        Console.WriteLine("Media frame playback benchmark (clock + display list + raster)");
        Console.WriteLine("Native Folio engine/renderer; synthetic decoded frames isolate presentation cost, not codec/device latency.");
        Console.WriteLine("| Frames | Median ms | p95 ms | Target |");
        Console.WriteLine($"| {frameCount} | {median:F2} | {p95:F2} | 16.67 |");
        if (presented.Count < 2) { Console.Error.WriteLine("Playback did not present changing video frames."); return 1; }
        if (p95 > 16.67) { Console.Error.WriteLine("Media presentation exceeds the 16.67 ms p95 frame budget."); return 1; }
        return 0;
    }

    private sealed class Decoder(int frameCount) : IMediaDecoder
    {
        public MediaDecodeResult? Decode(ReadOnlyMemory<byte> data, string? contentType, bool video, int budget)
        {
            if (!video) return null;
            var frames = new List<MediaVideoFrame>(frameCount);
            for (var i = 0; i < frameCount; i++)
            {
                var pixels = new byte[320 * 180 * 4];
                for (var p = 0; p < pixels.Length; p += 4)
                {
                    pixels[p] = (byte)(i * 17);
                    pixels[p + 1] = (byte)(255 - i * 17);
                    pixels[p + 2] = 128;
                    pixels[p + 3] = 255;
                }
                frames.Add(new MediaVideoFrame(i / 60d, new Image(pixels)));
            }
            return new MediaDecodeResult(frameCount / 60d, frames[0].Frame, null) { VideoFrames = frames };
        }
    }

    private sealed class Image(byte[] pixels) : IImageHandle
    {
        public int Width => 320;
        public int Height => 180;
        public ReadOnlyMemory<byte> Pixels => pixels;
    }
}
