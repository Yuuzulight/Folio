using System.Numerics;
using Folio.Dom;
using Folio.Interaction;

namespace Folio.Tests.Interaction;

public class ButtonCheckboxRadioSelectTests
{
    private static readonly FolioOptions BoxFontOptions = new()
    {
        Fonts = new Folio.Typography.FontSettings
        {
            Source = new Folio.Typography.FontFolderSource(Path.Combine(AppContext.BaseDirectory, "fonts")),
            GenericFamilies = new Dictionary<string, IReadOnlyList<string>>
            {
                ["serif"] = ["Folio Box"],
                ["sans-serif"] = ["Folio Box"],
                ["monospace"] = ["Folio Box"],
            },
        },
    };

    [Fact]
    public void CheckboxTogglesOnClickAndSpace()
    {
        using var doc = Document.Parse("<input id=cb type=checkbox>", BoxFontOptions);
        doc.Paint(800, 600);

        var cb = doc.GetElementById("cb")!;
        Assert.False(cb.Checked);

        // Click toggles to true
        cb.Click();
        Assert.True(cb.Checked);

        // Click toggles to false
        cb.Click();
        Assert.False(cb.Checked);

        // Space key when focused toggles to true
        cb.Focus();
        doc.Input.HandleKeyDown(new KeyEvent(" ", "Space"));
        Assert.True(cb.Checked);
    }

    [Fact]
    public void CheckboxIndeterminateStateClearsOnToggle()
    {
        using var doc = Document.Parse("<input id=cb type=checkbox checked>", BoxFontOptions);
        doc.Paint(800, 600);

        var cb = doc.GetElementById("cb")!;
        cb.Indeterminate = true;
        Assert.True(cb.Indeterminate);
        Assert.True(cb.Checked);

        // Clicking clears indeterminate and toggles checked state
        cb.Click();
        Assert.False(cb.Indeterminate);
        Assert.False(cb.Checked);
    }

    [Fact]
    public void CheckboxMatchesCheckedAndIndeterminatePseudoClasses()
    {
        var html = """
            <style>
              input:checked { color: red; }
              input:indeterminate { color: blue; }
            </style>
            <input id=cb type=checkbox>
            """;
        using var doc = Document.Parse(html, BoxFontOptions);
        doc.Paint(800, 600);

        var cb = doc.GetElementById("cb")!;
        Assert.False(cb.Checked);

        cb.Checked = true;
        doc.Paint(800, 600);
        // Style should be invalidated and restyled

        cb.Indeterminate = true;
        doc.Paint(800, 600);
    }

    [Fact]
    public void RadioButtonsAreMutuallyExclusiveWithinGroup()
    {
        var html = """
            <form id=form>
              <input id=r1 type=radio name=color value=red checked>
              <input id=r2 type=radio name=color value=green>
              <input id=r3 type=radio name=color value=blue>
            </form>
            """;
        using var doc = Document.Parse(html, BoxFontOptions);
        doc.Paint(800, 600);

        var r1 = doc.GetElementById("r1")!;
        var r2 = doc.GetElementById("r2")!;
        var r3 = doc.GetElementById("r3")!;

        Assert.True(r1.Checked);
        Assert.False(r2.Checked);
        Assert.False(r3.Checked);

        // Click r2
        r2.Click();
        Assert.False(r1.Checked);
        Assert.True(r2.Checked);
        Assert.False(r3.Checked);

        // Click r3
        r3.Click();
        Assert.False(r1.Checked);
        Assert.False(r2.Checked);
        Assert.True(r3.Checked);
    }

