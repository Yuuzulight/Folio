using System.Text.Json;
using Folio.Scripting.Dom;
using Folio.Scripting.Native;
using Folio.Skia;

namespace Folio.Scripting.Tests;

public sealed class ChartBridgeTests
{
    // A canvas whose context records every call (and the fill/stroke style in force), with 6px-per-character text.
    private const string Recorder = """
        function fakeCanvas(w, h) {
            var calls = [];
            var ctx = { fillStyle: '#000', strokeStyle: '#000', lineWidth: 1, font: '', textAlign: '', textBaseline: '' };
            ['clearRect','beginPath','moveTo','lineTo','stroke','fill','arc','fillRect','strokeRect','fillText'].forEach(function(name) {
                ctx[name] = function() { calls.push({ name: name, args: Array.prototype.slice.call(arguments), fill: ctx.fillStyle, stroke: ctx.strokeStyle }); };
            });
            ctx.measureText = function(t) { return { width: String(t).length * 6 }; };
            return { width: w, height: h, style: {}, calls: calls, getContext: function() { return ctx; } };
        }
        function draw(config, w, h) { var c = fakeCanvas(w || 480, h || 213); new Chart(c, config); return c.calls; }
        """;

    private const string GridStroke = "rgba(120,120,120,0.15)";

    private static NativeJavaScriptEngine Engine()
    {
        var engine = new NativeJavaScriptEngine();
        ChartBindings.Install(engine);
        engine.Evaluate(Recorder);
        return engine;
    }

    private static JsonElement Json(NativeJavaScriptEngine engine, string expression) =>
        JsonDocument.Parse(engine.Evaluate($"JSON.stringify({expression})").AsString()!).RootElement;

    /// <summary>An engine with a real Folio canvas, whose context parses colours.</summary>
    private static (Folio.Document Document, NativeJavaScriptEngine Engine, HtmlEventLoop Loop) CanvasEngine()
    {
        var document = Folio.Document.Parse("<canvas id='c' width='10' height='10'></canvas>", new FolioOptions { Raster = new SkiaRasterBackend() });
        var engine = new NativeJavaScriptEngine();
        var loop = new HtmlEventLoop(engine);
        DomBindings.Install(engine, document, eventLoop: loop);
        ChartBindings.Install(engine);
        engine.Evaluate("var cx = document.getElementById('c').getContext('2d');");
        return (document, engine, loop);
    }

    [Theory]
    [InlineData("'#5B5BF0'", true)]
    [InlineData("'#abc'", true)]
    [InlineData("'#5B5BF080'", true)]
    [InlineData("'rgb(91, 91, 240)'", true)]
    [InlineData("'rgba(91,91,240,0.5)'", true)]
    [InlineData("'crimson'", true)]
    [InlineData("'hsl(240 80% 60%)'", true)]
    [InlineData("'not a colour'", false)]
    [InlineData("''", false)]
    [InlineData("42", false)]
    [InlineData("null", false)]
    public void RecognisesTheCssColourFormsChartJsAccepts(string value, bool valid)
    {
        var (document, engine, loop) = CanvasEngine();
        using (document) using (engine) using (loop)
            Assert.Equal(valid, engine.Evaluate($"Chart.__bridge.validColour(cx, {value})").AsBoolean());
    }

    [Fact]
    public void ColourArraysGivePerPointColoursThatWrapAndInvalidOnesFallBack()
    {
        var (document, engine, loop) = CanvasEngine();
        using (document) using (engine) using (loop)
        {
            engine.Evaluate("var at = function(v, i) { return Chart.__bridge.colourAt(cx, v, i, 'FALLBACK'); };");
            Assert.Equal("#5B5BF0", engine.Evaluate("at('#5B5BF0', 7)").AsString());
            Assert.Equal("red", engine.Evaluate("at(['blue', 'red'], 1)").AsString());
            Assert.Equal("blue", engine.Evaluate("at(['blue', 'red'], 2)").AsString());
            Assert.Equal("FALLBACK", engine.Evaluate("at(['blue', 'nope'], 1)").AsString());
            Assert.Equal("FALLBACK", engine.Evaluate("at(undefined, 0)").AsString());
            Assert.Equal("FALLBACK", engine.Evaluate("at([], 0)").AsString());
        }
    }

