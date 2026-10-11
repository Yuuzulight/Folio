using System.Text;
using System.Text.RegularExpressions;

namespace Folio.Gen;

/// <summary>
/// Parser for Folio's WebIDL interface descriptions.
/// Strict parsing: fails fast on any unrecognized construct.
/// </summary>
public sealed class IdlParser
{
    private readonly string _source;
    private int _pos;

    public IdlParser(string source)
    {
        _source = source;
        _pos = 0;
    }

    public static IdlDocument Parse(string source) => new IdlParser(source).ParseDocument();

    public IdlDocument ParseDocument()
    {
        var interfaces = new List<IdlInterface>();
        var dictionaries = new List<IdlDictionary>();
        var enums = new List<IdlEnum>();

        SkipWhitespaceAndComments();
        while (_pos < _source.Length)
        {
            var word = PeekWord();
            if (word == "interface")
            {
                interfaces.Add(ParseInterface());
            }
            else if (word == "dictionary")
            {
                dictionaries.Add(ParseDictionary());
            }
            else if (word == "enum")
            {
                enums.Add(ParseEnum());
            }
            else
            {
                throw new FormatException($"Unexpected top-level construct '{word}' at index {_pos}");
            }
            SkipWhitespaceAndComments();
        }

        return new IdlDocument(interfaces, dictionaries, enums);
    }

    private IdlEnum ParseEnum()
    {
        Expect("enum");
        var name = ReadIdentifier();
        Expect("{");
        var values = new List<string>();
        while (true)
        {
            SkipWhitespaceAndComments();
            if (PeekChar() == '}')
                break;
            values.Add(ReadStringLiteral());
            SkipWhitespaceAndComments();
            if (PeekChar() == ',')
            {
                _pos++;
                SkipWhitespaceAndComments();
            }
            else
            {
                break;
            }
        }
        Expect("}");
        Expect(";");
        return new IdlEnum(name, values);
    }

    private IdlDictionary ParseDictionary()
    {
        Expect("dictionary");
        var name = ReadIdentifier();
        string? inherits = null;
        SkipWhitespaceAndComments();
        if (PeekWord() == ":")
        {
            Expect(":");
            inherits = ReadIdentifier();
        }
        Expect("{");

        var members = new List<IdlDictionaryMember>();
        SkipWhitespaceAndComments();
        while (PeekChar() != '}')
        {
            bool isRequired = false;
            if (PeekWord() == "required")
            {
                Expect("required");
                isRequired = true;
            }
            var type = ParseType();
            var memberName = ReadIdentifier();
            string? defaultValue = null;
            SkipWhitespaceAndComments();
            if (PeekChar() == '=')
            {
                Expect("=");
                defaultValue = ReadDefaultValue();
            }
            Expect(";");
            members.Add(new IdlDictionaryMember(memberName, type, isRequired, defaultValue));
            SkipWhitespaceAndComments();
        }
        Expect("}");
        Expect(";");

        return new IdlDictionary(name, inherits, members);
    }

