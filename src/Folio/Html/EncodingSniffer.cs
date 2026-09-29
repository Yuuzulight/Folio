using System.Text;

namespace Folio.Html;

/// <summary>
/// Picks the encoding of an HTML byte stream (https://html.spec.whatwg.org/multipage/parsing.html#encoding-sniffing-algorithm):
/// byte order mark, then a <c>&lt;meta charset&gt;</c> prescan of the first 1024 bytes, then UTF-8. Supported: UTF-8,
/// UTF-16LE/BE and windows-1252 (docs/study/01-html-parsing.md); other labels decode as UTF-8.
/// </summary>
internal static class EncodingSniffer
{
    public enum Kind
    {
        Utf8,
        Utf16LE,
        Utf16BE,
        Windows1252,
    }

    /// <returns>The encoding, how many BOM bytes to skip, and the label found in a meta element that Folio could not honour.</returns>
    public static (Kind Encoding, int BomLength, string? UnsupportedLabel) Sniff(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            return (Kind.Utf8, 3, null);
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
            return (Kind.Utf16BE, 2, null);
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
            return (Kind.Utf16LE, 2, null);

        var label = Prescan(bytes[..Math.Min(bytes.Length, 1024)]);
        if (label is null)
            return (Kind.Utf8, 0, null);
        return FromLabel(label) switch
        {
            // A meta element naming UTF-16 means UTF-8: the bytes were readable as ASCII to find it.
            Kind.Utf16LE or Kind.Utf16BE => (Kind.Utf8, 0, null),
            { } kind => (kind, 0, null),
            null => (Kind.Utf8, 0, label),
        };
    }

    public static string Decode(ReadOnlySpan<byte> bytes, Kind encoding) => encoding switch
    {
        Kind.Utf16LE => Encoding.Unicode.GetString(bytes),
        Kind.Utf16BE => Encoding.BigEndianUnicode.GetString(bytes),
        Kind.Windows1252 => DecodeWindows1252(bytes),
        _ => Encoding.UTF8.GetString(bytes),
    };

    // https://encoding.spec.whatwg.org/#names-and-labels (the labels of the supported encodings)
    private static Kind? FromLabel(string label) => label.Trim().ToLowerInvariant() switch
    {
        "unicode-1-1-utf-8" or "unicode11utf8" or "unicode20utf8" or "utf-8" or "utf8" or "x-unicode20utf8" => Kind.Utf8,
        "unicodefffe" or "utf-16be" => Kind.Utf16BE,
        "csunicode" or "iso-10646-ucs-2" or "ucs-2" or "unicode" or "unicodefeff" or "utf-16" or "utf-16le" => Kind.Utf16LE,
        "ansi_x3.4-1968" or "ascii" or "cp1252" or "cp819" or "csisolatin1" or "ibm819" or "iso-8859-1" or "iso-ir-100"
            or "iso8859-1" or "iso88591" or "iso_8859-1" or "iso_8859-1:1987" or "l1" or "latin1" or "us-ascii"
            or "windows-1252" or "x-cp1252" => Kind.Windows1252,
        _ => null,
    };

    // windows-1252: Latin-1 except 0x80–0x9F (https://encoding.spec.whatwg.org/index-windows-1252.txt).
    private static readonly char[] C1 =
    [
        '€', '\u0081', '‚', 'ƒ', '„', '…', '†', '‡', 'ˆ', '‰', 'Š', '‹', 'Œ', '\u008D', 'Ž', '\u008F',
        '\u0090', '‘', '’', '“', '”', '•', '–', '—', '˜', '™', 'š', '›', 'œ', '\u009D', 'ž', 'Ÿ',
    ];

    private static string DecodeWindows1252(ReadOnlySpan<byte> bytes)
    {
        var chars = new char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
            chars[i] = bytes[i] is >= 0x80 and <= 0x9F ? C1[bytes[i] - 0x80] : (char)bytes[i];
        return new string(chars);
    }