    [Theory]
    [InlineData("0, 5600, 11", "[0,1000,2000,3000,4000,5000,6000]")]
    [InlineData("0, 4, 11", "[0,0.5,1,1.5,2,2.5,3,3.5,4]")]
    [InlineData("0, 4.5, 11", "[0,0.5,1,1.5,2,2.5,3,3.5,4,4.5]")]
    [InlineData("0, 87, 11", "[0,10,20,30,40,50,60,70,80,90]")]
    [InlineData("0, 25, 6", "[0,5,10,15,20,25]")]
    [InlineData("3, 17, 11", "[2,4,6,8,10,12,14,16,18]")]
    [InlineData("0, 100, 11, null, 60", "[0,10,20,30,40,50,60]")]
    [InlineData("0, 100, 11, null, 55", "[0,10,20,30,40,50,55]")]
    [InlineData("0, 100, 11, 20, 80", "[20,30,40,50,60,70,80]")]
    [InlineData("0.1, 0.35, 11", "[0.1,0.15,0.2,0.25,0.3,0.35]")]
    public void TicksUseRoundStepsAndHonourMinAndMax(string arguments, string expected)
    {
        using var engine = Engine();
        Assert.Equal(expected, engine.Evaluate($"JSON.stringify(Chart.__bridge.niceTicks({arguments}))").AsString());
    }

    [Fact]
    public void TicksLeaveHeadroomAboveTheDataMaximum()
    {
        using var engine = Engine();
        Assert.Equal(6000, Json(engine, "Chart.__bridge.niceTicks(0, 5600, 11).slice(-1)[0]").GetDouble());
        Assert.Equal(100, Json(engine, "Chart.__bridge.niceTicks(0, 93, 11).slice(-1)[0]").GetDouble());
    }

    [Theory]
    [InlineData("0, 1", "0")]
    [InlineData("5000, 1000", "5,000")]
    [InlineData("1.5, 0.5", "1.5")]
    [InlineData("2, 0.5", "2.0")]
    [InlineData("-10, 5", "-10")]
    public void TickLabelsKeepTheStepsPrecisionAndGroupThousands(string arguments, string expected)
    {
        using var engine = Engine();
        Assert.Equal(expected, engine.Evaluate($"Chart.__bridge.formatTick({arguments})").AsString());
    }

    [Fact]
    public void TitleAndLegendTakeChartJsSpaceAtTheTop()
    {
        using var engine = Engine();
        var frame = Json(engine, "Chart.__bridge.layout({ data: { datasets: [{ label: 'A' }, { label: 'B' }] }, options: { plugins: { title: { display: true, text: 'T' } } } }, 480, function(t) { return t.length * 6; })");
        Assert.Equal(34.4, frame.GetProperty("titleHeight").GetDouble(), 6);
        Assert.Equal(32, frame.GetProperty("legendHeight").GetDouble());
        Assert.Equal(66.4, frame.GetProperty("top").GetDouble(), 6);
        Assert.Equal(0, frame.GetProperty("bottom").GetDouble());
        Assert.Equal("top", frame.GetProperty("legend").GetProperty("position").GetString());
    }

    [Fact]
    public void LegendAtTheBottomTakesBottomSpaceAndWrapsIntoRows()
    {
        using var engine = Engine();
        var frame = Json(engine, "Chart.__bridge.layout({ data: { datasets: [{ label: 'aaaaaaaaaa' }, { label: 'bbbbbbbbbb' }, { label: 'cccccccccc' }] }, options: { plugins: { legend: { position: 'bottom' } } } }, 200, function(t) { return t.length * 6; })");
        Assert.Equal(0, frame.GetProperty("top").GetDouble());
        Assert.Equal(10 + 3 * 22, frame.GetProperty("bottom").GetDouble());
        Assert.Equal(3, frame.GetProperty("legend").GetProperty("rows").GetArrayLength());
    }