    private IdlInterface ParseInterface()
    {
        Expect("interface");
        var name = ReadIdentifier();
        string? inherits = null;
        SkipWhitespaceAndComments();
        if (PeekWord() == ":")
        {
            Expect(":");
            inherits = ReadIdentifier();
        }
        Expect("{");

        var constants = new List<IdlConstant>();
        var attributes = new List<IdlAttribute>();
        var operations = new List<IdlOperation>();
        bool hasIterable = false;
        IdlType? iterableType = null;
        IdlIndexedGetter? indexedGetter = null;
        IdlIndexedSetter? indexedSetter = null;
        IdlNamedGetter? namedGetter = null;
        IdlNamedSetter? namedSetter = null;
        IdlNamedDeleter? namedDeleter = null;

        SkipWhitespaceAndComments();
        while (PeekChar() != '}')
        {
            var extendedAttrs = ParseExtendedAttributes();

            if (PeekWord() == "const")
            {
                Expect("const");
                var constType = ParseType();
                var constName = ReadIdentifier();
                Expect("=");
                var constVal = ReadConstantValue();
                Expect(";");
                constants.Add(new IdlConstant(constName, constType, constVal));
            }
            else if (PeekWord() == "iterable")
            {
                Expect("iterable");
                Expect("<");
                iterableType = ParseType();
                Expect(">");
                Expect(";");
                hasIterable = true;
            }
            else if (PeekWord() == "getter")
            {
                Expect("getter");
                var retType = ParseType();
                Expect("(");
                var argType = ParseType();
                var argName = ReadIdentifier();
                Expect(")");
                Expect(";");
                if (argType.Name is "unsigned long" or "long" or "int")
                    indexedGetter = new IdlIndexedGetter(retType);
                else
                    namedGetter = new IdlNamedGetter(retType);
            }
            else if (PeekWord() == "setter")
            {
                Expect("setter");
                Expect("undefined");
                Expect("(");
                var indexOrKeyType = ParseType();
                ReadIdentifier();
                Expect(",");
                var valType = ParseType();
                ReadIdentifier();
                Expect(")");
                Expect(";");
                if (indexOrKeyType.Name is "unsigned long" or "long" or "int")
                    indexedSetter = new IdlIndexedSetter(valType);
                else
                    namedSetter = new IdlNamedSetter(valType);
            }
            else if (PeekWord() == "deleter")
            {
                Expect("deleter");
                Expect("undefined");
                Expect("(");
                ParseType();
                ReadIdentifier();
                Expect(")");
                Expect(";");
                namedDeleter = new IdlNamedDeleter();
            }
            else
            {
                bool isStatic = false;
                if (PeekWord() == "static")
                {
                    Expect("static");
                    isStatic = true;
                }

                bool isReadOnly = false;
                if (PeekWord() == "readonly")
                {
                    Expect("readonly");
                    isReadOnly = true;
                }

                if (PeekWord() == "attribute")
                {
                    Expect("attribute");
                    var attrType = ParseType();
                    var attrName = ReadIdentifier();
                    Expect(";");

                    bool sameObject = extendedAttrs.ContainsKey("SameObject");
                    string? reflected = extendedAttrs.TryGetValue("Reflect", out var r) ? r : null;

                    attributes.Add(new IdlAttribute(attrName, attrType, isReadOnly, isStatic, sameObject, reflected));
                }
                else
                {
                    // Operation
                    var retType = ParseType();
                    var opName = ReadIdentifier();
                    Expect("(");
                    var args = ParseArguments();
                    Expect(")");
                    Expect(";");

                    operations.Add(new IdlOperation(opName, retType, args, isStatic));
                }
            }
            SkipWhitespaceAndComments();
        }
        Expect("}");
        Expect(";");

        return new IdlInterface(name, inherits, constants, attributes, operations, hasIterable, iterableType, indexedGetter, indexedSetter, namedGetter, namedSetter, namedDeleter);
    }

    private Dictionary<string, string?> ParseExtendedAttributes()
    {
        var dict = new Dictionary<string, string?>(StringComparer.Ordinal);
        SkipWhitespaceAndComments();
        if (PeekChar() == '[')
        {
            Expect("[");
            while (true)
            {
                var attrName = ReadIdentifier();
                string? val = null;
                SkipWhitespaceAndComments();
                if (PeekChar() == '=')
                {
                    Expect("=");
                    val = PeekChar() == '"' ? ReadStringLiteral() : ReadIdentifier();
                }
                dict[attrName] = val;
                SkipWhitespaceAndComments();
                if (PeekChar() == ',')
                {
                    Expect(",");
                    continue;
                }
                break;
            }
            Expect("]");
        }
        return dict;
    }

