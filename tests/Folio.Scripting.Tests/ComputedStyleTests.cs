namespace Folio.Scripting.Tests;

/// <summary><c>getComputedStyle</c> and <c>innerText</c> (#460): resolved values from the last layout, and text as it is rendered.</summary>
public class ComputedStyleTests
{
    private static string Computed(CssomViewTests.Page page, string id, string property) =>
        page.Run($"getComputedStyle(el('{id}')).getPropertyValue('{property}')");

    // ---- resolved values ----

    [Fact]
    public void TheWidthOfAnAutoWidthBlockIsItsUsedWidth()
    {
        using var page = new CssomViewTests.Page("<div id=a style='padding:10px;border:2px solid'><div id=b style='height:50px'></div></div><div id=c style='box-sizing:border-box;padding:10px;border:2px solid'></div>");
        Assert.Equal("776px", Computed(page, "a", "width"));
        Assert.Equal("50px", Computed(page, "b", "height"));
        Assert.Equal("800px", Computed(page, "c", "width")); // border-box: the border box itself
        Assert.Equal("776px", page.Run("getComputedStyle(el('a')).width"));
    }

    [Fact]
    public void PercentagesAreResolvedAgainstTheContainingBlock()
    {
        using var page = new CssomViewTests.Page("<div id=a style='width:50%;height:20px;padding:10%'></div><div id=b style='width:200px;height:10px;margin:0 auto'></div>");
        Assert.Equal("400px", Computed(page, "a", "width"));
        Assert.Equal("80px", Computed(page, "a", "padding-left"));
        Assert.Equal("80px", Computed(page, "a", "padding-top")); // the width, not the height, of the containing block
        Assert.Equal("300px", Computed(page, "b", "margin-left"));
        Assert.Equal("300px", Computed(page, "b", "margin-right"));
        Assert.Equal("0px", Computed(page, "b", "margin-top"));
    }

    [Fact]
    public void AValueIsResolvedFromTheLayoutThatAScriptChangeJustMade()
    {
        using var page = new CssomViewTests.Page("<div id=a style='width:100px;height:10px'></div>");
        Assert.Equal("100px", Computed(page, "a", "width"));
        page.Run("el('a').style.width = '240px';");
        Assert.Equal("240px", Computed(page, "a", "width"));
        page.Run("var live = getComputedStyle(el('a')); el('a').style.width = '30px';");
        Assert.Equal("30px", page.Run("live.width"));
    }

    [Fact]
    public void ALoopOfWritesAndReadsBringsTheLayoutUpToDateOncePerWriteAndNotPerRead()
    {
        using var page = new CssomViewTests.Page("<div id=a style='width:100px;height:10px'></div>");
        Computed(page, "a", "width");
        var before = page.Document.UpdatesRun;
        for (var i = 1; i <= 5; i++)
        {
            page.Run($"el('a').style.width = '{i * 10}px';");
            Assert.Equal($"{i * 10}px", Computed(page, "a", "width"));
            Assert.Equal($"{i * 10}", page.Run("String(el('a').offsetWidth)"));
            Assert.Equal($"{i * 10}", page.Run("String(el('a').getBoundingClientRect().width)"));
        }
        Assert.Equal(before + 5, page.Document.UpdatesRun);
    }

    [Fact]
    public void AnInlineElementKeepsItsComputedWidthAndAnElementWithNoBoxItsComputedValue()
    {
        using var page = new CssomViewTests.Page("<span id=a>text</span><div id=b style='display:none;width:50%'></div>");
        Assert.Equal("auto", Computed(page, "a", "width"));
        Assert.Equal("50%", Computed(page, "b", "width"));
        Assert.Equal("none", Computed(page, "b", "display"));
    }

    [Fact]
    public void AnElementThatIsNotInTheDocumentHasNothingResolved()
    {
        using var page = new CssomViewTests.Page("<div id=a></div>");
        Assert.Equal("", page.Run("var d = document.createElement('div'); getComputedStyle(d).display"));
        Assert.Equal("", page.Run("getComputedStyle(d).getPropertyValue('width')"));
    }