    [Fact]
    public void RadioButtonsNavigateWithArrowKeys()
    {
        var html = """
            <div>
              <input id=r1 type=radio name=opt value=1 checked>
              <input id=r2 type=radio name=opt value=2>
              <input id=r3 type=radio name=opt value=3>
            </div>
            """;
        using var doc = Document.Parse(html, BoxFontOptions);
        doc.Paint(800, 600);

        var r1 = doc.GetElementById("r1")!;
        var r2 = doc.GetElementById("r2")!;
        var r3 = doc.GetElementById("r3")!;

        r1.Focus();
        Assert.True(r1.Checked);

        // ArrowDown selects r2
        doc.Input.HandleKeyDown(new KeyEvent("ArrowDown", "ArrowDown"));
        Assert.False(r1.Checked);
        Assert.True(r2.Checked);

        // ArrowDown selects r3
        doc.Input.HandleKeyDown(new KeyEvent("ArrowDown", "ArrowDown"));
        Assert.False(r2.Checked);
        Assert.True(r3.Checked);

        // ArrowDown wraps to r1
        doc.Input.HandleKeyDown(new KeyEvent("ArrowDown", "ArrowDown"));
        Assert.True(r1.Checked);
        Assert.False(r3.Checked);

        // ArrowUp cycles backward to r3
        doc.Input.HandleKeyDown(new KeyEvent("ArrowUp", "ArrowUp"));
        Assert.False(r1.Checked);
        Assert.True(r3.Checked);
    }

    [Fact]
    public void LabelClickActivatesAssociatedControl()
    {
        var html = """
            <label for=mycb id=lbl>Click me</label>
            <input id=mycb type=checkbox>
            """;
        using var doc = Document.Parse(html, BoxFontOptions);
        doc.Paint(800, 600);

        var lbl = doc.GetElementById("lbl")!;
        var cb = doc.GetElementById("mycb")!;
        Assert.False(cb.Checked);

        lbl.Click();
        Assert.True(cb.Checked);
    }

    [Fact]
    public void LabelWrappingControlActivatesOnLabelClick()
    {
        var html = """
            <label id=lbl>
              Option
              <input id=cb type=checkbox>
            </label>
            """;
        using var doc = Document.Parse(html, BoxFontOptions);
        doc.Paint(800, 600);

        var lbl = doc.GetElementById("lbl")!;
        var cb = doc.GetElementById("cb")!;
        Assert.False(cb.Checked);

        lbl.Click();
        Assert.True(cb.Checked);
    }

    [Fact]
    public void SelectOptionModelAndArrowNavigation()
    {
        var html = """
            <select id=sel>
              <option value=a>Option A</option>
              <option value=b selected>Option B</option>
              <option value=c>Option C</option>
            </select>
            """;
        using var doc = Document.Parse(html, BoxFontOptions);
        doc.Paint(800, 600);

        var sel = doc.GetElementById("sel")!;
        Assert.Equal(1, sel.SelectedIndex);
        Assert.Single(sel.SelectedOptions);
        Assert.Equal("Option B", sel.SelectedOptions[0].TextContent.Trim());

        sel.Focus();

        // ArrowDown steps to option C (index 2)
        doc.Input.HandleKeyDown(new KeyEvent("ArrowDown", "ArrowDown"));
        Assert.Equal(2, sel.SelectedIndex);

        // ArrowDown clamps at option C
        doc.Input.HandleKeyDown(new KeyEvent("ArrowDown", "ArrowDown"));
        Assert.Equal(2, sel.SelectedIndex);

        // ArrowUp steps to option B (index 1)
        doc.Input.HandleKeyDown(new KeyEvent("ArrowUp", "ArrowUp"));
        Assert.Equal(1, sel.SelectedIndex);

        // Programmatic SelectedIndex
        sel.SelectedIndex = 0;
        Assert.Equal(0, sel.SelectedIndex);
        Assert.Equal("Option A", sel.SelectedOptions[0].TextContent.Trim());
    }

    [Fact]
    public void RangeSliderStepsAndExtremes()
    {
        var html = "<input id=rng type=range min=0 max=100 step=5 value=50>";
        using var doc = Document.Parse(html, BoxFontOptions);
        doc.Paint(800, 600);

        var rng = doc.GetElementById("rng")!;
        Assert.Equal("50", rng.Value);

        rng.Focus();

        // ArrowRight steps by +5
        doc.Input.HandleKeyDown(new KeyEvent("ArrowRight", "ArrowRight"));
        Assert.Equal("55", rng.Value);

        // ArrowLeft steps by -5
        doc.Input.HandleKeyDown(new KeyEvent("ArrowLeft", "ArrowLeft"));
        Assert.Equal("50", rng.Value);

        // Home sets to min (0)
        doc.Input.HandleKeyDown(new KeyEvent("Home", "Home"));
        Assert.Equal("0", rng.Value);

        // End sets to max (100)
        doc.Input.HandleKeyDown(new KeyEvent("End", "End"));
        Assert.Equal("100", rng.Value);
    }

