using Folio.Diagrams;
using System.Text.RegularExpressions;

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
    public void SequenceSupportsAliasesActivationsNotesAndBlocks()
    {
        var svg = DiagramRenderer.Render("sequenceDiagram\nparticipant A as Alice\nactor B as Bob\nactivate A\nloop retry\nA-->>B: ping\nend\nnote right of B: waiting\ndeactivate A");

        Assert.Contains("data-role=\"participant\"", svg);
        Assert.Contains("Alice", svg);
        Assert.Contains("Bob", svg);
        Assert.Contains("data-kind=\"--&gt;&gt;\"", svg);
        Assert.Contains("folio-diagram-activation", svg);
        Assert.Contains("sequence-loop", svg);
        Assert.Contains("folio-diagram-note", svg);
        Assert.Contains("1. ping", DiagramRenderer.Render("sequenceDiagram\nautonumber\nA->>B: ping"));
    }

    [Theory]
    [InlineData("sequenceDiagram\nA->>B: call")]
    [InlineData("sequenceDiagram\nA-->>B: reply")]
    [InlineData("sequenceDiagram\nA-xB: fail")]
    [InlineData("sequenceDiagram\nA-->B: signal")]
    public void SequenceMessageKindsArePreserved(string source)
    {
        var svg = DiagramRenderer.Render(source);
        Assert.Contains("sequence-message", svg);
        Assert.Contains("data-kind=", svg);
    }

    [Fact]
    public void RendersPieValuesAsSvg()
    {
        var svg = DiagramRenderer.Render("pie title Sales\n\"North\" : 60\n\"South\" : 40");
        Assert.Contains("North", svg);
        Assert.Contains("South", svg);
        Assert.Equal(2, Regex.Matches(svg, "folio-diagram-slice").Count);
        Assert.Contains(" A 130 130", svg);
    }

    [Fact]
    public void RendersClassAndStateEdgesAsSvg()
    {
        Assert.Contains("Base", DiagramRenderer.Render("classDiagram\nclass Base {\n+int id\n}\nBase <|-- Child"));
        Assert.Contains("class-member", DiagramRenderer.Render("classDiagram\nclass Base {\n+int id\n}"));
        Assert.Contains("Ready", DiagramRenderer.Render("stateDiagram-v2\n[*] --> Ready : boot"));
        Assert.Contains("state-transition-label", DiagramRenderer.Render("stateDiagram-v2\n[*] --> Ready : boot"));
        Assert.Contains("data-cardinality=\"many\"", DiagramRenderer.Render("classDiagram\nBase <|-- Child : many"));
        Assert.Contains("folio-diagram-note", DiagramRenderer.Render("stateDiagram-v2\nstate Ready {\n[*] --> Done\n}\nnote Ready: stable"));
    }

    [Fact]
    public void StateChoiceForkAndJoinMarkersExposeStableShapes()
    {
        var svg = DiagramRenderer.Render("stateDiagram-v2\nstate choose <<choice>>\nstate split <<fork>>\nstate merge <<join>>\n[*] --> choose\nchoose --> split\nsplit --> merge");

        Assert.Contains("state-choice", svg);
        Assert.Contains("data-kind=\"choice\"", svg);
        Assert.Contains("state-fork", svg);
        Assert.Contains("data-kind=\"fork\"", svg);
        Assert.Contains("state-join", svg);
        Assert.Contains("data-kind=\"join\"", svg);
    }

    [Fact]
    public void RendersEntityRelationshipTimelineAndGanttDiagrams()
    {
        Assert.Contains("er-edge", DiagramRenderer.Render("erDiagram\nCUSTOMER ||--o{ ORDER : places"));
        Assert.Contains("timeline-event", DiagramRenderer.Render("timeline\ntitle History\nsection Releases\n2024 : Launch"));
        Assert.Contains("folio-diagram-section", DiagramRenderer.Render("timeline\nsection Releases\n2024 : Launch"));
        Assert.Contains("gantt-task", DiagramRenderer.Render("gantt\ndateFormat YYYY-MM-DD\nBuild :done, id, 2024-01-30, 3d"));
        Assert.Contains("er-attribute", DiagramRenderer.Render("erDiagram\nCUSTOMER {\nstring id\n}\nCUSTOMER ||--o{ ORDER : places"));
    }

    [Fact]
    public void GanttDependenciesCrossMonthAndYearBoundaries()
    {
        var svg = DiagramRenderer.Render("gantt\ndateFormat YYYY-MM-DD\nBuild :build, 2024-12-30, 3d\nDeploy :deploy, after build, 2d");

        Assert.Contains("data-start=\"2024-12-30\"", svg);
        Assert.Contains("data-start=\"2025-01-02\"", svg);
        Assert.Contains("data-after=\"build\"", svg);
    }

    [Fact]
    public void PieDiagramUsesArcSlicesAndInvariantNumbers()
    {
        var svg = DiagramRenderer.Render("pie title Sales\n\"North\" : 60.5\n\"South\" : 39.5");

        Assert.Equal(2, Regex.Matches(svg, "folio-diagram-slice").Count);
        Assert.Contains("data-label=\"North\"", svg);
        Assert.Contains(" A 130 130", svg);
        Assert.Contains("39.5", svg);
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
    public void FlowchartsRenderSubgraphsAndClassStyles()
    {
        var svg = DiagramRenderer.Render("flowchart LR\nsubgraph Input\nA[Start] --> B[Validate]\nend\nclassDef hot fill:#fee2e2,stroke:#dc2626\nclass B hot");

        Assert.Contains("folio-diagram-subgraph", svg);
        Assert.Contains("data-name=\"Input\"", svg);
        Assert.Contains("data-class=\"hot\"", svg);
        Assert.Contains("fill:#fee2e2", svg);
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
