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
        ["hero-thinned-ticks"] = """
            {type:'bar', data:{labels:['Apr','May','Jun','Jul','Aug','Sep'], datasets:[
              {label:'Orders (thousands)', data:[2.8,3.1,3.4,3.9,4.1,4.5], backgroundColor:'#5B5BF0'}]}}
            """,
        ["default-palette"] = """
            {type:'bar', data:{labels:['A','B','C'], datasets:[
              {label:'one', data:[3,5,2]}, {label:'two', data:[4,2,6]}, {label:'three', data:[1,4,3]}, {label:'four', data:[2,3,5]}]}}
            """,
        ["stacked-bars"] = """
            {type:'bar', data:{labels:['A','B','C'], datasets:[
              {label:'one', data:[3,4,2], backgroundColor:'#5B5BF0'}, {label:'two', data:[2,1,5], backgroundColor:'#A5A1FF'}]},
             options:{scales:{x:{stacked:true}, y:{stacked:true}}}}
            """,
        ["horizontal-bars"] = """
            {type:'bar', data:{labels:['Alpha','Beta','Gamma'], datasets:[{label:'Score', data:[5,9,3], backgroundColor:'#5B5BF0'}]},
             options:{indexAxis:'y'}}
            """,
        ["line-fill-tension-dash"] = """
            {type:'line', data:{labels:['A','B','C','D'], datasets:[
              {label:'one', data:[3,4,2,5], borderColor:'#5B5BF0', backgroundColor:'rgba(91,91,240,0.2)', fill:true, tension:0.4, pointRadius:5, borderDash:[5,5]},
              {label:'two', data:[2,3,3,4], borderColor:'#2E2A8A', pointBackgroundColor:['red','green','blue','orange'], pointRadius:4}]}}
            """,
        ["pie-default-colours"] = """
            {type:'pie', data:{labels:['Red','Blue','Yellow'], datasets:[{label:'Votes', data:[12,19,8]}]}}
            """,
        ["doughnut-legend-right"] = """
            {type:'doughnut', data:{labels:['North','South','East'], datasets:[{data:[12,19,8], backgroundColor:['#5B5BF0','#A5A1FF','#2E2A8A']}]},
             options:{plugins:{legend:{position:'right'}}, cutout:'60%'}}
            """,
        ["legend-left-title-bottom"] = """
            {type:'bar', data:{labels:['A','B'], datasets:[
              {label:'one', data:[3,4], backgroundColor:'#5B5BF0'}, {label:'two', data:[2,1], backgroundColor:'#A5A1FF'}]},
             options:{plugins:{legend:{position:'left'}, title:{display:true, text:'Bottom title', position:'bottom', align:'start'}}}}
            """,
        ["bar-radius-grace-callback"] = """
            {type:'bar', data:{labels:['A','B','C'], datasets:[
              {label:'Net', data:[40,-15,25], backgroundColor:['#5B5BF0','#A5A1FF','#2E2A8A'], borderRadius:6, barPercentage:0.6}]},
             options:{scales:{y:{grace:'10%', ticks:{callback:function(v){return '$' + v;}}}}, plugins:{legend:{display:false}}}}
            """,
        ["thinned-category-labels"] = """
            {type:'line', data:{labels:['2001','2002','2003','2004','2005','2006','2007','2008','2009','2010','2011','2012','2013','2014','2015','2016','2017','2018','2019','2020'],
              datasets:[{label:'Index', data:[3,5,4,6,8,7,9,12,10,11,13,12,15,14,16,18,17,19,21,20], borderColor:'#5B5BF0', backgroundColor:'#5B5BF0', pointRadius:2}]},
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
