namespace Folio.Tests.Hosting;

public sealed class StageSixRoutingTests
{
    private static readonly FeatureCapabilities AllCapabilities = new()
    {
        WebGl = true, Media = true, Workers = true, WebAssembly = true,
        Iframes = true, WebRtc = true, ServiceWorkers = true
    };

    [Theory]
    [InlineData("<canvas id=c></canvas><script>c.getContext('webgl')</script>")]
    [InlineData("<video></video>")]
    [InlineData("<audio></audio>")]
    [InlineData("<iframe></iframe>")]
    [InlineData("<script>new Worker('worker.js')</script>")]
    [InlineData("<script>WebAssembly.instantiate(bytes)</script>")]
    [InlineData("<script>new RTCPeerConnection()</script>")]
    [InlineData("<script>navigator.serviceWorker.register('sw.js')</script>")]
    [InlineData("<button onclick=\"new Worker('worker.js')\">run</button>")]
    [InlineData("<a href=\"javascript:navigator.serviceWorker.register('sw.js')\">run</a>")]
    public void HostPermissionDoesNotMakeAnUnbuiltFeatureAvailable(string html)
    {
        var result = ArtifactClassifier.Classify(html, new FolioOptions { Capabilities = AllCapabilities });
        Assert.Equal(ArtifactKind.NeedsBrowser, result.Kind);
    }

    public static IEnumerable<object[]> AvailabilityAndGrantCases()
    {
        foreach (var (html, feature, name, scripted) in FeatureCases())
        foreach (var built in new[] { false, true })
        foreach (var granted in new[] { false, true })
            yield return [html, (int)feature, name, scripted, built, granted];
    }

    private static IEnumerable<(string Html, ArtifactClassifier.ImplementedFeatures Feature, string Name, bool Scripted)> FeatureCases()
    {
        yield return ("<canvas id=c></canvas><script>c.getContext('webgl2')</script>", ArtifactClassifier.ImplementedFeatures.WebGl, "WebGL", true);
        yield return ("<script>c['getContext']('experimental-webgl')</script>", ArtifactClassifier.ImplementedFeatures.WebGl, "WebGL", true);
        yield return ("<script>c.getContext?.('webgl')</script>", ArtifactClassifier.ImplementedFeatures.WebGl, "WebGL", true);
        yield return ("<video></video>", ArtifactClassifier.ImplementedFeatures.Media, "media", false);
        yield return ("<audio></audio>", ArtifactClassifier.ImplementedFeatures.Media, "media", false);
        yield return ("<iframe></iframe>", ArtifactClassifier.ImplementedFeatures.Iframes, "iframes", false);
        yield return ("<script>new Worker('worker.js')</script>", ArtifactClassifier.ImplementedFeatures.Workers, "workers", true);
        yield return ("<script>new globalThis['Worker']('worker.js')</script>", ArtifactClassifier.ImplementedFeatures.Workers, "workers", true);
        yield return ("<button onclick=\"new Worker('worker.js')\">run</button>", ArtifactClassifier.ImplementedFeatures.Workers, "workers", true);
        yield return ("<script>WebAssembly.instantiate(bytes)</script>", ArtifactClassifier.ImplementedFeatures.WebAssembly, "WebAssembly", true);
        yield return ("<script>WebAssembly['instantiate'](bytes)</script>", ArtifactClassifier.ImplementedFeatures.WebAssembly, "WebAssembly", true);
        yield return ("<script>new RTCPeerConnection()</script>", ArtifactClassifier.ImplementedFeatures.WebRtc, "WebRTC", true);
        yield return ("<script>navigator.mediaDevices?.['getUserMedia']({video:true})</script>", ArtifactClassifier.ImplementedFeatures.WebRtc, "WebRTC", true);
        yield return ("<script>navigator.mediaDevices.getUserMedia?.({video:true})</script>", ArtifactClassifier.ImplementedFeatures.WebRtc, "WebRTC", true);
        yield return ("<script>navigator.serviceWorker.register('sw.js')</script>", ArtifactClassifier.ImplementedFeatures.ServiceWorkers, "service workers", true);
        yield return ("<script>navigator?.serviceWorker.register('sw.js')</script>", ArtifactClassifier.ImplementedFeatures.ServiceWorkers, "service workers", true);
        yield return ("<script>navigator?.['serviceWorker'].register('sw.js')</script>", ArtifactClassifier.ImplementedFeatures.ServiceWorkers, "service workers", true);
    }

