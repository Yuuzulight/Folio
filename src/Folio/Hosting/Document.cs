using Folio.Dom;
using Folio.Html;

namespace Folio;

/// <summary>A parsed HTML document (docs/architecture.md, public API sketch).</summary>
public sealed class Document : IDisposable
{
    private readonly List<Diagnostic> _diagnostics;

    private Document(DocumentNode node, FolioOptions options, List<Diagnostic> diagnostics)
    {
        Node = node;
        Options = options;
        _diagnostics = diagnostics;
    }

    internal DocumentNode Node { get; }

    internal FolioOptions Options { get; }

    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    /// <summary>All allowed loads finished or failed. Nothing is loaded yet, so it is always complete.</summary>
    public Task ResourcesSettled => Task.CompletedTask;

    /// <summary>https://html.spec.whatwg.org/multipage/dom.html#document.title: the first title element's text,
    /// ASCII whitespace stripped and collapsed.</summary>
    public string Title
    {
        get
        {
            for (Node? node = Node; node is not null; node = node.NextInTree(Node))
            {
                if (node is Element { LocalName: "title" } title && title.Name.Namespace == Namespaces.Html)
                    return string.Join(' ', (title.TextContent ?? "").Split(Element.AsciiWhitespace, StringSplitOptions.RemoveEmptyEntries));
            }
            return "";
        }
    }

    public static Document Parse(string html, FolioOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(html);
        options ??= new FolioOptions();
        return Build(html, options, []);
    }

    /// <summary>Parses bytes, choosing the encoding from a byte order mark or a meta element, else UTF-8.</summary>
    public static Document Parse(Stream bytes, FolioOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        options ??= new FolioOptions();
        var diagnostics = new List<Diagnostic>();

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = bytes.Read(chunk, 0, chunk.Length)) > 0)
        {
            var room = options.Limits.MaxInputSize - (int)buffer.Length;
            buffer.Write(chunk, 0, Math.Min(read, room));
            if (read > room)
            {
                diagnostics.Add(Limit($"The input is larger than {options.Limits.MaxInputSize} bytes; the rest was not read."));
                break;
            }
        }

        var data = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        var (encoding, bom, unsupported) = EncodingSniffer.Sniff(data);
        if (unsupported is not null)
            diagnostics.Add(new Diagnostic(DiagnosticCode.UnsupportedEncoding, Severity.Warning,
                $"The encoding \"{unsupported}\" is not supported; the document was read as UTF-8.", null, "encoding"));
        return Build(EncodingSniffer.Decode(data[bom..], encoding), options, diagnostics);
    }

    private static Document Build(string html, FolioOptions options, List<Diagnostic> diagnostics)
    {
        if (html.Length > options.Limits.MaxInputSize)
        {
            html = html[..options.Limits.MaxInputSize];
            diagnostics.Add(Limit($"The input is longer than {options.Limits.MaxInputSize} characters; the rest was not parsed."));
        }

        var lines = new LineMap(html);
        var limits = new ParserLimits(options.Limits.MaxNestingDepth, options.Limits.MaxNodes);
        var node = TreeBuilder.Parse(html, limits, (code, offset) =>
        {
            switch (code)
            {
                case "nesting-depth-limit":
                    diagnostics.Add(Limit($"Elements nest deeper than {limits.MaxDepth}; deeper content was attached higher.", lines.At(offset)));
                    break;
                case "node-count-limit":
                    diagnostics.Add(Limit($"The document has more than {limits.MaxNodes} nodes; parsing stopped.", lines.At(offset)));
                    break;
                default:
                    if (options.CollectDiagnostics)
                        diagnostics.Add(new Diagnostic(DiagnosticCode.ParseError, Severity.Info, $"HTML parse error: {code}", lines.At(offset), "html-parsing"));
                    break;
            }
        });
        return new Document(node, options, diagnostics);
    }

    private static Diagnostic Limit(string message, SourceLocation? at = null) =>
        new(DiagnosticCode.LimitExceeded, Severity.Warning, message, at, null);

    public void Dispose()
    {
        // Nothing is held yet; decoded images, fonts and the content process will be released here.
    }

    // Maps parser offsets (in newline-normalised input) to line and column.
    private sealed class LineMap
    {
        private readonly List<int> _starts = [0];

        public LineMap(string html)
        {
            var offset = 0;
            for (var i = 0; i < html.Length; i++, offset++)
            {
                if (html[i] == '\r' && i + 1 < html.Length && html[i + 1] == '\n')
                    i++;
                if (html[i] is '\n' or '\r')
                    _starts.Add(offset + 1);
            }
        }

        public SourceLocation At(int offset)
        {
            var line = _starts.BinarySearch(offset);
            if (line < 0)
                line = ~line - 1;
            return new SourceLocation(line + 1, offset - _starts[line] + 1);
        }
    }
}
