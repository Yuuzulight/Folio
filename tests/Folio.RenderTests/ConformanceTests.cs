namespace Folio.RenderTests;

public class ConformanceTests
{
    [Fact]
    public void SortableDirectoryMatchesItsReference()
    {
        var artifact = Conformance.Discover().Single(a => a.Id == "D1/sortable-filter-table");
        var result = Conformance.Run(artifact);
        Assert.True(result.Outcome == ConformanceOutcome.Pass, $"{result.Outcome}: {result.Detail}");
    }

    [Fact]
    public void D1ArtifactsGiveIdenticalSettledImagesOnTwoRunsAndInteractionScriptsRun()
    {
        var d1Artifacts = Conformance.Discover().Where(a => a.Category == "D1").ToList();
        Assert.True(d1Artifacts.Count >= 2);
        foreach (var artifact in d1Artifacts)
        {
            var run1 = Conformance.Run(artifact);
            var run2 = Conformance.Run(artifact);

            Assert.Equal(run1.Outcome, run2.Outcome);
            Assert.False(run1.Broke, $"{artifact.Id} run 1 broke: {run1.Outcome} {run1.Detail}");
            Assert.False(run2.Broke, $"{artifact.Id} run 2 broke: {run2.Outcome} {run2.Detail}");
        }
    }

    [Fact]
    public void PagePrintingDateNowAndMathRandomIsStableUnderVirtualClock()
    {
        const string html = """
            <!DOCTYPE html>
            <p id="out"></p>
            <script>
                document.getElementById('out').textContent = Date.now() + ' ' + Math.random() + ' ' + (typeof crypto !== 'undefined' && crypto.getRandomValues ? crypto.getRandomValues(new Uint8Array(4)).join(',') : '');
            </script>
            """;
        var artifact = new ConformanceArtifact("", RepoPaths.Tests, "D1", "virtual-clock-stability");
        using var lifecycle1 = Conformance.ArtifactLifecycle.Start(artifact, html, 0);
        var text1 = lifecycle1.Document.QuerySelector("#out")?.TextContent;

        using var lifecycle2 = Conformance.ArtifactLifecycle.Start(artifact, html, 0);
        var text2 = lifecycle2.Document.QuerySelector("#out")?.TextContent;

        Assert.NotNull(text1);
        Assert.NotEmpty(text1);
        Assert.Equal(text1, text2);
    }

    [Fact]
    public void ConformanceReportListsD1ThroughD7WithPassRates()
    {
        ConformanceArtifact Artifact(string category, string name) => new("", "root", category, name);
        var results = new List<ConformanceResult>();
        for (var i = 1; i <= 7; i++)
        {
            results.Add(new(Artifact($"D{i}", $"item-{i}"), ConformanceOutcome.Pass, null));
        }

        var report = Conformance.Report(results);
        for (var i = 1; i <= 7; i++)
        {
            Assert.Contains($"| D{i} |", report);
            Assert.Contains("100.0% of 1", report);
        }
    }

    private const string None = "(none)";

    public static TheoryData<string> Artifacts =>
        Conformance.Discover().Select(a => a.Id).ToList() is { Count: > 0 } ids ? new(ids) : new(None);

    /// <summary>
    /// Every artifact renders without an exception, hang or limit hit, and its interaction assertions succeed unless explicitly expected to fail.
    /// Expected-failure marks must be removed when an artifact starts passing.
    /// Pass rates against the references are the report's job (<c>Folio.RenderTests conformance</c>), not a test's.
    /// </summary>
    [Theory]
    [MemberData(nameof(Artifacts))]
    public void HasNoRuntimeOrStaleExpectationFailure(string id)
    {
        Assert.SkipWhen(id == None, "The conformance corpus is empty.");

        var result = Conformance.Run(Conformance.Discover().Single(a => a.Id == id));

        Assert.False(result.Broke, $"{id}: {result.Outcome}: {result.Detail}");
    }

