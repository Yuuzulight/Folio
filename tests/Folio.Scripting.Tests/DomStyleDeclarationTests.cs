using Folio.Dom;
using Folio.Html;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;
using Folio.Scripting.Ipc;
using System.Text.Json;

namespace Folio.Scripting.Tests;

public sealed class DomStyleDeclarationTests
{
    [Fact]
    public void InlineStyleIsLiveAndSupportsDeclarationOperations()
    {
        var document = TreeBuilder.Parse("<!doctype html><html><body><div id='box' style='color: red; margin: 1px 2px; --accent: blue'></div></body></html>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);

        Assert.Equal("red", engine.Evaluate("box = document.getElementById('box'); box.style.color").AsString());
        Assert.Equal(3, engine.Evaluate("box.style.length").AsNumber());
        Assert.Equal("color", engine.Evaluate("box.style.item(0)").AsString());
        Assert.Equal("blue", engine.Evaluate("box.style.getPropertyValue('--accent')").AsString());
        Assert.Equal("1px", engine.Evaluate("box.style.marginTop").AsString());

        engine.Evaluate("box.style.setProperty('background-color', 'rgb(1, 2, 3)'); box.style.setProperty('margin-top', '4px', 'important');");
        Assert.Contains("background-color", engine.Evaluate("box.getAttribute('style')").AsString());
        Assert.Equal("rgb(1, 2, 3)", engine.Evaluate("box.style.backgroundColor").AsString());
        Assert.Equal("important", engine.Evaluate("box.style.getPropertyPriority('margin-top')").AsString());
        Assert.Contains("background-color", engine.Evaluate("box.getAttribute('style')").AsString());

        Assert.Equal("red", engine.Evaluate("box.style.removeProperty('color')").AsString());
        Assert.True(engine.Evaluate("box.style.color === undefined").AsBoolean());
    }

    [Fact]
    public void MutationObserverReceivesChildListRecords()
    {
        var document = TreeBuilder.Parse("<html><body></body></html>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        var result = engine.Evaluate(@"
            let seen = '';
            const observer = new MutationObserver(records => { seen = records[0].type + ':' + records[0].addedNodes.length; });
            observer.observe(document.body, { childList: true, subtree: true });
            document.body.appendChild(document.createElement('div'));
            seen;");
        Assert.Equal("childList:1", result.AsString());
    }

    [Fact]
    public void MutationObserverReceivesAttributeRecords()
    {
        var document = TreeBuilder.Parse("<html><body><div id='target'></div></body></html>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        var result = engine.Evaluate(@"
            let seen = '';
            const observer = new MutationObserver(records => { seen = records[0].type + ':' + records[0].attributeName; });
            observer.observe(document.body, { attributes: true, subtree: true });
            document.getElementById('target').setAttribute('data-state', 'ready');
            seen;");
        Assert.Equal("attributes:data-state", result.AsString());
    }

    [Fact]
    public void CanvasElementExposesBasicTwoDimensionalContext()
    {
        var document = TreeBuilder.Parse("<canvas id='chart' width='640' height='480'></canvas>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        Assert.Equal(640, engine.Evaluate("const canvas = document.getElementById('chart'); canvas.width").AsNumber());
        Assert.Equal("#000000", engine.Evaluate("const ctx = canvas.getContext('2d'); ctx.fillStyle").AsString());
        Assert.Equal("#f00", engine.Evaluate("ctx.fillStyle = '#f00'; ctx.fillRect(1, 2, 30, 40); ctx.fillStyle").AsString());
        Assert.Equal("object", engine.Evaluate("const path = new Path2D(); path.moveTo(0, 0); path.lineTo(10, 10); typeof path").AsString());
        engine.Evaluate("ctx.beginPath(); ctx.moveTo(0, 0); ctx.lineTo(10, 10); ctx.closePath(); ctx.stroke();");
        engine.Evaluate("ctx.beginPath(); ctx.rect(0, 0, 20, 20); ctx.quadraticCurveTo(5, 5, 10, 10); ctx.bezierCurveTo(1, 2, 3, 4, 5, 6); ctx.arc(10, 10, 4, 0, 3.14); ctx.fill();");
        engine.Evaluate("const p = new Path2D(); p.rect(0, 0, 2, 2); p.arc(1, 1, 1, 0, 6.28);");
        engine.Evaluate("const gradient = ctx.createLinearGradient(0, 0, 10, 10); gradient.addColorStop(0, 'red'); gradient.addColorStop(1, 'blue'); ctx.fillStyle = gradient; ctx.fillText('hello', 1, 2);");
        Assert.True(engine.Evaluate("ctx.measureText('hello').width > 0").AsBoolean());
        Assert.Equal(4, engine.Evaluate("ctx.createImageData(1, 1).data.length").AsNumber());
        Assert.StartsWith("data:image/png;base64,", engine.Evaluate("canvas.toDataURL()").AsString());
        Assert.True(engine.Evaluate("canvas.getContext('webgl') === null").AsBoolean());
    }

    [Fact]
    public void CanvasImageDataArrayMutationsAreAppliedByPutImageData()
    {
        var document = TreeBuilder.Parse("<canvas id='chart' width='1' height='1'></canvas>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);

        var result = engine.Evaluate(@"
            const ctx = document.getElementById('chart').getContext('2d');
            const image = ctx.createImageData(1, 1);
            image.data[0] = 255; image.data[3] = 255;
            ctx.putImageData(image, 0, 0);
            const copy = ctx.getImageData(0, 0, 1, 1);
            `${copy.data[0]},${copy.data[3]}`;");

        Assert.Equal("255,255", result.AsString());
    }

    [Fact]
    public void CanvasContextSupportsStateAndTransformOperations()
    {
        var document = TreeBuilder.Parse("<canvas id='chart'></canvas>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        var result = engine.Evaluate(@"
            const ctx = document.getElementById('chart').getContext('2d');
            ctx.lineWidth = 3; ctx.lineCap = 'round'; ctx.globalAlpha = 0.5;
            ctx.setLineDash([2, 4]);
            ctx.save(); ctx.fillStyle = 'red'; ctx.translate(10, 20); ctx.restore();
            `${ctx.lineWidth}|${ctx.lineCap}|${ctx.globalAlpha}|${ctx.fillStyle}|${ctx.getLineDash().join(',')}`;");
        Assert.Equal("3|round|0.5|#000000|2,4", result.AsString());
    }

    [Fact]
    public void CssSupportsAndEscapeAreAvailable()
    {
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, TreeBuilder.Parse("<html></html>"));
        Assert.True(engine.Evaluate("CSS.supports('display', 'grid')").AsBoolean());
        Assert.Equal("\\31 2abc", engine.Evaluate("CSS.escape('12abc')").AsString());
    }

    [Fact]
    public void CssStyleSheetsExposeRulesAndMutation()
    {
        var document = TreeBuilder.Parse("<html><head><style>.a { color: red; }</style></head><body></body></html>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);

        Assert.Equal(1, engine.Evaluate("document.styleSheets.length").AsNumber());
        Assert.Contains(".a", engine.Evaluate("document.styleSheets[0].cssRules[0].cssText").AsString());
        Assert.Equal(".a", engine.Evaluate("document.styleSheets[0].cssRules[0].selectorText").AsString());
        Assert.Equal("red", engine.Evaluate("document.styleSheets[0].cssRules[0].style.getPropertyValue('color')").AsString());
        Assert.Equal(1, engine.Evaluate("document.styleSheets[0].insertRule('.b { color: blue; }')").AsNumber());
        Assert.Equal(2, engine.Evaluate("document.styleSheets[0].cssRules.length").AsNumber());
        engine.Evaluate("document.styleSheets[0].deleteRule(0)");
        Assert.Equal(1, engine.Evaluate("document.styleSheets[0].cssRules.length").AsNumber());
        engine.Evaluate("document.querySelector('style').textContent = '.c { color: green; }'");
        Assert.Contains(".c", engine.Evaluate("document.styleSheets[0].cssRules[0].cssText").AsString());
    }

    [Fact]
    public void FetchAllowsDataUrlsAndRejectsNetworkUrls()
    {
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, TreeBuilder.Parse("<html></html>"));
        Assert.Equal("function", engine.Evaluate("typeof fetch").AsString());
        engine.Evaluate("globalThis.__fetchBody = ''; fetch('data:text/plain,hello').then(r => r.text()).then(v => globalThis.__fetchBody = v);");
        engine.PerformMicrotaskCheckpoint();
        Assert.Equal("hello", engine.Evaluate("__fetchBody").AsString());
        engine.Evaluate("globalThis.__fetchRejected = false; fetch('https://example.test/').catch(e => globalThis.__fetchRejected = e instanceof TypeError);");
        engine.PerformMicrotaskCheckpoint();
        Assert.True(engine.Evaluate("__fetchRejected").AsBoolean());
    }

    [Fact]
    public void InvalidPropertyValueDoesNotReplaceExistingValue()
    {
        var document = TreeBuilder.Parse("<div id='box' style='width: 10px'></div>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);

        engine.Evaluate("const box = document.getElementById('box'); box.style.width = 'not-a-length';");
        Assert.Equal("10px", engine.Evaluate("box.style.width").AsString());
    }

    [Fact]
    public void ComputedStyleAndInnerTextAreExposed()
    {
        var document = TreeBuilder.Parse("<div id='box'>Hello<br>world<span>!</span></div>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);

        Assert.Equal("Hello\nworld!", engine.Evaluate("const box = document.getElementById('box'); box.innerText").AsString());
        Assert.Equal("inline", engine.Evaluate("getComputedStyle(box).display").AsString());
        Assert.Equal("", engine.Evaluate("getComputedStyle(box).getPropertyPriority('display')").AsString());
    }

    [Fact]
    public void GeometryAndViewportApisAreExposed()
    {
        var document = TreeBuilder.Parse("<div id='box' style='width: 20px; height: 10px'></div>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);

        Assert.Equal(20, engine.Evaluate("const box = document.getElementById('box'); box.getBoundingClientRect().width").AsNumber());
        Assert.Equal(10, engine.Evaluate("box.offsetHeight").AsNumber());
        Assert.Equal(800, engine.Evaluate("innerWidth").AsNumber());
        Assert.Equal(1, engine.Evaluate("devicePixelRatio").AsNumber());
        Assert.True(engine.Evaluate("document.elementFromPoint(5, 5) === box").AsBoolean());
        Assert.Equal(2, engine.Evaluate("scrollTo(2, 3); scrollX").AsNumber());
        Assert.Equal(4, engine.Evaluate("scrollBy(1, 1); scrollY").AsNumber());
        Assert.Equal(3, engine.Evaluate("pageXOffset").AsNumber());
    }

    [Fact]
    public void ScrollPropertiesAndMethodsAreExposed()
    {
        var document = TreeBuilder.Parse("<div id='box' style='width: 20px; height: 10px'></div>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);

        engine.Evaluate("const box = document.getElementById('box'); box.scrollTo(3, 4); box.scrollBy(2, 1);");
        Assert.Equal(5, engine.Evaluate("box.scrollLeft").AsNumber());
        Assert.Equal(5, engine.Evaluate("box.scrollTop").AsNumber());
    }

    [Fact]
    public void MatchMediaAndViewportValuesAreExposed()
    {
        var document = TreeBuilder.Parse("<div></div>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);

        Assert.True(engine.Evaluate("matchMedia('(min-width: 400px)').matches").AsBoolean());
        Assert.False(engine.Evaluate("matchMedia('(min-width: 900px)').matches").AsBoolean());
        Assert.Equal("(max-width: 900px)", engine.Evaluate("matchMedia('(max-width: 900px)').media").AsString());
    }

    [Fact]
    public void ObserverApisCanBeConstructedAndManaged()
    {
        var document = TreeBuilder.Parse("<div id='box'></div>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);

        Assert.True(engine.Evaluate("const box = document.getElementById('box'); const ro = new ResizeObserver(()=>{}); ro.observe(box); ro.unobserve(box); ro.disconnect(); true").AsBoolean());
        Assert.Equal("0.5px", engine.Evaluate("const io = new IntersectionObserver(()=>{}, {threshold: [0, .5]}); io.observe(box); io.thresholds[1] + 'px'").AsString());
    }

    [Fact]
    public void FormDataSupportsEntryOperations()
    {
        var document = TreeBuilder.Parse("<div></div>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);

        Assert.Equal("a,b", engine.Evaluate("const fd = new FormData(); fd.append('tag','a'); fd.append('tag','b'); fd.getAll('tag').join(',')").AsString());
        Assert.Equal("c", engine.Evaluate("fd.set('tag','c'); fd.get('tag')").AsString());
        Assert.True(engine.Evaluate("fd.has('tag'); fd.delete('tag'); !fd.has('tag')").AsBoolean());
    }

    [Fact]
    public void StorageApisSupportKeyValueOperations()
    {
        var document = TreeBuilder.Parse("<div></div>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);

        Assert.Equal(1, engine.Evaluate("localStorage.setItem('answer', 42); localStorage.length").AsNumber());
        Assert.Equal("42", engine.Evaluate("localStorage.getItem('answer')").AsString());
        Assert.Equal("answer", engine.Evaluate("localStorage.key(0)").AsString());
        Assert.Equal(0, engine.Evaluate("localStorage.removeItem('answer'); localStorage.length").AsNumber());
        Assert.Equal("named", engine.Evaluate("localStorage.named = 'named'; localStorage.named").AsString());
        Assert.True(engine.Evaluate("delete localStorage.named; localStorage.getItem('named') === null").AsBoolean());
        Assert.True(engine.Evaluate("try { localStorage.setItem('large', 'x'.repeat(5 * 1024 * 1024)); false; } catch (error) { error.name === 'QuotaExceededError' && localStorage.getItem('large') === null; }").AsBoolean());
    }

    [Fact]
    public void UrlSearchParamsSupportsBasicParsingAndMutation()
    {
        var document = TreeBuilder.Parse("<div></div>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);

        Assert.Equal("one", engine.Evaluate("const params = new URLSearchParams('?a=one&b=two'); params.get('a')").AsString());
        Assert.Equal("a=one&b=changed", engine.Evaluate("params.set('b','changed'); params.toString()").AsString());
        Assert.Equal("https:", engine.Evaluate("const url = new URL('https://example.test/path?a=one#top'); url.protocol").AsString());
        Assert.Equal("one", engine.Evaluate("url.searchParams.get('a')").AsString());
    }

    [Fact]
    public void ScriptingUtilityApisAreAvailable()
    {
        var document = TreeBuilder.Parse("<div></div>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);

        Assert.Equal("hello", engine.Evaluate("atob(btoa('hello'))").AsString());
        Assert.Equal(4, engine.Evaluate("const bytes = new Uint8Array(4); crypto.getRandomValues(bytes); bytes.length").AsNumber());
    }

    [Fact]
    public void InputValidationApisReflectRequiredAndEmailRules()
    {
        var document = TreeBuilder.Parse("<input id='email' type='email' required>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);

        Assert.False(engine.Evaluate("const input = document.getElementById('email'); input.checkValidity()").AsBoolean());
        engine.Evaluate("input.value = 'person@example.com';");
        Assert.True(engine.Evaluate("input.reportValidity()").AsBoolean());
    }

    [Fact]
    public void InputValueAndSelectionApisAreExposed()
    {
        var document = TreeBuilder.Parse("<input id='number' type='number' value='2' step='0.5'>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);

        Assert.Equal(2, engine.Evaluate("const input = document.getElementById('number'); input.valueAsNumber").AsNumber());
        Assert.Equal("2.5", engine.Evaluate("input.stepUp(); input.value").AsString());
        Assert.Equal(3, engine.Evaluate("input.selectionStart").AsNumber());
    }

    [Fact]
    public void TextareaAndOptionApisAreExposed()
    {
        var document = TreeBuilder.Parse("<textarea id='notes' value='hello'></textarea><select id='choice'></select>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);

        Assert.Equal("hello", engine.Evaluate("const notes = document.getElementById('notes'); notes.defaultValue = 'hello'; notes.defaultValue").AsString());
        Assert.Equal("world", engine.Evaluate("notes.value = 'world'; notes.value").AsString());
        Assert.Equal("yes", engine.Evaluate("const option = new Option('Yes','yes',true,true); option.value").AsString());
    }

    [Fact]
    public void EncodingCloneAndConsoleUtilitiesAreAvailable()
    {
        var document = TreeBuilder.Parse("<div></div>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);

        Assert.Equal(5, engine.Evaluate("const encoded = new TextEncoder().encode('hello'); console.log(encoded.length); encoded.length").AsNumber());
        Assert.Equal("hello", engine.Evaluate("new TextDecoder().decode(encoded)").AsString());
        Assert.Equal(7, engine.Evaluate("const copy = structuredClone({ value: 7 }); copy.value").AsNumber());
    }

    [Fact]
    public void SelectOptionsAndMultipleAreExposed()
    {
        var document = TreeBuilder.Parse("<select id='choice'><option value='a'>A</option><option value='b'>B</option></select>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);

        Assert.Equal(2, engine.Evaluate("const select = document.getElementById('choice'); select.length").AsNumber());
        Assert.Equal("b", engine.Evaluate("select.selectedIndex = 1; select.value").AsString());
        Assert.Equal(1, engine.Evaluate("select.multiple = true; select.options.length - 1").AsNumber());
    }

    [Fact]
    public void ContentProtocolRoundTripsAndRejectsMalformedFrames()
    {
        using var payload = JsonDocument.Parse("{\"artifactId\":\"a1\"}");
        var encoded = ContentProtocol.Encode(new ContentMessage(ContentMessageKind.Load, 7, payload.RootElement.Clone()));
        Assert.True(ContentProtocol.TryDecode(encoded, out var decoded));
        Assert.Equal(ContentMessageKind.Load, decoded!.Kind);
        Assert.Equal(7, decoded.Sequence);
        Assert.Equal(ContentMessage.CurrentVersion, decoded.Version);
        Assert.False(ContentProtocol.TryDecode(encoded[..^1], out _));
        Assert.False(ContentProtocol.TryDecode(new byte[] { 0xff, 0xff, 0xff, 0x7f }, out _));
        using var mismatchedPayload = JsonDocument.Parse("{}");
        var mismatched = ContentProtocol.Encode(new ContentMessage(ContentMessageKind.Hello, 1, mismatchedPayload.RootElement.Clone(), 999));
        Assert.False(ContentProtocol.TryDecode(mismatched, out _));
        var reader = new ContentProtocolReader();
        Assert.Empty(reader.Push(encoded[..2].AsMemory()));
        Assert.Single(reader.Push(encoded[2..].AsMemory()));
    }

    [Fact]
    public void FormElementApisExposeControlsAndValidation()
    {
        var document = TreeBuilder.Parse("<form id='form'><input name='name' required value='Ada'></form>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);

        Assert.Equal(1, engine.Evaluate("const form = document.getElementById('form'); form.elements.length").AsNumber());
        Assert.True(engine.Evaluate("form.checkValidity()").AsBoolean());
        Assert.Equal("Ada", engine.Evaluate("new FormData(form).get('name')").AsString());
        Assert.True(engine.Evaluate("const details = document.createElement('details'); details.open = true; details.open").AsBoolean());
        Assert.True(engine.Evaluate("document.getElementById('form').focus(); document.activeElement === form").AsBoolean());
    }

    [Fact]
    public async Task ContentTransportRoundTripsOverAStream()
    {
        using var payload = JsonDocument.Parse("{\"ok\":true}");
        await using var stream = new MemoryStream();
        await ContentTransport.WriteAsync(stream, new ContentMessage(ContentMessageKind.Heartbeat, 3, payload.RootElement.Clone()), TestContext.Current.CancellationToken);
        stream.Position = 0;
        var message = await ContentTransport.ReadAsync(stream, TestContext.Current.CancellationToken);
        Assert.Equal(ContentMessageKind.Heartbeat, message!.Kind);
        Assert.Equal(3, message.Sequence);
    }

    [Fact]
    public void ContentSupervisorStopsAfterHeartbeatTimeout()
    {
        using var supervisor = new ContentProcessSupervisor(new ContentProcessLimits(HeartbeatTimeout: TimeSpan.FromMilliseconds(-1)));
        string? reason = null;
        supervisor.Stopped += value => reason = value;
        Assert.False(supervisor.CheckHeartbeat());
        Assert.True(supervisor.IsStopped);
        Assert.Contains("heartbeat", reason, StringComparison.OrdinalIgnoreCase);
        using var memorySupervisor = new ContentProcessSupervisor(new ContentProcessLimits(MemoryBytes: 10));
        Assert.False(memorySupervisor.CheckMemory(11));
    }

    [Fact]
    public async Task ContentHostSessionAcknowledgesHeartbeatAndStops()
    {
        using var payload = JsonDocument.Parse("{}");
        var input = new MemoryStream(ContentProtocol.Encode(new ContentMessage(ContentMessageKind.Heartbeat, 8, payload.RootElement.Clone())));
        var output = new MemoryStream();
        var session = new ContentHostSession(input, output);
        await session.RunAsync(TestContext.Current.CancellationToken);
        output.Position = 0;
        var response = await ContentTransport.ReadAsync(output, TestContext.Current.CancellationToken);
        Assert.Equal(ContentMessageKind.Heartbeat, response!.Kind);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task ContentProcessClientRunsScriptThroughContentHost()
    {
        var host = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tools/Folio.ContentHost/bin/Debug/net10.0/Folio.ContentHost.dll"));
        Assert.True(File.Exists(host), $"Content host was not built: {host}");
        await using var client = ContentProcessClient.Start("dotnet", $"\"{host}\"", new ContentProcessLimits(HeartbeatTimeout: TimeSpan.FromSeconds(10)));
        var received = new TaskCompletionSource<ContentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.MessageReceived += (message, _) =>
        {
            if (message.Kind == ContentMessageKind.Frame) received.TrySetResult(message);
            return ValueTask.CompletedTask;
        };
        await client.SendAsync(ContentMessageKind.Load, new { script = "1 + 2", url = "test" }, TestContext.Current.CancellationToken);
        var frame = await received.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(ContentMessageKind.Frame, frame.Kind);
        Assert.Equal("3", frame.Payload.GetProperty("value").GetString());

        var domResult = new TaskCompletionSource<ContentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.MessageReceived += (message, _) =>
        {
            if (message.Kind == ContentMessageKind.Frame && message.Sequence > frame.Sequence) domResult.TrySetResult(message);
            return ValueTask.CompletedTask;
        };
        await client.SendAsync(ContentMessageKind.Load, new { html = "<body><p id='x'>before</p></body>", script = "document.getElementById('x').textContent = 'after'; document.getElementById('x').textContent", url = "test" }, TestContext.Current.CancellationToken);
        var domFrame = await domResult.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal("after", domFrame.Payload.GetProperty("value").GetString());
    }

    [Fact]
    public async Task ContentProcessStopsAnInfiniteScriptWithDiagnostic()
    {
        var host = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tools/Folio.ContentHost/bin/Debug/net10.0/Folio.ContentHost.dll"));
        await using var client = ContentProcessClient.Start("dotnet", $"\"{host}\"");
        var diagnostic = new TaskCompletionSource<ContentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.MessageReceived += (message, _) =>
        {
            if (message.Kind == ContentMessageKind.Diagnostic) diagnostic.TrySetResult(message);
            return ValueTask.CompletedTask;
        };
        await client.SendAsync(ContentMessageKind.Load, new { script = "while (true) {}", url = "hostile" }, TestContext.Current.CancellationToken);
        var result = await diagnostic.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Contains(result.Payload.GetProperty("code").GetString(), new[] { "Timeout", "Statements" });
    }

    [Fact]
    public void ContentPayloadsDecodeDisplayAndInputMessages()
    {
        using var display = JsonDocument.Parse("{\"documentRevision\":4,\"viewportWidth\":800,\"viewportHeight\":600,\"commands\":[{\"op\":\"fill\"}]}");
        var displayMessage = new ContentMessage(ContentMessageKind.Frame, 1, display.RootElement.Clone());
        Assert.True(displayMessage.TryGetDisplayList(out var frame));
        Assert.Equal(4, frame!.DocumentRevision);
        using var input = JsonDocument.Parse("{\"type\":\"pointerdown\",\"x\":12.5,\"y\":4}");
        var inputMessage = new ContentMessage(ContentMessageKind.Input, 2, input.RootElement.Clone());
        Assert.True(inputMessage.TryGetInput(out var eventData));
        Assert.Equal("pointerdown", eventData!.Type);
        Assert.Equal(12.5, eventData.X);
    }

    [Fact]
    public async Task ContentHostBlocksResourcesByDefault()
    {
        using var payload = JsonDocument.Parse("{\"url\":\"https://example.test/a.css\"}");
        var input = new MemoryStream(ContentProtocol.Encode(new ContentMessage(ContentMessageKind.ResourceRequest, 9, payload.RootElement.Clone())));
        var output = new MemoryStream();
        var session = new ContentHostSession(input, output);
        await session.RunAsync(TestContext.Current.CancellationToken);
        output.Position = 0;
        var response = await ContentTransport.ReadAsync(output, TestContext.Current.CancellationToken);
        Assert.Equal(ContentMessageKind.ResourceResponse, response!.Kind);
        Assert.Equal(0, response.Payload.GetProperty("status").GetInt32());
        await session.DisposeAsync();
    }
}
