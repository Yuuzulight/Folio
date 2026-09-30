using System.Text;
using Folio.Dom;
using Folio.Html;
using Folio.Xml;

namespace Folio.Tests.Xml;

/// <summary>The XML parser for standalone SVG (XML 1.0, Namespaces in XML 1.0; docs/study/13-svg.md).</summary>
public class XmlParserTests
{
    private const string Svg = "xmlns=\"http://www.w3.org/2000/svg\"";

    [Theory]
    [InlineData("<svg " + Svg + "><rect width=\"5\"/></svg>", "<svg:svg xmlns=[xmlns]><svg:rect width>")]
    [InlineData("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!-- c --><svg " + Svg + "><g><circle/></g></svg>", "<svg:svg xmlns=[xmlns]><svg:g><svg:circle>")]
    [InlineData("<s:svg xmlns:s=\"http://www.w3.org/2000/svg\"><s:rect/></s:svg>", "<svg:svg xmlns:s=[xmlns]><svg:rect>")]
    [InlineData("<svg " + Svg + " xmlns:xlink=\"http://www.w3.org/1999/xlink\"><a xlink:href=\"#x\" xml:space=\"preserve\"/></svg>",
        "<svg:svg xmlns=[xmlns] xmlns:xlink=[xmlns]><svg:a xlink:href=[xlink] xml:space=[xml]>")]
    [InlineData("<svg " + Svg + "><foreignObject><div xmlns=\"http://www.w3.org/1999/xhtml\"><p/></div></foreignObject></svg>",
        "<svg:svg xmlns=[xmlns]><svg:foreignObject><html:div xmlns=[xmlns]><html:p>")]
    [InlineData("<svg><rect/></svg>", "<svg:svg><svg:rect>")]
    [InlineData("<svg " + Svg + "><x:y/></svg>", "<svg:svg xmlns=[xmlns]><:y>")]
    public void ResolvesNamespaces(string xml, string expected) => Assert.Equal(expected, Elements(XmlParser.Parse(xml)));

    [Theory]
    [InlineData("<t>&lt;&gt;&amp;&quot;&apos;</t>", "<>&\"'")]
    [InlineData("<t>&#65;&#x42;&#x1F600;&#0;&#xD800;</t>", "AB\U0001F600��")]
    [InlineData("<t>a<![CDATA[<b>&amp;</b>]]>c</t>", "a<b>&amp;</b>c")]
    [InlineData("<t>a<!-- b -->c<?pi x?>d</t>", "acd")]
    [InlineData("<t>a\r\nb\rc</t>", "a\nb\nc")]
    [InlineData("<t>&nbsp;&unknown</t>", "&nbsp;&unknown")]
    public void DecodesText(string xml, string expected) => Assert.Equal(expected, XmlParser.Parse(xml).DocumentElement!.TextContent);

    [Fact]
    public void NormalisesAttributeValues()
    {
        var root = XmlParser.Parse("<t a=\"x\ty\nz\r\nw\" b='&lt;&#65;'/>").DocumentElement!;

        Assert.Equal("x y z w", root.GetAttribute("a"));
        Assert.Equal("<A", root.GetAttribute("b"));
    }

    [Fact]
    public void TheDocumentTypeDeclarationIsSkippedAndItsEntitiesAreNeverExpanded()
    {
        // An entity expansion bomb: nothing is expanded, so it stays small and fast.
        var bomb = "<!DOCTYPE svg [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\"><!ENTITY c \"&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;\">]>"
                   + "<svg " + Svg + "><text>&c;</text></svg>";
        var errors = new List<string>();

        var document = XmlParser.Parse(bomb, error: (code, _) => errors.Add(code));

        Assert.Equal("&c;", document.DocumentElement!.TextContent);
        Assert.Equal(["undefined-entity"], errors);
    }

