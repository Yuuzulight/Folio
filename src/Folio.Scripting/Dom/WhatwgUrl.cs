using System.Globalization;
using System.Text;

namespace Folio.Scripting.Dom;

/// <summary>
/// A URL as the URL Standard (https://url.spec.whatwg.org/) models it: the record the basic URL parser fills in and the
/// serializer reads. The host is kept serialized (IPv6 in brackets); a null host is no host, an empty one the empty host.
/// </summary>
internal sealed class WhatwgUrl
{
    public string Scheme = "";
    public string Username = "";
    public string Password = "";
    public string? Host;
    public int? Port;
    public List<string> Path = [];
    public string? OpaquePath;
    public string? Query;
    public string? Fragment;

    public bool HasOpaquePath => OpaquePath is not null;

    public bool IsSpecial => SpecialPort(Scheme, out _);

    public bool IncludesCredentials => Username.Length > 0 || Password.Length > 0;

    /// <summary>A URL with no host, an empty host or the file scheme cannot carry a username, a password or a port.</summary>
    public bool CannotHaveCredentialsOrPort => Host is null or "" || Scheme == "file";

    public WhatwgUrl Clone() => new()
    {
        Scheme = Scheme, Username = Username, Password = Password, Host = Host, Port = Port, Path = [.. Path],
        OpaquePath = OpaquePath, Query = Query, Fragment = Fragment,
    };

    internal static bool SpecialPort(string scheme, out int? port)
    {
        switch (scheme)
        {
            case "ftp": port = 21; return true;
            case "file": port = null; return true;
            case "http": case "ws": port = 80; return true;
            case "https": case "wss": port = 443; return true;
            default: port = null; return false;
        }
    }

    // ---- parsing ----

    internal enum State
    {
        SchemeStart, Scheme, NoScheme, SpecialRelativeOrAuthority, PathOrAuthority, Relative, RelativeSlash,
        SpecialAuthoritySlashes, SpecialAuthorityIgnoreSlashes, Authority, Host, Hostname, Port, File, FileSlash,
        FileHost, PathStart, Path, OpaquePath, Query, Fragment,
    }

    /// <summary>The URL <paramref name="input"/> makes against <paramref name="baseUrl"/>, or null when it is not one.</summary>
    public static WhatwgUrl? Parse(string input, WhatwgUrl? baseUrl = null)
    {
        var url = new WhatwgUrl();
        return Run(input, baseUrl, url, null, trim: true) ? url : null;
    }