    [Fact]
    public void ReportsPassRatesPerCategory()
    {
        ConformanceArtifact Artifact(string category, string name) => new("", "root", category, name);
        var results = new ConformanceResult[]
        {
            new(Artifact("S1", "a"), ConformanceOutcome.Pass, null),
            new(Artifact("S1", "b"), ConformanceOutcome.Mismatch, "2.0% of pixels differ"),
            new(Artifact("S1", "c"), ConformanceOutcome.NoReference, null),
            new(Artifact("S3", "d"), ConformanceOutcome.PassSecondary, "review"),
            new(Artifact("D1", "e"), ConformanceOutcome.Crash, "boom"),
        };

        var report = Conformance.Report(results);

        Assert.Contains("| S1 | 3 | 1 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | 0 | 50.0% of 2 |", report);
        Assert.Contains("| S3 | 1 | 0 | 1 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 100.0% of 1 |", report);
        Assert.Contains("| D1 | 1 | 0 | 0 | 0 | 0 | 1 | 0 | 0 | 0 | 0 | 0.0% of 1 |", report);
        Assert.Contains("| S1/b | Mismatch | 2.0% of pixels differ |", report);
        Assert.DoesNotContain("| S1/a |", report);
    }

    // Rows of "text": dark 8px bands every 20px from `from` down, each 10px further down below row `shiftFrom` by `shift`.
    private static PixelBuffer Lines(int shift = 0, int shiftFrom = 0, int dx = 0)
    {
        var page = ImageComparerTests.Solid(200, 300, 255, 255, 255);
        for (var line = 0; line < 14; line++)
        {
            var top = 10 + 20 * line + (10 + 20 * line >= shiftFrom ? shift : 0);
            for (var y = top; y < top + 8 && y < 300; y++)
            {
                for (var x = 20 + dx + line * 3; x < 150 + dx; x++)
                    page.Pixel(x, y)[0] = page.Pixel(x, y)[1] = page.Pixel(x, y)[2] = 40;
            }
        }
        return page;
    }

    [Theory]
    [InlineData(3, 0, "mostly shifted down 3 px")]
    [InlineData(-2, 0, "mostly shifted up 2 px")]
    [InlineData(0, 4, "mostly shifted right 4 px")]
    public void DiagnosisNamesAUniformShift(int dy, int dx, string expected)
    {
        var (reference, actual) = (Lines(), Lines(dy, shiftFrom: 100, dx: dx));
        var diff = ImageComparer.Compare(reference, actual, Tolerance.Conformance).Diff;

        var diagnosis = Conformance.Diagnose(reference, actual, diff);

        Assert.EndsWith(expected, diagnosis);
    }

    [Fact]
    public void DiagnosisCallsLocalDifferencesScattered()
    {
        var (reference, actual) = (Lines(), Lines());
        for (var y = 50; y < 60; y++)
            actual.Pixel(170, y)[0] = actual.Pixel(170, y)[1] = actual.Pixel(170, y)[2] = 0;
        var diff = ImageComparer.Compare(reference, actual, Tolerance.Conformance).Diff;

        Assert.Equal("from row 50, within x 170-170, y 50-59; scattered", Conformance.Diagnose(reference, actual, diff));
    }

    [Fact]
    public void InteractionScriptExecutesCommandsAndAssertions()
    {
        const string html = """
            <!DOCTYPE html>
            <style>
              body { margin: 0; }
              button { width: 100px; height: 30px; }
              input { width: 120px; height: 24px; }
              details[open] .content { color: red; }
            </style>
            <details id=det><summary id=sum>Toggle</summary><div class=content id=cnt>Open text</div></details>
            <input id=txt type=text value=''>
            """;
        using var doc = Document.Parse(html);
        string[] script =
        [
            "exists #det",
            "exists #sum",
            "text #sum Toggle",
            "click #sum",
            "exists #cnt",
            "text #cnt Open text",
            "type #txt hello",
            "hover #sum",
            "wait 10",
        ];

        var err = Conformance.RunScript(doc, script, 800, 600);
        Assert.Null(err);
        Assert.Equal("hello", doc.QuerySelector("#txt")!.Value);
    }

