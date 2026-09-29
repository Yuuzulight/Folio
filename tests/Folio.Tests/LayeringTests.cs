using System.Reflection;
using System.Reflection.Emit;
using Folio.Painting;

namespace Folio.Tests;

public class LayeringTests
{
    // The engine stays free of native libraries and UI frameworks (docs/architecture.md, Boundaries).
    private static readonly string[] ForbiddenPrefixes =
    [
        "SkiaSharp",
        "HarfBuzzSharp",
        "System.Windows.Forms",
        "Folio.Skia",
        "Folio.WinForms",
    ];

    [Fact]
    public void EngineReferencesNoNativeOrUiAssembly()
    {
        var referenced = typeof(ICanvas).Assembly.GetReferencedAssemblies().Select(a => a.Name!);

        var violations = referenced
            .Where(name => ForbiddenPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.Empty(violations);
    }

    // The compiler drops references the code does not use, so also catch a package added to the
    // engine project: this test project references only the engine, so its output shows the closure.
    [Fact]
    public void EngineDependencyClosureHasNoNativeOrUiAssembly()
    {
        var violations = Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll")
            .Select(Path.GetFileName)
            .Where(name => ForbiddenPrefixes.Any(prefix => name!.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.Empty(violations);
    }

    // docs/architecture.md, Boundaries: lower layers never reference higher ones. Types directly in Folio are the
    // public hosting API, so they rank with Hosting. Resources is not in the documented order; it is left
    // unchecked until the docs place it.
    private static readonly string[][] Layers =
    [
        ["Dom", "Html", "Xml"],
        ["Css", "Style"],
        ["Text", "Imaging"],
        ["Layout", "Svg"],
        ["Painting"],
        ["Interaction"],
        ["Hosting", ""],
    ];

    private static readonly string[] Unlayered = ["Resources"];

    [Fact]
    public void NamespacesReferenceOnlyTheirOwnOrLowerLayers()
    {
        var engine = typeof(ICanvas).Assembly;

        var violations = engine.GetTypes()
            .Where(type => Layer(type) >= 0)
            .SelectMany(type => ReferencedTypes(type)
                .Where(used => used.Assembly == engine && Layer(used) > Layer(type))
                .Select(used => $"{type.FullName} -> {used.FullName}"))
            .Distinct()
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void EveryEngineNamespaceHasALayer()
    {
        var unknown = typeof(ICanvas).Assembly.GetTypes()
            .Where(type => type.Namespace?.Split('.')[0] == "Folio")
            .Select(type => type.Namespace!)
            .Where(ns => Layer(ns) < 0 && !Unlayered.Contains(Segment(ns)))
            .Distinct()
            .ToList();

        Assert.Empty(unknown);
    }

    [Fact]
    public void ReferenceScanSeesMethodBodies()
    {
        // Tokenizer uses EntityTable only inside a method body.
        Assert.Contains(typeof(Folio.Html.EntityTable), ReferencedTypes(typeof(Folio.Html.Tokenizer)));
    }

    private static string Segment(string ns) => ns == "Folio" ? "" : ns.Split('.')[1];

    private static int Layer(Type type) => type.Namespace is { } ns && ns.Split('.')[0] == "Folio" ? Layer(ns) : -1;

    private static int Layer(string ns) => Array.FindIndex(Layers, layer => layer.Contains(Segment(ns)));

    // Types named in the signatures and method bodies of one type (nested types are scanned on their own).
    internal static HashSet<Type> ReferencedTypes(Type type)
    {
        var found = new HashSet<Type>();
        void Add(Type? t)
        {
            while (t is not null && t.HasElementType)
                t = t.GetElementType();
            if (t is null || t.IsGenericParameter || !found.Add(t.IsGenericType ? t.GetGenericTypeDefinition() : t))
                return;
            if (t.IsGenericType)
            {
                foreach (var argument in t.GetGenericArguments())
                    Add(argument);
            }
        }

        const BindingFlags All = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static |
                                 BindingFlags.Public | BindingFlags.NonPublic;
        Add(type.BaseType);
        foreach (var i in type.GetInterfaces())
            Add(i);
        foreach (var field in type.GetFields(All))
            Add(field.FieldType);

        foreach (var method in type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All)))
        {
            if (method is MethodInfo info)
                Add(info.ReturnType);
            foreach (var parameter in method.GetParameters())
                Add(parameter.ParameterType);

            var body = method.GetMethodBody();
            if (body is null)
                continue;
            foreach (var local in body.LocalVariables)
                Add(local.LocalType);

            var typeArgs = type.IsGenericType ? type.GetGenericArguments() : null;
            var methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
            foreach (var token in MemberTokens(body.GetILAsByteArray()!))
            {
                switch (type.Module.ResolveMember(token, typeArgs, methodArgs))
                {
                    case Type t:
                        Add(t);
                        break;
                    case FieldInfo f:
                        Add(f.DeclaringType);
                        Add(f.FieldType);
                        break;
                    case MethodBase m:
                        Add(m.DeclaringType);
                        if (m is MethodInfo mi)
                            Add(mi.ReturnType);
                        foreach (var parameter in m.GetParameters())
                            Add(parameter.ParameterType);
                        break;
                }
            }
        }
        return found;
    }

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    // Metadata tokens of the field, method and type operands in an IL stream.
    private static IEnumerable<int> MemberTokens(byte[] il)
    {
        for (var i = 0; i < il.Length;)
        {
            var value = il[i] == 0xFE ? unchecked((short)(0xFE00 | il[i + 1])) : il[i];
            i += il[i] == 0xFE ? 2 : 1;
            var op = OpCodesByValue[value];
            switch (op.OperandType)
            {
                case OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineTok or OperandType.InlineType:
                    yield return BitConverter.ToInt32(il, i);
                    i += 4;
                    break;
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar:
                    i += 1;
                    break;
                case OperandType.InlineVar:
                    i += 2;
                    break;
                case OperandType.InlineI8 or OperandType.InlineR:
                    i += 8;
                    break;
                case OperandType.InlineSwitch:
                    i += 4 + 4 * BitConverter.ToInt32(il, i);
                    break;
                default: // InlineI, InlineBrTarget, InlineSig, InlineString, ShortInlineR
                    i += 4;
                    break;
            }
        }
    }
}
