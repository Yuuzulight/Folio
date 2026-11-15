using Folio.Diagrams;
using Folio.Skia;

namespace Folio.RenderTests;

/// <summary>
/// Writes one picture of every diagram kind, to look at while working on the renderer. Set FOLIO_CONTACT_SHEET to the PNG path
/// to write; without it the test does nothing.
/// </summary>
public class DiagramContactSheet
{
    [Fact]
    public void WritesAPictureOfEveryDiagramKindWhenAsked()
    {
        var path = Environment.GetEnvironmentVariable("FOLIO_CONTACT_SHEET");
        Assert.SkipWhen(string.IsNullOrEmpty(path), "FOLIO_CONTACT_SHEET is not set.");
        var kinds = new (string Title, string Source)[]
        {
            ("Flowchart LR", "flowchart LR\nA[Ingest] --> B[Validate]\nB --> C[Transform]\nC --> D[Store]\nD --> E[Publish]"),
            ("Flowchart TD, labels", "flowchart TD\nA[Order] --> B{In stock}\nB -->|yes| C[Pick]\nB -->|no| D[Backorder]\nC --> E[Ship]\nD --> E"),
            ("Sequence", "sequenceDiagram\nparticipant A as Alice\nparticipant B as Bob\nA->>B: Hello\nB-->>A: Hi there"),
            ("Class", "classDiagram\nclass Animal {\n+String name\n+eat()\n}\nclass Dog {\n+bark()\n}\nAnimal <|-- Dog"),
            ("State", "stateDiagram-v2\n[*] --> Idle\nIdle --> Running : start\nRunning --> Done : finish\nRunning --> Idle : cancel"),
            ("Entity relationship", "erDiagram\nCUSTOMER {\nstring id\n}\nCUSTOMER ||--o{ ORDER : places"),
            ("Pie", "pie title Sales\n\"North\" : 40\n\"South\" : 25\n\"East\" : 15\n\"West\" : 10"),
            ("Timeline", "timeline\nsection Releases\n2024 : Launch\n2025 : Growth"),
            ("Gantt", "gantt\ndateFormat YYYY-MM-DD\nBuild :build, 2024-01-30, 5d\nTest :test, 2024-02-03, 4d"),
        };
        var cells = string.Concat(kinds.Select(k =>
        {
            var extra = k.Title.StartsWith("Flowchart", StringComparison.Ordinal) ? " flow" : "";
            return $"<div class=\"cell{extra}\"><h3>{k.Title}</h3>{DiagramRenderer.Render(k.Source)}</div>";
        }));
        var html = "<!doctype html><html><head><meta charset=\"utf-8\"><style>" +
            "body{margin:0;background:#F4F4FB;color:#1E1B3A;font:15px \"Source Sans 3\",sans-serif}" +
            ".sheet{display:flex;flex-wrap:wrap}.cell{width:600px;margin:8px;background:#fff;border:1px solid #E3E3F3;border-radius:12px;padding:8px}" +
            "h3{margin:0 0 6px;font-size:14px;color:#5B5BF0}.cell svg{display:block;max-width:100%;height:auto}" +
            ".cell svg text{font-family:\"Source Sans 3\",sans-serif}" +
            // Flowcharts carry no colours of their own; this is the page styling the goldens use.
            ".flow .folio-diagram-node{fill:#ECECFF;stroke:#5B5BF0;stroke-width:2}.flow .folio-diagram-edge{stroke:#5B5BF0;stroke-width:2}" +
            ".flow .folio-diagram-label{fill:#1E1B3A;stroke:none;font-weight:600}#arrow path{fill:#5B5BF0}</style></head><body><div class=\"sheet\">" + cells + "</div></body></html>";
        using var document = Document.Parse(html, TestRenderer.Options);
        using var result = HeadlessRenderer.Render(document, new RenderRequest(1272, 1700));
        new PixelBuffer(result.Width, result.Height, result.Pixels.ToArray()).SavePng(path!);
    }
}