    // https://html.spec.whatwg.org/multipage/parsing.html#prescan-a-byte-stream-to-determine-its-encoding
    private static string? Prescan(ReadOnlySpan<byte> bytes)
    {
        var i = 0;
        while (i < bytes.Length)
        {
            if (Matches(bytes, i, "<!--"))
            {
                var end = bytes[(i + 4)..].IndexOf("-->"u8);
                if (end < 0)
                    return null;
                i += 4 + end + 3;
            }
            else if (Matches(bytes, i, "<meta") && i + 5 < bytes.Length && IsSpaceOrSlash(bytes[i + 5]))
            {
                i += 6;
                string? charset = null, content = null, httpEquiv = null;
                while (NextAttribute(bytes, ref i) is var (name, value))
                {
                    switch (name)
                    {
                        case "charset":
                            charset ??= value;
                            break;
                        case "content":
                            content ??= value;
                            break;
                        case "http-equiv":
                            httpEquiv ??= value;
                            break;
                    }
                }
                if (charset is not null)
                    return charset;
                if (content is not null && httpEquiv?.Equals("content-type", StringComparison.OrdinalIgnoreCase) == true
                    && CharsetFromContent(content) is { } fromContent)
                    return fromContent;
            }
            else if (bytes[i] == '<' && i + 1 < bytes.Length && (IsAsciiLetter(bytes[i + 1]) || (bytes[i + 1] == '/' && i + 2 < bytes.Length && IsAsciiLetter(bytes[i + 2]))))
            {
                // Another tag: skip its name and attributes.
                i++;
                while (i < bytes.Length && !IsSpaceOrSlash(bytes[i]) && bytes[i] != '>')
                    i++;
                while (NextAttribute(bytes, ref i) is not null)
                {
                }
            }
            else if (Matches(bytes, i, "<!") || Matches(bytes, i, "</") || Matches(bytes, i, "<?"))
            {
                var end = bytes[i..].IndexOf((byte)'>');
                if (end < 0)
                    return null;
                i += end + 1;
            }
            else
            {
                i++;
            }
        }
        return null;
    }

    private static bool Matches(ReadOnlySpan<byte> bytes, int at, string ascii)
    {
        if (at + ascii.Length > bytes.Length)
            return false;
        for (var k = 0; k < ascii.Length; k++)
        {
            if (char.ToLowerInvariant((char)bytes[at + k]) != ascii[k])
                return false;
        }
        return true;
    }

    private static bool IsSpace(byte b) => b is 0x09 or 0x0A or 0x0C or 0x0D or 0x20;
    private static bool IsSpaceOrSlash(byte b) => IsSpace(b) || b == '/';
    private static bool IsAsciiLetter(byte b) => b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z';

    // https://html.spec.whatwg.org/multipage/parsing.html#concept-get-attributes-when-sniffing
    private static (string Name, string Value)? NextAttribute(ReadOnlySpan<byte> bytes, ref int i)
    {
        while (i < bytes.Length && IsSpaceOrSlash(bytes[i]))
            i++;
        if (i >= bytes.Length || bytes[i] == '>')
        {
            i++;
            return null;
        }
        var name = new StringBuilder();
        while (i < bytes.Length && !(bytes[i] == '=' && name.Length > 0) && !IsSpaceOrSlash(bytes[i]) && bytes[i] != '>')
            name.Append(char.ToLowerInvariant((char)bytes[i++]));
        while (i < bytes.Length && IsSpace(bytes[i]))
            i++;
        if (i >= bytes.Length || bytes[i] != '=')
            return (name.ToString(), "");
        i++;
        while (i < bytes.Length && IsSpace(bytes[i]))
            i++;
        var value = new StringBuilder();
        if (i < bytes.Length && bytes[i] is (byte)'"' or (byte)'\'')
        {
            var quote = bytes[i++];
            while (i < bytes.Length && bytes[i] != quote)
                value.Append(char.ToLowerInvariant((char)bytes[i++]));
            i++;
        }
        else
        {
            while (i < bytes.Length && !IsSpace(bytes[i]) && bytes[i] != '>')
                value.Append(char.ToLowerInvariant((char)bytes[i++]));
        }
        return (name.ToString(), value.ToString());
    }

    // https://html.spec.whatwg.org/multipage/urls-and-fetching.html#algorithm-for-extracting-a-character-encoding-from-a-meta-element
    private static string? CharsetFromContent(string content)
    {
        var at = content.IndexOf("charset", StringComparison.OrdinalIgnoreCase);
        while (at >= 0)
        {
            var i = at + 7;
            while (i < content.Length && char.IsWhiteSpace(content[i]))
                i++;
            if (i < content.Length && content[i] == '=')
            {
                i++;
                while (i < content.Length && char.IsWhiteSpace(content[i]))
                    i++;
                if (i >= content.Length)
                    return null;
                if (content[i] is '"' or '\'')
                {
                    var end = content.IndexOf(content[i], i + 1);
                    return end < 0 ? null : content[(i + 1)..end];
                }
                var stop = i;
                while (stop < content.Length && !char.IsWhiteSpace(content[stop]) && content[stop] != ';')
                    stop++;
                return content[i..stop];
            }
            at = content.IndexOf("charset", at + 7, StringComparison.OrdinalIgnoreCase);
        }
        return null;
    }
}
