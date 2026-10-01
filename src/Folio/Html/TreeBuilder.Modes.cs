using Folio.Dom;

namespace Folio.Html;

// The insertion modes for tokens other than characters (those are in TreeBuilder.cs).
internal sealed partial class TreeBuilder
{
    private void Process(Mode mode, Token t)
    {
        switch (mode)
        {
            case Mode.Initial: Initial(t); break;
            case Mode.BeforeHtml: BeforeHtml(t); break;
            case Mode.BeforeHead: BeforeHead(t); break;
            case Mode.InHead: InHead(t); break;
            case Mode.InHeadNoscript: InHeadNoscript(t); break;
            case Mode.AfterHead: AfterHead(t); break;
            case Mode.InBody: InBody(t); break;
            case Mode.Text: Text(t); break;
            case Mode.InTable: InTable(t); break;
            case Mode.InTableText: FlushPendingTableText(); Process(_mode, t); break;
            case Mode.InCaption: InCaption(t); break;
            case Mode.InColumnGroup: InColumnGroup(t); break;
            case Mode.InTableBody: InTableBody(t); break;
            case Mode.InRow: InRow(t); break;
            case Mode.InCell: InCell(t); break;
            case Mode.InTemplate: InTemplate(t); break;
            case Mode.AfterBody: AfterBody(t); break;
            case Mode.AfterAfterBody: AfterAfterBody(t); break;
        }
    }

    private static bool IsStart(Token t, params ReadOnlySpan<string> names) => t.Kind == TokenKind.StartTag && names.Contains(t.Name);

    private static bool IsEnd(Token t, params ReadOnlySpan<string> names) => t.Kind == TokenKind.EndTag && names.Contains(t.Name);

    private void Reprocess(Mode mode, Token t)
    {
        _mode = mode;
        Process(mode, t);
    }

    // https://html.spec.whatwg.org/multipage/parsing.html#the-initial-insertion-mode
    private void Initial(Token t)
    {
        switch (t.Kind)
        {
            case TokenKind.Comment:
                InsertComment(t.Data, _document);
                return;
            case TokenKind.Doctype:
                var name = t.DoctypeName;
                if (name != "html" || t.PublicId is not null || (t.SystemId is not null && t.SystemId != "about:legacy-compat"))
                    Error();
                if (Count())
                    _document.AppendChild(_document.CreateDocumentType(name ?? "", t.PublicId ?? "", t.SystemId ?? ""));
                _document.Mode = DocumentModeFor(t);
                _mode = Mode.BeforeHtml;
                return;
            default:
                AnythingElse(Mode.Initial);
                Process(_mode, t);
                return;
        }
    }

