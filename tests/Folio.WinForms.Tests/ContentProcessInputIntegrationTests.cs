#if WINDOWS
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Windows.Forms;
using Folio.Hosting;
using Folio.Painting;
using Folio.Resources;
using Folio.Scripting;
using Folio.Scripting.Ipc;
using Folio.WinForms;

namespace Folio.WinForms.Tests;

public sealed class ContentProcessInputIntegrationTests
{
    [Fact]
    public Task ClickingTabButtonSwitchesPanels() => RunStaAsync(async () =>
    {
        using var form = CreateForm();
        using var view = new TestFolioView { Dock = DockStyle.Fill, Options = CreateOptions() };
        form.Controls.Add(view);
        form.Show();
        view.CreateControl();

        var html = """
            <!DOCTYPE html>
            <html>
            <head>
              <style>
                body { margin: 0; padding: 0; }
                button { width: 100px; height: 40px; margin: 0; padding: 0; }
                #p1 { width: 300px; height: 200px; background-color: rgb(255, 0, 0); }
                #p2 { width: 300px; height: 200px; background-color: rgb(0, 255, 0); }
              </style>
            </head>
            <body>
              <div id="tabs">
                <button id="tab1" onclick="document.getElementById('p1').style.display='block';document.getElementById('p2').style.display='none'">Tab 1</button>
                <button id="tab2" onclick="document.getElementById('p1').style.display='none';document.getElementById('p2').style.display='block'">Tab 2</button>
              </div>
              <div id="p1" style="display:block">Panel 1 Content</div>
              <div id="p2" style="display:none">Panel 2 Content</div>
            </body>
            </html>
            """;

        view.LoadHtml(html, baseUri: new Uri("https://example.com/"));

        // Wait for Panel 1 to be visible (red).
        await WaitForConditionAsync(() =>
        {
            using var bmp1 = Capture(view);
            var p1Color = bmp1.GetPixel(50, 100);
            return p1Color.R > 200 && p1Color.G < 50;
        }, TimeSpan.FromSeconds(5), "Expected red panel initially");

        // Click Tab 2
        view.SendClick("tab2", 150, 20);

        // Panel 2 should now become visible (green).
        await WaitForConditionAsync(() =>
        {
            using var bmp2 = Capture(view);
            var p2Color = bmp2.GetPixel(50, 100);
            return p2Color.G > 200 && p2Color.R < 50;
        }, TimeSpan.FromSeconds(5), "Expected green panel after clicking tab");
    });

    [Fact]
    public Task TypingIntoFieldFiresInputInPage() => RunStaAsync(async () =>
    {
        using var form = CreateForm();
        using var view = new TestFolioView { Dock = DockStyle.Fill, Options = CreateOptions() };
        form.Controls.Add(view);
        form.Show();
        view.CreateControl();

        var html = """
            <!DOCTYPE html>
            <html>
            <head>
              <style>
                body { margin: 0; }
                input { width: 200px; height: 35px; }
                #out { width: 300px; height: 100px; background-color: rgb(0, 0, 0); }
              </style>
            </head>
            <body>
              <input id="txt" oninput="document.getElementById('out').style.backgroundColor = 'rgb(0, 0, 255)'">
              <div id="out"></div>
            </body>
            </html>
            """;

        view.LoadHtml(html, baseUri: new Uri("https://example.com/"));

        // Wait for black initial box.
        await WaitForConditionAsync(() =>
        {
            using var bmp1 = Capture(view);
            var c1 = bmp1.GetPixel(50, 100);
            return c1.R < 50 && c1.G < 50 && c1.B < 50;
        }, TimeSpan.FromSeconds(5), "Expected black initially");

        // Click the input field to focus it.
        view.SendClick("txt", 50, 15);
        await Task.Delay(100);

        // Type 'x'.
        view.SendTextInput("x", "txt");

        // The input event fired and changed #out background to blue.
        await WaitForConditionAsync(() =>
        {
            using var bmp2 = Capture(view);
            var c2 = bmp2.GetPixel(50, 100);
            return c2.B > 200 && c2.R < 50;
        }, TimeSpan.FromSeconds(5), "Expected blue after typing into field");
    });