    [Theory]
    [InlineData("{ plugins: { legend: { display: false } } }", "[{ label: 'A' }]")]
    [InlineData("{}", "[{ data: [1] }]")]
    public void NoLegendWhenHiddenOrWhenNoDatasetHasALabel(string options, string datasets)
    {
        using var engine = Engine();
        var frame = Json(engine, $"Chart.__bridge.layout({{ data: {{ datasets: {datasets} }}, options: {options} }}, 480, function(t) {{ return 6; }})");
        Assert.Equal(JsonValueKind.Null, frame.GetProperty("legend").ValueKind);
        Assert.Equal(0, frame.GetProperty("top").GetDouble());
    }

    [Fact]
    public void TitleIsOffUnlessDisplayed()
    {
        using var engine = Engine();
        Assert.Equal(0, Json(engine, "Chart.__bridge.layout({ data: { datasets: [] }, options: { plugins: { title: { text: 'T' } } } }, 480, function() { return 1; }).titleHeight").GetDouble());
    }

    [Fact]
    public void GroupedBarsDrawOneBarPerDatasetPerCategoryInTheirOwnColours()
    {
        using var engine = Engine();
        var bars = Json(engine, """
            draw({ type: 'bar', data: { labels: ['A', 'B'], datasets: [
                { label: 'x', data: [1, 2], backgroundColor: '#5B5BF0' },
                { label: 'y', data: [2, 3], backgroundColor: '#A5A1FF' },
                { label: 'z', data: [3, 4], backgroundColor: '#2E2A8A' }] } })
              .filter(function(c) { return c.name === 'fillRect' && c.args[2] !== 40; })
              .map(function(c) { return { x: c.args[0], w: c.args[2], fill: c.fill }; })
            """);
        Assert.Equal(6, bars.GetArrayLength());
        // Datasets are drawn one after the other: dataset 0's two bars, then dataset 1's, then dataset 2's.
        string[] colours = ["#5B5BF0", "#5B5BF0", "#A5A1FF", "#A5A1FF", "#2E2A8A", "#2E2A8A"];
        for (var i = 0; i < 6; i++)
            Assert.Equal(colours[i], bars[i].GetProperty("fill").GetString());
        double X(int i) => bars[i].GetProperty("x").GetDouble();
        Assert.True(X(0) < X(2) && X(2) < X(4)); // side by side within category A
        Assert.True(X(4) < X(1));                // category B starts after category A's group
        Assert.Equal(bars[0].GetProperty("w").GetDouble(), bars[5].GetProperty("w").GetDouble(), 6);
    }

