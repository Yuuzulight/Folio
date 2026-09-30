using System.Globalization;
using System.Text;
using Folio.Dom;
using Folio.Html;

namespace Folio.Xml;

/// <summary>
/// Folio's small XML parser for standalone SVG documents (docs/study/13-svg.md): elements, attributes and text with
/// namespaces (https://www.w3.org/TR/xml-names/), character references and the five predefined entities. CDATA
/// sections are text; comments, processing instructions and the document type declaration (with any internal subset)
/// are skipped, so no custom entity is ever expanded and nothing external is loaded. Well-formedness errors are
/// reported and recovered from rather than ending the parse, so a slightly broken file still shows what it can.
/// </summary>
internal sealed class XmlParser
{
    private readonly string _text;
    private readonly ParserLimits _limits;
    private readonly Action<string, int>? _error;
    private readonly DocumentNode _document = new();
    private readonly List<(ElementNode Element, string Name, Dictionary<string, string>? Scope)> _open = [];
    private readonly StringBuilder _pending = new();
    private int _pos;
    private int _nodes;
    private bool _depthReported;

    private XmlParser(string text, ParserLimits limits, Action<string, int>? error)
    {
        _text = text;
        _limits = limits;
        _error = error;
    }

    /// <summary>Parses a document. Errors are reported by code (e.g. "mismatched-end-tag") with their offset.</summary>
    public static DocumentNode Parse(string text, ParserLimits? limits = null, Action<string, int>? error = null)
    {
        var parser = new XmlParser(text, limits ?? ParserLimits.Default, error);
        parser.Run();
        return parser._document;
    }

    /// <summary>
    /// Whether text is a standalone SVG document rather than HTML: after an optional byte order mark, XML declaration,
    /// comments and an svg document type declaration, the root element is <c>svg</c>, and either the file declares
    /// itself XML or the root element is in the SVG namespace.
    /// </summary>
    public static bool IsSvgDocument(string text)
    {
        var pos = text.Length > 0 && text[0] == '﻿' ? 1 : 0;
        var xml = false;
        while (true)
        {
            while (pos < text.Length && IsSpace(text[pos]))
                pos++;
            if (At(text, pos, "<?"))
            {
                xml = true;
                pos = Skip(text, pos, "?>");
            }
            else if (At(text, pos, "<!--"))
            {
                pos = Skip(text, pos + 4, "-->");
            }
            else if (At(text, pos, "<!DOCTYPE"))
            {
                // An SVG document type says XML; any other (html) says it is not an SVG document.
                if (!At(text, pos + 9, " svg"))
                    return false;
                xml = true;
                pos = SkipDoctype(text, pos);
            }
            else
            {
                break;
            }
        }
        if (!At(text, pos, "<svg") || pos + 4 >= text.Length || !(IsSpace(text[pos + 4]) || text[pos + 4] is '>' or '/'))
            return false;
        var end = text.IndexOf('>', pos);
        return xml || end > 0 && text.AsSpan(pos, end - pos).Contains(Namespaces.SvgUri, StringComparison.Ordinal);
    }

    private void Run()
    {
        if (_text.Length > 0 && _text[0] == '﻿')
            _pos = 1;
        while (_pos < _text.Length && _nodes <= _limits.MaxNodes)
        {
            if (_text[_pos] != '<')
            {
                Text();
                continue;
            }
            if (At(_text, _pos, "<?"))
            {
                _pos = Skip(_text, _pos, "?>");
            }
            else if (At(_text, _pos, "<!--"))
            {
                _pos = Skip(_text, _pos + 4, "-->");
            }
            else if (At(_text, _pos, "<![CDATA["))
            {
                var end = _text.IndexOf("]]>", _pos + 9, StringComparison.Ordinal);
                _pending.Append(_text, _pos + 9, (end < 0 ? _text.Length : end) - _pos - 9);
                _pos = end < 0 ? _text.Length : end + 3;
            }
            else if (At(_text, _pos, "<!DOCTYPE"))
            {
                _pos = SkipDoctype(_text, _pos);
            }
            else if (At(_text, _pos, "</"))
            {
                EndTag();
            }
            else if (_pos + 1 < _text.Length && IsNameStart(_text[_pos + 1]))
            {
                StartTag();
            }
            else
            {
                Error("invalid-less-than");
                _pending.Append('<');
                _pos++;
            }
        }
        if (_nodes > _limits.MaxNodes)
            Error("node-count-limit");
        Flush();
        if (_open.Count > 0)
            Error("unclosed-elements");
        if (_document.DocumentElement is null)
            Error("no-root-element");
    }