    [Fact]
    public void LengthsColoursAndKeywordsAreWrittenTheWayCssomWritesThem()
    {
        using var page = new CssomViewTests.Page("<div id=a style='color:#102030;background:rgba(1,2,3,0.5);border:3px solid red;font-size:20px;opacity:.5;z-index:4;letter-spacing:2px'></div><div id=b></div>");
        Assert.Equal("rgb(16, 32, 48)", Computed(page, "a", "color"));
        Assert.Equal("rgba(1, 2, 3, 0.5)", Computed(page, "a", "background-color"));
        Assert.Equal("3px", Computed(page, "a", "border-top-width"));
        Assert.Equal("solid", Computed(page, "a", "border-top-style"));
        Assert.Equal("rgb(255, 0, 0)", Computed(page, "a", "border-top-color"));
        Assert.Equal("20px", Computed(page, "a", "font-size"));
        Assert.Equal("0.5", Computed(page, "a", "opacity"));
        Assert.Equal("4", Computed(page, "a", "z-index"));
        Assert.Equal("2px", Computed(page, "a", "letter-spacing"));
        Assert.Equal("normal", Computed(page, "b", "letter-spacing"));
        Assert.Equal("0px", Computed(page, "b", "border-top-width")); // no border style: no border
        Assert.Equal("16px", Computed(page, "b", "font-size"));
        Assert.Equal("auto", Computed(page, "b", "z-index"));
    }

    [Fact]
    public void ShorthandsAreWrittenFromTheirLonghands()
    {
        using var page = new CssomViewTests.Page("<div id=a style='margin:5px;padding:1px 2px 3px 4px;border:2px solid blue;border-radius:4px 8px;overflow:hidden auto;gap:3px 6px'></div>");
        Assert.Equal("5px", Computed(page, "a", "margin"));
        Assert.Equal("1px 2px 3px 4px", Computed(page, "a", "padding"));
        Assert.Equal("2px solid rgb(0, 0, 255)", Computed(page, "a", "border"));
        Assert.Equal("2px", Computed(page, "a", "border-width"));
        Assert.Equal("4px 8px", Computed(page, "a", "border-radius"));
        Assert.Equal("hidden auto", Computed(page, "a", "overflow"));
        Assert.Equal("3px 6px", Computed(page, "a", "gap"));
        Assert.Equal("2px solid rgb(0, 0, 255)", Computed(page, "a", "border-top"));
    }

    [Fact]
    public void OffsetsOfAPositionedElementAreUsedValuesAndOthersAreComputed()
    {
        using var page = new CssomViewTests.Page(
            "<div id=box style='position:relative;left:20px;top:10px;width:300px;height:200px;margin:30px'>"
            + "<div id=abs style='position:absolute;left:10px;bottom:5px;width:50px;height:40px'></div>"
            + "<div id=auto style='position:absolute;width:50px;height:40px'></div>"
            + "<div id=rel style='position:relative;right:7px'></div></div><div id=st></div>");
        Assert.Equal("10px", Computed(page, "abs", "left"));
        Assert.Equal("5px", Computed(page, "abs", "bottom"));
        Assert.Equal("155px", Computed(page, "abs", "top")); // 200 - 5 - 40
        Assert.Equal("0px", Computed(page, "auto", "left"));
        Assert.Equal("0px", Computed(page, "auto", "top"));
        Assert.Equal("-7px", Computed(page, "rel", "left"));
        Assert.Equal("7px", Computed(page, "rel", "right"));
        Assert.Equal("auto", Computed(page, "st", "left"));
        Assert.Equal("20px", Computed(page, "box", "left"));
    }

    [Fact]
    public void LogicalPropertiesAnswerWithTheirPhysicalOnes()
    {
        using var page = new CssomViewTests.Page("<div id=a style='width:100px;height:30px;margin-left:12px;padding-top:6px'></div>");
        Assert.Equal("100px", Computed(page, "a", "inline-size"));
        Assert.Equal("30px", Computed(page, "a", "block-size"));
        Assert.Equal("12px", Computed(page, "a", "margin-inline-start"));
        Assert.Equal("6px", Computed(page, "a", "padding-block-start"));
    }

    [Fact]
    public void PseudoElementsHaveTheirOwnStyle()
    {
        using var page = new CssomViewTests.Page("<style>#a::before { content: 'x'; color: red; display: block }</style><div id=a></div>");
        Assert.Equal("block", page.Run("getComputedStyle(el('a'), '::before').display"));
        Assert.Equal("rgb(255, 0, 0)", page.Run("getComputedStyle(el('a'), ':before').color"));
        Assert.Equal("", page.Run("getComputedStyle(el('a'), '::nonsense').display"));
        Assert.Equal("block", page.Run("getComputedStyle(el('a'), null).display"));
    }

