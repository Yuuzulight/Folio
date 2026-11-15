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
            var ctx = { fillStyle: '#000', strokeStyle: '#000', lineWidth: 1, font: '', textAlign: '', textBaseline: '', lineJoin: '' };
            ['clearRect','beginPath','closePath','moveTo','lineTo','bezierCurveTo','stroke','fill','arc','fillRect','strokeRect','roundRect','fillText','setLineDash'].forEach(function(name) {
                ctx[name] = function() { calls.push({ name: name, args: Array.prototype.slice.call(arguments), fill: ctx.fillStyle, stroke: ctx.strokeStyle, lw: ctx.lineWidth }); };
            });
            ctx.measureText = function(t) { return { width: String(t).length * 6 }; };
            return { width: w, height: h, style: {}, calls: calls, getContext: function() { return ctx; } };
        }
        function draw(config, w, h) { var c = fakeCanvas(w || 480, h || 213); new Chart(c, config); return c.calls; }
        function measure(t) { return String(t).length * 6; }
        function frameOf(config, w, h) {
            var ctx = fakeCanvas(w || 480, h || 213).getContext();
            var type = config.type || 'bar';
            var colours = Chart.__bridge.resolveColours(ctx, config, type);
            return Chart.__bridge.layout(config, Chart.__bridge.legendItems(ctx, config, type, colours), w || 480, h || 213, measure);
        }
        function fills(calls) { return calls.filter(function(c) { return c.name === 'fillRect'; }); }
        function texts(calls) { return calls.filter(function(c) { return c.name === 'fillText'; }).map(function(c) { return [c.args[0], c.args[1], c.args[2]]; }); }
        """;

    private const string GridStroke = "rgba(0,0,0,0.1)";

    private static NativeJavaScriptEngine Engine()
    {
        var engine = new NativeJavaScriptEngine();
        ChartBindings.Install(engine);
        engine.Evaluate(Recorder);
        return engine;
    }

    private static JsonElement Json(NativeJavaScriptEngine engine, string expression) =>
        JsonDocument.Parse(engine.Evaluate($"JSON.stringify({expression})").AsString()!).RootElement;

    private static string[] Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()!).ToArray();

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

    [Fact]
    public void DatasetsWithoutColoursGetChartJsPaletteAndPartialColoursKeepTheFaintDefault()
    {
        using var engine = Engine();
        var palette = Json(engine, "[0, 1, 2, 7].map(function(i) { var ds = []; for (var k = 0; k <= i; k++) ds.push({ label: 'd' + k, data: [1] }); return Chart.__bridge.resolveColours(fakeCanvas(1, 1).getContext(), { type: 'bar', data: { datasets: ds } }, 'bar')[i]; })");
        Assert.Equal("rgba(54,162,235,0.5)", palette[0].GetProperty("bg").GetString());
        Assert.Equal("rgb(54,162,235)", palette[0].GetProperty("border").GetString());
        Assert.Equal("rgba(255,99,132,0.5)", palette[1].GetProperty("bg").GetString());
        Assert.Equal("rgba(255,159,64,0.5)", palette[2].GetProperty("bg").GetString());
        Assert.Equal("rgba(54,162,235,0.5)", palette[3].GetProperty("bg").GetString()); // the eighth dataset wraps
        var partial = Json(engine, "Chart.__bridge.resolveColours(fakeCanvas(1, 1).getContext(), { type: 'line', data: { datasets: [{ borderColor: 'red' }, {}] } }, 'line')");
        Assert.Equal("rgba(0,0,0,0.1)", partial[1].GetProperty("bg").GetString());
        Assert.Equal("red", partial[0].GetProperty("border").GetString());
        Assert.Equal("rgb(54,162,235)", Json(engine, "Chart.__bridge.resolveColours(fakeCanvas(1, 1).getContext(), { type: 'line', data: { datasets: [{}] } }, 'line')[0].border").GetString());
        Assert.Equal("rgba(0,0,0,0.1)", Json(engine, "Chart.__bridge.resolveColours(fakeCanvas(1, 1).getContext(), { type: 'line', options: { plugins: { colors: { enabled: false } } }, data: { datasets: [{}] } }, 'line')[0].bg").GetString());
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
    [InlineData("1.4, 0.39999999999", "1.4")]
    [InlineData("-10, 5", "-10")]
    public void TickLabelsKeepTheStepsPrecisionAndGroupThousands(string arguments, string expected)
    {
        using var engine = Engine();
        Assert.Equal(expected, engine.Evaluate($"Chart.__bridge.formatTick({arguments})").AsString());
    }

    [Theory]
    [InlineData("10, 152.6, 17.4, false", "[0,2,4,6,8]")]   // the hero chart: ten ticks do not fit, every second one stays
    [InlineData("9, 152.6, 17.4, false", "[0,1,2,3,4,5,6,7,8]")]
    [InlineData("4, 400, 40, true", "[0,1,2,3]")]
    [InlineData("20, 400, 40, true", "[0,2,4,6,8,10,12,14,16,18]")]
    [InlineData("20, 400, 40, false", "[0,2,4,6,8,10,12,14,16,18]")]
    [InlineData("11, 400, 40, false", "[0,1,2,3,4,5,6,7,8,9,10]")] // eleven ticks sit on the edges of ten gaps
    public void CrowdedTicksAreThinnedTheWayChartJsAutoSkipsThem(string arguments, string expected)
    {
        using var engine = Engine();
        Assert.Equal(expected, engine.Evaluate($"JSON.stringify(Chart.__bridge.skipIndices({arguments}))").AsString());
    }

    [Fact]
    public void RangeHonoursSuggestedBoundsAndGrace()
    {
        using var engine = Engine();
        string R(string call) => engine.Evaluate($"JSON.stringify(Chart.__bridge.range({call}))").AsString()!;
        Assert.Equal("[5,45]", R("{ grace: 5, suggestedMax: 40 }, 10, 20, false"));
        Assert.Equal("[9,21]", R("{ grace: '10%' }, 10, 20, false"));
        Assert.Equal("[0,40]", R("{ suggestedMax: 40 }, 3, 20, true"));
        Assert.Equal("[10,20]", R("{ suggestedMax: 15, suggestedMin: 12 }, 10, 20, false")); // suggestions never shrink the data
        Assert.Equal("[0,25]", R("{ grace: 5 }, 0, 20, true")); // grace keeps a zero baseline
    }

    [Fact]
    public void TitleAndLegendTakeChartJsSpaceAtTheTop()
    {
        using var engine = Engine();
        var frame = Json(engine, "frameOf({ data: { datasets: [{ label: 'A' }, { label: 'B' }] }, options: { plugins: { title: { display: true, text: 'T' } } } })");
        Assert.Equal(34.4, frame.GetProperty("title").GetProperty("height").GetDouble(), 6);
        Assert.Equal(32, frame.GetProperty("legend").GetProperty("height").GetDouble());
        Assert.Equal(66.4, frame.GetProperty("top").GetDouble(), 6);
        Assert.Equal(0, frame.GetProperty("bottom").GetDouble());
        Assert.Equal("top", frame.GetProperty("legend").GetProperty("position").GetString());
    }

    [Fact]
    public void LegendAtTheBottomTakesBottomSpaceAndWrapsIntoRows()
    {
        using var engine = Engine();
        var frame = Json(engine, "frameOf({ data: { datasets: [{ label: 'aaaaaaaaaa' }, { label: 'bbbbbbbbbb' }, { label: 'cccccccccc' }] }, options: { plugins: { legend: { position: 'bottom' } } } }, 200)");
        Assert.Equal(0, frame.GetProperty("top").GetDouble());
        Assert.Equal(10 + 3 * 22, frame.GetProperty("bottom").GetDouble());
        Assert.Equal(3, frame.GetProperty("legend").GetProperty("rows").GetArrayLength());
    }

    [Theory]
    [InlineData("left")]
    [InlineData("right")]
    public void LegendAtTheSideIsAsWideAsItsWidestEntryPlusPadding(string position)
    {
        using var engine = Engine();
        var frame = Json(engine, $"frameOf({{ data: {{ datasets: [{{ label: 'ab' }}, {{ label: 'abcd' }}] }}, options: {{ plugins: {{ legend: {{ position: '{position}' }} }} }} }})");
        // box 40 + gap 6 + widest text (4 chars at 6px) + 10px padding either side
        Assert.Equal(40 + 6 + 24 + 20, frame.GetProperty(position).GetDouble());
        Assert.Equal(0, frame.GetProperty("top").GetDouble());
        Assert.Equal(2 * 12 + 10, frame.GetProperty("legend").GetProperty("height").GetDouble());
    }

    [Fact]
    public void LegendLabelOptionsSetBoxSizeAndPadding()
    {
        using var engine = Engine();
        var frame = Json(engine, "frameOf({ data: { datasets: [{ label: 'a' }] }, options: { plugins: { legend: { labels: { boxWidth: 20, boxHeight: 8, padding: 4 } } } } })");
        Assert.Equal(20, frame.GetProperty("legend").GetProperty("boxWidth").GetDouble());
        Assert.Equal(4 + 12 + 4, frame.GetProperty("legend").GetProperty("height").GetDouble()); // padding + item (max of box and font) + padding
    }

    [Theory]
    [InlineData("{ plugins: { legend: { display: false } } }")]
    public void NoLegendWhenHidden(string options)
    {
        using var engine = Engine();
        var frame = Json(engine, $"frameOf({{ data: {{ datasets: [{{ label: 'A' }}] }}, options: {options} }})");
        Assert.Equal(JsonValueKind.Null, frame.GetProperty("legend").ValueKind);
        Assert.Equal(0, frame.GetProperty("top").GetDouble());
    }

    [Fact]
    public void ADatasetWithoutALabelStillGetsALegendEntryAsChartJsDrawsIt()
    {
        using var engine = Engine();
        // Chart.js prints the undefined label as text; so does the bridge.
        Assert.Equal(["undefined"], Strings(Json(engine, "frameOf({ data: { datasets: [{ data: [1] }] } }).legend.entries.map(function(e) { return e.text; })")));
    }

    [Fact]
    public void TitleOptionsPositionAndSizeIt()
    {
        using var engine = Engine();
        var frame = Json(engine, "frameOf({ data: { datasets: [] }, options: { plugins: { title: { display: true, text: ['One', 'Two'], position: 'bottom', align: 'end', font: { size: 20 } } } } })");
        Assert.Equal(2 * 20 * 1.2 + 20, frame.GetProperty("title").GetProperty("height").GetDouble(), 6);
        Assert.Equal(frame.GetProperty("title").GetProperty("height").GetDouble(), frame.GetProperty("bottom").GetDouble(), 6);
        Assert.Equal("end", frame.GetProperty("title").GetProperty("align").GetString());
        var drawn = Json(engine, "draw({ type: 'bar', data: { labels: ['a'], datasets: [{ data: [1] }] }, options: { plugins: { legend: { display: false }, title: { display: true, text: 'Hi', position: 'bottom', align: 'start' } } } }).filter(function(c) { return c.name === 'fillText' && c.args[0] === 'Hi'; }).map(function(c) { return c.args; })");
        Assert.Equal(0, drawn[0][1].GetDouble());
        Assert.True(drawn[0][2].GetDouble() > 190);
    }

    [Fact]
    public void TitleIsOffUnlessDisplayed()
    {
        using var engine = Engine();
        Assert.Equal(JsonValueKind.Null, Json(engine, "frameOf({ data: { datasets: [] }, options: { plugins: { title: { text: 'T' } } } }).title").ValueKind);
    }

    [Fact]
    public void GroupedBarsDrawOneBarPerDatasetPerCategoryInTheirOwnColours()
    {
        using var engine = Engine();
        var bars = Json(engine, """
            fills(draw({ type: 'bar', data: { labels: ['A', 'B'], datasets: [
                { label: 'x', data: [1, 2], backgroundColor: '#5B5BF0' },
                { label: 'y', data: [2, 3], backgroundColor: '#A5A1FF' },
                { label: 'z', data: [3, 4], backgroundColor: '#2E2A8A' }] } }))
              .filter(function(c) { return c.args[2] !== 40; })
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
    public void BarColourArraysApplyPerPointAndTheDefaultPaletteAppliesWithoutColours()
    {
        using var engine = Engine();
        var swatchFills = Json(engine, """
            fills(draw({ type: 'bar', data: { labels: ['A', 'B', 'C'], datasets: [{ data: [1, 2, 3], backgroundColor: ['red', 'green'] }] }, options: { plugins: { legend: { display: false } } } })).map(function(c) { return c.fill; })
            """);
        Assert.Equal(["red", "green", "red"], Strings(swatchFills));
        Assert.Equal("rgba(54,162,235,0.5)", Json(engine, "fills(draw({ type: 'bar', data: { labels: ['A'], datasets: [{ data: [1] }] }, options: { plugins: { legend: { display: false } } } }))[0].fill").GetString());
    }

    [Fact]
    public void BarPercentagesChangeBarWidthAndGroupSpread()
    {
        using var engine = Engine();
        double Width(string dataset) => Json(engine, $"fills(draw({{ type: 'bar', data: {{ labels: ['A'], datasets: [{dataset}] }}, options: {{ plugins: {{ legend: {{ display: false }} }} }} }}))[0].args[2]").GetDouble();
        var normal = Width("{ data: [1] }");
        Assert.Equal(normal * 0.5 / 0.9, Width("{ data: [1], barPercentage: 0.5 }"), 6);
        Assert.Equal(normal * 0.4 / 0.8, Width("{ data: [1], categoryPercentage: 0.4 }"), 6);
    }

    [Fact]
    public void StackedBarsShareOneSlotAndRiseFromEachOther()
    {
        using var engine = Engine();
        var bars = Json(engine, """
            fills(draw({ type: 'bar', data: { labels: ['A'], datasets: [{ data: [3], backgroundColor: 'red' }, { data: [2], backgroundColor: 'blue' }] },
                options: { scales: { x: { stacked: true }, y: { stacked: true } }, plugins: { legend: { display: false } } } }))
              .map(function(c) { return c.args; })
            """);
        Assert.Equal(2, bars.GetArrayLength());
        Assert.Equal(bars[0][0].GetDouble(), bars[1][0].GetDouble(), 6);                              // same column
        Assert.Equal(bars[1][1].GetDouble() + bars[1][3].GetDouble(), bars[0][1].GetDouble(), 6);     // the second sits on the first's top
    }

    [Fact]
    public void StackedAxisCoversTheSum()
    {
        using var engine = Engine();
        var labels = Strings(Json(engine, """
            texts(draw({ type: 'bar', data: { labels: ['A'], datasets: [{ data: [30] }, { data: [25] }] },
                options: { scales: { y: { stacked: true } }, plugins: { legend: { display: false } } } }))
              .filter(function(t) { return t[1] < 30; }).map(function(t) { return t[0]; })
            """));
        Assert.Equal("60", labels[^1]);
    }

    [Fact]
    public void HorizontalBarsRunAlongTheXAxisAndListCategoriesDownTheLeft()
    {
        using var engine = Engine();
        var bars = Json(engine, """
            fills(draw({ type: 'bar', data: { labels: ['Alpha', 'Beta'], datasets: [{ data: [5, 10], backgroundColor: 'red' }] },
                options: { indexAxis: 'y', plugins: { legend: { display: false } } } })).map(function(c) { return c.args; })
            """);
        Assert.Equal(2, bars.GetArrayLength());
        Assert.True(bars[1][1].GetDouble() > bars[0][1].GetDouble());        // the second category is lower
        Assert.True(bars[1][2].GetDouble() > bars[0][2].GetDouble() * 1.9);  // and its bar is twice as long
        var names = Json(engine, "texts(draw({ type: 'bar', data: { labels: ['Alpha'], datasets: [{ data: [5] }] }, options: { indexAxis: 'y', plugins: { legend: { display: false } } } })).filter(function(t) { return t[0] === 'Alpha'; })[0]");
        Assert.True(names[1].GetDouble() < 80);
    }

    [Fact]
    public void BorderRadiusRoundsTheFarEndOfABar()
    {
        using var engine = Engine();
        var round = Json(engine, """
            draw({ type: 'bar', data: { labels: ['A', 'B'], datasets: [{ data: [10, -5], borderRadius: 6 }] }, options: { plugins: { legend: { display: false } } } })
              .filter(function(c) { return c.name === 'roundRect'; }).map(function(c) { return c.args[4]; })
            """);
        Assert.Equal(2, round.GetArrayLength());
        Assert.Equal([6, 6, 0, 0], round[0].EnumerateArray().Select(e => e.GetInt32()).ToArray()); // positive: rounded at the top
        Assert.Equal([0, 0, 6, 6], round[1].EnumerateArray().Select(e => e.GetInt32()).ToArray()); // negative: rounded at the bottom
    }

    [Fact]
    public void BarBordersDrawOnlyWithABorderWidth()
    {
        using var engine = Engine();
        const string off = "options: { plugins: { legend: { display: false } } }";
        Assert.Equal(0, Json(engine, $"draw({{ type: 'bar', data: {{ labels: ['A'], datasets: [{{ data: [1], borderColor: 'red' }}] }}, {off} }}).filter(function(c) {{ return c.name === 'strokeRect'; }}).length").GetDouble());
        Assert.Equal("red", Json(engine, $"draw({{ type: 'bar', data: {{ labels: ['A'], datasets: [{{ data: [1], borderColor: 'red', borderWidth: 2 }}] }}, {off} }}).filter(function(c) {{ return c.name === 'strokeRect'; }})[0].stroke").GetString());
    }

    [Fact]
    public void SeveralLinesAreDrawnWithTheirOwnColoursAndPoints()
    {
        using var engine = Engine();
        var drawn = Strings(Json(engine, $$"""
            draw({ type: 'line', data: { labels: ['a', 'b', 'c'], datasets: [
                { label: 'one', data: [1, 3, 2], borderColor: 'rgb(10,20,30)', backgroundColor: 'rgba(10,20,30,0.5)' },
                { label: 'two', data: [2, 1, 3], borderColor: '#ff0000', backgroundColor: '#ff000080' }] } })
              .filter(function(c) { return c.name === 'arc' || (c.name === 'stroke' && c.stroke !== '{{GridStroke}}'); })
              .map(function(c) { return c.name + ':' + c.stroke; })
            """));
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
            draw({ type: 'line', data: { labels: ['a', 'b', 'c', 'd'], datasets: [{ data: [1, null, 3, 4], borderColor: 'red' }] }, options: { plugins: { legend: { display: false } } } })
              .filter(function(c) { return c.name === 'stroke' && c.stroke === 'red' && c.lw === 3; }).length
            """).GetDouble());
    }

    [Fact]
    public void TensionDrawsCurvesAndZeroTensionDrawsStraightSegments()
    {
        using var engine = Engine();
        Func<string, double> count = name => Json(engine, $"draw({{ type: 'line', data: {{ labels: ['a','b','c'], datasets: [{{ data: [1,3,2], tension: {name} }}] }}, options: {{ plugins: {{ legend: {{ display: false }} }} }} }}).filter(function(c) {{ return c.name === 'bezierCurveTo'; }}).length").GetDouble();
        Assert.Equal(2, count("0.4"));
        Assert.Equal(0, count("0"));
    }

    [Fact]
    public void FillShadesTheAreaDownToTheZeroLine()
    {
        using var engine = Engine();
        var kinds = Strings(Json(engine, """
            draw({ type: 'line', data: { labels: ['a','b'], datasets: [{ data: [1,3], backgroundColor: 'rgba(1,2,3,0.2)', fill: true }] }, options: { plugins: { legend: { display: false } } } })
              .filter(function(c) { return c.name === 'closePath' || (c.name === 'fill' && c.fill === 'rgba(1,2,3,0.2)'); }).map(function(c) { return c.name; })
            """));
        Assert.Equal(["closePath", "fill"], kinds.Take(2).ToArray()); // the area first, then the points
        Assert.Equal(0, Json(engine, "draw({ type: 'line', data: { labels: ['a','b'], datasets: [{ data: [1,3] }] }, options: { plugins: { legend: { display: false } } } }).filter(function(c) { return c.name === 'closePath'; }).length").GetDouble());
    }

    [Fact]
    public void PointRadiusDashAndPerPointColoursApplyToLines()
    {
        using var engine = Engine();
        var arcs = Json(engine, """
            draw({ type: 'line', data: { labels: ['a','b'], datasets: [{ data: [1,3], pointRadius: [5, 0], pointBackgroundColor: ['red', 'blue'], borderDash: [4, 2] }] }, options: { plugins: { legend: { display: false } } } })
              .filter(function(c) { return c.name === 'arc' || c.name === 'fill'; }).map(function(c) { return [c.name === 'arc' ? c.args[2] : 0, c.fill, c.name]; })
            """);
        Assert.Equal(2, arcs.GetArrayLength()); // one point (an arc, then its fill); radius 0 draws none
        Assert.Equal(5, arcs[0][0].GetDouble());
        Assert.Equal("red", arcs[1][1].GetString());
        var dashes = Json(engine, "draw({ type: 'line', data: { labels: ['a','b'], datasets: [{ data: [1,3], borderDash: [4, 2] }] }, options: { plugins: { legend: { display: false } } } }).filter(function(c) { return c.name === 'setLineDash'; }).map(function(c) { return c.args[0].length; })");
        Assert.Equal([2, 0], dashes.EnumerateArray().Select(e => e.GetInt32()).ToArray());
    }

    [Fact]
    public void StackedLinesAccumulate()
    {
        using var engine = Engine();
        var ys = Json(engine, """
            draw({ type: 'line', data: { labels: ['a'], datasets: [{ data: [3], pointRadius: 1 }, { data: [2], pointRadius: 1 }] }, options: { scales: { y: { stacked: true } }, plugins: { legend: { display: false } } } })
              .filter(function(c) { return c.name === 'arc'; }).map(function(c) { return c.args[1]; })
            """);
        Assert.Equal(2, ys.GetArrayLength());
        Assert.NotEqual(ys[0].GetDouble(), ys[1].GetDouble());
    }

    [Fact]
    public void BeginAtZeroSuggestedBoundsAndMinMaxShapeTheYAxis()
    {
        using var engine = Engine();
        string[] Labels(string config) => Strings(Json(engine, $"texts(draw({config})).filter(function(t) {{ return t[0] !== 'a' && t[0] !== 'b' && t[1] < 30; }}).map(function(t) {{ return t[0]; }})"));
        Assert.Equal("0", Labels("{ type: 'bar', data: { labels: ['a','b'], datasets: [{ data: [50, 60] }] }, options: { plugins: { legend: { display: false } } } }")[0]);
        Assert.DoesNotContain("0", Labels("{ type: 'line', data: { labels: ['a','b'], datasets: [{ data: [50, 60] }] }, options: { plugins: { legend: { display: false } } } }"));
        Assert.Contains("0", Labels("{ type: 'line', data: { labels: ['a','b'], datasets: [{ data: [50, 60] }] }, options: { plugins: { legend: { display: false } }, scales: { y: { beginAtZero: true } } } }"));
        Assert.Contains("100", Labels("{ type: 'bar', data: { labels: ['a'], datasets: [{ data: [5] }] }, options: { scales: { y: { max: 100 } }, plugins: { legend: { display: false } } } }"));
        Assert.Contains("100", Labels("{ type: 'bar', data: { labels: ['a'], datasets: [{ data: [5] }] }, options: { scales: { y: { suggestedMax: 100 } }, plugins: { legend: { display: false } } } }"));
    }

    [Fact]
    public void TickCallbackFormatsTheValueAxisLabels()
    {
        using var engine = Engine();
        var labels = Strings(Json(engine, """
            texts(draw({ type: 'bar', data: { labels: ['a'], datasets: [{ data: [10] }] },
                options: { scales: { y: { ticks: { callback: function(v) { return '$' + v; } } } }, plugins: { legend: { display: false } } } }))
              .filter(function(t) { return t[1] < 40; }).map(function(t) { return t[0]; })
            """));
        Assert.All(labels, l => Assert.StartsWith("$", l));
        Assert.Contains("$10", labels);
    }

    [Fact]
    public void CrowdedYTicksAndXLabelsAreThinned()
    {
        using var engine = Engine();
        // The hero chart: 0..4.5 in steps of 0.5 does not fit 152px, so only 0, 1.0, 2.0, 3.0 and 4.0 are labelled.
        var y = Strings(Json(engine, "texts(draw({ type: 'bar', data: { labels: ['a'], datasets: [{ label: 'x', data: [4.5] }] } }, 452, 213)).filter(function(t) { return t[1] < 30; }).map(function(t) { return t[0]; })"));
        Assert.Equal(["0", "1.0", "2.0", "3.0", "4.0"], y);
        var x = Strings(Json(engine, "texts(draw({ type: 'line', data: { labels: Array.apply(null, Array(30)).map(function(_, i) { return 'label ' + i; }), datasets: [{ data: Array.apply(null, Array(30)).map(function(_, i) { return i; }) }] }, options: { plugins: { legend: { display: false } } } })).filter(function(t) { return t[0].indexOf('label') === 0; }).map(function(t) { return t[0]; })"));
        Assert.True(x.Length < 30 && x.Length > 3);
        Assert.Equal("label 0", x[0]);
    }

    [Fact]
    public void ScalesCanBeHidden()
    {
        using var engine = Engine();
        Assert.Equal(0, Json(engine, "texts(draw({ type: 'bar', data: { labels: ['a'], datasets: [{ data: [10] }] }, options: { scales: { y: { display: false }, x: { display: false } }, plugins: { legend: { display: false } } } })).length").GetDouble());
    }

    [Fact]
    public void PieSlicesTakeTheirAnglesAndColoursFromTheData()
    {
        using var engine = Engine();
        var slices = Json(engine, """
            draw({ type: 'pie', data: { labels: ['a','b'], datasets: [{ data: [1, 3], backgroundColor: ['red', 'blue'] }] }, options: { plugins: { legend: { display: false } } } })
              .filter(function(c) { return c.name === 'arc'; }).map(function(c) { return c.args; })
            """);
        Assert.Equal(2, slices.GetArrayLength());
        Assert.Equal(-Math.PI / 2, slices[0][3].GetDouble(), 6);
        Assert.Equal(-Math.PI / 2 + Math.PI / 2, slices[0][4].GetDouble(), 6);  // a quarter of the circle
        Assert.Equal(slices[0][4].GetDouble(), slices[1][3].GetDouble(), 6);
        Assert.Equal(-Math.PI / 2 + 2 * Math.PI, slices[1][4].GetDouble(), 6);
        var fills = Strings(Json(engine, "draw({ type: 'pie', data: { labels: ['a','b'], datasets: [{ data: [1, 3], backgroundColor: ['red', 'blue'] }] }, options: { plugins: { legend: { display: false } } } }).filter(function(c) { return c.name === 'fill'; }).map(function(c) { return c.fill; })"));
        Assert.Equal(["red", "blue"], fills);
    }

    [Fact]
    public void PieUsesTheDefaultPaletteWithWhiteBordersAndOneLegendEntryPerSlice()
    {
        using var engine = Engine();
        var calls = Json(engine, "draw({ type: 'pie', data: { labels: ['a','b','c'], datasets: [{ data: [1, 1, 1] }] } }).filter(function(c) { return c.name === 'fill' || c.name === 'fillRect' || (c.name === 'stroke' && c.lw === 2); }).map(function(c) { return c.name + ':' + c.fill + ':' + c.stroke; })");
        var texts = Strings(calls);
        Assert.Equal(["rgb(54,162,235)", "rgb(255,99,132)", "rgb(255,159,64)"], texts.Where(t => t.StartsWith("fillRect:", StringComparison.Ordinal)).Select(t => t.Split(':')[1]).ToArray());
        Assert.Equal(3, texts.Count(t => t.StartsWith("stroke:", StringComparison.Ordinal) && t.EndsWith("#fff", StringComparison.Ordinal)));
    }

    [Fact]
    public void DoughnutCutsOutHalfByDefaultAndHonoursCutout()
    {
        using var engine = Engine();
        double Ratio(string options)
        {
            var radii = Json(engine, $"draw({{ type: 'doughnut', data: {{ labels: ['a'], datasets: [{{ data: [1] }}] }}, options: {{ plugins: {{ legend: {{ display: false }} }}{options} }} }}).filter(function(c) {{ return c.name === 'arc'; }}).map(function(c) {{ return c.args[2]; }})");
            return radii[1].GetDouble() / radii[0].GetDouble();
        }
        Assert.Equal(0.5, Ratio(""), 6);
        Assert.Equal(0.8, Ratio(", cutout: '80%'"), 6);
        Assert.Equal(1, Json(engine, "draw({ type: 'pie', data: { labels: ['a'], datasets: [{ data: [1] }] }, options: { plugins: { legend: { display: false } } } }).filter(function(c) { return c.name === 'arc'; }).length").GetDouble()); // a pie has no inner arc
    }

    [Fact]
    public void PieLegendAtTheRightShrinksThePie()
    {
        using var engine = Engine();
        double Radius(string options) => Json(engine, $"draw({{ type: 'pie', data: {{ labels: ['a'], datasets: [{{ data: [1] }}] }}, options: {options} }}, 480, 213).filter(function(c) {{ return c.name === 'arc'; }})[0].args[2]").GetDouble();
        // The radius is half the free square less the 2px border: all 213px without a legend, 181px under a top legend.
        Assert.Equal((213 - 2) / 2.0, Radius("{ plugins: { legend: { display: false } } }"), 6);
        Assert.Equal((181 - 2) / 2.0, Radius("{ plugins: { legend: { position: 'top' } } }"), 6);
        Assert.Equal((213 - 2) / 2.0, Radius("{ plugins: { legend: { position: 'right' } } }"), 6); // a side legend only narrows it
    }

    [Theory]
    [InlineData("radar")]
    [InlineData("scatter")]
    [InlineData("polarArea")]
    public void UnsupportedChartTypesDrawNothingAndAreReported(string type)
    {
        using var engine = Engine();
        Assert.Equal(0, Json(engine, $"draw({{ type: '{type}', data: {{ labels: ['a'], datasets: [{{ data: [1] }}] }} }}).length").GetDouble());
        var report = engine.Evaluate(ChartBindings.ReportScript).AsString();
        Assert.Contains($"\"{type}\"", report);
        Assert.Equal("", engine.Evaluate(ChartBindings.ReportScript).AsString()); // reported once
    }

    [Fact]
    public void AnUnsupportedChartTypeBecomesADiagnosticOnTheDocument()
    {
        var html = "<canvas id='c' width='200' height='100'></canvas>" +
                   "<script src='https://cdn.example/chart.umd.min.js'></script>" +
                   "<script>new Chart(document.getElementById('c'), { type: 'radar', data: { labels: ['a'], datasets: [{ data: [1] }] } });</script>";
        var options = new FolioOptions { BaseUri = new Uri("https://page.example/"), Scripting = ScriptingPolicy.ContentProcess, Raster = new SkiaRasterBackend() };
        using var document = Folio.Document.Parse(html, options);
        using var engine = new NativeJavaScriptEngine();
        using var loop = new HtmlEventLoop(engine);
        DomBindings.Install(engine, document, eventLoop: loop);
        ScriptGlobalsInstaller.Install(loop, engine);
        using var runner = new ScriptRunner(document.Node, engine, loop, options);
        runner.Run();
        var warning = Assert.Single(runner.Diagnostics, d => d.Severity == Severity.Warning && d.Feature == "canvas");
        Assert.Equal(DiagnosticCode.FeatureRouted, warning.Code);
        Assert.Contains("radar", warning.Message);
    }
}
