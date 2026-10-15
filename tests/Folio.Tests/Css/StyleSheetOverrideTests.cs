using Folio.Dom;
using Folio.Style;

namespace Folio.Tests.Css;

/// <summary>
/// A style element's rules can be replaced from the CSSOM without touching its DOM text (issue #499): the override is
/// what the cascade reads until the page changes the element's text again; a disabled style element is not applied.
/// </summary>
public class StyleSheetOverrideTests
{
    private static string ColorOf(Document document) =>
        document.QuerySelector("p")!.Node.ComputedStyle()!.Inherited.Color.ToString();

    private static (Document Document, ElementNode Style) Open(string styleAttributes = "")
    {
        var document = Document.Parse($"<!DOCTYPE html><style{styleAttributes}>p {{ color: red }}</style><p>text</p>");
        document.Paint(800, 600);
        return (document, document.QuerySelector("style")!.Node);
    }

    [Fact]
    public void TheOverrideReplacesTheTextForTheCascadeAndTheDomTextStaysAsItWas()
    {
        var (document, style) = Open();
        var red = ColorOf(document);

        style.SheetText = "p { color: blue }";
        document.Update();
        Assert.NotEqual(red, ColorOf(document));
        Assert.Equal("p { color: red }", style.TextContent);
    }

    [Fact]
    public void ChangingTheElementsTextClearsTheOverride()
    {
        var (document, style) = Open();
        style.SheetText = "p { color: blue }";
        document.Update();
        var blue = ColorOf(document);

        style.ReplaceAll(document.Node.CreateText("p { color: green }"));
        document.Update();

        Assert.Null(style.SheetText);
        Assert.NotEqual(blue, ColorOf(document));
    }

    [Fact]
    public void ChangingATextNodesDataClearsTheOverrideToo()
    {
        var (document, style) = Open();
        style.SheetText = "p { color: blue }";
        ((Text)style.FirstChild!).Data = "p { color: green }";
        Assert.Null(style.SheetText);
    }

    [Fact]
    public void ADisabledStyleElementIsNotApplied()
    {
        var (document, style) = Open();
        var red = ColorOf(document);

        style.SetAttribute("disabled", "");
        document.Update();
        Assert.NotEqual(red, ColorOf(document));

        style.RemoveAttribute("disabled");
        document.Update();
        Assert.Equal(red, ColorOf(document));
    }

    [Fact]
    public void AStyleElementAddedAtRunTimeAndRemovedAgainChangesTheCascade()
    {
        var (document, _) = Open();
        var red = ColorOf(document);

        var added = document.Node.CreateElement(Namespaces.Html, "style");
        added.AppendChild(document.Node.CreateText("p { color: blue }"));
        document.Node.DocumentElement!.AppendChild(added);
        document.Update();
        var blue = ColorOf(document);
        Assert.NotEqual(red, blue);

        added.Parent!.RemoveChild(added);
        document.Update();
        Assert.Equal(red, ColorOf(document));
    }
}