    [Theory]
    [InlineData("<svg><g><rect></g></svg>", "<svg:svg><svg:g><svg:rect>", "mismatched-end-tag")]
    [InlineData("<svg><g></h></g></svg>", "<svg:svg><svg:g>", "unexpected-end-tag")]
    [InlineData("<svg><rect width=5 x=\"1\"/></svg>", "<svg:svg><svg:rect x>", "unquoted-attribute-value")]
    [InlineData("<svg><rect x=\"1\" x=\"2\"/></svg>", "<svg:svg><svg:rect x>", "duplicate-attribute")]
    [InlineData("<svg><g>", "<svg:svg><svg:g>", "unclosed-elements")]
    [InlineData("<svg/><svg/>", "<svg:svg>", "second-root-element")]
    [InlineData("<svg>a < b</svg>", "<svg:svg>", "invalid-less-than")]
    [InlineData("", "", "no-root-element")]
    public void RecoversFromErrors(string xml, string expected, string error)
    {
        var errors = new List<string>();

        var document = XmlParser.Parse(xml, error: (code, _) => errors.Add(code));

        Assert.Equal(expected, Elements(document));
        Assert.Contains(error, errors);
    }

    [Fact]
    public void DeepNestingStopsAtTheDepthLimit()
    {
        var errors = new List<string>();
        var xml = "<svg>" + string.Concat(Enumerable.Repeat("<g>", 5000)) + "<rect/>" + "</svg>";

        var document = XmlParser.Parse(xml, new ParserLimits(MaxDepth: 50), (code, _) => errors.Add(code));

        var depth = 0;
        for (Node? node = document; node is not null; node = node.NextInTree(document))
        {
            var d = 0;
            for (var n = node; n.Parent is not null; n = n.Parent)
                d++;
            depth = Math.Max(depth, d);
        }
        Assert.True(depth <= 51, $"depth {depth}");
        Assert.Contains("nesting-depth-limit", errors);
    }

    [Fact]
    public void ParsingStopsAtTheNodeLimit()
    {
        var errors = new List<string>();

        var document = XmlParser.Parse("<svg>" + string.Concat(Enumerable.Repeat("<rect/>", 1000)) + "</svg>", new ParserLimits(MaxNodes: 100),
            (code, _) => errors.Add(code));

        Assert.True(document.DocumentElement!.Children.Count() <= 100);
        Assert.Contains("node-count-limit", errors);
    }

    [Theory]
    [InlineData("<svg " + Svg + "></svg>", true)]
    [InlineData("﻿  <?xml version=\"1.0\"?><svg></svg>", true)]
    [InlineData("<!DOCTYPE svg PUBLIC \"-//W3C//DTD SVG 1.1//EN\" \"x.dtd\"><svg>", true)]
    [InlineData("<!-- icon --><svg\n  " + Svg + "\n  viewBox=\"0 0 1 1\"/>", true)]
    [InlineData("<svg width=10></svg>", false)]
    [InlineData("<svgx " + Svg + ">", false)]
    [InlineData("<!DOCTYPE html><svg " + Svg + "></svg>", false)]
    [InlineData("<html><svg " + Svg + "></svg></html>", false)]
    [InlineData("", false)]
    public void RecognisesStandaloneSvg(string text, bool expected) => Assert.Equal(expected, XmlParser.IsSvgDocument(text));

    // Elements in tree order as "<prefix:name attributes>", the prefix from the namespace (svg, html, or empty for
    // none), attributes by name with "=[namespace]" when they have one.
    private static string Elements(DocumentNode document)
    {
        var text = new StringBuilder();
        for (Node? node = document; node is not null; node = node.NextInTree(document))
        {
            if (node is not ElementNode element)
                continue;
            var ns = element.Name.Namespace == Namespaces.Svg ? "svg" : element.Name.Namespace == Namespaces.Html ? "html" : "";
            text.Append('<').Append(ns).Append(':').Append(element.LocalName);
            foreach (var attribute in element.Attributes)
            {
                text.Append(' ').Append(document.TextOf(attribute.Name));
                if (!attribute.Namespace.IsNone)
                {
                    var uri = document.TextOf(attribute.Namespace);
                    text.Append("=[").Append(uri == Namespaces.XLinkUri ? "xlink" : uri == Namespaces.XmlUri ? "xml" : uri == Namespaces.XmlnsUri ? "xmlns" : uri).Append(']');
                }
            }
            text.Append('>');
        }
        return text.ToString();
    }
}
