using System.Text;
using Folio.Html;

namespace Folio.Tests.Html;

public class TokenizerTests
{
    private static readonly Lazy<Dictionary<string, CaseFiles.Case>> AllCases = new(() => CaseFiles.Load("Html", "Tokenizer"));

    public static TheoryData<string> Cases => new(AllCases.Value.Keys.Order());

    [Theory]
    [MemberData(nameof(Cases))]
    public void Tokenizes(string id)
    {
        var test = AllCases.Value[id];
        var state = TokenizerState.Data;
        string? lastStartTag = null;
        foreach (var directive in test.Directives)
        {
            var parts = directive.Split(' ');
            if (parts[0] == "state")
                (state, lastStartTag) = (Enum.Parse<TokenizerState>(parts[1]), parts[2]);
        }

        Assert.Equal(test.Expected, Run(test.Input, state, lastStartTag, test.Directives.Contains("cdata")));
    }

    [Fact]
    public void SinkCanSwitchStateAfterAStartTag()
    {
        var recorder = new Recorder();
        var tokenizer = new Tokenizer("<title><b>&amp;</title>", recorder);
        recorder.OnStartTag = name =>
        {
            if (name == "title")
                tokenizer.State = TokenizerState.RcData;
        };

        tokenizer.Run();

        Assert.Equal(["<title>", "\"<b>&\"", "</title>"], recorder.Lines());
    }

    [Fact]
    public void ReportsErrorOffsets()
    {
        var errors = new List<(string, int)>();
        var tokenizer = new Tokenizer("ab<>", new Recorder()) { ParseError = (code, offset) => errors.Add((code, offset)) };

        tokenizer.Run();

        Assert.Equal([("invalid-first-character-of-tag-name", 3)], errors);
    }

    private static List<string> Run(string input, TokenizerState state, string? lastStartTag, bool cdata)
    {
        var recorder = new Recorder();
        var errors = new List<string>();
        var tokenizer = new Tokenizer(input, recorder)
        {
            State = state,
            LastStartTagName = lastStartTag,
            CdataAllowed = cdata,
            ParseError = (code, _) => errors.Add("! " + code),
        };
        tokenizer.Run();
        return [.. recorder.Lines(), .. errors];
    }

    private sealed class Recorder : ITokenSink
    {
        private readonly List<string> _lines = [];
        private readonly StringBuilder _text = new();

        public Action<string>? OnStartTag { get; set; }

        public void Process(Token token)
        {
            if (token.Kind == TokenKind.Characters)
            {
                _text.Append(token.Data);
                return;
            }

            FlushText();
            switch (token.Kind)
            {
                case TokenKind.StartTag or TokenKind.EndTag:
                    var tag = new StringBuilder(token.Kind == TokenKind.EndTag ? "</" : "<").Append(CaseFiles.Escape(token.Name));
                    foreach (var attribute in token.Attributes)
                        tag.Append(' ').Append(CaseFiles.Escape(attribute.Name)).Append("=\"").Append(CaseFiles.Escape(attribute.Value)).Append('"');
                    _lines.Add(tag.Append(token.SelfClosing ? " />" : ">").ToString());
                    if (token.Kind == TokenKind.StartTag)
                        OnStartTag?.Invoke(token.Name);
                    break;
                case TokenKind.Comment:
                    _lines.Add($"<!--{CaseFiles.Escape(token.Data)}-->");
                    break;
                case TokenKind.Doctype:
                    var doctype = new StringBuilder("<!DOCTYPE");
                    if (token.DoctypeName is not null)
                        doctype.Append(' ').Append(CaseFiles.Escape(token.DoctypeName));
                    if (token.PublicId is not null)
                        doctype.Append(" public=\"").Append(CaseFiles.Escape(token.PublicId)).Append('"');
                    if (token.SystemId is not null)
                        doctype.Append(" system=\"").Append(CaseFiles.Escape(token.SystemId)).Append('"');
                    if (token.ForceQuirks)
                        doctype.Append(" quirks");
                    _lines.Add(doctype.Append('>').ToString());
                    break;
            }
        }

        public List<string> Lines()
        {
            FlushText();
            return _lines;
        }

        private void FlushText()
        {
            if (_text.Length == 0)
                return;
            _lines.Add($"\"{CaseFiles.Escape(_text.ToString())}\"");
            _text.Clear();
        }
    }
}