    [Fact]
    public Task CtrlCPutsSelectionOnHostClipboard() => RunStaAsync(async () =>
    {
        using var form = CreateForm();
        using var view = new TestFolioView { Dock = DockStyle.Fill, Options = CreateOptions() };
        form.Controls.Add(view);
        form.Show();
        view.CreateControl();

        var html = """
            <!DOCTYPE html>
            <html>
            <body style="margin: 0; font-size: 20px;">
              <p id="sel">TargetTextToCopy123</p>
            </body>
            </html>
            """;

        view.LoadHtml(html, baseUri: new Uri("https://example.com/"));

        // Wait for initial render
        await WaitForConditionAsync(() =>
        {
            using var bmp = Capture(view);
            return bmp.GetPixel(50, 10).A > 0;
        }, TimeSpan.FromSeconds(5), "Initial render timed out");

        // Clear clipboard first
        try { Clipboard.Clear(); } catch { }

        // Select all text via Ctrl+A
        view.SendKeyDown(Keys.Control | Keys.A);
        await Task.Delay(100);

        // Send Ctrl+C
        view.SendKeyDown(Keys.Control | Keys.C);

        // Poll for clipboard text up to 5 seconds.
        await WaitForConditionAsync(() =>
        {
            try
            {
                var text = Clipboard.GetText();
                return text.Contains("TargetTextToCopy123");
            }
            catch { return false; }
        }, TimeSpan.FromSeconds(5), "Ctrl+C did not place the selected text onto the host clipboard.");
    });

    [Fact]
    public Task LinkClickRaisesLinkActivatedWithResolvedUrl() => RunStaAsync(async () =>
    {
        using var form = CreateForm();
        using var view = new TestFolioView { Dock = DockStyle.Fill, Options = CreateOptions() };
        form.Controls.Add(view);
        form.Show();
        view.CreateControl();

        var html = """
            <!DOCTYPE html>
            <html>
            <body style="margin: 0;">
              <a id="link" href="/resolved/target" style="display: block; width: 200px; height: 50px;">Link</a>
            </body>
            </html>
            """;

        var linkTcs = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        view.LinkActivated += (_, e) => linkTcs.TrySetResult(e.Uri);

        view.LoadHtml(html, baseUri: new Uri("https://example.com/base/"));

        await WaitForConditionAsync(() =>
        {
            using var bmp = Capture(view);
            return bmp.GetPixel(50, 25).A > 0;
        }, TimeSpan.FromSeconds(5), "Initial render timed out");

        // Click the link at (50, 25).
        view.SimulateClick(50, 25);

        await WaitForConditionAsync(() => linkTcs.Task.IsCompleted, TimeSpan.FromSeconds(5), "LinkActivated timed out");
        var activatedUri = await linkTcs.Task;
        Assert.Equal("https://example.com/resolved/target", activatedUri.AbsoluteUri);
    });

    [Fact]
    public Task HoveringShowsPageCursorAndTitleTooltip() => RunStaAsync(async () =>
    {
        using var form = CreateForm();
        using var view = new TestFolioView { Dock = DockStyle.Fill, Options = CreateOptions() };
        form.Controls.Add(view);
        form.Show();
        view.CreateControl();

        var html = """
            <!DOCTYPE html>
            <html>
            <body style="margin: 0;">
              <div id="hover-box" title="Expected Title Tooltip" style="width: 150px; height: 100px; cursor: pointer;">
                Hover target
              </div>
            </body>
            </html>
            """;

        view.LoadHtml(html, baseUri: new Uri("https://example.com/"));

        await WaitForConditionAsync(() =>
        {
            using var bmp = Capture(view);
            return bmp.GetPixel(50, 50).A > 0;
        }, TimeSpan.FromSeconds(5), "Initial render timed out");

        // Initial cursor is default.
        Assert.Equal(Cursors.Default, view.Cursor);

        // Move mouse over the hover box (at x=50, y=50).
        view.SimulateMouseMove(50, 50);

        // Poll for cursor and tooltip updates.
        await WaitForConditionAsync(() =>
            view.Cursor == Cursors.Hand && view.GetActiveTooltip() == "Expected Title Tooltip",
            TimeSpan.FromSeconds(5),
            $"Expected cursor Hand and tooltip 'Expected Title Tooltip', got cursor {view.Cursor} and tooltip '{view.GetActiveTooltip()}'");
    });

