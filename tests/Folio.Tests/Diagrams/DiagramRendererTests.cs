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

    [Fact]
    public void RendersClassAndStateEdgesAsSvg()
    {
        Assert.Contains("Base", DiagramRenderer.Render("classDiagram\nBase <|-- Child"));
        Assert.Contains("Ready", DiagramRenderer.Render("stateDiagram-v2\n[*] --> Ready"));
    }

    [Fact]
    public void RendersEntityRelationshipTimelineAndGanttDiagrams()
    {
        Assert.Contains("er-edge", DiagramRenderer.Render("erDiagram\nCUSTOMER ||--o{ ORDER : places"));
        Assert.Contains("timeline-event", DiagramRenderer.Render("timeline\ntitle History\n2024 : Launch"));
        Assert.Contains("gantt-task", DiagramRenderer.Render("gantt\ndateFormat YYYY-MM-DD\nBuild :done, id, 2024-01-30, 3d"));
    }

    [Theory]
    [InlineData("flowchart LR\nA --> B")]
    [InlineData("sequenceDiagram\nAlice->>Bob: Hello")]
    [InlineData("classDiagram\nBase <|-- Child")]
    [InlineData("stateDiagram-v2\n[*] --> Ready")]
    [InlineData("erDiagram\nCUSTOMER ||--o{ ORDER : places")]
    [InlineData("timeline\n2024 : Launch")]
    [InlineData("gantt\nBuild :done, id, 2024-01-30, 3d")]
    [InlineData("pie\n\"North\" : 60")]
    public void EverySupportedKindExposesStableDomClasses(string source)
    {
        Assert.True(DiagramRenderer.TryRender(source, out var svg));
        Assert.Contains("class=\"folio-diagram\"", svg);
        Assert.Contains("class=\"folio-diagram-content\"", svg);
        Assert.Contains("folio-diagram-", svg);
    }

    [Fact]
    public void FlowchartsRenderCommonShapesEdgeKindsAndLabels()
    {
        var source = "flowchart LR\nA((Start)) -.-> B{Choice}\nB ==> C(Round)\nA -->|yes| C";
        var svg = DiagramRenderer.Render(source);

        Assert.Contains("folio-diagram-circle", svg);
        Assert.Contains("folio-diagram-diamond", svg);
        Assert.Contains("folio-diagram-round", svg);
        Assert.Contains("data-kind=\"-.-&gt;\"", svg);
        Assert.Contains("data-kind=\"==&gt;\"", svg);
        Assert.Contains("yes", svg);
    }

    [Fact]
    public void UnsupportedKnownDiagramReturnsAStableSourcePosition()
    {
        var source = "flowchart LR\nsubgraph Missing\nend";

        Assert.False(DiagramRenderer.TryRender(source, out _, out var diagnostic));
        Assert.NotNull(diagnostic);
        Assert.True(diagnostic!.Position >= source.IndexOf('\n') + 1);
        Assert.Contains("supported construct", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
    }
}
