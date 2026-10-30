using Xunit.MicrosoftTestingPlatform;
using Xunit.Runner.InProc.SystemConsole;
using Folio.Scripting;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;
using Folio.Scripting.Native;
using Folio.Skia;
using Folio.Painting;
using Folio.Typography;
using Folio.Scripting.Ipc;
using SkiaSharp;

namespace Folio.RenderTests;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "approve")
            return Approve(args[1..]);
        if (args.Length > 0 && args[0] == "conformance")
            return ConformanceReport(args[1..]);
        if (args.Length > 0 && args[0] == "canvas-perf")
            return CanvasPerformance(args.Length > 1 && int.TryParse(args[1], out var n) ? Math.Max(3, n) : 10);
        if (args.Length > 0 && args[0] == "canvas-frames")
            return CanvasFrames(args.Length > 1 && int.TryParse(args[1], out var frames) ? Math.Max(3, frames) : 30);
        if (args.Length > 0 && args[0] == "canvas-shared-memory")
            return CanvasSharedMemory(args.Length > 1 && int.TryParse(args[1], out var frames) ? Math.Max(3, frames) : 10);
        if (args.Length > 0 && args[0] == "shaping-differential")
            return ShapingDifferentialReport();
        if (args.Length > 0 && args[0] == "shaping-differential-system")
            return SystemShapingDifferentialReport();

        // The rest mirrors the entry point xUnit generates.
        if (args.Any(arg => arg is "--server" or "--internal-msbuild-node"))
            return TestPlatformTestFramework.RunAsync(args, SelfRegisteredExtensions.AddSelfRegisteredExtensions).GetAwaiter().GetResult();
        return ConsoleRunner.Run(args).GetAwaiter().GetResult();
    }


    private static int CanvasPerformance(int iterations)
    {
        var artifacts = Conformance.Discover().Where(a => a.Category is "D5" or "D7").ToArray();
        Console.WriteLine($"Canvas conformance wall-time benchmark ({iterations} iterations per artifact)");
        Console.WriteLine("| Artifact | Median ms | p95 ms |");
        Console.WriteLine("|---|--:|--:|");
        foreach (var artifact in artifacts)
        {
            var samples = new List<double>(iterations);
            for (var i = 0; i < iterations; i++)
            {
                var start = System.Diagnostics.Stopwatch.GetTimestamp();
                _ = Conformance.Run(artifact);
                samples.Add(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            samples.Sort();
            var median = samples[samples.Count / 2];
            var p95 = samples[Math.Min(samples.Count - 1, (int)Math.Ceiling(samples.Count * .95) - 1)];
            Console.WriteLine($"| {artifact.Id} | {median:F1} | {p95:F1} |");
        }
        return 0;
    }

    private static int CanvasFrames(int frameCount)
    {
        Console.WriteLine($"Script-driven canvas frame benchmark ({frameCount} frames, 16.7 ms virtual ticks)");
        Console.WriteLine("| Artifact | Script/update p95 ms | Display-list p95 ms | Host paint p95 ms | Total p95 ms | Target |");
        Console.WriteLine("|---|--:|--:|--:|--:|---:|");
        var failed = false;
        foreach (var artifact in Conformance.Discover().Where(a => a.Category is "D5" or "D7"))
        {
            var html = File.ReadAllText(Path.Combine(artifact.Folder, "index.html"));
            using var document = Document.Parse(html, Conformance.Options);
            document.Paint(Conformance.ManifestOf(artifact).Viewport, 600);
            using var engine = string.Equals(Environment.GetEnvironmentVariable("FOLIO_CONFORMANCE_ENGINE"), "native", StringComparison.OrdinalIgnoreCase)
                ? (IScriptEngine)new NativeJavaScriptEngine()
                : new JintScriptEngine();
            var clock = new VirtualEventLoopClock();
            using var loop = new HtmlEventLoop(engine, clock);
            DomBindings.Install(engine, document, eventLoop: loop);
            ScriptGlobalsInstaller.Install(loop, engine);
            using var runner = new ScriptRunner(document.Node, engine, loop, Conformance.Options);
            runner.Run();
            loop.UpdateTheRendering();
            using var bitmap = new SKBitmap(new SKImageInfo(artifact.Category == "D5" ? 800 : 800, 600, SKColorType.Bgra8888, SKAlphaType.Premul));
            using var canvas = new SKCanvas(bitmap);
            var shaper = new HarfBuzzShaper();
            DisplayList BuildFrame()
            {
                // Canvas scripts mutate the backing surface without changing layout. Reuse the
                // retained layout/display-list path so this gate measures a frame update rather
                // than rebuilding the entire document tree on every canvas draw.
                return document.PaintFrame(clock.NowMs / 1000d)
                    ?? document.Paint(Conformance.ManifestOf(artifact).Viewport, 600, 1, shaper).List;
            }
            void Replay(DisplayList displayList)
            {
                canvas.Clear(SKColors.White);
                DisplayListPlayer.Replay(displayList, new SkiaCanvas(canvas), new RectF(0, 0, 800, 600));
                canvas.Flush();
            }
            for (var warmup = 0; warmup < 3; warmup++)
            {
                clock.Advance(16.7);
                loop.UpdateTheRendering();
                Replay(BuildFrame());
            }
            var scriptSamples = new List<double>(frameCount);
            var buildSamples = new List<double>(frameCount);
            var paintSamples = new List<double>(frameCount);
            var totalSamples = new List<double>(frameCount);
            for (var i = 0; i < frameCount; i++)
            {
                clock.Advance(16.7);
                var totalStart = System.Diagnostics.Stopwatch.GetTimestamp();
                var scriptStart = totalStart;
                loop.UpdateTheRendering();
                scriptSamples.Add(System.Diagnostics.Stopwatch.GetElapsedTime(scriptStart).TotalMilliseconds);
                var buildStart = System.Diagnostics.Stopwatch.GetTimestamp();
                var displayList = BuildFrame();
                buildSamples.Add(System.Diagnostics.Stopwatch.GetElapsedTime(buildStart).TotalMilliseconds);
                var paintStart = System.Diagnostics.Stopwatch.GetTimestamp();
                Replay(displayList);
                paintSamples.Add(System.Diagnostics.Stopwatch.GetElapsedTime(paintStart).TotalMilliseconds);
                totalSamples.Add(System.Diagnostics.Stopwatch.GetElapsedTime(totalStart).TotalMilliseconds);
            }
            var scriptP95 = Percentile95(scriptSamples);
            var buildP95 = Percentile95(buildSamples);
            var paintP95 = Percentile95(paintSamples);
            var totalP95 = Percentile95(totalSamples);
            failed |= totalP95 > 16.7;
            Console.WriteLine($"| {artifact.Id} | {scriptP95:F2} | {buildP95:F2} | {paintP95:F2} | {totalP95:F2} | 16.70 ms |");
        }
        return failed ? 1 : 0;
    }

    private static double Percentile95(List<double> samples)
    {
        samples.Sort();
        return samples[Math.Min(samples.Count - 1, (int)Math.Ceiling(samples.Count * .95) - 1)];
    }

    private static int ShapingDifferentialReport()
    {
        var corpus = new (string Family, string Text, string Name)[]
        {
            ("Source Sans 3", "The quick brown fox, AVATAR", "Latin"),
            ("Folio Box", "1234", "Latin digits"),
            ("Source Sans 3", "Γειά σου Κόσμε", "Greek"),
            ("Source Sans 3", "Привет, мир", "Cyrillic"),
            ("Source Sans 3", "Ångström — naïve", "Latin extended"),
            ("Noto Sans Hebrew", "שלום", "Hebrew base"),
        };
        var fonts = FontCollection.FromFolder(Path.Combine(RepoPaths.Tests, "fonts"));
        var failures = 0;
        Console.WriteLine("Folio shaping differential report (bundled corpus)");
        Console.WriteLine("| Run | Script | Glyphs | Clusters | Advances | Result |");
        Console.WriteLine("|---|---|---:|---:|---:|---|");
        foreach (var (family, text, name) in corpus)
        {
            var face = fonts.Match(family, FaceStyle.Normal, 400, 100);
            if (face is null || !SimpleShaper.CanShape(text, face))
            {
                Console.WriteLine($"| {name} | {name} | - | - | - | FAIL (not on Folio path) |");
                failures++;
                continue;
            }
            var folio = SimpleShaper.Shape(text, 0, text.Length, face, 16);
            var oracle = new HarfBuzzShaper().Shape(text, 0, text.Length, face, 16,
                rightToLeft: name.Contains("Hebrew", StringComparison.Ordinal),
                language: name.Contains("Hebrew", StringComparison.Ordinal) ? "he" : null);
            var glyphs = folio.Glyphs.SequenceEqual(oracle.Glyphs);
            var clusters = folio.Clusters.SequenceEqual(oracle.Clusters);
            var advances = folio.Advances.SequenceEqual(oracle.Advances);
            var pass = glyphs && clusters && advances;
            Console.WriteLine($"| {name} | {name} | {folio.Glyphs.Length} | {folio.Clusters.Length} | {folio.Advances.Length} | {(pass ? "PASS" : "FAIL")} |");
            if (!pass) failures++;
        }

        var complexCorpus = new (string Family, string Text, string Name, bool RightToLeft, string Language)[]
        {
            ("Noto Sans Hebrew", "שָׁלוֹם", "Hebrew niqqud", true, "he"),
            ("Noto Sans Hebrew", "שלום לכולם. המרכז הקהילתי פותח את שעריו בכל יום ראשון בשעה 09:30, ומציע סדנאות כתיבה, צילום ומוזיקה לכל המשפחה.", "Hebrew RTL paragraph", true, "he"),
            ("Noto Sans Hebrew", "ברוכים הבאים ללוח המודעות: מפגש חדש יתקיים ביום שלישי הקרוב, וההרשמה פתוחה עד סוף החודש.", "Hebrew mixed paragraph", true, "he"),
            ("Noto Sans Arabic", "سلام", "Arabic joining", true, "ar"),
            ("Noto Sans Arabic", "مُحَمَّد", "Arabic marks", true, "ar"),
            ("Noto Sans Arabic", "العربية لغة جميلة", "Arabic extended", true, "ar"),
            ("Noto Sans Arabic", "الكتاب والمدرسة والحديقة", "Arabic vocabulary", true, "ar"),
            ("Noto Sans Arabic", "يكتبون ويقرؤون في المكتبة", "Arabic joining vocabulary", true, "ar"),
            ("Noto Sans Arabic", "مُسْتَقْبَلٌ مُشْرِقٌ", "Arabic vowel marks", true, "ar"),
            ("Noto Sans Arabic", "ضِياءٌ وقُرْآنٌ كريمٌ", "Arabic Quranic marks", true, "ar"),
            ("Noto Sans Arabic", "الطريق إلى النجاح يبدأ بالتعلّم", "Arabic sentence", true, "ar"),
            ("Noto Sans Arabic", "إِنَّ اللُّغَةَ جِسْرٌ بَيْنَ الشُّعُوبِ", "Arabic marked sentence", true, "ar"),
            ("Noto Sans Arabic", "هٰذِهِ كَلِمَاتٌ مُشَكَّلةٌ بِعِنَايَةٍ", "Arabic superscript and marks", true, "ar"),
            ("Noto Sans Arabic", "وَالسَّمَاءِ وَالطَّارِقِ", "Arabic Quranic joining", true, "ar"),
            ("Noto Sans Arabic", "مَدِينَةٌ عَرِيقَةٌ وَمَكْتَبَةٌ كَبِيرَةٌ", "Arabic marked vocabulary", true, "ar"),
            ("Noto Sans Arabic", "تَعَلَّمْ، فَإِنَّ العِلْمَ نُورٌ", "Arabic punctuation", true, "ar"),
            ("Noto Sans Arabic", "اللَّهُمَّ اغْفِرْ لَنَا", "Arabic prayer marks", true, "ar"),
            ("Noto Sans Arabic", "رَبِّ زِدْنِي عِلْمًا", "Arabic shadda vocabulary", true, "ar"),
            ("Noto Sans Arabic", "السَّلَامُ عَلَيْكُمْ وَرَحْمَةُ اللَّهِ", "Arabic greeting paragraph", true, "ar"),
            ("Noto Sans Arabic", "أَحْرُفٌ مُتَنَوِّعَةٌ", "Arabic varied marks", true, "ar"),
            ("Noto Sans Arabic", "إِقْرَأْ بِاسْمِ رَبِّكَ", "Arabic hamza marks", true, "ar"),
            ("Noto Sans Arabic", "اللَّهُ", "Arabic ligature mark", true, "ar"),
            ("Noto Sans Arabic", "اللَّهُمَّ", "Arabic ligature prayer", true, "ar"),
            ("Noto Sans Arabic", "مرحباً بكم في مركز الواحة الثقافي. نقدّم ورش عمل في الخط العربي والتصوير والطبخ طوال فصل الخريف، وتبدأ التسجيلات يوم الأحد القادم.", "Arabic RTL paragraph", true, "ar"),
        };
        foreach (var (family, text, name, rightToLeft, language) in complexCorpus)
        {
            var face = fonts.Match(family, FaceStyle.Normal, 400, 100);
            if (face is null)
            {
                Console.WriteLine($"| {name} | {name} | - | - | - | FAIL (font unavailable) |");
                failures++;
                continue;
            }
            var folio = ComplexShaper.Instance.Shape(text, 0, text.Length, face, 16, rightToLeft, language);
            var oracle = new HarfBuzzShaper().Shape(text, 0, text.Length, face, 16, rightToLeft, language);
            var glyphs = folio.Glyphs.SequenceEqual(oracle.Glyphs);
            var clusters = folio.Clusters.SequenceEqual(oracle.Clusters);
            var advances = folio.Advances.SequenceEqual(oracle.Advances);
            var offsets = folio.Offsets.Length == oracle.Offsets.Length && folio.Offsets.Zip(oracle.Offsets).All(pair =>
                MathF.Abs(pair.First.X - pair.Second.X) <= 0.001f && MathF.Abs(pair.First.Y - pair.Second.Y) <= 0.001f);
            var pass = glyphs && clusters && advances && offsets;
            Console.WriteLine($"| {name} | {name} | {folio.Glyphs.Length} | {folio.Clusters.Length} | {folio.Advances.Length} | {(pass ? "PASS" : "FAIL")} |");
            if (!pass) failures++;
        }

        var kerningFace = fonts.Match("Folio Box", FaceStyle.Normal, 400, 100)!;
        var kerningText = "AVATAR";
        var expected = new HarfBuzzShaper().Shape(kerningText, 0, kerningText.Length, kerningFace, 16, false, null);
        var actual = SimpleShaper.Shape(kerningText, 0, kerningText.Length, kerningFace, 16);
        var broken = (float[])actual.Advances.Clone();
        broken[0] += 1;
        var perturbationDetected = !expected.Advances.SequenceEqual(broken);
        Console.WriteLine($"| deliberate kerning perturbation | Latin | - | - | - | {(perturbationDetected ? "PASS" : "FAIL")} |");
        if (!perturbationDetected) failures++;
        return failures == 0 ? 0 : 1;
    }

    private static int SystemShapingDifferentialReport()
    {
        var corpus = new (string Family, string Text)[]
        {
            ("Arial", "The quick brown fox, AVATAR"),
            ("Times New Roman", "AVATAR fi office"),
            ("Segoe UI", "Numbers 0123456789"),
            ("Arial", "שלום לכולם"),
            ("Arial", "مرحبا بكم"),
            ("Arial", "שָׁלוֹם וברוכים הבאים"),
            ("Arial", "יְרוּשָׁלַיִם היא עיר עתיקה ויפה"),
            ("Arial", "מִשְׁפָּחָה, חֲבֵרִים וְשָׁלוֹם"),
            ("Arial", "مرحباً بكم في مركز الواحة الثقافي"),
            ("Arial", "مُسْتَقْبَلٌ مُشْرِقٌ"),
            ("Arial", "नमस्ते दुनिया"),
            ("Arial", "বাংলা ভাষা"),
            ("Arial", "ภาษาไทย"),
            ("Arial", "ພາສາລາວ"),
            ("Arial", "ភាសាខ្មែរ"),
            ("Arial", "မြန်မာဘာသာ"),
        };
        var fonts = FontCollection.For(new FontSettings { Source = new SystemFontSource() });
        var compared = 0;
        var failures = 0;
        Console.WriteLine("Folio shaping differential report (local system-font corpus)");
        Console.WriteLine("| Family | Script | Glyphs | Clusters | Advances | Result |");
        Console.WriteLine("|---|---|---:|---:|---:|---|");
        foreach (var (family, text) in corpus)
        {
            var face = fonts.Match(family, FaceStyle.Normal, 400, 100);
            if (face is null)
            {
                Console.WriteLine($"| {family} | - | - | - | - | SKIP (family unavailable) |");
                continue;
            }
            compared++;
            var script = ClassifyScript(text.AsSpan());
            var complex = script is "Arabic" or "Hebrew" or "Indic" or "Southeast Asian";
            var rightToLeft = script is "Arabic" or "Hebrew";
            var language = script == "Arabic" ? "ar" : script == "Hebrew" ? "he" : null;
            var folio = new HybridShaper().Shape(text, 0, text.Length, face, 16, rightToLeft, language);
            var oracle = new HarfBuzzShaper().Shape(text, 0, text.Length, face, 16, rightToLeft, language);
            var pass = folio.Glyphs.SequenceEqual(oracle.Glyphs)
                && folio.Clusters.SequenceEqual(oracle.Clusters)
                && folio.Advances.SequenceEqual(oracle.Advances)
                && folio.Offsets.Length == oracle.Offsets.Length
                && folio.Offsets.Zip(oracle.Offsets).All(pair => MathF.Abs(pair.First.X - pair.Second.X) <= 0.001f && MathF.Abs(pair.First.Y - pair.Second.Y) <= 0.001f);
            Console.WriteLine($"| {family} | {script} | {folio.Glyphs.Length} | {folio.Clusters.Length} | {folio.Advances.Length} | {(pass ? "PASS" : "FAIL")} |");
            if (!pass) failures++;
        }
        if (compared == 0)
        {
            Console.WriteLine("No requested system families were available; run this command on the target host.");
            return 2;
        }
        return failures == 0 ? 0 : 1;
    }

    private static ShapedText SimpleToShapedText(ShapedRun run) =>
        new(run.Glyphs, run.Clusters, run.Advances, new System.Numerics.Vector2[run.Glyphs.Length]);

    private static string ClassifyScript(ReadOnlySpan<char> text)
    {
        string? script = null;
        foreach (var rune in text.EnumerateRunes())
        {
            var current = rune.Value switch
            {
                >= 0x0590 and <= 0x05FF => "Hebrew",
                >= 0x0600 and <= 0x06FF or >= 0x0750 and <= 0x077F or >= 0x08A0 and <= 0x08FF => "Arabic",
                >= 0x0900 and <= 0x0DFF => "Indic",
                >= 0x0E00 and <= 0x0EFF => "Southeast Asian",
                >= 0x0041 and <= 0x005A or >= 0x0061 and <= 0x007A => "Latin",
                _ => null,
            };
            if (current is null) continue;
            if (script is not null && !string.Equals(script, current, StringComparison.Ordinal)) return "Mixed";
            script = current;
        }
        return script ?? "Latin";
    }

    private static int CanvasSharedMemory(int frameCount)
    {
        const int width = 1920, height = 1080, stride = width * 4;
        var path = Path.Combine(Path.GetTempPath(), $"folio-canvas-bench-{Guid.NewGuid():N}.frame");
        var pixels = new byte[stride * height];
        var samples = new List<double>(frameCount);
        try
        {
            using var store = new CanvasFrameStore(path, width, height, stride);
            for (var i = 0; i < frameCount; i++)
            {
                pixels[(i * 4099) % pixels.Length] = (byte)i;
                var start = System.Diagnostics.Stopwatch.GetTimestamp();
                store.Publish(pixels);
                _ = store.ReadLatest(out var sequence);
                if (sequence != i + 1) throw new InvalidDataException("Canvas frame sequence did not advance.");
                samples.Add(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            samples.Sort();
            var p95 = samples[Math.Min(samples.Count - 1, (int)Math.Ceiling(samples.Count * .95) - 1)];
            Console.WriteLine($"Shared-memory canvas transfer benchmark ({width}x{height}, {frameCount} changing frames)");
            Console.WriteLine("| Median ms | p95 ms | Bytes/frame |");
            Console.WriteLine($"| {samples[samples.Count / 2]:F2} | {p95:F2} | {pixels.Length} |");
            return 0;
        }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }

    // folio-test conformance [category|category/name]...: renders the corpus and prints pass rates per category (also to
    // the GitHub Actions job summary). Fails on runtime/interaction failures and stale expected-failure marks.
    private static int ConformanceReport(string[] filters)
    {
        var native = string.Equals(Environment.GetEnvironmentVariable("FOLIO_CONFORMANCE_ENGINE"), "native", StringComparison.OrdinalIgnoreCase);
        Func<IScriptEngine>? engineFactory = native ? static () => new NativeJavaScriptEngine() : null;
        Console.WriteLine($"Conformance engine: {(native ? "native Folio" : "Jint compatibility")}");
        var discovered = Conformance.Discover();
        if (!native)
        {
            // D8 exercises Folio's native WebGL implementation. The compatibility engine deliberately
            // routes WebGL artifacts away, so running that category with Jint reports false failures.
            discovered = discovered.Where(a => !string.Equals(a.Category, "D8", StringComparison.Ordinal));
            Console.WriteLine("Skipping D8 for the Jint compatibility report; it is verified by the native-engine report.");
        }
        var results = discovered
            .Where(a => filters.Length == 0 || filters.Any(f => a.Id == f || a.Id.StartsWith(f.TrimEnd('/') + "/", StringComparison.Ordinal)))
            .Select(a => Conformance.Run(a, engineFactory: engineFactory))
            .ToList();
        var report = Conformance.Report(results);
        Console.WriteLine(report);
        if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summary)
            File.AppendAllText(summary, "### Conformance\n\n" + report);
        return results.Any(r => r.Broke) ? 1 : 0;
    }

    // folio-test approve <area|area/name>: copies the last actual images over the goldens.
    private static int Approve(string[] targets)
    {
        if (targets.Length == 0)
        {
            Console.Error.WriteLine("usage: approve <area|area/name>...");
            return 2;
        }

        var approved = targets.SelectMany(target => GoldenSuite.Repo.Approve(target,
            target.Replace('\\', '/').Trim('/') is "feature-fallback" || target.Replace('\\', '/').StartsWith("feature-fallback/", StringComparison.Ordinal)
                ? FeatureFallbackRenderTests.GoldenNames : null)).ToList();
        foreach (var test in approved)
            Console.WriteLine($"approved {test}");
        if (approved.Count == 0)
            Console.Error.WriteLine($"No actual images for {string.Join(", ", targets)} in {GoldenSuite.Repo.Output}; run the golden tests first.");
        return approved.Count == 0 ? 1 : 0;
    }
}