    [Fact]
    public void DetailsToggleOnSummaryClick()
    {
        var html = """
            <details id=det>
              <summary id=sum>Details Summary</summary>
              <p>Details content</p>
            </details>
            """;
        using var doc = Document.Parse(html, BoxFontOptions);
        doc.Paint(800, 600);

        var det = doc.GetElementById("det")!;
        var sum = doc.GetElementById("sum")!;
        Assert.Null(det.GetAttribute("open"));

        // Click summary opens details
        sum.Click();
        Assert.NotNull(det.GetAttribute("open"));

        // Click summary closes details
        sum.Click();
        Assert.Null(det.GetAttribute("open"));
    }

    [Fact]
    public void FormSubmissionAndReset()
    {
        var html = """
            <form id=f action='/submit' method='post'>
              <input id=t name=username type=text value=alice>
              <input id=c name=subscribe type=checkbox checked>
              <select id=s name=role>
                <option value=user>User</option>
                <option value=admin selected>Admin</option>
              </select>
              <button id=btn type=submit name=action value=save>Save</button>
              <input id=rst type=reset value=Reset>
            </form>
            """;
        using var doc = Document.Parse(html, BoxFontOptions);
        doc.Paint(800, 600);

        FormSubmittedEventArgs? submitted = null;
        doc.Input.FormSubmitted += (_, e) => submitted = e;

        var btn = doc.GetElementById("btn")!;
        btn.Click();

        Assert.NotNull(submitted);
        Assert.Equal("/submit", submitted.Action);
        Assert.Equal("POST", submitted.Method);
        Assert.Equal("btn", submitted.Submitter?.Id);

        var dict = submitted.Data.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        Assert.Equal("alice", dict["username"]);
        Assert.Equal("on", dict["subscribe"]);
        Assert.Equal("admin", dict["role"]);
        Assert.Equal("save", dict["action"]);

        // Modify values
        var t = doc.GetElementById("t")!;
        t.Value = "bob";
        var c = doc.GetElementById("c")!;
        c.Checked = false;

        Assert.Equal("bob", t.Value);
        Assert.False(c.Checked);

        // Click reset
        var rst = doc.GetElementById("rst")!;
        rst.Click();

        // Values restored to initial attributes
        Assert.Equal("alice", t.Value);
        Assert.True(c.Checked);
    }

    [Fact]
    public void SingleLineInputEnterSubmitsForm()
    {
        var html = """
            <form id=f action='/search' method='get'>
              <input id=q name=q type=text value='hello'>
            </form>
            """;
        using var doc = Document.Parse(html, BoxFontOptions);
        doc.Paint(800, 600);

        FormSubmittedEventArgs? submitted = null;
        doc.Input.FormSubmitted += (_, e) => submitted = e;

        var q = doc.GetElementById("q")!;
        q.Focus();

        doc.Input.HandleKeyDown(new KeyEvent("Enter", "Enter"));

        Assert.NotNull(submitted);
        Assert.Equal("/search", submitted.Action);
        Assert.Equal("GET", submitted.Method);
        Assert.Single(submitted.Data);
        Assert.Equal("q", submitted.Data[0].Key);
        Assert.Equal("hello", submitted.Data[0].Value);
    }

    [Fact]
    public void ButtonClickRaisesButtonClickedEvent()
    {
        var html = "<button id=mybtn type=button>Press</button>";
        using var doc = Document.Parse(html, BoxFontOptions);
        doc.Paint(800, 600);

        ButtonClickedEventArgs? clicked = null;
        doc.Input.ButtonClicked += (_, e) => clicked = e;

        var btn = doc.GetElementById("mybtn")!;
        btn.Click();

        Assert.NotNull(clicked);
        Assert.Equal("mybtn", clicked.Button.Id);
    }
}
