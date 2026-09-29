using System.Globalization;
using System.Text;
using Folio.Html;

namespace Folio.Tests.Html;

public class TokenizerTests
{
    private static readonly Lazy<Dictionary<string, Case>> AllCases = new(LoadCases);

    public static TheoryData<string> Cases => new(AllCases.Value.Keys.Order());

    [Theory]
    [MemberData(nameof(Cases))]
    public void Tokenizes(string id)
    {
        var test = AllCases.Value[id];

        Assert.Equal(test.Expected, Run(test.Input, test.State, test.LastStartTag, test.Cdata));
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

    private sealed record Case(string Input, TokenizerState State, string? LastStartTag, bool Cdata, List<string> Expected);

    private static Dictionary<string, Case> LoadCases()
    {
        var cases = new Dictionary<string, Case>();
        var dir = Path.Combine(AppContext.BaseDirectory, "Html", "Tokenizer");
        foreach (var file in Directory.GetFiles(dir, "*.txt").Where(f => !f.EndsWith("README.txt", StringComparison.Ordinal)))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].StartsWith("=== ", StringComparison.Ordinal))
                    continue;

                var id = $"{Path.GetFileNameWithoutExtension(file)}: {lines[i][4..]}";
                var state = TokenizerState.Data;
                string? lastStartTag = null;
                var cdata = false;
                for (i++; lines[i].StartsWith('@'); i++)
                {
                    var parts = lines[i].Split(' ');
                    if (parts[0] == "@state")
                    {
                        state = Enum.Parse<TokenizerState>(parts[1]);
                        lastStartTag = parts[2];
                    }
                    else if (parts[0] == "@cdata")
                    {
                        cdata = true;
                    }
                }

                var input = new List<string>();
                for (; lines[i] != "---"; i++)
                    input.Add(lines[i]);

                var expected = new List<string>();
                for (i++; i < lines.Length && !lines[i].StartsWith("=== ", StringComparison.Ordinal); i++)
                {
                    if (lines[i].Length > 0)
                        expected.Add(lines[i]);
                }
                i--;

                cases.Add(id, new Case(Unescape(string.Join('\n', input)), state, lastStartTag, cdata, expected));
            }
        }
        return cases;
    }

    internal static string Unescape(string text)
    {
        var result = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 5 < text.Length && text[i + 1] == 'u')
            {
                result.Append((char)int.Parse(text.AsSpan(i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                i += 5;
            }
            else
            {
                result.Append(text[i]);
            }
        }
        return result.ToString();
    }

    internal static string Escape(string text)
    {
        var result = new StringBuilder();
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\': result.Append(@"\\"); break;
                case '"': result.Append("\\\""); break;
                case '\n': result.Append(@"\n"); break;
                case '\t': result.Append(@"\t"); break;
                case < ' ' or (>= '\u007F' and <= '\u009F') or >= '\uFFFD':
                    result.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:X4}");
                    break;
                default: result.Append(c); break;
            }
        }
        return result.ToString();
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
                    var tag = new StringBuilder(token.Kind == TokenKind.EndTag ? "</" : "<").Append(Escape(token.Name));
                    foreach (var attribute in token.Attributes)
                        tag.Append(' ').Append(Escape(attribute.Name)).Append("=\"").Append(Escape(attribute.Value)).Append('"');
                    _lines.Add(tag.Append(token.SelfClosing ? " />" : ">").ToString());
                    if (token.Kind == TokenKind.StartTag)
                        OnStartTag?.Invoke(token.Name);
                    break;
                case TokenKind.Comment:
                    _lines.Add($"<!--{Escape(token.Data)}-->");
                    break;
                case TokenKind.Doctype:
                    var doctype = new StringBuilder("<!DOCTYPE");
                    if (token.DoctypeName is not null)
                        doctype.Append(' ').Append(Escape(token.DoctypeName));
                    if (token.PublicId is not null)
                        doctype.Append(" public=\"").Append(Escape(token.PublicId)).Append('"');
                    if (token.SystemId is not null)
                        doctype.Append(" system=\"").Append(Escape(token.SystemId)).Append('"');
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
            _lines.Add($"\"{Escape(_text.ToString())}\"");
            _text.Clear();
        }
    }
}