    private void Error(string code) => _error?.Invoke(code, _pos);

    // Character data up to the next markup, with references decoded. Text outside the root element is dropped.
    private void Text()
    {
        var end = _text.IndexOf('<', _pos);
        if (end < 0)
            end = _text.Length;
        Decode(_text.AsSpan(_pos, end - _pos), _pending, attribute: false);
        _pos = end;
    }

    private void Flush()
    {
        if (_pending.Length == 0)
            return;
        if (_open.Count > 0)
        {
            _open[^1].Element.AppendChild(_document.CreateText(_pending.ToString()));
            _nodes++;
        }
        else if (_pending.ToString().Any(c => !IsSpace(c)))
        {
            Error("text-outside-root");
        }
        _pending.Clear();
    }

    private void StartTag()
    {
        Flush();
        var tagStart = _pos;
        _pos++;
        var name = Name();
        var attributes = new List<(string Name, string Value)>();
        var selfClosing = false;
        while (true)
        {
            SkipSpace();
            if (_pos >= _text.Length)
            {
                Error("eof-in-tag");
                return;
            }
            if (At(_text, _pos, "/>"))
            {
                _pos += 2;
                selfClosing = true;
                break;
            }
            if (_text[_pos] == '>')
            {
                _pos++;
                break;
            }
            if (!IsNameStart(_text[_pos]))
            {
                Error("invalid-attribute");
                _pos++;
                continue;
            }
            var attributeName = Name();
            SkipSpace();
            if (_pos >= _text.Length || _text[_pos] != '=')
            {
                Error("attribute-without-value");
                continue;
            }
            _pos++;
            SkipSpace();
            if (_pos >= _text.Length || _text[_pos] is not ('"' or '\''))
            {
                Error("unquoted-attribute-value");
                continue;
            }
            var quote = _text[_pos++];
            var close = _text.IndexOf(quote, _pos);
            if (close < 0)
                close = _text.Length;
            var value = new StringBuilder();
            Decode(_text.AsSpan(_pos, close - _pos), value, attribute: true);
            _pos = Math.Min(_text.Length, close + 1);
            if (attributes.Any(a => a.Name == attributeName))
                Error("duplicate-attribute");
            else
                attributes.Add((attributeName, value.ToString()));
        }

        // Namespace declarations first, then the element's and attributes' prefixes resolve through them.
        Dictionary<string, string>? scope = null;
        foreach (var (attributeName, value) in attributes)
        {
            if (attributeName == "xmlns")
                (scope ??= [])[""] = value;
            else if (attributeName.StartsWith("xmlns:", StringComparison.Ordinal))
                (scope ??= [])[attributeName[6..]] = value;
        }
        var parent = _open.Count > 0 ? _open[^1].Element : null;
        var (prefix, localName) = Split(name);
        var ns = Resolve(prefix, scope);
        // A root svg element with no namespace declared at all is taken as SVG, the one kind of document this parser is for.
        if (ns is null && prefix.Length == 0 && parent is null && localName == "svg")
        {
            Error("svg-without-namespace");
            ns = (scope ??= [])[""] = Namespaces.SvgUri;
        }
        var element = _document.CreateElement(ns is null ? Atom.None : _document.Intern(ns), localName);
        var parsed = new Dom.Attribute[attributes.Count];
        for (var i = 0; i < attributes.Count; i++)
        {
            var (attributeName, value) = attributes[i];
            var (attributePrefix, _) = Split(attributeName);
            var attributeNs = attributeName == "xmlns" || attributePrefix == "xmlns" ? Namespaces.XmlnsUri
                : attributePrefix.Length == 0 ? null
                : Resolve(attributePrefix, scope);
            parsed[i] = new Dom.Attribute(_document.Intern(attributeName), value, attributeNs is null ? Atom.None : _document.Intern(attributeNs));
        }
        element.SetParsedAttributes(parsed);

        if (parent is null)
        {
            if (_document.DocumentElement is not null)
            {
                // A second root element ends the document.
                Error("second-root-element");
                _pos = _text.Length;
                return;
            }
            _document.AppendChild(element);
        }
        else
        {
            parent.AppendChild(element);
        }
        _nodes++;
        if (selfClosing)
            return;
        if (_open.Count >= _limits.MaxDepth)
        {
            // Deeper content is attached here rather than nested further.
            if (!_depthReported)
            {
                _depthReported = true;
                _error?.Invoke("nesting-depth-limit", tagStart);
            }
            return;
        }
        _open.Add((element, name, scope));
    }