    [Fact]
    public void InteractionScriptResolvesSrcdocFrameSelectors()
    {
        const string html = "<iframe id='preview' srcdoc=\"<p id='message'>Nested text</p><button id='go' onclick='document.getElementById(&quot;message&quot;).textContent=&quot;Changed&quot;'>Go</button>\"></iframe>";
        var options = new FolioOptions
        {
            Scripting = ScriptingPolicy.ContentProcess,
            Capabilities = new FeatureCapabilities { Iframes = true },
        };
        using var doc = Document.Parse(html, options);
        using var engine = new Folio.Scripting.Jint.JintScriptEngine();
        using var loop = new Folio.Scripting.HtmlEventLoop(engine);
        Folio.Scripting.Dom.DomBindings.Install(engine, doc, eventLoop: loop);
        try
        {
            Folio.Scripting.ScriptGlobalsInstaller.Install(loop, engine);
            using var runner = new Folio.Scripting.ScriptRunner(doc.Node, engine, loop, options);
            runner.Run();

            var error = Conformance.RunScript(doc, ["frame #preview exists #message", "frame #preview text #message Nested text", "frame #preview click #go", "frame #preview text #message Changed"], 800, 600);

            Assert.Null(error);
        }
        finally
        {
            Folio.Scripting.Dom.DomBindings.Dispose(engine);
        }
    }

    [Fact]
    public void InteractionScriptFailsOnMissingElementOrAssertionMismatch()
    {
        const string html = "<!DOCTYPE html><div id=box>hello</div>";
        using var doc = Document.Parse(html);

        var errNotFound = Conformance.RunScript(doc, ["click #nonexistent"], 800, 600);
        Assert.NotNull(errNotFound);
        Assert.Contains("element not found", errNotFound);

        var errMismatch = Conformance.RunScript(doc, ["text #box world"], 800, 600);
        Assert.NotNull(errMismatch);
        Assert.Contains("text mismatch", errMismatch);
    }

    [Fact]
    public void ReopenCreatesANewOfflinePageAndRetiresThePreviousLoad()
    {
        var artifact = Conformance.Discover().Single(a => a.Id == "D14/sw-01");
        const string html = "<p id=status>new offline load</p><script src='sw.js'></script><script>var worker = new Worker('data:text/javascript,' + encodeURIComponent(\"onmessage = function() { postMessage('worker started'); };\")); worker.onmessage = function(event) { document.getElementById('status').textContent = event.data; }; worker.postMessage(0);</script>";
        using var lifecycle = Conformance.ArtifactLifecycle.Start(artifact, html, animationTime: 100);
        var oldDocument = lifecycle.Document;
        var oldEngine = Assert.IsType<Folio.Scripting.Jint.JintScriptEngine>(lifecycle.Host.Loop.Engine);

        Assert.Equal("worker started", oldDocument.QuerySelector("#status")!.TextContent.Trim());

        var persistentKey = artifact.Id + ":reopen-proof";
        lifecycle.Storage.Write(persistentKey, "survives a new document");

        lifecycle.Reopen();

        var retired = Assert.Single(lifecycle.RetiredPages);
        Assert.True(retired.IsDisposed);
        Assert.Same(oldDocument, retired.Document);
        Assert.NotSame(oldDocument, lifecycle.Document);
        Assert.NotSame(oldEngine, lifecycle.Host.Loop.Engine);
        Assert.Throws<ObjectDisposedException>(() => oldEngine.Evaluate("1"));
        Assert.Equal("worker started", lifecycle.Document.QuerySelector("#status")!.TextContent.Trim());
        Assert.Equal("survives a new document", lifecycle.Storage.Read(persistentKey));
        Assert.Equal(2, lifecycle.OfflineRequests.Count(request => request.Url.EndsWith("/sw.js", StringComparison.Ordinal)));
    }
}