    private IReadOnlyList<IdlArgument> ParseArguments()
    {
        var list = new List<IdlArgument>();
        SkipWhitespaceAndComments();
        while (PeekChar() != ')')
        {
            bool isOptional = false;
            if (PeekWord() == "optional")
            {
                Expect("optional");
                isOptional = true;
            }
            var type = ParseType();
            bool isVariadic = false;
            SkipWhitespaceAndComments();
            if (PeekWord() == "...")
            {
                Expect("...");
                isVariadic = true;
            }
            var argName = ReadIdentifier();
            string? defVal = null;
            SkipWhitespaceAndComments();
            if (PeekChar() == '=')
            {
                Expect("=");
                defVal = ReadDefaultValue();
            }
            list.Add(new IdlArgument(argName, type, isOptional, isVariadic, defVal));

            SkipWhitespaceAndComments();
            if (PeekChar() == ',')
            {
                Expect(",");
                continue;
            }
            break;
        }
        return list;
    }

    private IdlType ParseType()
    {
        var extendedAttrs = ParseExtendedAttributes();
        bool enforceRange = extendedAttrs.ContainsKey("EnforceRange");
        bool clamp = extendedAttrs.ContainsKey("Clamp");

        SkipWhitespaceAndComments();
        IdlType type;
        if (PeekWord() == "sequence")
        {
            Expect("sequence");
            Expect("<");
            var itemType = ParseType();
            Expect(">");
            type = new IdlType(IdlTypeKind.Sequence, "sequence", SubTypes: [itemType]);
        }
        else if (PeekWord() == "record")
        {
            Expect("record");
            Expect("<");
            var keyType = ParseType();
            Expect(",");
            var valType = ParseType();
            Expect(">");
            type = new IdlType(IdlTypeKind.Record, "record", SubTypes: [keyType, valType]);
        }
        else if (PeekWord() == "Promise")
        {
            Expect("Promise");
            Expect("<");
            var resType = ParseType();
            Expect(">");
            type = new IdlType(IdlTypeKind.Promise, "Promise", SubTypes: [resType]);
        }
        else if (PeekChar() == '(')
        {
            // Union type: (A or B or C)
            Expect("(");
            var unionTypes = new List<IdlType>();
            while (true)
            {
                unionTypes.Add(ParseType());
                SkipWhitespaceAndComments();
                if (PeekWord() == "or")
                {
                    Expect("or");
                    continue;
                }
                break;
            }
            Expect(")");
            type = new IdlType(IdlTypeKind.Union, "union", SubTypes: unionTypes);
        }
        else
        {
            // Primitive, string, or identifier
            var typeName = ReadTypeName();
            var kind = typeName switch
            {
                "boolean" or "byte" or "octet" or "short" or "unsigned short" or "long" or "unsigned long" or
                "long long" or "unsigned long long" or "float" or "unrestricted float" or "double" or
                "unrestricted double" or "undefined" => IdlTypeKind.Primitive,
                "DOMString" or "ByteString" or "USVString" => IdlTypeKind.String,
                "any" => IdlTypeKind.Any,
                "object" => IdlTypeKind.Object,
                _ => IdlTypeKind.Interface,
            };
            type = new IdlType(kind, typeName, EnforceRange: enforceRange, Clamp: clamp);
        }

        SkipWhitespaceAndComments();
        if (PeekChar() == '?')
        {
            _pos++;
            type = type with { IsNullable = true };
        }

        return type;
    }

    private string ReadTypeName()
    {
        SkipWhitespaceAndComments();
        var words = new List<string>();
        while (_pos < _source.Length)
        {
            var w = ReadIdentifier();
            words.Add(w);
            SkipWhitespaceAndComments();
            var combined = string.Join(" ", words);
            if (combined is "unsigned short" or "unsigned long" or "long long" or "unsigned long long" or
                "unrestricted float" or "unrestricted double")
            {
                var next = PeekWord();
                if (next is "long" && combined is "unsigned" or "long")
                    continue;
                return combined;
            }

            if (combined is "unsigned" or "unrestricted")
                continue;

            return combined;
        }
        return string.Join(" ", words);
    }

