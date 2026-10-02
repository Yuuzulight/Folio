using Folio.Interaction;
using Folio.Painting;

namespace Folio.Tests.Interaction;

public class AccessibilityTreeTests
{
    private static readonly string Root = FindRoot();

    /// <summary>
    /// The tree of a static page against its snapshot in this folder (&lt;name&gt;.txt). Set FOLIO_UPDATE_SNAPSHOTS=1 to
    /// write the snapshots instead.
    /// </summary>
    [Theory]
    [InlineData("tests/conformance/S1/resume-cv/index.html", "s1-resume-cv")]
    [InlineData("tests/conformance/S3/class-timetable/index.html", "s3-class-timetable")]
    [InlineData("tests/Folio.Tests/Interaction/forms.html", "forms")]
    public void TreeMatchesSnapshot(string page, string snapshot)
    {
        using var document = Document.Parse(File.ReadAllText(Path.Combine(Root, page)));
        document.Paint(800, 600);
        var actual = document.AccessibilityTree()!.Dump();

        var path = Path.Combine(Root, "tests", "Folio.Tests", "Interaction", snapshot + ".txt");
        if (Environment.GetEnvironmentVariable("FOLIO_UPDATE_SNAPSHOTS") == "1")
            File.WriteAllText(path, actual);
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }

    [Fact]
    public void NothingBeforeThePageIsPainted()
    {
        Assert.Null(Document.Parse("<p>x").AccessibilityTree());
    }

    [Fact]
    public void HiddenContentIsLeftOut()
    {
        var tree = Tree("<p aria-hidden=true>a</p><p style='display:none'>b</p>"
            + "<p style='visibility:hidden'>c <span style='visibility:visible'>d</span></p><input type=hidden>");

        Assert.Equal("document \"\"\n  text \"d\"\n", tree.Dump());
    }

    [Fact]
    public void NamesFollowTheComputationOrder()
    {
        var tree = Tree("<span id=a>From</span> <span id=b>ids</span>"
            + "<button aria-labelledby='a b' aria-label=Label>Content</button>"
            + "<button aria-label=Label title=Tip>Content</button>"
            + "<button title=Tip>Content</button>"
            + "<button title=Tip></button>"
            + "<a href=#x>Go <img alt=home> <input value=now></a>");

        Assert.Equal(["From ids", "Label", "Content", "Tip", "Go home now"],
            tree.Children.Where(n => n.Role is "button" or "link").Select(n => n.Name));
    }

    [Fact]
    public void BoundsComeFromFragments()
    {
        var tree = Tree("<body style='margin:0'><div style='height:30px'></div>"
            + "<ul style='margin:0; padding:0 0 0 10px; width:200px'><li style='height:20px'></li><li style='height:25px'></li></ul>"
            + "<div style='display:contents'><button style='display:block; width:50px; height:10px; margin-left:5px; border:0; padding:0'></button></div>");

        var list = tree.Children[0];
        Assert.Equal(new RectF(0, 30, 210, 45), list.Bounds);
        Assert.Equal(new RectF(10, 30, 200, 20), list.Children[0].Bounds);
        Assert.Equal(new RectF(10, 50, 200, 25), list.Children[1].Bounds);
        Assert.Equal(new RectF(5, 75, 50, 10), tree.Children[1].Bounds);
    }

    private static AccessibleNode Tree(string html)
    {
        using var document = Document.Parse(html);
        document.Paint(800, 600);
        return document.AccessibilityTree()!;
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Folio.slnx")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("Folio.slnx not found above " + AppContext.BaseDirectory);
    }
}