    [Fact]
    public Task SelectDropdownOpensAndPicksOption() => RunStaAsync(async () =>
    {
        using var form = CreateForm();
        using var view = new TestFolioView { Dock = DockStyle.Fill, Options = CreateOptions() };
        form.Controls.Add(view);
        form.Show();
        view.CreateControl();

        var html = """
            <!DOCTYPE html>
            <html>
            <head>
              <style>
                body { margin: 0; }
                select { width: 150px; height: 35px; }
                #result { width: 300px; height: 100px; background-color: rgb(0, 0, 0); }
              </style>
            </head>
            <body>
              <select id="fruit-select">
                <option value="apple">Apple</option>
                <option value="banana">Banana</option>
                <option value="cherry">Cherry</option>
              </select>
              <div id="result">Initial</div>
              <script>
                document.getElementById('fruit-select').addEventListener('change', function() {
                  if (this.value === 'banana') {
                    document.getElementById('result').style.backgroundColor = 'rgb(0, 255, 0)';
                  }
                });
              </script>
            </body>
            </html>
            """;

        var dropdownTcs = new TaskCompletionSource<SelectDropdownEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        view.SelectDropdownOpened += (_, e) => dropdownTcs.TrySetResult(e);

        view.LoadHtml(html, baseUri: new Uri("https://example.com/"));

        await WaitForConditionAsync(() =>
        {
            using var bmp = Capture(view);
            return bmp.GetPixel(50, 15).A > 0;
        }, TimeSpan.FromSeconds(5), "Initial render timed out");

        // Click the select element
        view.SendClick("fruit-select", 50, 15);

        await WaitForConditionAsync(() => dropdownTcs.Task.IsCompleted, TimeSpan.FromSeconds(5), "Dropdown did not open");
        var dropdown = await dropdownTcs.Task;
        Assert.NotNull(dropdown);
        Assert.Equal("fruit-select", dropdown.SelectId);
        Assert.Equal(3, dropdown.Options.Count);
        Assert.Equal("banana", dropdown.Options[1].Value);

        // Pick option index 1 ("banana").
        view.PickSelectOption("fruit-select", 1);

        // Change event ran and updated #result background to green.
        await WaitForConditionAsync(() =>
        {
            using var bmp = Capture(view);
            var resColor = bmp.GetPixel(50, 100);
            return resColor.G > 200 && resColor.R < 50;
        }, TimeSpan.FromSeconds(5), "Expected green result after picking banana");
    });

    private static Form CreateForm() =>
        new() { ClientSize = new Size(400, 300), ShowInTaskbar = false, Opacity = 0, Location = new Point(-32000, -32000) };

    private static FolioOptions CreateOptions()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
        var host = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tools/Folio.ContentHost/bin", configuration, "net10.0/Folio.ContentHost.dll"));
        return new FolioOptions
        {
            Scripting = ScriptingPolicy.ContentProcess,
            ContentProcessPath = host,
        };
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout, string failureMessage)
    {
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < timeout)
        {
            Application.DoEvents();
            if (condition())
                return;
            await Task.Delay(50);
        }
        throw new TimeoutException(failureMessage);
    }

    private static Bitmap Capture(Control control)
    {
        var bitmap = new Bitmap(control.ClientSize.Width, control.ClientSize.Height, PixelFormat.Format32bppPArgb);
        control.DrawToBitmap(bitmap, control.ClientRectangle);
        return bitmap;
    }

    private static Task RunStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            Application.Idle += Start;
            Application.Run();
            void Start(object? _, EventArgs __) { Application.Idle -= Start; _ = ExecuteAsync(); }
            async Task ExecuteAsync()
            {
                try { await action(); completion.TrySetResult(); }
                catch (Exception error) { completion.TrySetException(error); }
                finally { Application.ExitThread(); }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return AwaitCompletionAsync();
        async Task AwaitCompletionAsync() { await completion.Task.ConfigureAwait(false); thread.Join(); }
    }

    private sealed class TestFolioView : FolioView
    {
        public void SendClick(string elementId, int fallbackX = 0, int fallbackY = 0)
        {
            SendContentInput(new ContentInputEvent("click", X: fallbackX, Y: fallbackY, Key: elementId));
        }

        public void SendTextInput(string text, string? key = null)
        {
            SendContentInput(new ContentInputEvent("textinput", Key: key, Text: text));
        }

        public void SendKeyDown(Keys key)
        {
            OnKeyDown(new KeyEventArgs(key));
        }

        public void SimulateMouseMove(int x, int y) =>
            OnMouseMove(new MouseEventArgs(MouseButtons.None, 0, x, y, 0));

        public void SimulateMouseDown(int x, int y) =>
            OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, x, y, 0));

        public void SimulateMouseUp(int x, int y) =>
            OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, x, y, 0));

        public void SimulateClick(int x, int y)
        {
            SimulateMouseDown(x, y);
            SimulateMouseUp(x, y);
        }

        public string GetActiveTooltip()
        {
            var field = typeof(FolioView).GetField("_toolTip", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.GetValue(this) is ToolTip tip)
                return tip.GetToolTip(this) ?? "";
            return "";
        }
    }
}
#else
namespace Folio.WinForms.Tests;

public sealed class ContentProcessInputIntegrationTests
{
    [Fact(Skip = "The content-process input integration tests require Windows Forms.")]
    public void WindowsOnlyPlaceholder() { }
}
#endif