    [Fact]
    public void BarColourArraysApplyPerPointAndDefaultsApplyWithoutColours()
    {
        using var engine = Engine();
        var fills = Json(engine, """
            draw({ type: 'bar', data: { labels: ['A', 'B', 'C'], datasets: [{ data: [1, 2, 3], backgroundColor: ['red', 'green'] }] } })
              .filter(function(c) { return c.name === 'fillRect'; }).map(function(c) { return c.fill; })
            """);
        Assert.Equal(["red", "green", "red"], fills.EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal("rgba(54,162,235,0.5)", Json(engine, "draw({ type: 'bar', data: { labels: ['A'], datasets: [{ data: [1] }] } }).filter(function(c) { return c.name === 'fillRect'; })[0].fill").GetString());
    }

    [Fact]
    public void BarBordersDrawOnlyWithABorderWidth()
    {
        using var engine = Engine();
        Assert.Equal(0, Json(engine, "draw({ type: 'bar', data: { labels: ['A'], datasets: [{ data: [1], borderColor: 'red' }] } }).filter(function(c) { return c.name === 'strokeRect'; }).length").GetDouble());
        Assert.Equal("red", Json(engine, "draw({ type: 'bar', data: { labels: ['A'], datasets: [{ data: [1], borderColor: 'red', borderWidth: 2 }] } }).filter(function(c) { return c.name === 'strokeRect'; })[0].stroke").GetString());
    }

    [Fact]
    public void SeveralLinesAreDrawnWithTheirOwnColoursAndPoints()
    {
        using var engine = Engine();
        var drawn = Json(engine, $$"""
            draw({ type: 'line', data: { labels: ['a', 'b', 'c'], datasets: [
                { label: 'one', data: [1, 3, 2], borderColor: 'rgb(10,20,30)', backgroundColor: 'rgba(10,20,30,0.5)' },
                { label: 'two', data: [2, 1, 3], borderColor: '#ff0000', backgroundColor: '#ff000080' }] } })
              .filter(function(c) { return c.name === 'arc' || (c.name === 'stroke' && c.stroke !== '{{GridStroke}}'); })
              .map(function(c) { return c.name + ':' + c.stroke; })
            """).EnumerateArray().Select(e => e.GetString()).ToArray();
        // Per dataset: the line stroke, then a point (an arc, outlined in the line colour) at each of its three values.
        Assert.Equal(4, drawn.Count(s => s == "stroke:rgb(10,20,30)"));
        Assert.Equal(3, drawn.Count(s => s == "arc:rgb(10,20,30)"));
        Assert.Equal(3, drawn.Count(s => s == "arc:#ff0000"));
        Assert.Equal(["stroke:rgb(10,20,30)", "stroke:#ff0000"], drawn.Where(s => s.StartsWith("stroke:", StringComparison.Ordinal)).Distinct().ToArray());
    }

    [Fact]
    public void ALineGapsAtMissingValues()
    {
        using var engine = Engine();
        Assert.Equal(2, Json(engine, """
            draw({ type: 'line', data: { labels: ['a', 'b', 'c', 'd'], datasets: [{ data: [1, null, 3, 4], borderColor: 'red' }] } })
              .filter(function(c) { return c.name === 'moveTo' && c.stroke === 'red'; }).length
            """).GetDouble());
    }

    [Fact]
    public void BeginAtZeroAndMinMaxShapeTheYAxis()
    {
        using var engine = Engine();
        string[] Labels(string config) => Json(engine, $"draw({config}).filter(function(c) {{ return c.name === 'fillText' && c.args[0] !== 'a' && c.args[0] !== 'b' && c.args[1] < 30; }}).map(function(c) {{ return c.args[0]; }})")
            .EnumerateArray().Select(e => e.GetString()!).ToArray();
        // A bar chart starts at zero, a line chart at its data, unless told otherwise.
        Assert.Equal("0", Labels("{ type: 'bar', data: { labels: ['a','b'], datasets: [{ data: [50, 60] }] } }")[0]);
        Assert.DoesNotContain("0", Labels("{ type: 'line', data: { labels: ['a','b'], datasets: [{ data: [50, 60] }] } }"));
        Assert.Contains("0", Labels("{ type: 'line', data: { labels: ['a','b'], datasets: [{ data: [50, 60] }] }, options: { scales: { y: { beginAtZero: true } } } }"));
        Assert.Contains("100", Labels("{ type: 'bar', data: { labels: ['a'], datasets: [{ data: [5] }] }, options: { scales: { y: { max: 100 } } } }"));
    }

    [Fact]
    public void TheTitleIsDrawnAboveTheLegend()
    {
        using var engine = Engine();
        var texts = Json(engine, """
            draw({ type: 'bar', data: { labels: ['a'], datasets: [{ label: 'Orders', data: [1] }] }, options: { plugins: { title: { display: true, text: 'Report' } } } })
              .filter(function(c) { return c.name === 'fillText'; }).map(function(c) { return [c.args[0], c.args[2]]; })
            """);
        var title = texts.EnumerateArray().First(t => t[0].GetString() == "Report");
        var legend = texts.EnumerateArray().First(t => t[0].GetString() == "Orders");
        Assert.True(title[1].GetDouble() < legend[1].GetDouble());
        Assert.True(legend[1].GetDouble() < 60);
    }
}
