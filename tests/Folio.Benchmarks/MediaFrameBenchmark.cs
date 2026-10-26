using System.Diagnostics;
using Folio.Dom;
using Folio.Imaging;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;
using Folio.Scripting;
using Folio.Skia;

namespace Folio.Benchmarks;

/// <summary>Measures clock-driven video frame selection through display-list paint and raster output.</summary>
internal static class MediaFrameBenchmark
{
    public static int Run(int frameCount)
    {
        frameCount = Math.Clamp(frameCount, 30, 240);
        using var engine = new JintScriptEngine();
        var clock = new VirtualEventLoopClock();
        using var loop = new HtmlEventLoop(engine, clock);
        using var document = Document.Parse("<video id='clip' style='width:320px;height:180px' src='data:video/test;base64,AQID'></video>",
            new FolioOptions
            {
                Capabilities = new FeatureCapabilities { Media = true },
                MediaDecoder = new Decoder(frameCount),
                Raster = new SkiaRasterBackend()
            });
        DomBindings.Install(engine, document, eventLoop: loop);
        clock.SetNow(0);
        loop.UpdateTheRendering();
        engine.Evaluate("document.getElementById('clip').play()");

        var samples = new List<double>(frameCount);
        for (var i = -10; i < frameCount; i++)
        {
            clock.SetNow(Math.Max(0, i) * (1000d / 60));
            var start = Stopwatch.GetTimestamp();
            loop.UpdateTheRendering();
            using var rendered = HeadlessRenderer.Render(document, new RenderRequest(320, 180));
            if (i >= 0) samples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        samples.Sort();
        var median = samples[samples.Count / 2];
        var p95 = samples[Math.Min(samples.Count - 1, (int)Math.Ceiling(samples.Count * .95) - 1)];
        Console.WriteLine("Media frame playback benchmark (clock + display list + raster)");
        Console.WriteLine("| Frames | Median ms | p95 ms | Target |");
        Console.WriteLine($"| {frameCount} | {median:F2} | {p95:F2} | 16.67 |");
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
