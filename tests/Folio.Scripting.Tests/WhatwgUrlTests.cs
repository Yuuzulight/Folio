using System.Text.Json;
using Folio.Html;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

/// <summary>
/// The URL parser and setters against the web-platform-tests data at a pinned revision, read from .cache/wpt-url/&lt;revision&gt;
/// (filled by CI, checked against tools/Folio.Gen/wpt-url.sha256, or by hand); the tests skip when it is missing.
/// </summary>
public class WhatwgUrlTests
{
    private const string Revision = "514913ff14fde47147cc4fa84444d86dc8bc3112";

    private static string Data(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Folio.slnx")))
            {
                var path = Path.Combine(dir.FullName, ".cache", "wpt-url", Revision, name);
                if (File.Exists(path))
                    return path;
                break;
            }
        }
        Assert.Skip($"{name} is not in .cache/wpt-url/{Revision}/. Fetch it from " +
                    $"https://raw.githubusercontent.com/web-platform-tests/wpt/{Revision}/url/resources/{name}.");
        return "";
    }

    private static Dictionary<string, string> Properties(WhatwgUrl url) => new()
    {
        ["href"] = url.Serialize(),
        ["protocol"] = url.Scheme + ":",
        ["username"] = url.Username,
        ["password"] = url.Password,
        ["host"] = url.HostString,
        ["hostname"] = url.Host ?? "",
        ["port"] = url.Port?.ToString() ?? "",
        ["pathname"] = url.PathString,
        ["search"] = string.IsNullOrEmpty(url.Query) ? "" : "?" + url.Query,
        ["hash"] = string.IsNullOrEmpty(url.Fragment) ? "" : "#" + url.Fragment,
        ["origin"] = url.Origin,
    };

    private static string? Mismatch(WhatwgUrl url, JsonElement expected)
    {
        var actual = Properties(url);
        foreach (var property in expected.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String && actual.TryGetValue(property.Name, out var value) && value != property.Value.GetString())
                return $"{property.Name} is '{value}', not '{property.Value.GetString()}'";
        }
        return null;
    }

    // JSON text may hold a lone surrogate escape, which System.Text.Json refuses; the parser makes it U+FFFD anyway.
    private static string WithoutLoneSurrogates(string json)
    {
        const string High = @"\\u[dD][89abAB][0-9a-fA-F]{2}", Low = @"\\u[dD][c-fC-F][0-9a-fA-F]{2}";
        json = System.Text.RegularExpressions.Regex.Replace(json, High + "(?!" + Low + ")", @"\uFFFD");
        return System.Text.RegularExpressions.Regex.Replace(json, "(?<!" + High + ")" + Low, @"\uFFFD");
    }

    private static void CheckParsing(string file)
    {
        var failures = new List<string>();
        var count = 0;
        foreach (var entry in JsonDocument.Parse(WithoutLoneSurrogates(File.ReadAllText(Data(file)))).RootElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;
            count++;
            var input = entry.GetProperty("input").GetString()!;
            var baseText = entry.TryGetProperty("base", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
            var baseUrl = baseText is null ? null : WhatwgUrl.Parse(baseText);
            var url = baseText is not null && baseUrl is null ? null : WhatwgUrl.Parse(input, baseUrl);
            var shouldFail = entry.TryGetProperty("failure", out var f) && f.GetBoolean();
            var problem = shouldFail ? url is null ? null : "parsed, but should not have" : url is null ? "did not parse" : Mismatch(url, entry);
            if (problem is not null)
                failures.Add($"'{input}' against '{baseText}': {problem}");
        }
        Assert.True(count > 0, $"No tests read from {file}.");
        Assert.True(failures.Count == 0, $"{failures.Count} of {count} failed:\n{string.Join("\n", failures.Take(20))}");
    }

    [Fact]
    public void ParsesTheUrlTestData() => CheckParsing("urltestdata.json");

    [Fact]
    public void ParsesTheJavaScriptOnlyUrlTestData() => CheckParsing("urltestdata-javascript-only.json");

    [Fact]
    public void SettersMatchTheSettersTestData()
    {
        var failures = new List<string>();
        var count = 0;
        foreach (var group in JsonDocument.Parse(File.ReadAllText(Data("setters_tests.json"))).RootElement.EnumerateObject())
        {
            if (group.Name == "comment")
                continue;
            foreach (var entry in group.Value.EnumerateArray())
            {
                count++;
                var url = WhatwgUrl.Parse(entry.GetProperty("href").GetString()!)!;
                var value = entry.GetProperty("new_value").GetString()!;
                switch (group.Name)
                {
                    case "href": url = WhatwgUrl.Parse(value) ?? url; break;
                    case "protocol": url.SetProtocol(value); break;
                    case "username": url.SetUsername(value); break;
                    case "password": url.SetPassword(value); break;
                    case "host": url.SetHost(value); break;
                    case "hostname": url.SetHostname(value); break;
                    case "port": url.SetPort(value); break;
                    case "pathname": url.SetPathname(value); break;
                    case "search": url.SetSearch(value); break;
                    case "hash": url.SetHash(value); break;
                    default: Assert.Fail($"The test data has a setter this test does not know: {group.Name}."); break;
                }
                if (Mismatch(url, entry.GetProperty("expected")) is { } problem)
                    failures.Add($"{group.Name} of '{entry.GetProperty("href").GetString()}' to '{value}': {problem}");
            }
        }
        Assert.True(count > 0, "No tests read from setters_tests.json.");
        Assert.True(failures.Count == 0, $"{failures.Count} of {count} failed:\n{string.Join("\n", failures.Take(20))}");
    }

    [Fact]
    public void ConvertsHostsToAsciiAsTheTestDataSaysExceptWhereTheExpectationsListThem()
    {
        var known = new HashSet<string>(JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "wpt-url-expectations.json")))
            .RootElement.GetProperty("toascii").EnumerateArray().Select(e => e.GetString()!));
        var failures = new List<string>();
        var count = 0;
        foreach (var entry in JsonDocument.Parse(File.ReadAllText(Data("toascii.json"))).RootElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;
            count++;
            var input = entry.GetProperty("input").GetString()!;
            var want = entry.GetProperty("output").ValueKind == JsonValueKind.Null ? null : entry.GetProperty("output").GetString();
            var got = WhatwgUrl.Parse("https://" + input + "/x")?.Host;
            if (got != want && !known.Contains(input))
                failures.Add($"'{input}' gives {got ?? "no URL"}, not {want ?? "no URL"}");
        }
        Assert.True(count > 0, "No tests read from toascii.json.");
        Assert.True(failures.Count == 0, $"{failures.Count} of {count} failed:\n{string.Join("\n", failures.Take(20))}");
    }
}