    private void EndTag()
    {
        Flush();
        _pos += 2;
        var name = Name();
        SkipSpace();
        if (_pos < _text.Length && _text[_pos] == '>')
            _pos++;
        else
            Error("invalid-end-tag");
        var index = _open.FindLastIndex(o => o.Name == name);
        if (index < 0)
        {
            // Past the depth limit, or not open at all: nothing to close.
            if (!_depthReported)
                Error("unexpected-end-tag");
            return;
        }
        if (index != _open.Count - 1)
            Error("mismatched-end-tag");
        _open.RemoveRange(index, _open.Count - index);
    }

    private string? Resolve(string prefix, Dictionary<string, string>? own)
    {
        if (prefix == "xml")
            return Namespaces.XmlUri;
        if (own is not null && own.TryGetValue(prefix, out var uri))
            return uri.Length == 0 ? null : uri;
        for (var i = _open.Count - 1; i >= 0; i--)
        {
            if (_open[i].Scope is { } scope && scope.TryGetValue(prefix, out uri))
                return uri.Length == 0 ? null : uri;
        }
        if (prefix.Length > 0)
            Error("undeclared-prefix");
        return null;
    }

    private static (string Prefix, string LocalName) Split(string name)
    {
        var colon = name.IndexOf(':');
        return colon > 0 && colon < name.Length - 1 ? (name[..colon], name[(colon + 1)..]) : ("", name);
    }

    private string Name()
    {
        var start = _pos;
        while (_pos < _text.Length && IsNameChar(_text[_pos]))
            _pos++;
        return _text[start.._pos];
    }

    private void SkipSpace()
    {
        while (_pos < _text.Length && IsSpace(_text[_pos]))
            _pos++;
    }

    // Character references and the predefined entities (https://www.w3.org/TR/xml/#sec-references); in attribute
    // values, white space characters become spaces (https://www.w3.org/TR/xml/#AVNormalize). Line breaks are
    // normalised to line feeds (https://www.w3.org/TR/xml/#sec-line-ends).
    private void Decode(ReadOnlySpan<char> text, StringBuilder output, bool attribute)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                output.Append(attribute ? ' ' : '\n');
                if (i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                continue;
            }
            if (c != '&')
            {
                output.Append(attribute && c is '\t' or '\n' ? ' ' : c);
                continue;
            }
            // References are short; a lone ampersand does not scan the rest of the text.
            var semicolon = text.Slice(i, Math.Min(text.Length - i, 34)).IndexOf(';');
            var reference = semicolon > 1 ? text.Slice(i + 1, semicolon - 1) : default;
            string? replacement = reference switch
            {
                "lt" => "<",
                "gt" => ">",
                "amp" => "&",
                "quot" => "\"",
                "apos" => "'",
                _ when reference.Length > 1 && reference[0] == '#' => CharacterReference(reference[1..]),
                _ => null,
            };
            if (replacement is null)
            {
                Error("undefined-entity");
                output.Append('&');
                continue;
            }
            output.Append(replacement);
            i += semicolon;
        }
    }

    private static string CharacterReference(ReadOnlySpan<char> digits)
    {
        var hex = digits.Length > 1 && digits[0] == 'x';
        var ok = hex
            ? int.TryParse(digits[1..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code)
            : int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out code);
        return ok && code is > 0 and <= 0x10FFFF && code is not (>= 0xD800 and <= 0xDFFF) ? char.ConvertFromUtf32(code) : "�";
    }

    private static bool At(string text, int pos, string what) => string.CompareOrdinal(text, pos, what, 0, what.Length) == 0;

    // The position after the next occurrence of the terminator, or the end.
    private static int Skip(string text, int pos, string terminator)
    {
        var end = text.IndexOf(terminator, pos, StringComparison.Ordinal);
        return end < 0 ? text.Length : end + terminator.Length;
    }

    // A document type declaration, stepping over an internal subset in brackets and quoted strings.
    private static int SkipDoctype(string text, int pos)
    {
        var (bracket, quote) = (false, '\0');
        for (pos += 9; pos < text.Length; pos++)
        {
            var c = text[pos];
            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '[')
            {
                bracket = true;
            }
            else if (c == ']')
            {
                bracket = false;
            }
            else if (c == '>' && !bracket)
            {
                return pos + 1;
            }
        }
        return pos;
    }

    private static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\r';

    // https://www.w3.org/TR/xml/#NT-NameStartChar, with every non-ASCII character allowed.
    private static bool IsNameStart(char c) => char.IsAsciiLetter(c) || c is '_' or ':' || c > 0x7F;

    private static bool IsNameChar(char c) => IsNameStart(c) || char.IsAsciiDigit(c) || c is '-' or '.';
}