    [Fact]
    public void ACustomPropertyIsItsComputedValueAndTheDeclarationIsReadOnly()
    {
        using var page = new CssomViewTests.Page("<div id=a style='--gap: 4px'></div>");
        Assert.Equal("4px", page.Run("getComputedStyle(el('a')).getPropertyValue('--gap')"));
        Assert.Equal("", page.Run("getComputedStyle(el('a')).getPropertyValue('--missing')"));
    }

    // ---- innerText ----

    private static string Text(CssomViewTests.Page page, string id = "a") => page.Run($"JSON.stringify(el('{id}').innerText)");

    [Fact]
    public void BlocksAreOnTheirOwnLinesAndParagraphsAreSeparatedByABlankLine()
    {
        using var page = new CssomViewTests.Page("<div id=a>one<div>two</div>three</div><div id=b><p>a</p><p>b</p></div><div id=c><div> <div>a</div> <div>b</div> </div></div>");
        Assert.Equal("\"one\\ntwo\\nthree\"", Text(page));
        Assert.Equal("\"a\\n\\nb\"", Text(page, "b"));
        Assert.Equal("\"a\\nb\"", Text(page, "c"));
    }

    [Fact]
    public void ALineBreakElementIsANewline()
    {
        using var page = new CssomViewTests.Page("<div id=a>a<br>b</div><div id=b>x<br></div><div id=c><br></div>");
        Assert.Equal("\"a\\nb\"", Text(page));
        Assert.Equal("\"x\\n\"", Text(page, "b"));
        Assert.Equal("\"\\n\"", Text(page, "c"));
    }

    [Fact]
    public void WhiteSpaceCollapsesAcrossInlineElementsAndIsKeptWhereTheStyleKeepsIt()
    {
        using var page = new CssomViewTests.Page(
            "<div id=a>  a   b \n c  </div><div id=b>a <span> b</span> <i> c </i></div><pre id=c>a   b\n c</pre><div id=d style='white-space:pre-line'>a   b\n   c</div>");
        Assert.Equal("\"a b c\"", Text(page));
        Assert.Equal("\"a b c\"", Text(page, "b"));
        Assert.Equal("\"a   b\\n c\"", Text(page, "c"));
        Assert.Equal("\"a b\\nc\"", Text(page, "d"));
    }

    [Fact]
    public void HiddenContentIsLeftOut()
    {
        using var page = new CssomViewTests.Page(
            "<div id=a>a<span style='display:none'>X</span><span style='visibility:hidden'>Y</span><span style='visibility:hidden'><span style='visibility:visible'>Z</span></span>b</div>"
            + "<div id=b style='display:none'>x <b>y</b></div>");
        Assert.Equal("\"aZb\"", Text(page));
        Assert.Equal("\"x y\"", Text(page, "b")); // not rendered: the text content
    }

    [Fact]
    public void TextTransformIsApplied()
    {
        using var page = new CssomViewTests.Page("<div id=a style='text-transform:uppercase'>abc <b>def</b></div><div id=b style='text-transform:capitalize'>one two</div>");
        Assert.Equal("\"ABC DEF\"", Text(page));
        Assert.Equal("\"One Two\"", Text(page, "b"));
    }

    [Fact]
    public void TableCellsAreTabSeparatedAndRowsAreLines()
    {
        using var page = new CssomViewTests.Page("<table id=a><tr><td>a</td><td>b</td></tr><tr><td>c</td><td>d</td></tr></table>");
        Assert.Equal("\"a\\tb\\nc\\td\"", Text(page));
    }

    [Fact]
    public void SettingInnerTextMakesTextAndLineBreaks()
    {
        using var page = new CssomViewTests.Page("<div id=a>old <b>stuff</b></div>");
        page.Run("el('a').innerText = 'x\\ny\\r\\nz';");
        Assert.Equal("x<br>y<br>z", page.Run("el('a').innerHTML"));
        page.Run("el('a').innerText = null;");
        Assert.Equal("0", page.Run("String(el('a').childNodes.length)"));
        page.Run("el('a').innerText = '<b>'");
        Assert.Equal("&lt;b&gt;", page.Run("el('a').innerHTML"));
    }

    [Fact]
    public void SettingOuterTextReplacesTheElementAndJoinsTheTextAround()
    {
        using var page = new CssomViewTests.Page("<div id=p>x<span id=s>y</span>z</div>");
        page.Run("el('s').outerText = 'Q';");
        Assert.Equal("xQz", page.Run("el('p').textContent"));
        Assert.Equal("1", page.Run("String(el('p').childNodes.length)"));
        Assert.Equal("NoModificationAllowedError", page.Run("try { document.createElement('div').outerText = 'x'; } catch (e) { e.name }"));
    }
}
