using Folio.Diagrams;

namespace Folio.Tests.Diagrams;

public sealed class DiagramRendererTests
{
    [Fact]
    public void RendersFlowchartTextAsSvg()
    {
        var svg = DiagramRenderer.Render("flowchart LR\nA[Start] --> B[Finish]");
        Assert.StartsWith("<svg", svg);
        Assert.Contains("Start", svg);
        Assert.Contains("Finish", svg);
        Assert.Contains("marker-end", svg);
    }

    [Fact]
    public void RendersSequenceMessagesAsSvg()
    {
        var svg = DiagramRenderer.Render("sequenceDiagram\nAlice->>Bob: Hello");
        Assert.Contains("Alice", svg);
        Assert.Contains("Bob", svg);
        Assert.Contains("Hello", svg);
    }

    [Fact]
    public void RendersPieValuesAsSvg()
    {
        var svg = DiagramRenderer.Render("pie title Sales\n\"North\" : 60\n\"South\" : 40");
        Assert.Contains("North", svg);
        Assert.Contains("South", svg);
        Assert.Contains("width=\"300", svg);
    }
}
