namespace Folio.Gen;

public enum IdlTypeKind
{
    Primitive,
    String,
    Enum,
    Dictionary,
    Interface,
    Sequence,
    Record,
    Union,
    Promise,
    Any,
    Object,
    CallbackFunction,
}

public sealed record IdlType(
    IdlTypeKind Kind,
    string Name,
    bool IsNullable = false,
    bool EnforceRange = false,
    bool Clamp = false,
    IReadOnlyList<IdlType>? SubTypes = null
);

public sealed record IdlConstant(
    string Name,
    IdlType Type,
    string Value
);

public sealed record IdlAttribute(
    string Name,
    IdlType Type,
    bool IsReadOnly = false,
    bool IsStatic = false,
    bool SameObject = false,
    string? ReflectedAttribute = null
);

public sealed record IdlArgument(
    string Name,
    IdlType Type,
    bool IsOptional = false,
    bool IsVariadic = false,
    string? DefaultValue = null
);

public sealed record IdlOperation(
    string Name,
    IdlType ReturnType,
    IReadOnlyList<IdlArgument> Arguments,
    bool IsStatic = false
);

public sealed record IdlIndexedGetter(IdlType ReturnType);
public sealed record IdlIndexedSetter(IdlType ValueType);
public sealed record IdlNamedGetter(IdlType ReturnType);
public sealed record IdlNamedSetter(IdlType ValueType);
public sealed record IdlNamedDeleter();

public sealed record IdlDictionaryMember(
    string Name,
    IdlType Type,
    bool IsRequired = false,
    string? DefaultValue = null
);

public sealed record IdlDictionary(
    string Name,
    string? Inherits,
    IReadOnlyList<IdlDictionaryMember> Members
);

public sealed record IdlEnum(
    string Name,
    IReadOnlyList<string> Values
);

public sealed record IdlInterface(
    string Name,
    string? Inherits,
    IReadOnlyList<IdlConstant> Constants,
    IReadOnlyList<IdlAttribute> Attributes,
    IReadOnlyList<IdlOperation> Operations,
    bool HasIterable = false,
    IdlType? IterableType = null,
    IdlIndexedGetter? IndexedGetter = null,
    IdlIndexedSetter? IndexedSetter = null,
    IdlNamedGetter? NamedGetter = null,
    IdlNamedSetter? NamedSetter = null,
    IdlNamedDeleter? NamedDeleter = null
);

public sealed record IdlDocument(
    IReadOnlyList<IdlInterface> Interfaces,
    IReadOnlyList<IdlDictionary> Dictionaries,
    IReadOnlyList<IdlEnum> Enums
);
