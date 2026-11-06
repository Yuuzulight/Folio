using System.Threading.Channels;
using Folio.Scripting.Ipc;

namespace Folio.Scripting.Tests;

[Collection("Content process round trips")]
public sealed class ContentHostProductionTests
{
    [Fact]
    public async Task CssAnimationPublishesChangingPixelsWithoutAdditionalLoads()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
        var host = Environment.GetEnvironmentVariable("FOLIO_CONTENT_HOST") ?? ContentHostTestPaths.Resolve();
        await using var client = ContentProcessClient.Start(host);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var frames = Channel.CreateUnbounded<ContentMessage>();
        client.ScriptStopped += (_, stopped) => frames.Writer.TryComplete(new IOException(stopped.Reason));
        client.MessageReceived += (message, _) => { frames.Writer.TryWrite(message); return ValueTask.CompletedTask; };
        await client.SendAsync(ContentMessageKind.Load, new
        {
            html = "<style>body{margin:0;background:red;animation:pulse .4s linear infinite}@keyframes pulse{0%,40%{background-color:red}50%,90%{background-color:blue}100%{background-color:red}}</style>"
        }, timeout.Token);
        using var store = new CanvasFrameStore(client.FramePath, 1280, 720, 1280 * 4);
        var colors = new HashSet<(byte Blue, byte Red)>();
        var unsolicited = false;
        while (!colors.Contains(((byte)0, (byte)255)) || !colors.Contains(((byte)255, (byte)0)) || !unsolicited)
        {
            var message = await frames.Reader.ReadAsync(timeout.Token);
            Assert.NotEqual(ContentMessageKind.Diagnostic, message.Kind);
            if (message.Kind != ContentMessageKind.Frame) continue;
            Assert.Empty(message.Payload.GetProperty("diagnostics").EnumerateArray());
            unsolicited |= message.Payload.GetProperty("sequence").GetInt64() == 0;
            var pixels = store.ReadLatest(out _);
            var offset = (100 * 1280 + 100) * 4;
            colors.Add((pixels[offset], pixels[offset + 2]));
        }
        Assert.Contains(((byte)0, (byte)255), colors);
        Assert.Contains(((byte)255, (byte)0), colors);
    }

    [Theory]
    [InlineData("sans-serif", "Default text", "generic")]
    [InlineData("serif", "Serif text", "serif")]
    [InlineData("monospace", "iii WWW 123", "monospace")]
    [InlineData("Source Sans 3", "Readable text", "latin")]
    [InlineData("Noto Sans Arabic", "مرحبا", "arabic")]
    [InlineData("Noto Sans Hebrew", "שלום", "hebrew")]
    [InlineData("Noto Sans JP", "こんにちは", "japanese")]
    [InlineData("Noto Sans SC", "你好", "chinese")]
    [InlineData("Noto Color Emoji", "😀", "emoji")]
    public async Task ShippedFontsPaintVisibleTextWithoutWebFonts(string family, string text, string label)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
        var host = Environment.GetEnvironmentVariable("FOLIO_CONTENT_HOST") ?? ContentHostTestPaths.Resolve();
        await using var client = ContentProcessClient.Start(host);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var frames = Channel.CreateUnbounded<ContentMessage>();
        client.ScriptStopped += (_, stopped) => frames.Writer.TryComplete(new IOException(stopped.Reason));
        client.MessageReceived += (message, _) => { frames.Writer.TryWrite(message); return ValueTask.CompletedTask; };
        await client.SendAsync(ContentMessageKind.Load, new
        {
            html = $"<style>body{{margin:0;background:white;color:black;font:40px '{family}'}}</style>{text}"
        }, timeout.Token);
        while (true)
        {
            var message = await frames.Reader.ReadAsync(timeout.Token);
            Assert.NotEqual(ContentMessageKind.Diagnostic, message.Kind);
            if (message.Kind != ContentMessageKind.Frame) continue;
            Assert.Empty(message.Payload.GetProperty("diagnostics").EnumerateArray());
            break;
        }
        using var store = new CanvasFrameStore(client.FramePath, 1280, 720, 1280 * 4);
        var pixels = store.ReadLatest(out _);
        var visible = false;
        for (var y = 0; y < 120 && !visible; y++)
            for (var x = 0; x < 600; x++)
            {
                var offset = (y * 1280 + x) * 4;
                if (pixels[offset] < 240 || pixels[offset + 1] < 240 || pixels[offset + 2] < 240) { visible = true; break; }
            }
        Assert.True(visible, $"No {label} glyph pixels were painted.");
        if (label == "monospace")
        {
            await client.SendAsync(ContentMessageKind.Load, new
            {
                script = "var metrics=document.createElement('canvas').getContext('2d');metrics.font='20px monospace';Math.abs(metrics.measureText('iiii').width-metrics.measureText('WWWW').width)<0.01;"
            }, timeout.Token);
            while (true)
            {
                var message = await frames.Reader.ReadAsync(timeout.Token);
                Assert.NotEqual(ContentMessageKind.Diagnostic, message.Kind);
                if (message.Kind != ContentMessageKind.Frame) continue;
                Assert.Equal("true", message.Payload.GetProperty("value").GetString());
                break;
            }
        }
    }

    [Fact]
    public async Task ResizeAndDpiChangesPreserveScriptStateAndRenderPhysicalPixels()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
        var host = Environment.GetEnvironmentVariable("FOLIO_CONTENT_HOST") ?? ContentHostTestPaths.Resolve();
        await using var client = ContentProcessClient.Start(host);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var frames = Channel.CreateUnbounded<ContentMessage>();
        client.ScriptStopped += (_, stopped) => frames.Writer.TryComplete(new IOException($"Content host stopped: {stopped.Reason}; stderr: {client.StandardError}"));
        client.MessageReceived += (message, _) => { frames.Writer.TryWrite(message); return ValueTask.CompletedTask; };
        async Task<ContentMessage> Receive()
        {
            while (true)
            {
                var message = await frames.Reader.ReadAsync(timeout.Token);
                Assert.NotEqual(ContentMessageKind.Diagnostic, message.Kind);
                if (message.Kind == ContentMessageKind.Frame) return message;
            }
        }
        await client.SendAsync(ContentMessageKind.Load, new
        {
            html = "<style>body{margin:0;background:blue}@media(min-width:600px){body{background:red}}#box{width:100px;height:50px;background:lime}</style><div id=box></div><script>var retained=42,resized=0;window.addEventListener('resize',function(){resized++;});</script>"
        }, timeout.Token);
        var initial = await Receive();
        Assert.Empty(initial.Payload.GetProperty("diagnostics").EnumerateArray());
        var revision = 0;
        foreach (var viewport in new[] { (320, 200, 1d), (1600, 900, 2d), (200, 120, 1d) })
        {
            revision++;
            await client.UpdateViewportAsync(viewport.Item1, viewport.Item2, viewport.Item3, timeout.Token);
            var frame = await Receive();
            var descriptor = frame.Payload.GetProperty("canvasFrame");
            Assert.Equal(viewport.Item1, descriptor.GetProperty("width").GetInt32());
            Assert.Equal(viewport.Item2, descriptor.GetProperty("height").GetInt32());
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(client.FramePath)!, "*.bin"));
            using var store = new CanvasFrameStore(client.FramePath, viewport.Item1, viewport.Item2, viewport.Item1 * 4);
            var pixels = store.ReadLatest(out var sequence);
            Assert.True(sequence > 0);
            var inside = ((int)(25 * viewport.Item3) * viewport.Item1 + (int)(50 * viewport.Item3)) * 4;
            Assert.Equal((byte)255, pixels[inside + 1]);
            var outside = (viewport.Item2 * viewport.Item1 - 1) * 4;
            Assert.Equal((byte)255, pixels[outside + (viewport.Item1 / viewport.Item3 >= 600 ? 2 : 0)]);
            await client.SendAsync(ContentMessageKind.Load, new { script = "retained+':'+innerWidth+':'+devicePixelRatio+':'+resized" }, timeout.Token);
            Assert.Equal($"42:{viewport.Item1 / viewport.Item3}:{viewport.Item3}:{revision}", (await Receive()).Payload.GetProperty("value").GetString());
        }
    }

    [Fact]
    public async Task ShippedHostPaintsNavigatesDispatchesInputAndTicksWithoutLoads()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
        var host = Environment.GetEnvironmentVariable("FOLIO_CONTENT_HOST") ?? ContentHostTestPaths.Resolve();
        await using var client = ContentProcessClient.Start(host);
        var messages = Channel.CreateUnbounded<ContentMessage>();
        client.ScriptStopped += (_, stopped) => messages.Writer.TryComplete(new IOException($"Content host stopped: {stopped.Reason}; stderr: {client.StandardError}"));
        client.MessageReceived += (message, _) => { messages.Writer.TryWrite(message); return ValueTask.CompletedTask; };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        async Task<ContentMessage> Receive(bool unsolicited = false)
        {
            while (true)
            {
                var message = await messages.Reader.ReadAsync(timeout.Token);
                Assert.NotEqual(ContentMessageKind.Diagnostic, message.Kind);
                if (message.Kind == ContentMessageKind.Frame &&
                    (message.Payload.GetProperty("sequence").GetInt64() == 0) == unsolicited) return message;
            }
        }
        async Task<string> Evaluate(string script)
        {
            await client.SendAsync(ContentMessageKind.Load, new { script }, timeout.Token);
            return (await Receive()).Payload.GetProperty("value").GetString()!;
        }
        await client.SendAsync(ContentMessageKind.Load, new { html = "<canvas id=c width=100 height=100></canvas><script>document.getElementById('c').getContext('2d').fillRect(0,0,100,100);</script>" }, timeout.Token);
        await Receive();
        using var store = new CanvasFrameStore(client.FramePath, 1280, 720, 1280 * 4);
        var first = store.ReadLatest(out var firstSequence);
        Assert.True(firstSequence > 0);
        await client.SendAsync(ContentMessageKind.Load, new
        {
            html = "<style>body{margin:0;background:red}button{width:100px;height:100px}</style><button id=b>Test</button><input id=i><script>var clicks=0,keys=0;document.getElementById('b').addEventListener('click',function(){clicks++;});document.addEventListener('keydown',function(){keys++;});</script>"
        }, timeout.Token);
        var loaded = await Receive();
        Assert.Empty(loaded.Payload.GetProperty("diagnostics").EnumerateArray());
        var next = store.ReadLatest(out var nextSequence);
        Assert.True(nextSequence > firstSequence);
        Assert.False(first.SequenceEqual(next));
        await client.SendAsync(ContentMessageKind.Input, new ContentInputEvent("pointerdown", 20, 20, 0, Buttons: 1), timeout.Token);
        await Receive();
        await client.SendAsync(ContentMessageKind.Input, new ContentInputEvent("pointerup", 20, 20, 0), timeout.Token);
        await Receive();
        Assert.Equal("1", await Evaluate("clicks"));
        await client.SendAsync(ContentMessageKind.Input, new ContentInputEvent("keydown", Key: "a", Code: "KeyA"), timeout.Token);
        await Receive();
        Assert.Equal("1", await Evaluate("keys"));
        await Evaluate("document.getElementById('i').focus();''");
        await client.SendAsync(ContentMessageKind.Input, new ContentInputEvent("textinput", Text: "hello"), timeout.Token);
        await Receive();
        Assert.Equal("hello", await Evaluate("document.getElementById('i').value"));
        await Evaluate("var delayed=0,animated=0;setTimeout(function(){delayed=1;document.body.style.backgroundColor='blue';},100);requestAnimationFrame(function(){animated=1;});''");
        var tick = await Receive(unsolicited: true);
        Assert.True(tick.Payload.GetProperty("canvasFrame").GetProperty("sequence").GetInt64() > nextSequence);
        while (true)
        {
            var pixels = store.ReadLatest(out _);
            var sample = (500 * 1280 + 500) * 4;
            if (pixels[sample] == 255 && pixels[sample + 1] == 0 && pixels[sample + 2] == 0) break;
            await Receive(unsolicited: true);
        }
        Assert.Equal("1:1", await Evaluate("delayed+':'+animated"));
        var processId = client.Process.Id;
        var framePath = client.FramePath;
        store.Dispose();
        await client.DisposeAsync();
        Assert.False(File.Exists(framePath));
        Assert.DoesNotContain(System.Diagnostics.Process.GetProcessesByName("Folio.ContentHost"), process => process.Id == processId);
    }

    [Fact]
    public async Task DiagnosticJsonSurvivesAScriptFailureAndTheHostCanContinue()
    {
        await using var client = ContentProcessClient.Start(ContentHostTestPaths.Resolve());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var messages = Channel.CreateUnbounded<ContentMessage>();
        client.ScriptStopped += (_, stopped) => messages.Writer.TryComplete(new IOException(stopped.Reason + "; " + client.StandardError));
        client.MessageReceived += (message, _) => { messages.Writer.TryWrite(message); return ValueTask.CompletedTask; };
        await client.SendAsync(ContentMessageKind.Load, new { html = "<p>diagnostic</p>", script = "throw new Error('diagnostic-roundtrip')" }, timeout.Token);
        ContentMessage diagnostic;
        do { diagnostic = await messages.Reader.ReadAsync(timeout.Token); } while (diagnostic.Kind != ContentMessageKind.Diagnostic);
        Assert.Contains("diagnostic-roundtrip", diagnostic.Payload.GetProperty("message").GetString());
        Assert.False(client.Process.HasExited);
        await client.SendAsync(ContentMessageKind.Load, new { script = "JSON.stringify({value:42})" }, timeout.Token);
        ContentMessage frame;
        do { frame = await messages.Reader.ReadAsync(timeout.Token); }
        while (frame.Kind != ContentMessageKind.Frame || frame.Payload.GetProperty("sequence").GetInt64() == 0);
        Assert.Equal("{\"value\":42}", frame.Payload.GetProperty("value").GetString());
        Assert.Empty(frame.Payload.GetProperty("diagnostics").EnumerateArray());
    }

    [Theory]
    [InlineData("{\"Type\":\"keydown\",\"Key\":\"a\"}", true)]
    [InlineData("{\"type\":\"keydown\",\"key\":\"a\"}", true)]
    [InlineData("{\"type\":123}", false)]
    [InlineData("[]", false)]
    public void InputPayloadsAcceptHostCasingAndRejectMalformedData(string json, bool accepted)
    {
        using var payload = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(accepted, new ContentMessage(ContentMessageKind.Input, 1, payload.RootElement).TryGetInput(out _));
    }
}