    /// <summary>
    /// The basic URL parser. With <paramref name="stateOverride"/> it changes <paramref name="url"/> in place, as the setters of the
    /// URL API do, and returns false when it stopped without a result the setter should keep (the record may be partly changed
    /// only where the Standard says so).
    /// </summary>
    internal static bool Run(string rawInput, WhatwgUrl? baseUrl, WhatwgUrl url, State? stateOverride, bool trim = false)
    {
        var text = ToScalarValues(rawInput);
        if (trim)
        {
            var start = 0;
            var end = text.Length;
            while (start < end && text[start] <= 0x20)
                start++;
            while (end > start && text[end - 1] <= 0x20)
                end--;
            text = text[start..end];
        }
        var input = new List<int>(text.Length);
        foreach (var c in text)
        {
            if (c is 0x09 or 0x0A or 0x0D)
                continue;
            input.Add(c);
        }

        var state = stateOverride ?? State.SchemeStart;
        var buffer = new StringBuilder();
        var atSignSeen = false;
        var insideBrackets = false;
        var passwordTokenSeen = false;
        var pointer = 0;

        while (true)
        {
            var c = pointer < input.Count ? input[pointer] : -1; // -1 is the end of the input
            bool Remaining(int offset, int expected) => pointer + offset < input.Count && input[pointer + offset] == expected;
            bool RemainingStartsWithDriveLetter() => StartsWithWindowsDriveLetter(input, pointer);

            switch (state)
            {
                case State.SchemeStart:
                    if (IsAsciiAlpha(c))
                    {
                        buffer.Append((char)(c | 0x20));
                        state = State.Scheme;
                    }
                    else if (stateOverride is null)
                    {
                        state = State.NoScheme;
                        pointer--;
                    }
                    else
                        return false;
                    break;

                case State.Scheme:
                    if (IsAsciiAlpha(c) || IsAsciiDigit(c) || c is '+' or '-' or '.')
                        buffer.Append(char.ToLowerInvariant((char)c));
                    else if (c == ':')
                    {
                        var scheme = buffer.ToString();
                        if (stateOverride is not null)
                        {
                            var bufferSpecial = SpecialPort(scheme, out _);
                            if (url.IsSpecial != bufferSpecial)
                                return true;
                            if ((url.IncludesCredentials || url.Port is not null) && scheme == "file")
                                return true;
                            if (url.Scheme == "file" && url.Host == "")
                                return true;
                        }
                        url.Scheme = scheme;
                        if (stateOverride is not null)
                        {
                            if (SpecialPort(url.Scheme, out var defaultPort) && url.Port == defaultPort)
                                url.Port = null;
                            return true;
                        }
                        buffer.Clear();
                        if (url.Scheme == "file")
                            state = State.File;
                        else if (url.IsSpecial && baseUrl is not null && baseUrl.Scheme == url.Scheme)
                            state = State.SpecialRelativeOrAuthority;
                        else if (url.IsSpecial)
                            state = State.SpecialAuthoritySlashes;
                        else if (Remaining(1, '/'))
                        {
                            state = State.PathOrAuthority;
                            pointer++;
                        }
                        else
                        {
                            url.OpaquePath = "";
                            state = State.OpaquePath;
                        }
                    }
                    else if (stateOverride is null)
                    {
                        buffer.Clear();
                        state = State.NoScheme;
                        pointer = -1;
                    }
                    else
                        return false;
                    break;

                case State.NoScheme:
                    if (baseUrl is null || (baseUrl.HasOpaquePath && c != '#'))
                        return false;
                    if (baseUrl.HasOpaquePath && c == '#')
                    {
                        url.Scheme = baseUrl.Scheme;
                        url.Path = [.. baseUrl.Path];
                        url.OpaquePath = baseUrl.OpaquePath;
                        url.Query = baseUrl.Query;
                        url.Fragment = "";
                        state = State.Fragment;
                    }
                    else if (baseUrl.Scheme != "file")
                    {
                        state = State.Relative;
                        pointer--;
                    }
                    else
                    {
                        state = State.File;
                        pointer--;
                    }
                    break;

                case State.SpecialRelativeOrAuthority:
                    if (c == '/' && Remaining(1, '/'))
                    {
                        state = State.SpecialAuthorityIgnoreSlashes;
                        pointer++;
                    }
                    else
                    {
                        state = State.Relative;
                        pointer--;
                    }
                    break;

                case State.PathOrAuthority:
                    if (c == '/')
                        state = State.Authority;
                    else
                    {
                        state = State.Path;
                        pointer--;
                    }
                    break;

                case State.Relative:
                    url.Scheme = baseUrl!.Scheme;
                    if (c == '/')
                        state = State.RelativeSlash;
                    else if (url.IsSpecial && c == '\\')
                        state = State.RelativeSlash;
                    else
                    {
                        url.Username = baseUrl.Username;
                        url.Password = baseUrl.Password;
                        url.Host = baseUrl.Host;
                        url.Port = baseUrl.Port;
                        url.Path = [.. baseUrl.Path];
                        url.Query = baseUrl.Query;
                        if (c == '?')
                        {
                            url.Query = "";
                            state = State.Query;
                        }
                        else if (c == '#')
                        {
                            url.Fragment = "";
                            state = State.Fragment;
                        }
                        else if (c != -1)
                        {
                            url.Query = null;
                            ShortenPath(url);
                            state = State.Path;
                            pointer--;
                        }
                    }
                    break;

                case State.RelativeSlash:
                    if (url.IsSpecial && (c == '/' || c == '\\'))
                        state = State.SpecialAuthorityIgnoreSlashes;
                    else if (c == '/')
                        state = State.Authority;
                    else
                    {
                        url.Username = baseUrl!.Username;
                        url.Password = baseUrl.Password;
                        url.Host = baseUrl.Host;
                        url.Port = baseUrl.Port;
                        state = State.Path;
                        pointer--;
                    }
                    break;

                case State.SpecialAuthoritySlashes:
                    if (c == '/' && Remaining(1, '/'))
                    {
                        state = State.SpecialAuthorityIgnoreSlashes;
                        pointer++;
                    }
                    else
                    {
                        state = State.SpecialAuthorityIgnoreSlashes;
                        pointer--;
                    }
                    break;

                case State.SpecialAuthorityIgnoreSlashes:
                    if (c != '/' && c != '\\')
                    {
                        state = State.Authority;
                        pointer--;
                    }
                    break;

                case State.Authority:
                    if (c == '@')
                    {
                        if (atSignSeen)
                            buffer.Insert(0, "%40");
                        atSignSeen = true;
                        foreach (var cp in CodePoints(buffer.ToString()))
                        {
                            if (cp == ':' && !passwordTokenSeen)
                            {
                                passwordTokenSeen = true;
                                continue;
                            }
                            var encoded = PercentEncode(cp, UserinfoSet);
                            if (passwordTokenSeen)
                                url.Password += encoded;
                            else
                                url.Username += encoded;
                        }
                        buffer.Clear();
                    }
                    else if (c == -1 || c is '/' or '?' or '#' || (url.IsSpecial && c == '\\'))
                    {
                        if (atSignSeen && buffer.Length == 0)
                            return false;
                        pointer -= CodePoints(buffer.ToString()).Count() + 1;
                        buffer.Clear();
                        state = State.Host;
                    }
                    else
                        buffer.Append(char.ConvertFromUtf32(c));
                    break;

                case State.Host:
                case State.Hostname:
                    if (stateOverride is not null && url.Scheme == "file")
                    {
                        pointer--;
                        state = State.FileHost;
                    }
                    else if (c == ':' && !insideBrackets)
                    {
                        if (buffer.Length == 0)
                            return false;
                        if (stateOverride == State.Hostname)
                            return false;
                        var host = ParseHost(buffer.ToString(), !url.IsSpecial);
                        if (host is null)
                            return false;
                        url.Host = host;
                        buffer.Clear();
                        state = State.Port;
                    }
                    else if (c == -1 || c is '/' or '?' or '#' || (url.IsSpecial && c == '\\'))
                    {
                        pointer--;
                        if (url.IsSpecial && buffer.Length == 0)
                            return false;
                        if (stateOverride is not null && buffer.Length == 0 && (url.IncludesCredentials || url.Port is not null))
                            return false;
                        var host = ParseHost(buffer.ToString(), !url.IsSpecial);
                        if (host is null)
                            return false;
                        url.Host = host;
                        buffer.Clear();
                        state = State.PathStart;
                        if (stateOverride is not null)
                            return true;
                    }
                    else
                    {
                        if (c == '[')
                            insideBrackets = true;
                        if (c == ']')
                            insideBrackets = false;
                        buffer.Append(char.ConvertFromUtf32(c));
                    }
                    break;

                case State.Port:
                    if (IsAsciiDigit(c))
                        buffer.Append((char)c);
                    else if (c == -1 || c is '/' or '?' or '#' || (url.IsSpecial && c == '\\') || stateOverride is not null)
                    {
                        if (buffer.Length > 0)
                        {
                            if (!int.TryParse(buffer.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port > 65535)
                                return false;
                            SpecialPort(url.Scheme, out var defaultPort);
                            url.Port = port == defaultPort ? null : port;
                            buffer.Clear();
                        }
                        if (stateOverride is not null)
                            return true;
                        state = State.PathStart;
                        pointer--;
                    }
                    else
                        return false;
                    break;

                case State.File:
                    url.Scheme = "file";
                    url.Host = "";
                    if (c == '/' || c == '\\')
                        state = State.FileSlash;
                    else if (baseUrl is not null && baseUrl.Scheme == "file")
                    {
                        url.Host = baseUrl.Host;
                        url.Path = [.. baseUrl.Path];
                        url.Query = baseUrl.Query;
                        if (c == '?')
                        {
                            url.Query = "";
                            state = State.Query;
                        }
                        else if (c == '#')
                        {
                            url.Fragment = "";
                            state = State.Fragment;
                        }
                        else if (c != -1)
                        {
                            url.Query = null;
                            if (!RemainingStartsWithDriveLetter())
                                ShortenPath(url);
                            else
                                url.Path = [];
                            state = State.Path;
                            pointer--;
                        }
                    }
                    else
                    {
                        state = State.Path;
                        pointer--;
                    }
                    break;

                case State.FileSlash:
                    if (c == '/' || c == '\\')
                        state = State.FileHost;
                    else
                    {
                        if (baseUrl is not null && baseUrl.Scheme == "file")
                        {
                            url.Host = baseUrl.Host;
                            if (!RemainingStartsWithDriveLetter() && baseUrl.Path.Count > 0 && IsNormalizedWindowsDriveLetter(baseUrl.Path[0]))
                                url.Path.Add(baseUrl.Path[0]);
                        }
                        state = State.Path;
                        pointer--;
                    }
                    break;

                case State.FileHost:
                    if (c == -1 || c is '/' or '\\' or '?' or '#')
                    {
                        pointer--;
                        if (stateOverride is null && IsWindowsDriveLetter(buffer.ToString()))
                            state = State.Path;
                        else if (buffer.Length == 0)
                        {
                            url.Host = "";
                            if (stateOverride is not null)
                                return true;
                            state = State.PathStart;
                        }
                        else
                        {
                            var host = ParseHost(buffer.ToString(), !url.IsSpecial);
                            if (host is null)
                                return false;
                            if (host == "localhost")
                                host = "";
                            url.Host = host;
                            if (stateOverride is not null)
                                return true;
                            buffer.Clear();
                            state = State.PathStart;
                        }
                    }
                    else
                        buffer.Append(char.ConvertFromUtf32(c));
                    break;

                case State.PathStart:
                    if (url.IsSpecial)
                    {
                        state = State.Path;
                        if (c != '/' && c != '\\')
                            pointer--;
                    }
                    else if (stateOverride is null && c == '?')
                    {
                        url.Query = "";
                        state = State.Query;
                    }
                    else if (stateOverride is null && c == '#')
                    {
                        url.Fragment = "";
                        state = State.Fragment;
                    }
                    else if (c != -1)
                    {
                        state = State.Path;
                        if (c != '/')
                            pointer--;
                    }
                    else if (stateOverride is not null && url.Host is null)
                        url.Path.Add("");
                    break;

                case State.Path:
                    if (c == -1 || c == '/' || (url.IsSpecial && c == '\\') || (stateOverride is null && c is '?' or '#'))
                    {
                        var segment = buffer.ToString();
                        var slash = c == '/' || (url.IsSpecial && c == '\\');
                        if (IsDoubleDotSegment(segment))
                        {
                            ShortenPath(url);
                            if (!slash)
                                url.Path.Add("");
                        }
                        else if (IsSingleDotSegment(segment) && !slash)
                            url.Path.Add("");
                        else if (!IsSingleDotSegment(segment))
                        {
                            if (url.Scheme == "file" && url.Path.Count == 0 && IsWindowsDriveLetter(segment))
                                segment = segment[0] + ":";
                            url.Path.Add(segment);
                        }
                        buffer.Clear();
                        if (c == '?')
                        {
                            url.Query = "";
                            state = State.Query;
                        }
                        if (c == '#')
                        {
                            url.Fragment = "";
                            state = State.Fragment;
                        }
                    }
                    else
                        buffer.Append(PercentEncode(c, PathSet));
                    break;

                case State.OpaquePath:
                    if (c == '?')
                    {
                        url.Query = "";
                        state = State.Query;
                    }
                    else if (c == '#')
                    {
                        url.Fragment = "";
                        state = State.Fragment;
                    }
                    else if (c == ' ')
                        url.OpaquePath += Remaining(1, '?') || Remaining(1, '#') ? "%20" : " ";
                    else if (c != -1)
                        url.OpaquePath += PercentEncode(c, C0ControlSet);
                    break;

                case State.Query:
                    if ((stateOverride is null && c == '#') || c == -1)
                    {
                        var set = url.IsSpecial ? SpecialQuerySet : QuerySet;
                        var encoded = new StringBuilder();
                        foreach (var cp in CodePoints(buffer.ToString()))
                            encoded.Append(PercentEncode(cp, set));
                        url.Query += encoded.ToString();
                        buffer.Clear();
                        if (c == '#')
                        {
                            url.Fragment = "";
                            state = State.Fragment;
                        }
                    }
                    else if (c != -1)
                        buffer.Append(char.ConvertFromUtf32(c));
                    break;

                case State.Fragment:
                    if (c != -1)
                        url.Fragment += PercentEncode(c, FragmentSet);
                    break;
            }

            if (pointer >= input.Count)
                break;
            pointer++;
        }
        return true;
    }

    private static int[] ToScalarValues(string text)
    {
        var result = new List<int>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                result.Add(char.ConvertToUtf32(c, text[i + 1]));
                i++;
            }
            else if (char.IsSurrogate(c))
                result.Add(0xFFFD);
            else
                result.Add(c);
        }
        return [.. result];
    }

    private static IEnumerable<int> CodePoints(string text) => ToScalarValues(text);

    private static bool IsAsciiAlpha(int c) => (c | 0x20) is >= 'a' and <= 'z';

    private static bool IsAsciiDigit(int c) => c is >= '0' and <= '9';

    private static void ShortenPath(WhatwgUrl url)
    {
        if (url.Scheme == "file" && url.Path.Count == 1 && IsNormalizedWindowsDriveLetter(url.Path[0]))
            return;
        if (url.Path.Count > 0)
            url.Path.RemoveAt(url.Path.Count - 1);
    }

    private static bool IsWindowsDriveLetter(string s) => s.Length == 2 && IsAsciiAlpha(s[0]) && (s[1] == ':' || s[1] == '|');

    private static bool IsNormalizedWindowsDriveLetter(string s) => s.Length == 2 && IsAsciiAlpha(s[0]) && s[1] == ':';

    private static bool StartsWithWindowsDriveLetter(List<int> input, int pointer)
    {
        if (input.Count - pointer < 2 || !IsAsciiAlpha(input[pointer]) || (input[pointer + 1] != ':' && input[pointer + 1] != '|'))
            return false;
        return input.Count - pointer == 2 || input[pointer + 2] is '/' or '\\' or '?' or '#';
    }

    private static bool IsSingleDotSegment(string s) => s == "." || s.Equals("%2e", StringComparison.OrdinalIgnoreCase);

    private static bool IsDoubleDotSegment(string s) =>
        s == ".." || s.Equals(".%2e", StringComparison.OrdinalIgnoreCase) || s.Equals("%2e.", StringComparison.OrdinalIgnoreCase)
        || s.Equals("%2e%2e", StringComparison.OrdinalIgnoreCase);

    // ---- percent-encoding ----

    internal delegate bool EncodeSet(int c);

    internal static readonly EncodeSet C0ControlSet = c => c <= 0x1F || c > 0x7E;
    internal static readonly EncodeSet FragmentSet = c => C0ControlSet(c) || c is ' ' or '"' or '<' or '>' or '`';
    internal static readonly EncodeSet QuerySet = c => C0ControlSet(c) || c is ' ' or '"' or '#' or '<' or '>';
    internal static readonly EncodeSet SpecialQuerySet = c => QuerySet(c) || c == '\'';
    internal static readonly EncodeSet PathSet = c => QuerySet(c) || c is '?' or '^' or '`' or '{' or '}';
    internal static readonly EncodeSet UserinfoSet = c => PathSet(c) || c is '/' or ':' or ';' or '=' or '@' or (>= '[' and <= ']') or '|';
    internal static readonly EncodeSet ComponentSet = c => UserinfoSet(c) || c is >= '$' and <= '&' or '+' or ',';

    internal static string PercentEncode(int codePoint, EncodeSet set)
    {
        if (!set(codePoint))
            return char.ConvertFromUtf32(codePoint);
        var sb = new StringBuilder();
        Span<byte> bytes = stackalloc byte[4];
        var count = new Rune(codePoint).EncodeToUtf8(bytes);
        for (var i = 0; i < count; i++)
            sb.Append('%').Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>The percent-encoded form of a string in a set, as the setters and the userinfo and query parsers write it.</summary>
    internal static string PercentEncodeString(string text, EncodeSet set)
    {
        var sb = new StringBuilder();
        foreach (var cp in CodePoints(text))
            sb.Append(PercentEncode(cp, set));
        return sb.ToString();
    }

    internal static byte[] PercentDecode(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var result = new List<byte>(bytes.Length);
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == '%' && i + 2 < bytes.Length && Hex(bytes[i + 1]) >= 0 && Hex(bytes[i + 2]) >= 0)
            {
                result.Add((byte)(Hex(bytes[i + 1]) * 16 + Hex(bytes[i + 2])));
                i += 2;
            }
            else
                result.Add(bytes[i]);
        }
        return [.. result];
    }

    private static bool IsHex(int c) => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static int Hex(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };

    // ---- hosts ----

    /// <summary>The serialized host the input makes, or null when it is not one.</summary>
    internal static string? ParseHost(string input, bool isOpaque)
    {
        if (input.StartsWith('['))
        {
            if (!input.EndsWith(']'))
                return null;
            var address = ParseIPv6(input[1..^1]);
            return address is null ? null : "[" + SerializeIPv6(address) + "]";
        }
        if (isOpaque)
        {
            foreach (var cp in CodePoints(input))
            {
                if (IsForbiddenHostCodePoint(cp))
                    return null;
            }
            return PercentEncodeString(input, C0ControlSet);
        }
        var domain = Encoding.UTF8.GetString(PercentDecode(input));
        var ascii = DomainToAscii(domain);
        if (ascii is null || ascii.Length == 0)
            return null;
        foreach (var ch in ascii)
        {
            if (IsForbiddenHostCodePoint(ch) || ch <= 0x1F || ch == '%' || ch == 0x7F)
                return null;
        }
        if (EndsInANumber(ascii))
        {
            var v4 = ParseIPv4(ascii);
            return v4 is null ? null : SerializeIPv4(v4.Value);
        }
        return ascii;
    }

    private static bool IsForbiddenHostCodePoint(int c) =>
        c is 0 or 0x09 or 0x0A or 0x0D or ' ' or '#' or '/' or ':' or '<' or '>' or '?' or '@' or '[' or '\\' or ']' or '^' or '|';

    private static string? DomainToAscii(string domain)
    {
        var plain = true;
        foreach (var ch in domain)
        {
            if (ch > 0x7F)
            {
                plain = false;
                break;
            }
        }
        if (plain && !domain.Contains("xn--", StringComparison.OrdinalIgnoreCase))
            return domain.ToLowerInvariant();
        try
        {
            // UTS 46 processing label by label: an ACE label (xn--) must be valid Punycode that decodes to more than
            // ASCII; any other is mapped (case folded and normalised) and converted.
            var mapping = new IdnMapping { AllowUnassigned = true, UseStd3AsciiRules = false };
            var labels = domain.Split('.', '。', '．', '｡');
            var result = new string[labels.Length];
            for (var i = 0; i < labels.Length; i++)
            {
                var label = labels[i];
                if (label.StartsWith("xn--", StringComparison.OrdinalIgnoreCase))
                {
                    // A bare "xn--" is kept as it is, as browsers do.
                    var decoded = label.Length == 4 ? "é" : PunycodeDecode(label[4..].ToLowerInvariant());
                    if (decoded is null || decoded.All(ch => ch < 0x80))
                        return null;
                    result[i] = label.ToLowerInvariant();
                }
                else if (label.All(ch => ch < 0x80))
                    result[i] = label.ToLowerInvariant();
                else
                    result[i] = mapping.GetAscii(label).ToLowerInvariant();
            }
            return string.Join('.', result);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    // RFC 3492: the Unicode the Punycode text stands for, or null when it is not Punycode.
    private static string? PunycodeDecode(string input)
    {
        const int Base = 36, TMin = 1, TMax = 26, Skew = 38, Damp = 700;
        var output = new List<int>();
        var delimiter = input.LastIndexOf('-');
        if (delimiter > 0)
        {
            foreach (var ch in input[..delimiter])
            {
                if (ch >= 0x80)
                    return null;
                output.Add(ch);
            }
        }
        var (n, i, bias) = (128, 0, 72);
        for (var at = delimiter > 0 ? delimiter + 1 : 0; at < input.Length;)
        {
            var oldI = i;
            for (int w = 1, k = Base; ; k += Base)
            {
                if (at >= input.Length)
                    return null;
                var ch = input[at++];
                var digit = ch switch { >= 'a' and <= 'z' => ch - 'a', >= 'A' and <= 'Z' => ch - 'A', >= '0' and <= '9' => ch - '0' + 26, _ => -1 };
                if (digit < 0)
                    return null;
                if (digit > (int.MaxValue - i) / w)
                    return null;
                i += digit * w;
                var t = k <= bias ? TMin : k >= bias + TMax ? TMax : k - bias;
                if (digit < t)
                    break;
                if (w > int.MaxValue / (Base - t))
                    return null;
                w *= Base - t;
            }
            var count = output.Count + 1;
            // Bias adaptation.
            var delta = oldI == 0 ? (i - oldI) / Damp : (i - oldI) / 2;
            delta += delta / count;
            var kk = 0;
            while (delta > ((Base - TMin) * TMax) / 2)
            {
                delta /= Base - TMin;
                kk += Base;
            }
            bias = kk + (Base - TMin + 1) * delta / (delta + Skew);
            if (i / count > int.MaxValue - n)
                return null;
            n += i / count;
            i %= count;
            if (n > 0x10FFFF || (n >= 0xD800 && n <= 0xDFFF))
                return null;
            output.Insert(i, n);
            i++;
        }
        var sb = new StringBuilder();
        foreach (var cp in output)
            sb.Append(char.ConvertFromUtf32(cp));
        return sb.ToString();
    }

    private static bool EndsInANumber(string input)
    {
        var parts = input.Split('.').ToList();
        if (parts[^1].Length == 0)
        {
            if (parts.Count == 1)
                return false;
            parts.RemoveAt(parts.Count - 1);
        }
        var last = parts[^1];
        if (last.Length > 0 && last.All(c => c is >= '0' and <= '9'))
            return true;
        return ParseIPv4Number(last) is not null;
    }

    private static long? ParseIPv4Number(string input)
    {
        if (input.Length == 0)
            return null;
        var radix = 10;
        if (input.Length >= 2 && (input.StartsWith("0x", StringComparison.Ordinal) || input.StartsWith("0X", StringComparison.Ordinal)))
        {
            input = input[2..];
            radix = 16;
        }
        else if (input.Length >= 2 && input[0] == '0')
        {
            input = input[1..];
            radix = 8;
        }
        if (input.Length == 0)
            return 0;
        long value = 0;
        foreach (var ch in input)
        {
            var digit = ch switch { >= '0' and <= '9' => ch - '0', >= 'a' and <= 'f' => ch - 'a' + 10, >= 'A' and <= 'F' => ch - 'A' + 10, _ => 99 };
            if (digit >= radix)
                return null;
            value = value * radix + digit;
            if (value > 0xFFFFFFFFFFL)
                value = 0xFFFFFFFFFFL; // far past any limit; keeps the arithmetic finite
        }
        return value;
    }

    private static uint? ParseIPv4(string input)
    {
        var parts = input.Split('.').ToList();
        if (parts[^1].Length == 0 && parts.Count > 1)
            parts.RemoveAt(parts.Count - 1);
        if (parts.Count > 4)
            return null;
        var numbers = new List<long>();
        foreach (var part in parts)
        {
            var number = ParseIPv4Number(part);
            if (number is null)
                return null;
            numbers.Add(number.Value);
        }
        for (var i = 0; i < numbers.Count - 1; i++)
        {
            if (numbers[i] > 255)
                return null;
        }
        if (numbers[^1] >= Math.Pow(256, 5 - numbers.Count))
            return null;
        var ipv4 = numbers[^1];
        for (var i = 0; i < numbers.Count - 1; i++)
            ipv4 += numbers[i] * (long)Math.Pow(256, 3 - i);
        return (uint)ipv4;
    }

    private static string SerializeIPv4(uint address) =>
        $"{address >> 24}.{(address >> 16) & 255}.{(address >> 8) & 255}.{address & 255}";

    private static ushort[]? ParseIPv6(string input)
    {
        var address = new ushort[8];
        var pieceIndex = 0;
        int? compress = null;
        var pointer = 0;
        int C(int at) => at < input.Length ? input[at] : -1;
        if (C(pointer) == ':')
        {
            if (C(pointer + 1) != ':')
                return null;
            pointer += 2;
            pieceIndex++;
            compress = pieceIndex;
        }
        while (C(pointer) != -1)
        {
            if (pieceIndex == 8)
                return null;
            if (C(pointer) == ':')
            {
                if (compress is not null)
                    return null;
                pointer++;
                pieceIndex++;
                compress = pieceIndex;
                continue;
            }
            var value = 0;
            var length = 0;
            while (length < 4 && IsHex(C(pointer)))
            {
                value = value * 16 + Hex((byte)C(pointer));
                pointer++;
                length++;
            }
            if (C(pointer) == '.')
            {
                if (length == 0)
                    return null;
                pointer -= length;
                if (pieceIndex > 6)
                    return null;
                var numbersSeen = 0;
                while (C(pointer) != -1)
                {
                    int? ipv4Piece = null;
                    if (numbersSeen > 0)
                    {
                        if (C(pointer) == '.' && numbersSeen < 4)
                            pointer++;
                        else
                            return null;
                    }
                    if (C(pointer) is < '0' or > '9')
                        return null;
                    while (C(pointer) is >= '0' and <= '9')
                    {
                        var number = C(pointer) - '0';
                        if (ipv4Piece is null)
                            ipv4Piece = number;
                        else if (ipv4Piece == 0)
                            return null;
                        else
                            ipv4Piece = ipv4Piece * 10 + number;
                        if (ipv4Piece > 255)
                            return null;
                        pointer++;
                    }
                    address[pieceIndex] = (ushort)(address[pieceIndex] * 0x100 + ipv4Piece!.Value);
                    numbersSeen++;
                    if (numbersSeen is 2 or 4)
                        pieceIndex++;
                }
                if (numbersSeen != 4)
                    return null;
                break;
            }
            if (C(pointer) == ':')
            {
                pointer++;
                if (C(pointer) == -1)
                    return null;
            }
            else if (C(pointer) != -1)
                return null;
            address[pieceIndex] = (ushort)value;
            pieceIndex++;
        }
        if (compress is not null)
        {
            var swaps = pieceIndex - compress.Value;
            pieceIndex = 7;
            while (pieceIndex != 0 && swaps > 0)
            {
                (address[pieceIndex], address[compress.Value + swaps - 1]) = (address[compress.Value + swaps - 1], address[pieceIndex]);
                pieceIndex--;
                swaps--;
            }
        }
        else if (pieceIndex != 8)
            return null;
        return address;
    }

    private static string SerializeIPv6(ushort[] address)
    {
        // The first longest run of two or more zero pieces is compressed.
        int bestStart = -1, bestLength = 0;
        for (var i = 0; i < 8;)
        {
            if (address[i] != 0)
            {
                i++;
                continue;
            }
            var start = i;
            while (i < 8 && address[i] == 0)
                i++;
            if (i - start > bestLength)
                (bestStart, bestLength) = (start, i - start);
        }
        if (bestLength < 2)
            bestStart = -1;
        var sb = new StringBuilder();
        var ignore0 = false;
        for (var i = 0; i < 8; i++)
        {
            if (ignore0 && address[i] == 0)
                continue;
            ignore0 = false;
            if (bestStart == i)
            {
                sb.Append(i == 0 ? "::" : ":");
                ignore0 = true;
                continue;
            }
            sb.Append(address[i].ToString("x", CultureInfo.InvariantCulture));
            if (i != 7)
                sb.Append(':');
        }
        return sb.ToString();
    }

    // ---- serializing ----

    public string PathString
    {
        get
        {
            if (OpaquePath is not null)
                return OpaquePath;
            var sb = new StringBuilder();
            foreach (var segment in Path)
                sb.Append('/').Append(segment);
            return sb.ToString();
        }
    }

    public string HostString => Host is null ? "" : Port is null ? Host : Host + ":" + Port.Value.ToString(CultureInfo.InvariantCulture);

    public string Serialize(bool excludeFragment = false)
    {
        var sb = new StringBuilder(Scheme).Append(':');
        if (Host is not null)
        {
            sb.Append("//");
            if (IncludesCredentials)
            {
                sb.Append(Username);
                if (Password.Length > 0)
                    sb.Append(':').Append(Password);
                sb.Append('@');
            }
            sb.Append(Host);
            if (Port is not null)
                sb.Append(':').Append(Port.Value.ToString(CultureInfo.InvariantCulture));
        }
        else if (OpaquePath is null && Path.Count > 1 && Path[0].Length == 0)
            sb.Append("/.");
        sb.Append(PathString);
        if (Query is not null)
            sb.Append('?').Append(Query);
        if (!excludeFragment && Fragment is not null)
            sb.Append('#').Append(Fragment);
        return sb.ToString();
    }

    public string Origin
    {
        get
        {
            switch (Scheme)
            {
                case "blob":
                    var inner = Parse(PathString);
                    return inner is { Scheme: "http" or "https" } ? inner.Origin : "null";
                case "ftp": case "http": case "https": case "ws": case "wss":
                    return Scheme + "://" + HostString;
                default:
                    return "null";
            }
        }
    }

    // ---- the setters of the URL API ----

    public void SetProtocol(string value) => Run(value + ":", null, this, State.SchemeStart);

    public void SetUsername(string value)
    {
        if (CannotHaveCredentialsOrPort)
            return;
        Username = PercentEncodeString(value, UserinfoSet);
    }

    public void SetPassword(string value)
    {
        if (CannotHaveCredentialsOrPort)
            return;
        Password = PercentEncodeString(value, UserinfoSet);
    }

    public void SetHost(string value)
    {
        if (HasOpaquePath)
            return;
        Run(value, null, this, State.Host);
    }

    public void SetHostname(string value)
    {
        if (HasOpaquePath)
            return;
        Run(value, null, this, State.Hostname);
    }

    public void SetPort(string value)
    {
        if (CannotHaveCredentialsOrPort)
            return;
        if (value.Length == 0)
            Port = null;
        else
            Run(value, null, this, State.Port);
    }

    public void SetPathname(string value)
    {
        if (HasOpaquePath)
            return;
        Path = [];
        Run(value, null, this, State.PathStart);
    }

    public void SetSearch(string value)
    {
        if (value.Length == 0)
        {
            Query = null;
            StripTrailingSpacesFromOpaquePath();
            return;
        }
        var input = value.StartsWith('?') ? value[1..] : value;
        Query = "";
        Run(input, null, this, State.Query);
    }

    public void SetHash(string value)
    {
        if (value.Length == 0)
        {
            Fragment = null;
            StripTrailingSpacesFromOpaquePath();
            return;
        }
        var input = value.StartsWith('#') ? value[1..] : value;
        Fragment = "";
        Run(input, null, this, State.Fragment);
    }

    public void StripTrailingSpacesFromOpaquePath()
    {
        if (OpaquePath is null || Fragment is not null || Query is not null)
            return;
        OpaquePath = OpaquePath.TrimEnd(' ');
    }

    // ---- application/x-www-form-urlencoded ----

    internal static List<(string Name, string Value)> ParseForm(string input)
    {
        var result = new List<(string, string)>();
        foreach (var sequence in input.Split('&'))
        {
            if (sequence.Length == 0)
                continue;
            var at = sequence.IndexOf('=');
            var name = at < 0 ? sequence : sequence[..at];
            var value = at < 0 ? "" : sequence[(at + 1)..];
            result.Add((FormDecode(name), FormDecode(value)));
        }
        return result;
    }

    internal static string FormDecode(string text) => Encoding.UTF8.GetString(PercentDecode(text.Replace('+', ' ')));

    internal static string FormEncode(string text)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(ToWellFormed(text)))
        {
            if (b == ' ')
                sb.Append('+');
            else if (b is (>= (byte)'a' and <= (byte)'z') or (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'0' and <= (byte)'9') or (byte)'*' or (byte)'-' or (byte)'.' or (byte)'_')
                sb.Append((char)b);
            else
                sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    internal static string SerializeForm(IEnumerable<(string Name, string Value)> pairs) =>
        string.Join('&', pairs.Select(p => FormEncode(p.Name) + "=" + FormEncode(p.Value)));

    private static string ToWellFormed(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var cp in ToScalarValues(text))
            sb.Append(char.ConvertFromUtf32(cp));
        return sb.ToString();
    }
}
