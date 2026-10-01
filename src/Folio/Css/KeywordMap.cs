namespace Folio.Css;

/// <summary>
/// A keyword-to-enum map for the property table. It keeps the values as ints in one dictionary type shared by every
/// enum: a <c>Dictionary&lt;string, T&gt;</c> per enum is a separate generic instantiation the JIT compiles in full,
/// and building the table compiled one for each of them in a new process (#203).
/// </summary>
internal sealed class KeywordMap<T> where T : struct, Enum
{
    private readonly Dictionary<string, int> _values;
    private string[]? _keys;

    public KeywordMap() => _values = [];

    public KeywordMap(KeywordMap<T> other) => _values = new(other._values);

    // Property enums are int-based, so a value and its int convert both ways without boxing.
    public T this[string keyword]
    {
        get => System.Runtime.CompilerServices.Unsafe.BitCast<int, T>(_values[keyword]);
        set
        {
            _values[keyword] = System.Runtime.CompilerServices.Unsafe.BitCast<T, int>(value);
            _keys = null;
        }
    }

    /// <summary>The keywords, in the order they were added.</summary>
    public string[] Keys => _keys ??= [.. _values.Keys];

    public T GetValueOrDefault(string keyword) =>
        _values.TryGetValue(keyword, out var value) ? System.Runtime.CompilerServices.Unsafe.BitCast<int, T>(value) : default;

    /// <summary>The enum's members in declaration order, named by the keywords in that order.</summary>
    public static KeywordMap<T> InOrder(params string[] keywords)
    {
        var values = Enum.GetValuesAsUnderlyingType(typeof(T));
        var map = new KeywordMap<T>();
        for (var i = 0; i < keywords.Length; i++)
            map._values[keywords[i]] = Convert.ToInt32(values.GetValue(i));
        return map;
    }
}