    // https://html.spec.whatwg.org/multipage/parsing.html#the-initial-insertion-mode (quirks and limited quirks).
    // Identifiers compare ASCII case-insensitively; an empty system identifier counts as missing here.
    private static DocumentMode DocumentModeFor(Token t)
    {
        var publicId = t.PublicId ?? "";
        var noSystemId = string.IsNullOrEmpty(t.SystemId);
        if (t.ForceQuirks || t.DoctypeName != "html"
            || publicId.Equals("-//W3O//DTD W3 HTML Strict 3.0//EN//", StringComparison.OrdinalIgnoreCase)
            || publicId.Equals("-/W3C/DTD HTML 4.0 Transitional/EN", StringComparison.OrdinalIgnoreCase)
            || publicId.Equals("HTML", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t.SystemId, "http://www.ibm.com/data/dtd/v11/ibmxhtml1-transitional.dtd", StringComparison.OrdinalIgnoreCase)
            || QuirksPublicIdPrefixes.Any(prefix => publicId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            || (noSystemId && StartsWithHtml401FramesetOrTransitional(publicId)))
            return DocumentMode.Quirks;

        if (publicId.StartsWith("-//W3C//DTD XHTML 1.0 Frameset//", StringComparison.OrdinalIgnoreCase)
            || publicId.StartsWith("-//W3C//DTD XHTML 1.0 Transitional//", StringComparison.OrdinalIgnoreCase)
            || (!noSystemId && StartsWithHtml401FramesetOrTransitional(publicId)))
            return DocumentMode.LimitedQuirks;

        return DocumentMode.NoQuirks;
    }

    private static bool StartsWithHtml401FramesetOrTransitional(string publicId) =>
        publicId.StartsWith("-//W3C//DTD HTML 4.01 Frameset//", StringComparison.OrdinalIgnoreCase)
        || publicId.StartsWith("-//W3C//DTD HTML 4.01 Transitional//", StringComparison.OrdinalIgnoreCase);

    // The public identifier prefixes that select quirks mode, as the standard lists them.
    private static readonly string[] QuirksPublicIdPrefixes =
    [
        "+//Silmaril//dtd html Pro v0r11 19970101//",
        "-//AS//DTD HTML 3.0 asWedit + extensions//",
        "-//AdvaSoft Ltd//DTD HTML 3.0 asWedit + extensions//",
        "-//IETF//DTD HTML 2.0 Level 1//",
        "-//IETF//DTD HTML 2.0 Level 2//",
        "-//IETF//DTD HTML 2.0 Strict Level 1//",
        "-//IETF//DTD HTML 2.0 Strict Level 2//",
        "-//IETF//DTD HTML 2.0 Strict//",
        "-//IETF//DTD HTML 2.0//",
        "-//IETF//DTD HTML 2.1E//",
        "-//IETF//DTD HTML 3.0//",
        "-//IETF//DTD HTML 3.2 Final//",
        "-//IETF//DTD HTML 3.2//",
        "-//IETF//DTD HTML 3//",
        "-//IETF//DTD HTML Level 0//",
        "-//IETF//DTD HTML Level 1//",
        "-//IETF//DTD HTML Level 2//",
        "-//IETF//DTD HTML Level 3//",
        "-//IETF//DTD HTML Strict Level 0//",
        "-//IETF//DTD HTML Strict Level 1//",
        "-//IETF//DTD HTML Strict Level 2//",
        "-//IETF//DTD HTML Strict Level 3//",
        "-//IETF//DTD HTML Strict//",
        "-//IETF//DTD HTML//",
        "-//Metrius//DTD Metrius Presentational//",
        "-//Microsoft//DTD Internet Explorer 2.0 HTML Strict//",
        "-//Microsoft//DTD Internet Explorer 2.0 HTML//",
        "-//Microsoft//DTD Internet Explorer 2.0 Tables//",
        "-//Microsoft//DTD Internet Explorer 3.0 HTML Strict//",
        "-//Microsoft//DTD Internet Explorer 3.0 HTML//",
        "-//Microsoft//DTD Internet Explorer 3.0 Tables//",
        "-//Netscape Comm. Corp.//DTD HTML//",
        "-//Netscape Comm. Corp.//DTD Strict HTML//",
        "-//O'Reilly and Associates//DTD HTML 2.0//",
        "-//O'Reilly and Associates//DTD HTML Extended 1.0//",
        "-//O'Reilly and Associates//DTD HTML Extended Relaxed 1.0//",
        "-//SQ//DTD HTML 2.0 HoTMetaL + extensions//",
        "-//SoftQuad Software//DTD HoTMetaL PRO 6.0::19990601::extensions to HTML 4.0//",
        "-//SoftQuad//DTD HoTMetaL PRO 4.0::19971010::extensions to HTML 4.0//",
        "-//Spyglass//DTD HTML 2.0 Extended//",
        "-//Sun Microsystems Corp.//DTD HotJava HTML//",
        "-//Sun Microsystems Corp.//DTD HotJava Strict HTML//",
        "-//W3C//DTD HTML 3 1995-03-24//",
        "-//W3C//DTD HTML 3.2 Draft//",
        "-//W3C//DTD HTML 3.2 Final//",
        "-//W3C//DTD HTML 3.2//",
        "-//W3C//DTD HTML 3.2S Draft//",
        "-//W3C//DTD HTML 4.0 Frameset//",
        "-//W3C//DTD HTML 4.0 Transitional//",
        "-//W3C//DTD HTML Experimental 19960712//",
        "-//W3C//DTD HTML Experimental 970421//",
        "-//W3C//DTD W3 HTML//",
        "-//W3O//DTD W3 HTML 3.0//",
        "-//WebTechs//DTD Mozilla HTML 2.0//",
        "-//WebTechs//DTD Mozilla HTML//",
    ];

    // https://html.spec.whatwg.org/multipage/parsing.html#the-before-html-insertion-mode
    private void BeforeHtml(Token t)
    {
        if (t.Kind == TokenKind.Doctype)
        {
            Error();
        }
        else if (t.Kind == TokenKind.Comment)
        {
            InsertComment(t.Data, _document);
        }
        else if (IsStart(t, "html"))
        {
            InsertHtmlElementIntoDocument(t);
            _mode = Mode.BeforeHead;
        }
        else if (t.Kind == TokenKind.EndTag && !IsEnd(t, "head", "body", "html", "br"))
        {
            Error();
        }
        else
        {
            AnythingElse(Mode.BeforeHtml);
            Process(_mode, t);
        }
    }

    // https://html.spec.whatwg.org/multipage/parsing.html#the-before-head-insertion-mode
    private void BeforeHead(Token t)
    {
        if (t.Kind == TokenKind.Comment)
        {
            InsertComment(t.Data);
        }
        else if (t.Kind == TokenKind.Doctype)
        {
            Error();
        }
        else if (IsStart(t, "html"))
        {
            InBody(t);
        }
        else if (IsStart(t, "head"))
        {
            _head = InsertHtmlElement(t);
            _mode = Mode.InHead;
        }
        else if (t.Kind == TokenKind.EndTag && !IsEnd(t, "head", "body", "html", "br"))
        {
            Error();
        }
        else
        {
            AnythingElse(Mode.BeforeHead);
            Process(_mode, t);
        }
    }

    // https://html.spec.whatwg.org/multipage/parsing.html#parsing-main-inhead
    private void InHead(Token t)
    {
        if (t.Kind == TokenKind.Comment)
        {
            InsertComment(t.Data);
        }
        else if (t.Kind == TokenKind.Doctype)
        {
            Error();
        }
        else if (IsStart(t, "html"))
        {
            InBody(t);
        }
        else if (IsStart(t, "base", "basefont", "bgsound", "link", "meta"))
        {
            InsertVoid(t);
        }
        else if (IsStart(t, "title"))
        {
            GenericText(t, TokenizerState.RcData);
        }
        else if (IsStart(t, "noframes", "style"))
        {
            GenericText(t, TokenizerState.RawText);
        }
        else if (IsStart(t, "noscript"))
        {
            // The scripting flag is off (docs/study/01-html-parsing.md).
            InsertHtmlElement(t);
            _mode = Mode.InHeadNoscript;
        }
        else if (IsStart(t, "script"))
        {
            GenericText(t, TokenizerState.ScriptData);
        }
        else if (IsEnd(t, "head"))
        {
            Pop();
            _mode = Mode.AfterHead;
        }
        else if (IsStart(t, "template"))
        {
            InsertHtmlElement(t);
            _formatting.Add(null);
            _mode = Mode.InTemplate;
            _templateModes.Add(Mode.InTemplate);
        }
        else if (IsEnd(t, "template"))
        {
            if (!OpenContains("template"))
            {
                Error();
                return;
            }
            GenerateAllImpliedEndTagsThoroughly();
            if (!CurrentIsHtml("template"))
                Error();
            PopUntil("template");
            ClearFormattingToLastMarker();
            _templateModes.RemoveAt(_templateModes.Count - 1);
            ResetInsertionMode();
        }
        else if (IsStart(t, "head") || (t.Kind == TokenKind.EndTag && !IsEnd(t, "body", "html", "br")))
        {
            Error();
        }
        else
        {
            AnythingElse(Mode.InHead);
            Process(_mode, t);
        }
    }

    // https://html.spec.whatwg.org/multipage/parsing.html#parsing-main-inheadnoscript
    private void InHeadNoscript(Token t)
    {
        if (t.Kind == TokenKind.Doctype)
        {
            Error();
        }
        else if (IsStart(t, "html"))
        {
            InBody(t);
        }
        else if (IsEnd(t, "noscript"))
        {
            Pop();
            _mode = Mode.InHead;
        }
        else if (t.Kind == TokenKind.Comment || IsStart(t, "basefont", "bgsound", "link", "meta", "noframes", "style"))
        {
            InHead(t);
        }
        else if (IsStart(t, "head", "noscript") || (t.Kind == TokenKind.EndTag && !IsEnd(t, "br")))
        {
            Error();
        }
        else
        {
            AnythingElse(Mode.InHeadNoscript);
            Process(_mode, t);
        }
    }

    // https://html.spec.whatwg.org/multipage/parsing.html#the-after-head-insertion-mode
    private void AfterHead(Token t)
    {
        if (t.Kind == TokenKind.Comment)
        {
            InsertComment(t.Data);
        }
        else if (t.Kind == TokenKind.Doctype)
        {
            Error();
        }
        else if (IsStart(t, "html"))
        {
            InBody(t);
        }
        else if (IsStart(t, "body"))
        {
            InsertHtmlElement(t);
            _mode = Mode.InBody;
        }
        else if (IsStart(t, "base", "basefont", "bgsound", "link", "meta", "noframes", "script", "style", "template", "title"))
        {
            Error();
            _open.Add(_head!);
            InHead(t);
            _open.Remove(_head!);
        }
        else if (IsEnd(t, "template"))
        {
            InHead(t);
        }
        else if (IsStart(t, "head") || (t.Kind == TokenKind.EndTag && !IsEnd(t, "body", "html", "br")))
        {
            Error();
        }
        else
        {
            AnythingElse(Mode.AfterHead);
            Process(_mode, t);
        }
    }

    // https://html.spec.whatwg.org/multipage/parsing.html#generic-raw-text-element-parsing-algorithm
    private void GenericText(Token t, TokenizerState state)
    {
        InsertHtmlElement(t);
        _tokenizer.State = state;
        _originalMode = _mode;
        _mode = Mode.Text;
    }

    // https://html.spec.whatwg.org/multipage/parsing.html#parsing-main-incdata
    private void Text(Token t)
    {
        if (t.Kind == TokenKind.EndOfFile)
        {
            Error();
            Pop();
            _mode = _originalMode;
            Process(_mode, t);
        }
        else if (t.Kind == TokenKind.EndTag)
        {
            Pop();
            _mode = _originalMode;
        }
    }

    // https://html.spec.whatwg.org/multipage/parsing.html#parsing-main-inbody
    private void InBody(Token t)
    {
        switch (t.Kind)
        {
            case TokenKind.Comment:
                InsertComment(t.Data);
                return;
            case TokenKind.Doctype:
                Error();
                return;
            case TokenKind.EndOfFile:
                if (_templateModes.Count > 0)
                    InTemplate(t);
                return;
            case TokenKind.StartTag:
                InBodyStartTag(t);
                return;
            case TokenKind.EndTag:
                InBodyEndTag(t);
                return;
        }
    }

    private void InBodyStartTag(Token t)
    {
        switch (t.Name)
        {
            case "html":
                Error();
                if (!OpenContains("template"))
                    AddMissingAttributes(_open[0], t);
                return;

            case "base" or "basefont" or "bgsound" or "link" or "meta" or "noframes" or "script" or "style" or "template" or "title":
                InHead(t);
                return;

            case "body":
                Error();
                if (_open.Count == 1 || !IsHtml(_open[1], "body") || OpenContains("template"))
                    return;
                AddMissingAttributes(_open[1], t);
                return;

            case "address" or "article" or "aside" or "blockquote" or "center" or "details" or "dialog" or "dir" or "div"
                or "dl" or "fieldset" or "figcaption" or "figure" or "footer" or "header" or "hgroup" or "main" or "menu"
                or "nav" or "ol" or "p" or "search" or "section" or "summary" or "ul":
                ClosePIfInButtonScope();
                InsertHtmlElement(t);
                return;

            case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                ClosePIfInButtonScope();
                if (CurrentIsHtml("h1", "h2", "h3", "h4", "h5", "h6"))
                {
                    Error();
                    Pop();
                }
                InsertHtmlElement(t);
                return;

            case "pre" or "listing":
                ClosePIfInButtonScope();
                InsertHtmlElement(t);
                _skipNextNewline = true;
                return;

            case "form":
                var inTemplate = OpenContains("template");
                if (_form is not null && !inTemplate)
                {
                    Error();
                    return;
                }
                ClosePIfInButtonScope();
                var form = InsertHtmlElement(t);
                if (!inTemplate)
                    _form = form;
                return;

            case "li":
                CloseListItem(t, ["li"]);
                return;

            case "dd" or "dt":
                CloseListItem(t, ["dd", "dt"]);
                return;

            case "plaintext":
                ClosePIfInButtonScope();
                InsertHtmlElement(t);
                _tokenizer.State = TokenizerState.PlainText;
                return;

            case "button":
                if (InScope("button"))
                {
                    Error();
                    GenerateImpliedEndTags();
                    PopUntil("button");
                }
                ReconstructActiveFormattingElements();
                InsertHtmlElement(t);
                return;

            case "a":
                var active = LastFormattingAfterMarker("a");
                if (active >= 0)
                {
                    Error();
                    var element = _formatting[active]!;
                    if (!AdoptionAgency("a"))
                        AnyOtherEndTag("a");
                    _formatting.Remove(element);
                    _open.Remove(element);
                }
                ReconstructActiveFormattingElements();
                PushFormatting(InsertHtmlElement(t));
                return;

            case "b" or "big" or "code" or "em" or "font" or "i" or "s" or "small" or "strike" or "strong" or "tt" or "u":
                ReconstructActiveFormattingElements();
                PushFormatting(InsertHtmlElement(t));
                return;

            case "nobr":
                ReconstructActiveFormattingElements();
                if (InScope("nobr"))
                {
                    Error();
                    if (!AdoptionAgency("nobr"))
                        AnyOtherEndTag("nobr");
                    ReconstructActiveFormattingElements();
                }
                PushFormatting(InsertHtmlElement(t));
                return;

            case "applet" or "marquee" or "object":
                ReconstructActiveFormattingElements();
                InsertHtmlElement(t);
                _formatting.Add(null);
                return;

            case "table":
                if (_document.Mode != DocumentMode.Quirks)
                    ClosePIfInButtonScope();
                InsertHtmlElement(t);
                _mode = Mode.InTable;
                return;

            case "area" or "br" or "embed" or "img" or "keygen" or "wbr":
                ReconstructActiveFormattingElements();
                InsertVoid(t);
                return;

            case "input":
                if (InScope("select"))
                {
                    Error();
                    PopUntil("select");
                }
                ReconstructActiveFormattingElements();
                InsertVoid(t);
                return;

            case "param" or "source" or "track":
                InsertVoid(t);
                return;

            case "hr":
                ClosePIfInButtonScope();
                if (InScope("select"))
                {
                    GenerateImpliedEndTags();
                    if (InScope("option") || InScope("optgroup"))
                        Error();
                }
                InsertVoid(t);
                return;

            case "image":
                Error();
                t.Name = "img";
                InBodyStartTag(t);
                return;

            case "textarea":
                InsertHtmlElement(t);
                _skipNextNewline = true;
                _tokenizer.State = TokenizerState.RcData;
                _originalMode = _mode;
                _mode = Mode.Text;
                return;

            case "xmp":
                ClosePIfInButtonScope();
                ReconstructActiveFormattingElements();
                GenericText(t, TokenizerState.RawText);
                return;

            case "iframe":
                GenericText(t, TokenizerState.RawText);
                return;

            case "noembed":
                GenericText(t, TokenizerState.RawText);
                return;

            case "select":
                if (InScope("select"))
                {
                    Error();
                    PopUntil("select");
                    return;
                }
                ReconstructActiveFormattingElements();
                InsertHtmlElement(t);
                return;

            case "option":
                if (InScope("select"))
                {
                    GenerateImpliedEndTags("optgroup");
                    if (InScope("option"))
                        Error();
                }
                else if (CurrentIsHtml("option"))
                {
                    Pop();
                }
                ReconstructActiveFormattingElements();
                InsertHtmlElement(t);
                return;

            case "optgroup":
                if (InScope("select"))
                {
                    GenerateImpliedEndTags();
                    if (InScope("option") || InScope("optgroup"))
                        Error();
                }
                else if (CurrentIsHtml("option"))
                {
                    Pop();
                }
                ReconstructActiveFormattingElements();
                InsertHtmlElement(t);
                return;

            case "rb" or "rtc":
                if (InScope("ruby"))
                {
                    GenerateImpliedEndTags();
                    if (!CurrentIsHtml("ruby"))
                        Error();
                }
                InsertHtmlElement(t);
                return;

            case "rp" or "rt":
                if (InScope("ruby"))
                {
                    GenerateImpliedEndTags("rtc");
                    if (!CurrentIsHtml("rtc", "ruby"))
                        Error();
                }
                InsertHtmlElement(t);
                return;

            case "math":
                ReconstructActiveFormattingElements();
                InsertForeign(t, Namespaces.MathML);
                return;

            case "svg":
                ReconstructActiveFormattingElements();
                InsertForeign(t, Namespaces.Svg);
                return;

            case "caption" or "col" or "colgroup" or "frame" or "head" or "tbody" or "td" or "tfoot" or "th" or "thead" or "tr":
                Error();
                return;

            default:
                // Includes frameset, which is an ordinary element here (docs/study/01-html-parsing.md).
                ReconstructActiveFormattingElements();
                InsertHtmlElement(t);
                return;
        }
    }

    // The li and dd/dt start tag steps.
    private void CloseListItem(Token t, string[] names)
    {
        for (var i = _open.Count - 1; i >= 0; i--)
        {
            var node = _open[i];
            if (IsHtml(node, names))
            {
                var name = node.LocalName;
                GenerateImpliedEndTags(name);
                if (!CurrentIsHtml(name))
                    Error();
                PopUntil(name);
                break;
            }
            if (IsSpecial(node) && !IsHtml(node, "address", "div", "p"))
                break;
        }
        ClosePIfInButtonScope();
        InsertHtmlElement(t);
    }

    private void InBodyEndTag(Token t)
    {
        switch (t.Name)
        {
            case "template":
                InHead(t);
                return;

            case "body" or "html":
                if (!InScope("body"))
                {
                    Error();
                    return;
                }
                _mode = Mode.AfterBody;
                if (t.Name == "html")
                    Process(_mode, t);
                return;

            case "address" or "article" or "aside" or "blockquote" or "button" or "center" or "details" or "dialog"
                or "dir" or "div" or "dl" or "fieldset" or "figcaption" or "figure" or "footer" or "header" or "hgroup"
                or "listing" or "main" or "menu" or "nav" or "ol" or "pre" or "search" or "section" or "select" or "summary" or "ul":
                if (!InScope(t.Name))
                {
                    Error();
                    return;
                }
                GenerateImpliedEndTags();
                if (!CurrentIsHtml(t.Name))
                    Error();
                PopUntil(t.Name);
                return;

            case "form":
                if (!OpenContains("template"))
                {
                    var node = _form;
                    _form = null;
                    if (node is null || !InScope(node))
                    {
                        Error();
                        return;
                    }
                    GenerateImpliedEndTags();
                    if (CurrentNode != node)
                        Error();
                    _open.Remove(node);
                }
                else
                {
                    if (!InScope("form"))
                    {
                        Error();
                        return;
                    }
                    GenerateImpliedEndTags();
                    if (!CurrentIsHtml("form"))
                        Error();
                    PopUntil("form");
                }
                return;

            case "p":
                if (!InScope("p", Scope.Button))
                {
                    Error();
                    InsertHtmlElement("p");
                }
                ClosePElement();
                return;

            case "li":
                if (!InScope("li", Scope.ListItem))
                {
                    Error();
                    return;
                }
                GenerateImpliedEndTags("li");
                if (!CurrentIsHtml("li"))
                    Error();
                PopUntil("li");
                return;

            case "dd" or "dt":
                if (!InScope(t.Name))
                {
                    Error();
                    return;
                }
                GenerateImpliedEndTags(t.Name);
                if (!CurrentIsHtml(t.Name))
                    Error();
                PopUntil(t.Name);
                return;

            case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                if (!InScope(e => IsHtml(e, "h1", "h2", "h3", "h4", "h5", "h6"), Scope.Default))
                {
                    Error();
                    return;
                }
                GenerateImpliedEndTags();
                if (!CurrentIsHtml(t.Name))
                    Error();
                PopUntil("h1", "h2", "h3", "h4", "h5", "h6");
                return;

            case "a" or "b" or "big" or "code" or "em" or "font" or "i" or "nobr" or "s" or "small" or "strike" or "strong" or "tt" or "u":
                if (!AdoptionAgency(t.Name))
                    AnyOtherEndTag(t.Name);
                return;

            case "applet" or "marquee" or "object":
                if (!InScope(t.Name))
                {
                    Error();
                    return;
                }
                GenerateImpliedEndTags();
                if (!CurrentIsHtml(t.Name))
                    Error();
                PopUntil(t.Name);
                ClearFormattingToLastMarker();
                return;

            case "br":
                Error();
                InBodyStartTag(Tag(TokenKind.StartTag, "br"));
                return;

            default:
                AnyOtherEndTag(t.Name);
                return;
        }
    }

    // "Any other end tag" in "in body".
    private void AnyOtherEndTag(string name)
    {
        for (var i = _open.Count - 1; i >= 0; i--)
        {
            var node = _open[i];
            if (IsHtml(node, name))
            {
                GenerateImpliedEndTags(name);
                if (CurrentNode != node)
                    Error();
                PopUntil(node);
                return;
            }
            if (IsSpecial(node))
            {
                Error();
                return;
            }
        }
    }

    // https://html.spec.whatwg.org/multipage/parsing.html#parsing-main-intable
    private void InTable(Token t)
    {
        if (t.Kind == TokenKind.Comment)
        {
            InsertComment(t.Data);
        }
        else if (t.Kind == TokenKind.Doctype)
        {
            Error();
        }
        else if (IsStart(t, "caption"))
        {
            ClearStackBackTo("table", "template", "html");
            _formatting.Add(null);
            InsertHtmlElement(t);
            _mode = Mode.InCaption;
        }
        else if (IsStart(t, "colgroup"))
        {
            ClearStackBackTo("table", "template", "html");
            InsertHtmlElement(t);
            _mode = Mode.InColumnGroup;
        }
        else if (IsStart(t, "col"))
        {
            ClearStackBackTo("table", "template", "html");
            InsertHtmlElement("colgroup");
            Reprocess(Mode.InColumnGroup, t);
        }
        else if (IsStart(t, "tbody", "tfoot", "thead"))
        {
            ClearStackBackTo("table", "template", "html");
            InsertHtmlElement(t);
            _mode = Mode.InTableBody;
        }
        else if (IsStart(t, "td", "th", "tr"))
        {
            ClearStackBackTo("table", "template", "html");
            InsertHtmlElement("tbody");
            Reprocess(Mode.InTableBody, t);
        }
        else if (IsStart(t, "table"))
        {
            Error();
            if (!InScope("table", Scope.Table))
                return;
            PopUntil("table");
            ResetInsertionMode();
            Process(_mode, t);
        }
        else if (IsEnd(t, "table"))
        {
            if (!InScope("table", Scope.Table))
            {
                Error();
                return;
            }
            PopUntil("table");
            ResetInsertionMode();
        }
        else if (IsEnd(t, "body", "caption", "col", "colgroup", "html", "tbody", "td", "tfoot", "th", "thead", "tr"))
        {
            Error();
        }
        else if (IsStart(t, "style", "script", "template") || IsEnd(t, "template"))
        {
            InHead(t);
        }
        else if (IsStart(t, "input") && t.Attributes.Find(a => a.Name == "type").Value is { } type
                 && type.Equals("hidden", StringComparison.OrdinalIgnoreCase))
        {
            Error();
            InsertVoid(t);
        }
        else if (IsStart(t, "form"))
        {
            Error();
            if (OpenContains("template") || _form is not null)
                return;
            _form = InsertHtmlElement(t);
            Pop();
        }
        else if (t.Kind == TokenKind.EndOfFile)
        {
            InBody(t);
        }
        else
        {
            Error();
            _fosterParenting = true;
            InBody(t);
            _fosterParenting = false;
        }
    }

    private void ClearStackBackTo(params ReadOnlySpan<string> names)
    {
        while (!CurrentIsHtml(names))
            Pop();
    }

    // https://html.spec.whatwg.org/multipage/parsing.html#parsing-main-incaption
    private void InCaption(Token t)
    {
        if (IsEnd(t, "caption") || IsStart(t, "caption", "col", "colgroup", "tbody", "td", "tfoot", "th", "thead", "tr") || IsEnd(t, "table"))
        {
            if (!InScope("caption", Scope.Table))
            {
                Error();
                return;
            }
            GenerateImpliedEndTags();
            if (!CurrentIsHtml("caption"))
                Error();
            PopUntil("caption");
            ClearFormattingToLastMarker();
            _mode = Mode.InTable;
            if (!IsEnd(t, "caption"))
                Process(_mode, t);
        }
        else if (IsEnd(t, "body", "col", "colgroup", "html", "tbody", "td", "tfoot", "th", "thead", "tr"))
        {
            Error();
        }
        else
        {
            InBody(t);
        }
    }

    // https://html.spec.whatwg.org/multipage/parsing.html#parsing-main-incolgroup
    private void InColumnGroup(Token t)
    {
        if (t.Kind == TokenKind.Comment)
        {
            InsertComment(t.Data);
        }
        else if (t.Kind == TokenKind.Doctype)
        {
            Error();
        }
        else if (IsStart(t, "html"))
        {
            InBody(t);
        }
        else if (IsStart(t, "col"))
        {
            InsertVoid(t);
        }
        else if (IsEnd(t, "colgroup"))
        {
            if (!CurrentIsHtml("colgroup"))
            {
                Error();
                return;
            }
            Pop();
            _mode = Mode.InTable;
        }
        else if (IsEnd(t, "col"))
        {
            Error();
        }
        else if (IsStart(t, "template") || IsEnd(t, "template"))
        {
            InHead(t);
        }
        else if (t.Kind == TokenKind.EndOfFile)
        {
            InBody(t);
        }
        else
        {
            if (!CurrentIsHtml("colgroup"))
            {
                Error();
                return;
            }
            Pop();
            Reprocess(Mode.InTable, t);
        }
    }

    // https://html.spec.whatwg.org/multipage/parsing.html#parsing-main-intbody
    private void InTableBody(Token t)
    {
        if (IsStart(t, "tr"))
        {
            ClearStackBackTo("tbody", "tfoot", "thead", "template", "html");
            InsertHtmlElement(t);
            _mode = Mode.InRow;
        }
        else if (IsStart(t, "th", "td"))
        {
            Error();
            ClearStackBackTo("tbody", "tfoot", "thead", "template", "html");
            InsertHtmlElement("tr");
            Reprocess(Mode.InRow, t);
        }
        else if (IsEnd(t, "tbody", "tfoot", "thead"))
        {
            if (!InScope(t.Name, Scope.Table))
            {
                Error();
                return;
            }
            ClearStackBackTo("tbody", "tfoot", "thead", "template", "html");
            Pop();
            _mode = Mode.InTable;
        }
        else if (IsStart(t, "caption", "col", "colgroup", "tbody", "tfoot", "thead") || IsEnd(t, "table"))
        {
            if (!InScope("tbody", Scope.Table) && !InScope("thead", Scope.Table) && !InScope("tfoot", Scope.Table))
            {
                Error();
                return;
            }
            ClearStackBackTo("tbody", "tfoot", "thead", "template", "html");
            Pop();
            Reprocess(Mode.InTable, t);
        }
        else if (IsEnd(t, "body", "caption", "col", "colgroup", "html", "td", "th", "tr"))
        {
            Error();
        }
        else
        {
            InTable(t);
        }
    }

    // https://html.spec.whatwg.org/multipage/parsing.html#parsing-main-intr
    private void InRow(Token t)
    {
        if (IsStart(t, "th", "td"))
        {
            ClearStackBackTo("tr", "template", "html");
            InsertHtmlElement(t);
            _mode = Mode.InCell;
            _formatting.Add(null);
        }
        else if (IsEnd(t, "tr"))
        {
            if (!InScope("tr", Scope.Table))
            {
                Error();
                return;
            }
            ClearStackBackTo("tr", "template", "html");
            Pop();
            _mode = Mode.InTableBody;
        }
        else if (IsStart(t, "caption", "col", "colgroup", "tbody", "tfoot", "thead", "tr") || IsEnd(t, "table"))
        {
            if (!InScope("tr", Scope.Table))
            {
                Error();
                return;
            }
            ClearStackBackTo("tr", "template", "html");
            Pop();
            Reprocess(Mode.InTableBody, t);
        }
        else if (IsEnd(t, "tbody", "tfoot", "thead"))
        {
            if (!InScope(t.Name, Scope.Table))
            {
                Error();
                return;
            }
            if (!InScope("tr", Scope.Table))
                return;
            ClearStackBackTo("tr", "template", "html");
            Pop();
            Reprocess(Mode.InTableBody, t);
        }
        else if (IsEnd(t, "body", "caption", "col", "colgroup", "html", "td", "th"))
        {
            Error();
        }
        else
        {
            InTable(t);
        }
    }

    // https://html.spec.whatwg.org/multipage/parsing.html#parsing-main-intd
    private void InCell(Token t)
    {
        if (IsEnd(t, "td", "th"))
        {
            if (!InScope(t.Name, Scope.Table))
            {
                Error();
                return;
            }
            GenerateImpliedEndTags();
            if (!CurrentIsHtml(t.Name))
                Error();
            PopUntil(t.Name);
            ClearFormattingToLastMarker();
            _mode = Mode.InRow;
        }
        else if (IsStart(t, "caption", "col", "colgroup", "tbody", "td", "tfoot", "th", "thead", "tr"))
        {
            if (!InScope("td", Scope.Table) && !InScope("th", Scope.Table))
            {
                Error();
                return;
            }
            CloseCell();
            Process(_mode, t);
        }
        else if (IsEnd(t, "body", "caption", "col", "colgroup", "html"))
        {
            Error();
        }
        else if (IsEnd(t, "table", "tbody", "tfoot", "thead", "tr"))
        {
            if (!InScope(t.Name, Scope.Table))
            {
                Error();
                return;
            }
            CloseCell();
            Process(_mode, t);
        }
        else
        {
            InBody(t);
        }
    }

    // https://html.spec.whatwg.org/multipage/parsing.html#close-the-cell
    private void CloseCell()
    {
        GenerateImpliedEndTags();
        if (!CurrentIsHtml("td", "th"))
            Error();
        PopUntil("td", "th");
        ClearFormattingToLastMarker();
        _mode = Mode.InRow;
    }

    // https://html.spec.whatwg.org/multipage/parsing.html#parsing-main-intemplate
    private void InTemplate(Token t)
    {
        if (t.Kind is TokenKind.Comment or TokenKind.Doctype)
        {
            InBody(t);
        }
        else if (IsStart(t, "base", "basefont", "bgsound", "link", "meta", "noframes", "script", "style", "template", "title")
                 || IsEnd(t, "template"))
        {
            InHead(t);
        }
        else if (t.Kind == TokenKind.StartTag)
        {
            var mode = t.Name switch
            {
                "caption" or "colgroup" or "tbody" or "tfoot" or "thead" => Mode.InTable,
                "col" => Mode.InColumnGroup,
                "tr" => Mode.InTableBody,
                "td" or "th" => Mode.InRow,
                _ => Mode.InBody,
            };
            _templateModes[^1] = mode;
            Reprocess(mode, t);
        }
        else if (t.Kind == TokenKind.EndTag)
        {
            Error();
        }
        else if (t.Kind == TokenKind.EndOfFile)
        {
            if (!OpenContains("template"))
                return;
            Error();
            PopUntil("template");
            ClearFormattingToLastMarker();
            _templateModes.RemoveAt(_templateModes.Count - 1);
            ResetInsertionMode();
            _reprocessEndOfFile = true; // the dispatcher reprocesses it, in a loop rather than one nested call per template
        }
    }

    // https://html.spec.whatwg.org/multipage/parsing.html#parsing-main-afterbody
    private void AfterBody(Token t)
    {
        if (t.Kind == TokenKind.Comment)
        {
            InsertComment(t.Data, _open[0]);
        }
        else if (t.Kind == TokenKind.Doctype)
        {
            Error();
        }
        else if (IsStart(t, "html"))
        {
            InBody(t);
        }
        else if (IsEnd(t, "html"))
        {
            _mode = Mode.AfterAfterBody;
        }
        else if (t.Kind != TokenKind.EndOfFile)
        {
            Error();
            Reprocess(Mode.InBody, t);
        }
    }

    // https://html.spec.whatwg.org/multipage/parsing.html#the-after-after-body-insertion-mode
    private void AfterAfterBody(Token t)
    {
        if (t.Kind == TokenKind.Comment)
        {
            InsertComment(t.Data, _document);
        }
        else if (t.Kind == TokenKind.Doctype || IsStart(t, "html"))
        {
            InBody(t);
        }
        else if (t.Kind != TokenKind.EndOfFile)
        {
            Error();
            Reprocess(Mode.InBody, t);
        }
    }
}