    private string ReadConstantValue()
    {
        SkipWhitespaceAndComments();
        var start = _pos;
        while (_pos < _source.Length && _source[_pos] != ';')
            _pos++;
        return _source[start.._pos].Trim();
    }

    private string ReadDefaultValue()
    {
        SkipWhitespaceAndComments();
        if (PeekChar() == '"')
            return ReadStringLiteral();
        if (PeekChar() == '[')
        {
            Expect("[");
            Expect("]");
            return "[]";
        }
        if (PeekChar() == '{')
        {
            Expect("{");
            Expect("}");
            return "{}";
        }
        var start = _pos;
        while (_pos < _source.Length && _source[_pos] != ';' && _source[_pos] != ',' && _source[_pos] != ')')
            _pos++;
        return _source[start.._pos].Trim();
    }

    private string ReadStringLiteral()
    {
        Expect("\"");
        var sb = new StringBuilder();
        while (_pos < _source.Length)
        {
            var c = _source[_pos++];
            if (c == '"')
                return sb.ToString();
            if (c == '\\')
                sb.Append(_source[_pos++]);
            else
                sb.Append(c);
        }
        throw new FormatException("Unterminated string literal");
    }

    private string ReadIdentifier()
    {
        SkipWhitespaceAndComments();
        var start = _pos;
        if (_pos < _source.Length && (char.IsLetter(_source[_pos]) || _source[_pos] == '_'))
        {
            _pos++;
            while (_pos < _source.Length && (char.IsLetterOrDigit(_source[_pos]) || _source[_pos] == '_'))
                _pos++;
            return _source[start.._pos];
        }
        throw new FormatException($"Expected identifier at index {_pos}, found '{(_pos < _source.Length ? _source[_pos] : "<EOF>")}'");
    }

    private string PeekWord()
    {
        SkipWhitespaceAndComments();
        var p = _pos;
        if (p < _source.Length && (char.IsLetter(_source[p]) || _source[p] == '_'))
        {
            while (p < _source.Length && (char.IsLetterOrDigit(_source[p]) || _source[p] == '_'))
                p++;
            return _source[_pos..p];
        }
        if (p < _source.Length && _source[p] == ':')
            return ":";
        if (p + 2 < _source.Length && _source[p..(p + 3)] == "...")
            return "...";
        return "";
    }

    private char PeekChar()
    {
        SkipWhitespaceAndComments();
        return _pos < _source.Length ? _source[_pos] : '\0';
    }

    private void Expect(string expected)
    {
        SkipWhitespaceAndComments();
        if (_pos + expected.Length <= _source.Length && _source.Substring(_pos, expected.Length) == expected)
        {
            _pos += expected.Length;
            return;
        }
        throw new FormatException($"Expected '{expected}' at position {_pos}, found '{(_pos < _source.Length ? _source[_pos..Math.Min(_source.Length, _pos + expected.Length + 5)] : "<EOF>")}'");
    }

    private void SkipWhitespaceAndComments()
    {
        while (_pos < _source.Length)
        {
            if (char.IsWhiteSpace(_source[_pos]))
            {
                _pos++;
            }
            else if (_source[_pos] == '/' && _pos + 1 < _source.Length && _source[_pos + 1] == '/')
            {
                _pos += 2;
                while (_pos < _source.Length && _source[_pos] != '\n')
                    _pos++;
            }
            else if (_source[_pos] == '/' && _pos + 1 < _source.Length && _source[_pos + 1] == '*')
            {
                _pos += 2;
                while (_pos + 1 < _source.Length && !(_source[_pos] == '*' && _source[_pos + 1] == '/'))
                    _pos++;
                _pos = Math.Min(_source.Length, _pos + 2);
            }
            else
            {
                break;
            }
        }
    }
}
