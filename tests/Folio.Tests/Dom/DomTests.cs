using Folio.Dom;

namespace Folio.Tests.Dom;

public class DomTests
{
    private readonly DocumentNode _doc = new();

    private Element Html(string name) => _doc.CreateElement(Namespaces.Html, name);

    private static string Names(ContainerNode parent) =>
        string.Join(",", parent.Children.Select(n => n switch
        {
            Element e => e.LocalName,
            Text t => $"'{t.Data}'",
            _ => n.GetType().Name,
        }));

    [Fact]
    public void AppendAndInsertKeepSiblingLinksConsistent()
    {
        var parent = Html("div");
        var a = parent.AppendChild(Html("a"));
        var c = parent.AppendChild(Html("c"));
        var b = parent.InsertBefore(Html("b"), c);

        Assert.Equal("a,b,c", Names(parent));
        Assert.Same(a, parent.FirstChild);
        Assert.Same(c, parent.LastChild);
        Assert.Same(b, a.NextSibling);
        Assert.Same(a, b.PreviousSibling);
        Assert.Same(parent, b.Parent);
    }

    [Fact]
    public void RemoveChildUnlinks()
    {
        var parent = Html("div");
        var a = parent.AppendChild(Html("a"));
        var b = parent.AppendChild(Html("b"));
        var c = parent.AppendChild(Html("c"));

        parent.RemoveChild(b);

        Assert.Equal("a,c", Names(parent));
        Assert.Same(c, a.NextSibling);
        Assert.Null(b.Parent);
        Assert.Null(b.NextSibling);
        Assert.Null(b.PreviousSibling);
    }

    [Fact]
    public void InsertingAnAttachedNodeMovesIt()
    {
        var first = Html("div");
        var second = Html("div");
        var child = first.AppendChild(Html("p"));

        second.AppendChild(child);

        Assert.Null(first.FirstChild);
        Assert.Same(second, child.Parent);
    }

    [Fact]
    public void InsertBeforeItselfKeepsPosition()
    {
        var parent = Html("div");
        parent.AppendChild(Html("a"));
        var b = parent.AppendChild(Html("b"));
        parent.AppendChild(Html("c"));

        parent.InsertBefore(b, b);

        Assert.Equal("a,b,c", Names(parent));
    }

    [Fact]
    public void FragmentInsertionMovesItsChildren()
    {
        var parent = Html("div");
        var end = parent.AppendChild(Html("z"));
        var fragment = _doc.CreateFragment();
        fragment.AppendChild(Html("x"));
        fragment.AppendChild(_doc.CreateText("y"));

        parent.InsertBefore(fragment, end);

        Assert.Equal("x,'y',z", Names(parent));
        Assert.Null(fragment.FirstChild);
    }

    [Fact]
    public void RejectsHierarchyErrors()
    {
        var parent = Html("div");
        var child = parent.AppendChild(Html("p"));

        Assert.Throws<InvalidOperationException>(() => child.AppendChild(parent));
        Assert.Throws<InvalidOperationException>(() => parent.AppendChild(parent));
        Assert.Throws<ArgumentException>(() => parent.InsertBefore(Html("a"), Html("b")));
        Assert.Throws<ArgumentException>(() => parent.RemoveChild(Html("a")));
        Assert.Throws<ArgumentException>(() => parent.AppendChild(new DocumentNode().CreateText("x")));
        Assert.Throws<InvalidOperationException>(() => parent.AppendChild(_doc.CreateDocumentType("html", "", "")));
    }

    [Fact]
    public void DocumentAllowsOneDoctypeBeforeOneElement()
    {
        _doc.AppendChild(_doc.CreateComment("c"));
        var doctype = _doc.AppendChild(_doc.CreateDocumentType("html", "", ""));
        var html = _doc.AppendChild(Html("html"));

        Assert.Same(html, _doc.DocumentElement);
        Assert.Throws<InvalidOperationException>(() => _doc.AppendChild(Html("body")));
        Assert.Throws<InvalidOperationException>(() => _doc.AppendChild(_doc.CreateDocumentType("x", "", "")));
        Assert.Throws<InvalidOperationException>(() => _doc.AppendChild(_doc.CreateText("x")));

        _doc.RemoveChild(html);
        _doc.RemoveChild(doctype);
        _doc.AppendChild(Html("html"));
        Assert.Throws<InvalidOperationException>(() => _doc.AppendChild(_doc.CreateDocumentType("html", "", "")));
    }

    [Fact]
    public void AttributesAreSetReplacedAndRead()
    {
        var element = Html("a");

        element.SetAttribute("href", "x");
        element.SetAttribute("title", "t");
        element.SetAttribute("href", "y");

        Assert.Equal("y", element.GetAttribute("href"));
        Assert.Equal("t", element.GetAttribute("title"));
        Assert.Null(element.GetAttribute("never-interned-attribute-name"));
        Assert.Equal(2, element.Attributes.Length);
    }

    [Fact]
    public void IdAndClassesAreParsedWhenSet()
    {
        var element = Html("div");

        element.SetAttribute("id", "main");
        element.SetAttribute("class", " a\tb  a\nc ");

        Assert.Equal("main", _doc.TextOf(element.Id));
        Assert.Equal(["a", "b", "c"], element.Classes.Select(_doc.TextOf));

        element.SetAttribute("id", "");
        Assert.True(element.Id.IsNone);
    }

    [Fact]
    public void TextContentConcatenatesDescendantText()
    {
        var div = Html("div");
        div.AppendChild(_doc.CreateText("a"));
        var span = div.AppendChild(Html("span"));
        span.AppendChild(_doc.CreateText("b"));
        div.AppendChild(_doc.CreateComment("not text"));
        div.AppendChild(_doc.CreateText("c"));

        Assert.Equal("abc", div.TextContent);
        Assert.Null(_doc.TextContent);
    }

    [Fact]
    public void MutationsMarkTheElementAndItsAncestors()
    {
        var root = _doc.AppendChild(Html("html"));
        var body = root.AppendChild(Html("body"));
        var text = body.AppendChild(_doc.CreateText("x"));
        root.Flags = body.Flags = _doc.Flags = NodeFlags.None;

        text.Data = "y";

        Assert.Equal(NodeFlags.NeedsStyle, body.Flags);
        Assert.Equal(NodeFlags.DescendantNeedsStyle, root.Flags);
        Assert.Equal(NodeFlags.DescendantNeedsStyle, _doc.Flags);
    }

    [Fact]
    public void AtomsAreSharedAcrossDocuments()
    {
        Assert.Equal(_doc.Intern("div"), new DocumentNode().Intern("div"));
        Assert.Equal("div", _doc.TextOf(_doc.Intern("div")));
    }

    [Fact]
    public void FullSharedTableFallsBackToDocumentAtoms()
    {
        var table = new AtomTable(capacity: 2);
        var doc = new DocumentNode(table);

        var a = doc.Intern("a");
        var b = doc.Intern("b");
        var c = doc.Intern("c");

        Assert.True(a.Value > 0 && b.Value > 0);
        Assert.True(c.Value < 0);
        Assert.Equal(c, doc.Intern("c"));
        Assert.Equal(c, doc.Find("c"));
        Assert.Equal("c", doc.TextOf(c));
        Assert.True(new DocumentNode(table).Find("c").IsNone);
        Assert.Equal(2, table.Count);
    }
}
