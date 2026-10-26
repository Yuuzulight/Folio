using System.Numerics;

namespace Folio.Scripting.WebGl;

/// <summary>An active uniform as the API lists it: one scalar, vector, matrix or sampler, or an array of one, in either or both stages.</summary>
internal sealed class GlUniform
{
    public required string Name { get; init; }
    public required GlslType Type { get; init; }
    public int Size { get; init; } = 1;
    public int Location { get; set; }
    public int VertexSlot { get; set; } = -1;
    public int FragmentSlot { get; set; } = -1;

    /// <summary>Slots taken by one element.</summary>
    public int ElementSlots => 1;
}

internal sealed class GlAttribute
{
    public required string Name { get; init; }
    public required GlslType Type { get; init; }
    public int Location { get; set; }
    public required int Slot { get; init; }

    /// <summary>A matrix attribute takes one location per column.</summary>
    public int Locations => Type.IsMatrix ? Type.Size : 1;
}

/// <summary>A linked program: the two compiled stages, their active attributes and uniforms, and the contexts the pipeline runs them in.</summary>
internal sealed class GlLinkedProgram
{
    public required GlslShader Vertex { get; init; }
    public required GlslShader Fragment { get; init; }
    public required ShaderContext VertexContext { get; init; }
    public required ShaderContext FragmentContext { get; init; }
    public required List<GlAttribute> Attributes { get; init; }
    public required List<GlUniform> Uniforms { get; init; }
    public required List<(int VertexSlot, int FragmentSlot)> Varyings { get; init; }
    public int UniformLocationCount { get; init; }

    private readonly Dictionary<int, (GlUniform Uniform, int Element)> _byLocation = [];

    internal void IndexLocations()
    {
        _byLocation.Clear();
        foreach (var u in Uniforms)
            for (var i = 0; i < u.Size; i++)
                _byLocation[u.Location + i] = (u, i);
    }

    public bool TryGetUniform(int location, out GlUniform uniform, out int element)
    {
        if (_byLocation.TryGetValue(location, out var entry))
        {
            (uniform, element) = entry;
            return true;
        }
        uniform = null!;
        element = 0;
        return false;
    }

    public GlUniform? FindUniform(string name, out int element)
    {
        element = 0;
        // "name", "name[0]" and "name[3]" (an element of an array) all find the uniform.
        var bracket = name.IndexOf('[', StringComparison.Ordinal);
        string baseName = name;
        if (bracket > 0 && name.EndsWith(']'))
        {
            if (!int.TryParse(name[(bracket + 1)..^1], out element) || element < 0) return null;
            baseName = name[..bracket];
        }
        foreach (var u in Uniforms)
        {
            if (u.Name == name && element == 0) return u;
            if (u.Size > 1 || u.Name.EndsWith("[0]", StringComparison.Ordinal))
            {
                if (u.Name == baseName + "[0]" && element < u.Size) return u;
            }
        }
        return null;
    }

    /// <summary>Writes one value into both stages' copy of a uniform element.</summary>
    public void WriteUniform(GlUniform uniform, int element, Val value)
    {
        if (uniform.VertexSlot >= 0) VertexContext.G[uniform.VertexSlot + element] = value;
        if (uniform.FragmentSlot >= 0) FragmentContext.G[uniform.FragmentSlot + element] = value;
    }

    public Val ReadUniform(GlUniform uniform, int element) =>
        uniform.VertexSlot >= 0 ? VertexContext.G[uniform.VertexSlot + element] : FragmentContext.G[uniform.FragmentSlot + element];

