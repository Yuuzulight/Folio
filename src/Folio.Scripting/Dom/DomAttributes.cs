using System.Text;
using Folio.Dom;

namespace Folio.Scripting.Dom;

/// <summary>
/// Implements DOMTokenList wrapping an element's class attribute (WHATWG DOM §4.2.14, #452).
/// </summary>
internal sealed class LiveDomTokenList(ElementNode element, string attributeName = "class")
{
    public ElementNode Element { get; } = element;
    public string AttributeName { get; } = attributeName;

    public List<string> GetTokens()
    {
        var val = Element.GetAttribute(AttributeName);
        if (string.IsNullOrEmpty(val))
            return [];

        return val.Split(ElementNode.AsciiWhitespace, StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private void SetTokens(List<string> tokens)
    {
        var joined = string.Join(" ", tokens);
        if (joined.Length == 0)
            Element.RemoveAttribute(AttributeName);
        else
            Element.SetAttribute(AttributeName, joined);
    }

    public static void ValidateToken(string token)
    {
        if (string.IsNullOrEmpty(token))
            throw WebIdlException.DomException(DomExceptionNames.SyntaxError, "The token provided must not be empty.");

        if (token.IndexOfAny(ElementNode.AsciiWhitespace) >= 0)
            throw WebIdlException.DomException(DomExceptionNames.InvalidCharacterError, "The token provided contains HTML whitespace.");
    }

    public int Length => GetTokens().Count;

    public string Value
    {
        get => Element.GetAttribute(AttributeName) ?? "";
        set => Element.SetAttribute(AttributeName, value);
    }

    public string? Item(int index)
    {
        if (index < 0)
            return null;

        var tokens = GetTokens();
        return index < tokens.Count ? tokens[index] : null;
    }

    public bool Contains(string token)
    {
        ValidateToken(token);

        return GetTokens().Contains(token, StringComparer.Ordinal);
    }

    public void Add(ReadOnlySpan<string> tokens)
    {
        for (var i = 0; i < tokens.Length; i++)
            ValidateToken(tokens[i]);

        var current = GetTokens();
        var changed = false;

        for (var i = 0; i < tokens.Length; i++)
        {
            var tok = tokens[i];
            if (!current.Contains(tok, StringComparer.Ordinal))
            {
                current.Add(tok);
                changed = true;
            }
        }

        if (changed)
            SetTokens(current);
    }

    public void Remove(ReadOnlySpan<string> tokens)
    {
        for (var i = 0; i < tokens.Length; i++)
            ValidateToken(tokens[i]);

        var current = GetTokens();
        var changed = false;

        for (var i = 0; i < tokens.Length; i++)
        {
            var tok = tokens[i];
            if (current.Remove(tok))
                changed = true;
        }

        if (changed)
            SetTokens(current);
    }

    public bool Toggle(string token, bool? force = null)
    {
        ValidateToken(token);

        var current = GetTokens();
        var exists = current.Contains(token, StringComparer.Ordinal);

        var shouldHave = force ?? !exists;
        if (shouldHave)
        {
            if (!exists)
            {
                current.Add(token);
                SetTokens(current);
            }
            return true;
        }
        else
        {
            if (exists)
            {
                current.Remove(token);
                SetTokens(current);
            }
            return false;
        }
    }

    public bool Replace(string oldToken, string newToken)
    {
        ValidateToken(oldToken);
        ValidateToken(newToken);

        var current = GetTokens();
        var index = current.FindIndex(t => string.Equals(t, oldToken, StringComparison.Ordinal));
        if (index < 0)
            return false;

        current[index] = newToken;
        // Ensure distinctness
        var distinct = current.Distinct(StringComparer.Ordinal).ToList();
        SetTokens(distinct);
        return true;
    }
}

/// <summary>
/// Implements DOMStringMap for Element.dataset (WHATWG HTML §3.2.6.2, #452).
/// </summary>
internal sealed class LiveDomStringMap(ElementNode element)
{
    public ElementNode Element { get; } = element;

    public static string PropertyToAttribute(string name)
    {
        // Must not contain '-' followed by ASCII lowercase
        for (var i = 0; i < name.Length - 1; i++)
        {
            if (name[i] == '-' && char.IsBetween(name[i + 1], 'a', 'z'))
                throw WebIdlException.DomException(DomExceptionNames.SyntaxError, $"'{name}' is not a valid dataset property name.");
        }

        var sb = new StringBuilder("data-", name.Length + 10);
        foreach (var c in name)
        {
            if (char.IsBetween(c, 'A', 'Z'))
            {
                sb.Append('-');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    public static string AttributeToProperty(string attrName)
    {
        if (!attrName.StartsWith("data-", StringComparison.Ordinal))
            return attrName;

        var name = attrName["data-".Length..];
        var sb = new StringBuilder(name.Length);
        for (var i = 0; i < name.Length; i++)
        {
            if (name[i] == '-' && i + 1 < name.Length && char.IsBetween(name[i + 1], 'a', 'z'))
            {
                sb.Append(char.ToUpperInvariant(name[i + 1]));
                i++;
            }
            else
            {
                sb.Append(name[i]);
            }
        }
        return sb.ToString();
    }

    public string? Get(string propName)
    {
        var attrName = PropertyToAttribute(propName);
        return Element.GetAttribute(attrName);
    }

    public void Set(string propName, string value)
    {
        var attrName = PropertyToAttribute(propName);
        Element.SetAttribute(attrName, value);
    }

    public bool Delete(string propName)
    {
        var attrName = PropertyToAttribute(propName);
        return Element.RemoveAttribute(attrName);
    }

    public List<string> EnumerateProperties()
    {
        var list = new List<string>();
        var attrs = Element.Attributes;
        for (var i = 0; i < attrs.Length; i++)
        {
            var text = Element.OwnerDocument.TextOf(attrs[i].Name);
            if (text.StartsWith("data-", StringComparison.Ordinal))
            {
                list.Add(AttributeToProperty(text));
            }
        }
        return list;
    }
}

/// <summary>
/// Implements live NamedNodeMap for Element.attributes (WHATWG DOM §4.2.11, #452).
/// </summary>
internal sealed class LiveNamedNodeMap(ElementNode element)
{
    public ElementNode Element { get; } = element;

    public int Length => Element.Attributes.Length;

    public string? GetAttributeName(int index)
    {
        if (index < 0 || index >= Element.Attributes.Length)
            return null;

        return Element.OwnerDocument.TextOf(Element.Attributes[index].Name);
    }

    public List<string> EnumerateNames()
    {
        var list = new List<string>();
        var attrs = Element.Attributes;
        for (var i = 0; i < attrs.Length; i++)
        {
            list.Add(Element.OwnerDocument.TextOf(attrs[i].Name));
        }
        return list;
    }
}

/// <summary>
/// Implements Attr DOM node (WHATWG DOM §4.9, #452).
/// </summary>
internal sealed class LiveAttr(ElementNode? element, string name, string? detachedValue = null)
{
    private string _detachedValue = detachedValue ?? "";

    public ElementNode? Element { get; internal set; } = element;
    public string Name { get; } = name;
    public string LocalName => Name;

    public string Value
    {
        get => Element is not null ? (Element.GetAttribute(Name) ?? "") : _detachedValue;
        set
        {
            if (Element is not null)
                Element.SetAttribute(Name, value);
            else
                _detachedValue = value;
        }
    }

    public void Detach(string finalValue)
    {
        _detachedValue = finalValue;
        Element = null;
    }
}
