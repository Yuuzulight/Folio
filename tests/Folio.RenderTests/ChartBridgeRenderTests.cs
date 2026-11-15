using Folio.Scripting;
using Folio.Scripting.Dom;
using Folio.Scripting.Native;
using Folio.Skia;

namespace Folio.RenderTests;

/// <summary>
/// Renders pages whose Chart.js script is replaced by Folio's native chart bridge and compares them with reviewed goldens
/// under tests/goldens/chart-bridge. The pages are inline so each case reads as its config.
/// </summary>
public class ChartBridgeRenderTests
{
    private const string Library = "<script src=\"https://cdn.jsdelivr.net/npm/chart.js@4.4.7/dist/chart.umd.min.js\"></script>";

    private static readonly Dictionary<string, string> Cases = new(StringComparer.Ordinal)
    {
        ["grouped-bars-legend"] = """
            {type:'bar', data:{labels:['Q1','Q2','Q3','Q4'], datasets:[
              {label:'North', data:[12,19,14,22], backgroundColor:'#5B5BF0'},
              {label:'South', data:[9,15,18,17], backgroundColor:'#A5A1FF'},
              {label:'East', data:[7,11,10,16], backgroundColor:'#2E2A8A'}]},
             options:{plugins:{legend:{position:'top'}}}}
            """,
        ["line-two-datasets-title"] = """
            {type:'line', data:{labels:['Mon','Tue','Wed','Thu','Fri'], datasets:[
              {label:'Visits', data:[120,180,150,240,210], borderColor:'rgb(91,91,240)', backgroundColor:'rgba(91,91,240,0.5)'},
              {label:'Sign-ups', data:[30,55,40,90,75], borderColor:'crimson', backgroundColor:'#DC143C80'}]},
             options:{plugins:{title:{display:true,text:'Weekly traffic'}}}}
            """,
        ["bar-max-5600"] = """
            {type:'bar', data:{labels:['Jan','Feb','Mar','Apr'], datasets:[
              {label:'Revenue', data:[1800,3200,5600,4100], backgroundColor:['#5B5BF0','#A5A1FF','#2E2A8A','rgb(91,91,240)']}]},
             options:{plugins:{legend:{display:false}}}}
            """,
    };

    public static TheoryData<string> Names => new(Cases.Keys);

    public static IReadOnlySet<string> GoldenNames { get; } =
        new HashSet<string>(Cases.Keys.Select(name => "chart-bridge/" + name), StringComparer.Ordinal);

    [Theory]
    [MemberData(nameof(Names))]
    public void MatchesItsGolden(string name) =>
        Assert.Null(GoldenSuite.Repo.Check("chart-bridge/" + name, Render(Cases[name])));

    private static PixelBuffer Render(string config)
    {
        var html = "<!doctype html><html><body style='margin:0;background:white'>" +
                   "<canvas id='chart' width='480' height='213' style='display:block'></canvas>" + Library +
                   "<script>new Chart(document.getElementById('chart')," + config + ");</script></body></html>";
        var options = new FolioOptions
        {
            Fonts = TestRenderer.Options.Fonts,
            BaseUri = new Uri("https://chart.invalid/"),
            Scripting = ScriptingPolicy.ContentProcess,
            Raster = new SkiaRasterBackend(),
        };
        using var document = Document.Parse(html, options);
        document.Paint(480, 213);
        using var engine = new NativeJavaScriptEngine();
        var clock = new VirtualEventLoopClock();
        using var loop = new HtmlEventLoop(engine, clock);
        DomBindings.Install(engine, document, eventLoop: loop);
        ScriptGlobalsInstaller.Install(loop, engine);
        using var runner = new ScriptRunner(document.Node, engine, loop, options);
        runner.Run();
        loop.PerformMicrotaskCheckpoint();
        loop.UpdateTheRendering();
        using var result = HeadlessRenderer.Render(document, new RenderRequest(480, 213));
        return new PixelBuffer(result.Width, result.Height, result.Pixels.ToArray());
    }
}