    public static (GlLinkedProgram? Program, string Log) Link(GlslShader vertex, GlslShader fragment, IReadOnlyDictionary<string, int> bound)
    {
        var vertexContext = vertex.CreateContext();
        var fragmentContext = fragment.CreateContext();

        // varyings: every fragment input needs a vertex output of the same name and type
        var varyings = new List<(int, int)>();
        foreach (var input in fragment.Variables.Where(v => v.Storage == GlslStorage.VaryingIn))
        {
            var output = vertex.Variables.FirstOrDefault(v => v.Storage == GlslStorage.VaryingOut && v.Name == input.Name);
            if (output is null) continue;
            if (!output.Type.SameAs(input.Type))
                return (null, $"ERROR: varying '{input.Name}' has different types in the vertex ({output.Type}) and fragment ({input.Type}) shaders");
            for (var i = 0; i < input.Type.Slots; i++) varyings.Add((output.Slot + i, input.Slot + i));
        }

        // uniforms: merged by name across stages, with matching types
        var uniforms = new List<GlUniform>();
        foreach (var variable in vertex.Uniforms)
            Enumerate(variable.Name, variable.Type, variable.Slot, stage: 0, uniforms);
        foreach (var variable in fragment.Uniforms)
        {
            var before = uniforms.Count;
            var leaves = new List<GlUniform>();
            Enumerate(variable.Name, variable.Type, variable.Slot, stage: 1, leaves);
            foreach (var leaf in leaves)
            {
                var same = uniforms.FirstOrDefault(u => u.Name == leaf.Name);
                if (same is null) { uniforms.Add(leaf); continue; }
                if (!same.Type.SameAs(leaf.Type) || same.Size != leaf.Size)
                    return (null, $"ERROR: uniform '{leaf.Name}' has different types in the vertex and fragment shaders");
                same.FragmentSlot = leaf.FragmentSlot;
            }
            _ = before;
        }
        var location = 0;
        foreach (var u in uniforms)
        {
            u.Location = location;
            location += u.Size;
        }

        // attributes: bound indices first, then the shader's own, then the lowest free ones
        var attributes = new List<GlAttribute>();
        foreach (var variable in vertex.Attributes)
            attributes.Add(new GlAttribute { Name = variable.Name, Type = variable.Type, Slot = variable.Slot, Location = variable.Location });
        var used = new HashSet<int>();
        foreach (var a in attributes)
            if (bound.TryGetValue(a.Name, out var index)) a.Location = index;
        foreach (var a in attributes.Where(a => a.Location >= 0))
        {
            for (var i = 0; i < a.Locations; i++)
                if (!used.Add(a.Location + i))
                    return (null, $"ERROR: two attributes are bound to location {a.Location + i}");
        }
        foreach (var a in attributes.Where(a => a.Location < 0))
        {
            var next = 0;
            for (;;)
            {
                var free = true;
                for (var i = 0; i < a.Locations; i++) if (used.Contains(next + i)) free = false;
                if (free) break;
                next++;
            }
            a.Location = next;
            for (var i = 0; i < a.Locations; i++) used.Add(next + i);
        }
        if (used.Count > 0 && used.Max() >= 16)
            return (null, "ERROR: too many vertex attributes (the limit is 16)");

        var program = new GlLinkedProgram
        {
            Vertex = vertex,
            Fragment = fragment,
            VertexContext = vertexContext,
            FragmentContext = fragmentContext,
            Attributes = attributes,
            Uniforms = uniforms,
            Varyings = varyings,
            UniformLocationCount = location,
        };
        program.IndexLocations();
        return (program, "");
    }

    private static void Enumerate(string name, GlslType type, int slot, int stage, List<GlUniform> into)
    {
        if (type.IsArray && !type.Element.IsAggregate)
        {
            into.Add(Leaf($"{name}[0]", type.Element, type.ArraySize, slot, stage));
        }
        else if (type.IsArray)
        {
            var stride = type.Element.Slots;
            for (var i = 0; i < type.ArraySize; i++) Enumerate($"{name}[{i}]", type.Element, slot + i * stride, stage, into);
        }
        else if (type.Base == GlslBase.Struct)
        {
            var offset = 0;
            foreach (var (fieldName, fieldType) in type.Struct!.Fields)
            {
                Enumerate($"{name}.{fieldName}", fieldType, slot + offset, stage, into);
                offset += fieldType.Slots;
            }
        }
        else
        {
            into.Add(Leaf(name, type, 1, slot, stage));
        }
    }

    private static GlUniform Leaf(string name, GlslType type, int size, int slot, int stage) => new()
    {
        Name = name,
        Type = type,
        Size = size,
        VertexSlot = stage == 0 ? slot : -1,
        FragmentSlot = stage == 1 ? slot : -1,
    };

    /// <summary>The GL enum that names a uniform or attribute type.</summary>
    public static uint TypeEnum(GlslType type) => type.Base switch
    {
        GlslBase.Float => 0x1406,
        GlslBase.Vec2 => 0x8B50,
        GlslBase.Vec3 => 0x8B51,
        GlslBase.Vec4 => 0x8B52,
        GlslBase.Int => 0x1404,
        GlslBase.IVec2 => 0x8B53,
        GlslBase.IVec3 => 0x8B54,
        GlslBase.IVec4 => 0x8B55,
        GlslBase.Bool => 0x8B56,
        GlslBase.BVec2 => 0x8B57,
        GlslBase.BVec3 => 0x8B58,
        GlslBase.BVec4 => 0x8B59,
        GlslBase.Mat2 => 0x8B5A,
        GlslBase.Mat3 => 0x8B5B,
        GlslBase.Mat4 => 0x8B5C,
        GlslBase.Sampler2D => 0x8B5E,
        GlslBase.SamplerCube => 0x8B60,
        GlslBase.Sampler2DShadow => 0x8B62,
        _ => 0,
    };
}