    [Theory]
    [MemberData(nameof(AvailabilityAndGrantCases))]
    public void FeatureRequiresBothItsImplementationAndItsOwnHostGrant(string html, int feature, string name, bool scripted, bool built, bool granted)
    {
        var required = (ArtifactClassifier.ImplementedFeatures)feature;
        var options = new FolioOptions { Capabilities = granted ? Grant(required) : FeatureCapabilities.Default };
        var result = ArtifactClassifier.Classify(html, options, built ? required : ArtifactClassifier.ImplementedFeatures.None);
        Assert.Equal(built && granted ? scripted ? ArtifactKind.Scripted : ArtifactKind.Static : ArtifactKind.NeedsBrowser, result.Kind);
        if (!built) Assert.Contains(result.Reasons, reason => reason.Contains(name) && reason.Contains("not yet complete"));
        if (!granted) Assert.Contains(result.Reasons, reason => reason.Contains(name) && reason.Contains("has not granted"));

        // A different feature's implementation/grant cannot unlock this one.
        var unrelated = required == ArtifactClassifier.ImplementedFeatures.Workers
            ? ArtifactClassifier.ImplementedFeatures.Iframes : ArtifactClassifier.ImplementedFeatures.Workers;
        Assert.Equal(ArtifactKind.NeedsBrowser,
            ArtifactClassifier.Classify(html, new FolioOptions { Capabilities = Grant(unrelated) }, unrelated).Kind);
    }

    private static FeatureCapabilities Grant(ArtifactClassifier.ImplementedFeatures feature) => new()
    {
        WebGl = feature == ArtifactClassifier.ImplementedFeatures.WebGl,
        Media = feature == ArtifactClassifier.ImplementedFeatures.Media,
        Workers = feature == ArtifactClassifier.ImplementedFeatures.Workers,
        WebAssembly = feature == ArtifactClassifier.ImplementedFeatures.WebAssembly,
        Iframes = feature == ArtifactClassifier.ImplementedFeatures.Iframes,
        WebRtc = feature == ArtifactClassifier.ImplementedFeatures.WebRtc,
        ServiceWorkers = feature == ArtifactClassifier.ImplementedFeatures.ServiceWorkers
    };

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SharedWorkersFollowTheDedicatedWorkerCapability(bool built, bool granted)
    {
        var result = ArtifactClassifier.Classify("<script>new SharedWorker('worker.js')</script>",
            new FolioOptions { Capabilities = new() { Workers = granted } },
            built ? ArtifactClassifier.ImplementedFeatures.Workers : ArtifactClassifier.ImplementedFeatures.None);
        Assert.Equal(built && granted ? ArtifactKind.Scripted : ArtifactKind.NeedsBrowser, result.Kind);
        if (!built) Assert.Contains(result.Reasons, reason => reason.Contains("workers") && reason.Contains("not yet complete"));
        if (!granted) Assert.Contains(result.Reasons, reason => reason.Contains("workers") && reason.Contains("has not granted"));
        Assert.Equal(ArtifactKind.NeedsBrowser, ArtifactClassifier.Classify("<script>new window['SharedWorker']('worker.js')</script>",
            new FolioOptions { Capabilities = AllCapabilities }, ArtifactClassifier.ImplementedFeatures.None).Kind);
    }

    [Fact]
    public void SimulatedAvailabilityDoesNotChangeProductionRouting()
    {
        const string html = "<script>new Worker('worker.js')</script>";
        var options = new FolioOptions { Capabilities = AllCapabilities };
        Assert.Equal(ArtifactKind.Scripted, ArtifactClassifier.Classify(html, options, ArtifactClassifier.ImplementedFeatures.Workers).Kind);
        Assert.Equal(ArtifactKind.NeedsBrowser, ArtifactClassifier.Classify(html, options).Kind);
        Assert.Equal(ArtifactKind.Static, ArtifactClassifier.Classify("<p>ordinary page</p>").Kind);
        Assert.Equal(ArtifactKind.Scripted, ArtifactClassifier.Classify("<button onclick='alert(1)'>run</button>").Kind);
    }
}