public class UrlBindingTests
{
    private static ScriptValue Run(string script)
    {
        var document = TreeBuilder.Parse("<div></div>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        return engine.Evaluate(script);
    }

    private static string Text(string script) => Run(script).ToString() ?? "";

    [Fact]
    public void ConstructorResolvesAgainstABaseAndRefusesWhatIsNotAUrl()
    {
        Assert.Equal("http://x.test/p/a?b#c", Text("new URL('../a?b#c', 'http://x.test/p/q/r').href"));
        Assert.Equal("TypeError", Text("try { new URL('foo'); 'no' } catch (e) { e.name }"));
        Assert.Equal("TypeError", Text("try { new URL('foo', 'bar'); 'no' } catch (e) { e.name }"));
        Assert.Equal("TypeError", Text("try { URL('http://a.test'); 'no' } catch (e) { e.name }"));
        Assert.Equal("true,false", Text("URL.canParse('/x', 'http://a.test') + ',' + URL.canParse('/x')"));
        Assert.Equal("null,http://a.test/x", Text("URL.parse('/x') + ',' + URL.parse('/x', 'http://a.test').href"));
    }

    [Fact]
    public void ExposesTheComponentsOfAUrl()
    {
        Assert.Equal("https:|user|pw|a.test:8080|a.test|8080|/p/q|?x=1&y=2|#frag|https://a.test:8080",
            Text("var u = new URL('https://user:pw@a.test:8080/p/q?x=1&y=2#frag'); [u.protocol, u.username, u.password, u.host, u.hostname, u.port, u.pathname, u.search, u.hash, u.origin].join('|')"));
        Assert.Equal("null", Text("new URL('data:text/plain,hi').origin"));
        Assert.Equal("[object URL]", Text("Object.prototype.toString.call(new URL('http://a.test'))"));
        Assert.Equal("\"http://a.test/\"", Text("JSON.stringify(new URL('http://a.test'))"));
        Assert.Equal("http://a.test/|http://a.test/", Text("var u = new URL('http://a.test'); u.toString() + '|' + u"));
    }

    [Fact]
    public void SettersChangeTheUrlTheWayTheStandardSays()
    {
        Assert.Equal("ftp://a.test/", Text("var u = new URL('http://a.test/'); u.protocol = 'ftp'; u.port = '21'; u.href"));
        Assert.Equal("http://a.test/q%20r?y=2#h", Text("var u = new URL('http://a.test/p?x=1'); u.search = '?y=2'; u.hash = 'h'; u.pathname = '/q r'; u.href"));
        Assert.Equal("http://a.test/p", Text("var u = new URL('http://a.test/p?x=1#f'); u.search = ''; u.hash = ''; u.href"));
        Assert.Equal("http://u:p%3A@b.test:81/", Text("var u = new URL('http://a.test/'); u.username = 'u'; u.password = 'p:'; u.host = 'b.test:81'; u.href"));
        Assert.Equal("http://a.test/", Text("var u = new URL('http://a.test/'); u.host = ''; u.href"));
        Assert.Equal("TypeError", Text("var u = new URL('http://a.test/'); try { u.href = 'nope'; 'no' } catch (e) { e.name }"));
    }

    [Fact]
    public void SearchParamsFollowTheUrlAndTheUrlFollowsThem()
    {
        Assert.Equal("1|http://a.test/?x=1&y=2", Text("var u = new URL('http://a.test/?x=1'); u.searchParams.append('y', '2'); u.searchParams.get('x') + '|' + u.href"));
        Assert.Equal("http://a.test/", Text("var u = new URL('http://a.test/?x=1'); u.searchParams.delete('x'); u.href"));
        Assert.Equal("3|a=3", Text("var u = new URL('http://a.test/'); u.search = '?a=3'; u.searchParams.get('a') + '|' + u.search.slice(1)"));
        Assert.Equal("z|http://a.test/?q=z", Text("var u = new URL('http://a.test/?q=1'); u.href = 'http://a.test/?q=z'; u.searchParams.get('q') + '|' + u.href"));
        Assert.True(Run("var u = new URL('http://a.test/'); u.searchParams === u.searchParams").AsBoolean());
    }

    [Fact]
    public void URLSearchParamsParsesSerializesAndSorts()
    {
        Assert.Equal("a=x+y&b=%C3%A9&c=*%21", Text("new URLSearchParams({a: 'x y', b: '\\u00e9', c: '*!'}).toString()"));
        Assert.Equal("a=1&b=2&b=1", Text("var p = new URLSearchParams('b=2&a=1&b=1'); p.sort(); p.toString()"));
        Assert.Equal("a b|é|", Text("var p = new URLSearchParams('?x=a+b&y=%C3%A9&z='); [p.get('x'), p.get('y'), p.get('z')].join('|')"));
        Assert.Equal("x=1&x=2", Text("new URLSearchParams([['x', '1'], ['x', '2']]).toString()"));
        Assert.Equal("1,2", Text("new URLSearchParams('x=1&x=2').getAll('x').join()"));
        Assert.Equal("true,false,true", Text("var p = new URLSearchParams('a=1&a=2'); [p.has('a'), p.has('a', '3'), p.has('a', '2')].join()"));
        Assert.Equal("a=2", Text("var p = new URLSearchParams('a=1&b=3&a=2'); p.delete('b'); p.set('a', '2'); p.toString()"));
        Assert.Equal("2", Text("new URLSearchParams('a=1&b=2').size.toString()"));
        Assert.Equal("TypeError", Text("try { new URLSearchParams([['a']]); 'no' } catch (e) { e.name }"));
        Assert.Equal("TypeError", Text("try { new URLSearchParams().append('a'); 'no' } catch (e) { e.name }"));
    }

    [Fact]
    public void URLSearchParamsIteratesLive()
    {
        Assert.Equal("a=1;b=2", Text("var p = new URLSearchParams('a=1&b=2'); var out = []; for (var [k, v] of p) out.push(k + '=' + v); out.join(';')"));
        Assert.Equal("a,b|1,2", Text("var p = new URLSearchParams('a=1&b=2'); Array.from(p.keys()).join() + '|' + Array.from(p.values()).join()"));
        Assert.Equal("a,b,c", Text("var p = new URLSearchParams('a=1&b=2'); var seen = []; for (var [k] of p) { seen.push(k); if (k === 'a') p.append('c', '3'); } seen.join()"));
        Assert.Equal("a1;b2", Text("var out = []; new URLSearchParams('a=1&b=2').forEach(function (v, k) { out.push(k + v); }); out.join(';')"));
        Assert.Equal("[object URLSearchParams Iterator]", Text("Object.prototype.toString.call(new URLSearchParams().entries())"));
    }
}
