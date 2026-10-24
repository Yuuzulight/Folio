using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Folio.Scripting.WebGl;

internal sealed class GlFixedState
{
    public bool DepthTest, Blend, CullFace, StencilTest, ScissorTest;
    public uint DepthFunc = 0x0201;
    public bool DepthMask = true;
    public float DepthNear = 0, DepthFar = 1;
    public uint BlendSrcRgb = 1, BlendDstRgb = 0, BlendSrcAlpha = 1, BlendDstAlpha = 0, BlendEquationRgb = 0x8006, BlendEquationAlpha = 0x8006;
    public Vector4 BlendColor;
    public bool MaskR = true, MaskG = true, MaskB = true, MaskA = true;
    public uint CullMode = 0x0405, FrontFace = 0x0901;
    public int ViewportX, ViewportY, ViewportWidth, ViewportHeight;
    public int ScissorX, ScissorY, ScissorWidth, ScissorHeight;
    public uint StencilFunc = 0x0207, StencilFail = 0x1E00, StencilDepthFail = 0x1E00, StencilDepthPass = 0x1E00;
    public int StencilRef;
    public uint StencilReadMask = 0xFFFFFFFF, StencilWriteMask = 0xFFFFFFFF;
    public bool PremultipliedAlpha = true;
    public float PolygonOffsetFactor, PolygonOffsetUnits;
    public bool PolygonOffsetFill;
}

/// <summary>One vertex attribute as the draw sees it: a bound array (or its generic value when not enabled).</summary>
internal sealed class GlAttributeSource
{
    public bool Enabled;
    public int Size = 4;
    public uint Type = 0x1406;
    public bool Normalized;
    public int Stride;
    public int Offset;
    public byte[]? Buffer;
    public int Divisor;
    public Vector4 Generic = new(0, 0, 0, 1);
}

internal sealed class GlDrawCall
{
    public required GlLinkedProgram Program { get; init; }
    public required GlAttributeSource[] Attributes { get; init; }
    public required uint Mode { get; init; }
    public required int Count { get; init; }
    public required Func<int, int> IndexOf { get; init; }
    public int MaxIndex { get; init; }
    public int InstanceCount { get; init; } = 1;
    public required GlRenderTarget Target { get; init; }
    public required GlFixedState State { get; init; }
    public IShaderSampler? Sampler { get; init; }
    public long Deadline { get; init; } = long.MaxValue;
}

internal enum GlDrawStatus { Ok, InvalidOperation, Timeout }

/// <summary>
/// The software pipeline behind <c>drawArrays</c> and <c>drawElements</c>: vertex fetch, the vertex shader, primitive assembly and clipping,
/// the viewport transform, rasterisation (four samples a pixel for a multisampled target) with perspective-correct varyings, the fragment
/// shader, then scissor, stencil, depth, blending and the colour mask.
/// </summary>
internal sealed class GlPipeline
{
    private struct Vertex
    {
        public Vector4 Clip;
        public float PointSize;
        public Val[] Varyings;
        public Vector2 PointCoord;
    }

    private struct Screen
    {
        public float X, Y, Z, InvW;
        public Val[] Varyings;
        public Vector2 PointCoord;
    }

    private readonly GlDrawCall _call;
    private readonly GlLinkedProgram _program;
    private readonly GlFixedState _state;
    private readonly GlRenderTarget _target;
    private readonly ShaderContext _vertexContext;
    private readonly ShaderContext _fragmentContext;
    private readonly int _varyingCount;
    private readonly bool _wantDerivatives;
    private readonly int _outputSlot;
    private readonly Vector4[] _derivX;
    private readonly Vector4[] _derivY;
    private readonly Val[] _scratchVaryings;

    private GlPipeline(GlDrawCall call)
    {
        _call = call;
        _program = call.Program;
        _state = call.State;
        _target = call.Target;
        _vertexContext = _program.VertexContext;
        _fragmentContext = _program.FragmentContext;
        _varyingCount = _program.Varyings.Count;
        _wantDerivatives = _program.Fragment.UsesDerivatives || _program.Uniforms.Any(u => u.Type.IsSampler && u.FragmentSlot >= 0);
        _outputSlot = _program.Fragment.FragOutputSlots.Length > 0 ? _program.Fragment.FragOutputSlots[0] : -1;
        _derivX = new Vector4[_program.Fragment.GlobalSlots];
        _derivY = new Vector4[_program.Fragment.GlobalSlots];
        _scratchVaryings = new Val[_varyingCount];
        _vertexContext.Deadline = call.Deadline;
        _fragmentContext.Deadline = call.Deadline;
        _fragmentContext.Sampler = call.Sampler;
        _fragmentContext.DerivX = _derivX;
        _fragmentContext.DerivY = _derivY;
    }

    public static GlDrawStatus Draw(GlDrawCall call)
    {
        try
        {
            return new GlPipeline(call).Run();
        }
        catch (ShaderTimeoutException)
        {
            return GlDrawStatus.Timeout;
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or OverflowException or InvalidOperationException or NullReferenceException)
        {
            // A shader that gets the interpreter to misbehave is a bad draw, never a failure of the page that issued it.
            return GlDrawStatus.InvalidOperation;
        }
    }

    // ---- vertex processing ----

    private GlDrawStatus Run()
    {
        var mode = _call.Mode;
        var count = _call.Count;
        var vertices = new Dictionary<int, Vertex>();

        Vertex VertexAt(int position, int instance)
        {
            var index = _call.IndexOf(position);
            var key = _call.InstanceCount > 1 ? index * 4096 + instance : index;
            if (vertices.TryGetValue(key, out var cached)) return cached;
            var v = RunVertexShader(index, instance);
            vertices[key] = v;
            return v;
        }

        for (var instance = 0; instance < _call.InstanceCount; instance++)
        {
            if (_call.InstanceCount > 1) vertices.Clear();
            switch (mode)
            {
                case 0x0004: // TRIANGLES
                    for (var i = 0; i + 2 < count; i += 3)
                        Triangle(VertexAt(i, instance), VertexAt(i + 1, instance), VertexAt(i + 2, instance));
                    break;
                case 0x0005: // TRIANGLE_STRIP
                    for (var i = 0; i + 2 < count; i++)
                    {
                        if ((i & 1) == 0) Triangle(VertexAt(i, instance), VertexAt(i + 1, instance), VertexAt(i + 2, instance));
                        else Triangle(VertexAt(i + 1, instance), VertexAt(i, instance), VertexAt(i + 2, instance));
                    }
                    break;
                case 0x0006: // TRIANGLE_FAN
                    for (var i = 1; i + 1 < count; i++)
                        Triangle(VertexAt(0, instance), VertexAt(i, instance), VertexAt(i + 1, instance));
                    break;
                case 0x0001: // LINES
                    for (var i = 0; i + 1 < count; i += 2)
                        Line(VertexAt(i, instance), VertexAt(i + 1, instance));
                    break;
                case 0x0003: // LINE_STRIP
                    for (var i = 0; i + 1 < count; i++)
                        Line(VertexAt(i, instance), VertexAt(i + 1, instance));
                    break;
                case 0x0002: // LINE_LOOP
                    for (var i = 0; i + 1 < count; i++)
                        Line(VertexAt(i, instance), VertexAt(i + 1, instance));
                    if (count > 2) Line(VertexAt(count - 1, instance), VertexAt(0, instance));
                    break;
                case 0x0000: // POINTS
                    for (var i = 0; i < count; i++)
                        Point(VertexAt(i, instance));
                    break;
            }
        }
        return GlDrawStatus.Ok;
    }

    private Vertex RunVertexShader(int index, int instance)
    {
        var shader = _program.Vertex;
        var context = _vertexContext;
        foreach (var attribute in _program.Attributes)
        {
            var type = attribute.Type;
            if (type.IsMatrix)
            {
                var value = new Val();
                for (var column = 0; column < type.Size; column++)
                    value.SetColumn(column, Fetch(attribute.Location + column, index, instance));
                context.G[attribute.Slot] = value;
            }
            else
            {
                var v = Fetch(attribute.Location, index, instance);
                if (type.ScalarKind == GlslBase.Int) v = new Vector4(MathF.Truncate(v.X), MathF.Truncate(v.Y), MathF.Truncate(v.Z), MathF.Truncate(v.W));
                context.G[attribute.Slot] = new Val(v);
            }
        }
        if (shader.VertexIdSlot >= 0) context.G[shader.VertexIdSlot] = new Val(index);
        if (shader.InstanceIdSlot >= 0) context.G[shader.InstanceIdSlot] = new Val(instance);
        context.G[shader.PositionSlot] = default;
        context.G[shader.PointSizeSlot] = new Val(1);
        shader.Main(context);
        var vertex = new Vertex
        {
            Clip = context.G[shader.PositionSlot].C0,
            PointSize = context.G[shader.PointSizeSlot].X,
            Varyings = new Val[_varyingCount],
        };
        for (var i = 0; i < _varyingCount; i++) vertex.Varyings[i] = context.G[_program.Varyings[i].VertexSlot];
        return vertex;
    }

    private Vector4 Fetch(int location, int vertexIndex, int instance)
    {
        if (location < 0 || location >= _call.Attributes.Length) return new Vector4(0, 0, 0, 1);
        var source = _call.Attributes[location];
        if (!source.Enabled || source.Buffer is null) return source.Generic;
        var element = source.Divisor == 0 ? vertexIndex : instance / source.Divisor;
        var componentBytes = source.Type switch { 0x1400 or 0x1401 => 1, 0x1402 or 0x1403 => 2, _ => 4 };
        var stride = source.Stride > 0 ? source.Stride : source.Size * componentBytes;
        var at = (long)element * stride + source.Offset;
        var v = new Vector4(0, 0, 0, 1);
        for (var c = 0; c < source.Size; c++)
        {
            var offset = at + c * componentBytes;
            if (offset < 0 || offset + componentBytes > source.Buffer.Length) break;
            var span = source.Buffer.AsSpan((int)offset, componentBytes);
            float x = source.Type switch
            {
                0x1406 => BitConverter.ToSingle(span),
                0x1400 => source.Normalized ? Math.Max((sbyte)span[0] / 127f, -1f) : (sbyte)span[0],
                0x1401 => source.Normalized ? span[0] / 255f : span[0],
                0x1402 => source.Normalized ? Math.Max(BinaryPrimitives.ReadInt16LittleEndian(span) / 32767f, -1f) : BinaryPrimitives.ReadInt16LittleEndian(span),
                0x1403 => source.Normalized ? BinaryPrimitives.ReadUInt16LittleEndian(span) / 65535f : BinaryPrimitives.ReadUInt16LittleEndian(span),
                0x1404 => source.Normalized ? Math.Max(BinaryPrimitives.ReadInt32LittleEndian(span) / 2147483647f, -1f) : BinaryPrimitives.ReadInt32LittleEndian(span),
                0x1405 => source.Normalized ? BinaryPrimitives.ReadUInt32LittleEndian(span) / 4294967295f : BinaryPrimitives.ReadUInt32LittleEndian(span),
                _ => 0f,
            };
            switch (c)
            {
                case 0: v.X = x; break;
                case 1: v.Y = x; break;
                case 2: v.Z = x; break;
                default: v.W = x; break;
            }
        }
        return v;
    }

    // ---- primitive assembly ----

    private Screen ToScreen(Vector4 clip, Val[] varyings, Vector2 pointCoord = default)
    {
        var invW = 1f / clip.W;
        var ndcX = clip.X * invW;
        var ndcY = clip.Y * invW;
        var ndcZ = clip.Z * invW;
        return new Screen
        {
            X = _state.ViewportX + (ndcX + 1) * 0.5f * _state.ViewportWidth,
            Y = _state.ViewportY + (ndcY + 1) * 0.5f * _state.ViewportHeight,
            Z = _state.DepthNear + (ndcZ + 1) * 0.5f * (_state.DepthFar - _state.DepthNear),
            InvW = invW,
            Varyings = varyings,
            PointCoord = pointCoord,
        };
    }

    private static bool Inside(Vector4 c, int plane) => plane switch
    {
        0 => c.X >= -c.W,
        1 => c.X <= c.W,
        2 => c.Y >= -c.W,
        3 => c.Y <= c.W,
        4 => c.Z >= -c.W,
        _ => c.Z <= c.W,
    };

    private static float Distance(Vector4 c, int plane) => plane switch
    {
        0 => c.X + c.W,
        1 => c.W - c.X,
        2 => c.Y + c.W,
        3 => c.W - c.Y,
        4 => c.Z + c.W,
        _ => c.W - c.Z,
    };

    private static Vertex Lerp(Vertex a, Vertex b, float t)
    {
        var varyings = new Val[a.Varyings.Length];
        for (var i = 0; i < varyings.Length; i++)
        {
            var x = a.Varyings[i]; var y = b.Varyings[i];
            varyings[i] = new Val
            {
                C0 = Vector4.Lerp(x.C0, y.C0, t),
                C1 = Vector4.Lerp(x.C1, y.C1, t),
                C2 = Vector4.Lerp(x.C2, y.C2, t),
                C3 = Vector4.Lerp(x.C3, y.C3, t),
            };
        }
        return new Vertex
        {
            Clip = Vector4.Lerp(a.Clip, b.Clip, t),
            PointSize = a.PointSize + (b.PointSize - a.PointSize) * t,
            Varyings = varyings,
            PointCoord = Vector2.Lerp(a.PointCoord, b.PointCoord, t),
        };
    }

    // Sutherland-Hodgman against the six clip planes.
    private static List<Vertex> ClipPolygon(List<Vertex> polygon)
    {
        for (var plane = 0; plane < 6 && polygon.Count > 0; plane++)
        {
            var next = new List<Vertex>(polygon.Count + 2);
            for (var i = 0; i < polygon.Count; i++)
            {
                var current = polygon[i];
                var previous = polygon[(i + polygon.Count - 1) % polygon.Count];
                var currentInside = Inside(current.Clip, plane);
                var previousInside = Inside(previous.Clip, plane);
                if (currentInside != previousInside)
                {
                    var dp = Distance(previous.Clip, plane);
                    var dc = Distance(current.Clip, plane);
                    next.Add(Lerp(previous, current, dp / (dp - dc)));
                }
                if (currentInside) next.Add(current);
            }
            polygon = next;
        }
        return polygon;
    }

    private static bool OutsideSamePlane(Vertex a, Vertex b, Vertex c)
    {
        for (var plane = 0; plane < 6; plane++)
            if (!Inside(a.Clip, plane) && !Inside(b.Clip, plane) && !Inside(c.Clip, plane)) return true;
        return false;
    }

    private static bool AllInside(Vertex a, Vertex b, Vertex c)
    {
        for (var plane = 0; plane < 6; plane++)
            if (!Inside(a.Clip, plane) || !Inside(b.Clip, plane) || !Inside(c.Clip, plane)) return false;
        return true;
    }

    private void Triangle(Vertex a, Vertex b, Vertex c)
    {
        if (float.IsNaN(a.Clip.X + b.Clip.X + c.Clip.X + a.Clip.W + b.Clip.W + c.Clip.W)) return;
        if (OutsideSamePlane(a, b, c)) return;
        if (AllInside(a, b, c))
        {
            Fill(ToScreen(a.Clip, a.Varyings), ToScreen(b.Clip, b.Varyings), ToScreen(c.Clip, c.Varyings));
            return;
        }
        var polygon = ClipPolygon([a, b, c]);
        for (var i = 1; i + 1 < polygon.Count; i++)
        {
            var p0 = polygon[0]; var p1 = polygon[i]; var p2 = polygon[i + 1];
            if (p0.Clip.W <= 0 || p1.Clip.W <= 0 || p2.Clip.W <= 0) continue;
            Fill(ToScreen(p0.Clip, p0.Varyings), ToScreen(p1.Clip, p1.Varyings), ToScreen(p2.Clip, p2.Varyings));
        }
    }

    private void Line(Vertex a, Vertex b)
    {
        // A one pixel wide line is a quad along it; each end keeps its own varyings.
        if (a.Clip.W <= 0 || b.Clip.W <= 0)
        {
            if (a.Clip.W <= 0 && b.Clip.W <= 0) return;
            // Clip against the near plane only: move the bad end to w = epsilon along the segment.
            const float epsilon = 1e-4f;
            if (a.Clip.W <= epsilon) a = Lerp(a, b, (epsilon - a.Clip.W) / (b.Clip.W - a.Clip.W));
            else b = Lerp(b, a, (epsilon - b.Clip.W) / (a.Clip.W - b.Clip.W));
        }
        var sa = ToScreen(a.Clip, a.Varyings);
        var sb = ToScreen(b.Clip, b.Varyings);
        var direction = new Vector2(sb.X - sa.X, sb.Y - sa.Y);
        if (direction.LengthSquared() < 1e-12f) return;
        direction = Vector2.Normalize(direction);
        var normal = new Vector2(-direction.Y, direction.X) * 0.5f;
        Screen Corner(Screen s, float sign)
        {
            var corner = s;
            corner.X += normal.X * sign;
            corner.Y += normal.Y * sign;
            return corner;
        }
        var a0 = Corner(sa, 1); var a1 = Corner(sa, -1); var b0 = Corner(sb, 1); var b1 = Corner(sb, -1);
        // Lines are never culled.
        FillQuad(a0, a1, b1, b0);
    }

    private void Point(Vertex v)
    {
        if (v.Clip.W <= 0) return;
        if (Math.Abs(v.Clip.X) > Math.Abs(v.Clip.W) * 2 || Math.Abs(v.Clip.Y) > Math.Abs(v.Clip.W) * 2) return;
        var s = ToScreen(v.Clip, v.Varyings);
        var half = Math.Clamp(v.PointSize, 1, 1024) * 0.5f;
        Screen Corner(float dx, float dy, float u, float t)
        {
            var corner = s;
            corner.X += dx * half;
            corner.Y += dy * half;
            corner.PointCoord = new Vector2(u, t);
            return corner;
        }
        // gl_PointCoord has (0, 0) at the top left of the point.
        FillQuad(Corner(-1, -1, 0, 1), Corner(1, -1, 1, 1), Corner(1, 1, 1, 0), Corner(-1, 1, 0, 0));
    }

    private void FillQuad(Screen a, Screen b, Screen c, Screen d)
    {
        FillTriangle(a, b, c, front: true, cullable: false);
        FillTriangle(a, c, d, front: true, cullable: false);
    }

    private void Fill(Screen a, Screen b, Screen c) => FillTriangle(a, b, c, front: true, cullable: true);

    // ---- rasterisation ----

    private static readonly (float X, float Y)[] SamplePattern = [(0.375f, 0.125f), (0.875f, 0.375f), (0.125f, 0.625f), (0.625f, 0.875f)];

    private void FillTriangle(Screen a, Screen b, Screen c, bool front, bool cullable)
    {
        var area = (b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y);
        if (area == 0 || float.IsNaN(area)) return;
        var ccw = area > 0;
        if (cullable)
        {
            front = ccw == (_state.FrontFace == 0x0901);
            if (_state.CullFace)
            {
                var mode = _state.CullMode;
                if (mode == 0x0408 || (mode == 0x0404 && front) || (mode == 0x0405 && !front)) return;
            }
        }
        // Work with a counter-clockwise triangle.
        if (!ccw) (b, c) = (c, b);
        area = Math.Abs(area);

        var target = _target;
        var samples = target.Samples;
        var minX = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))));
        var maxX = Math.Min(target.Width - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
        var minY = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))));
        var maxY = Math.Min(target.Height - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
        var st = _state;
        var viewportRight = st.ViewportX + st.ViewportWidth;
        var viewportTop = st.ViewportY + st.ViewportHeight;
        minX = Math.Max(minX, Math.Max(0, st.ViewportX));
        minY = Math.Max(minY, Math.Max(0, st.ViewportY));
        maxX = Math.Min(maxX, viewportRight - 1);
        maxY = Math.Min(maxY, viewportTop - 1);
        if (st.ScissorTest)
        {
            minX = Math.Max(minX, st.ScissorX);
            minY = Math.Max(minY, st.ScissorY);
            maxX = Math.Min(maxX, st.ScissorX + st.ScissorWidth - 1);
            maxY = Math.Min(maxY, st.ScissorY + st.ScissorHeight - 1);
        }
        if (minX > maxX || minY > maxY) return;

        // edge functions: e_bc is the weight of a, e_ca of b, e_ab of c
        static (float A, float B, float C0) Edge(Screen p, Screen q) => (-(q.Y - p.Y), q.X - p.X, (q.Y - p.Y) * p.X - (q.X - p.X) * p.Y);
        // e(x, y) = (q.x-p.x)*(y-p.y) - (q.y-p.y)*(x-p.x) = A*x + B*y + C0
        var eAb = Edge(a, b); var eBc = Edge(b, c); var eCa = Edge(c, a);
        static bool TopLeft(Screen p, Screen q) => q.Y - p.Y < 0 || (q.Y - p.Y == 0 && q.X - p.X < 0);
        var tlAb = TopLeft(a, b); var tlBc = TopLeft(b, c); var tlCa = TopLeft(c, a);
        var invArea = 1f / area;

        var depthSpan = target.DepthBytes is null ? default : MemoryMarshal.Cast<byte, float>(target.DepthBytes.AsSpan());
        var program = _program;
        var fragment = program.Fragment;
        var fctx = _fragmentContext;
        var tickCounter = 0;

        // Polygon offset: factor times the largest depth slope plus units times the smallest resolvable depth step of a 24-bit buffer.
        var depthOffset = 0f;
        if (_state.PolygonOffsetFill)
        {
            var dzdx = (eBc.A * a.Z + eCa.A * b.Z + (-eBc.A - eCa.A) * c.Z) * invArea;
            var dzdy = (eBc.B * a.Z + eCa.B * b.Z + (-eBc.B - eCa.B) * c.Z) * invArea;
            depthOffset = _state.PolygonOffsetFactor * MathF.Max(MathF.Abs(dzdx), MathF.Abs(dzdy)) + _state.PolygonOffsetUnits * (1f / 16777216f);
        }

        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                // coverage
                uint mask = 0;
                var covered = 0;
                if (samples == 1)
                {
                    var px = x + 0.5f; var py = y + 0.5f;
                    var e0 = eBc.A * px + eBc.B * py + eBc.C0;
                    var e1 = eCa.A * px + eCa.B * py + eCa.C0;
                    var e2 = eAb.A * px + eAb.B * py + eAb.C0;
                    if ((e0 > 0 || (e0 == 0 && tlBc)) && (e1 > 0 || (e1 == 0 && tlCa)) && (e2 > 0 || (e2 == 0 && tlAb))) { mask = 1; covered = 1; }
                }
                else
                {
                    for (var s = 0; s < samples; s++)
                    {
                        var px = x + SamplePattern[s].X; var py = y + SamplePattern[s].Y;
                        var e0 = eBc.A * px + eBc.B * py + eBc.C0;
                        var e1 = eCa.A * px + eCa.B * py + eCa.C0;
                        var e2 = eAb.A * px + eAb.B * py + eAb.C0;
                        if ((e0 > 0 || (e0 == 0 && tlBc)) && (e1 > 0 || (e1 == 0 && tlCa)) && (e2 > 0 || (e2 == 0 && tlAb))) { mask |= 1u << s; covered++; }
                    }
                }
                if (covered == 0) continue;
                if ((++tickCounter & 0x3FF) == 0 && System.Diagnostics.Stopwatch.GetTimestamp() > _call.Deadline) throw new ShaderTimeoutException();

                // shading position: the pixel centre (extrapolated when the centre is outside the triangle)
                var cx = x + 0.5f; var cy = y + 0.5f;
                var wa = (eBc.A * cx + eBc.B * cy + eBc.C0) * invArea;
                var wb = (eCa.A * cx + eCa.B * cy + eCa.C0) * invArea;
                var wc = 1 - wa - wb;
                var wsum = wa * a.InvW + wb * b.InvW + wc * c.InvW;
                if (wsum <= 1e-12f) wsum = 1e-12f;
                var pa = wa * a.InvW / wsum; var pb = wb * b.InvW / wsum; var pc = wc * c.InvW / wsum;

                var z = wa * a.Z + wb * b.Z + wc * c.Z;
                for (var i = 0; i < _varyingCount; i++)
                {
                    var va = a.Varyings[i]; var vb = b.Varyings[i]; var vc = c.Varyings[i];
                    var interpolated = new Val
                    {
                        C0 = va.C0 * pa + vb.C0 * pb + vc.C0 * pc,
                        C1 = va.C1 * pa + vb.C1 * pb + vc.C1 * pc,
                        C2 = va.C2 * pa + vb.C2 * pb + vc.C2 * pc,
                        C3 = va.C3 * pa + vb.C3 * pb + vc.C3 * pc,
                    };
                    fctx.G[program.Varyings[i].FragmentSlot] = interpolated;
                }
                if (_wantDerivatives)
                {
                    // d/dx and d/dy of the perspective-correct varyings from the barycentric gradients.
                    var dwa_dx = eBc.A * invArea; var dwa_dy = eBc.B * invArea;
                    var dwb_dx = eCa.A * invArea; var dwb_dy = eCa.B * invArea;
                    var dwc_dx = -dwa_dx - dwb_dx; var dwc_dy = -dwa_dy - dwb_dy;
                    var dD_dx = dwa_dx * a.InvW + dwb_dx * b.InvW + dwc_dx * c.InvW;
                    var dD_dy = dwa_dy * a.InvW + dwb_dy * b.InvW + dwc_dy * c.InvW;
                    for (var i = 0; i < _varyingCount; i++)
                    {
                        var va = a.Varyings[i].C0 * a.InvW; var vb = b.Varyings[i].C0 * b.InvW; var vc = c.Varyings[i].C0 * c.InvW;
                        var n = va * wa + vb * wb + vc * wc;
                        var nx = va * dwa_dx + vb * dwb_dx + vc * dwc_dx;
                        var ny = va * dwa_dy + vb * dwb_dy + vc * dwc_dy;
                        var slot = program.Varyings[i].FragmentSlot;
                        // d(n/D) = (dn*D - n*dD) / D^2
                        _derivX[slot] = (nx * wsum - n * dD_dx) / (wsum * wsum);
                        _derivY[slot] = (ny * wsum - n * dD_dy) / (wsum * wsum);
                    }
                }

                if (fragment.FragCoordSlot >= 0) fctx.G[fragment.FragCoordSlot] = new Val(cx, cy, z, wsum);
                if (fragment.FrontFacingSlot >= 0) fctx.G[fragment.FrontFacingSlot] = new Val(front ? 1 : 0);
                if (fragment.PointCoordSlot >= 0)
                {
                    var pcoord = a.PointCoord * pa + b.PointCoord * pb + c.PointCoord * pc;
                    fctx.G[fragment.PointCoordSlot] = new Val(pcoord.X, pcoord.Y, 0, 0);
                }
                if (fragment.FragDepthSlot >= 0) fctx.G[fragment.FragDepthSlot] = new Val(float.NaN);
                if (_outputSlot >= 0) fctx.G[_outputSlot] = new Val(0, 0, 0, 1);
                fctx.Discarded = false;
                fragment.Main(fctx);
                if (fctx.Discarded) continue;
                var color = _outputSlot >= 0 ? fctx.G[_outputSlot].C0 : Vector4.Zero;
                var shaderDepth = fragment.FragDepthSlot >= 0 ? fctx.G[fragment.FragDepthSlot].X : float.NaN;

                // per-sample tests and writes
                for (var s = 0; s < samples; s++)
                {
                    if ((mask & (1u << s)) == 0) continue;
                    var index = (y * target.Width + x) * samples + s;
                    float sampleZ;
                    if (!float.IsNaN(shaderDepth)) sampleZ = Math.Clamp(shaderDepth, 0, 1);
                    else
                    {
                        float sx, sy;
                        if (samples == 1) { sx = cx; sy = cy; } else { sx = x + SamplePattern[s].X; sy = y + SamplePattern[s].Y; }
                        var sa = (eBc.A * sx + eBc.B * sy + eBc.C0) * invArea;
                        var sb = (eCa.A * sx + eCa.B * sy + eCa.C0) * invArea;
                        sampleZ = Math.Clamp(sa * a.Z + sb * b.Z + (1 - sa - sb) * c.Z + depthOffset, 0, 1);
                    }
                    WriteSample(target, depthSpan, index, color, sampleZ, front);
                }
            }
        }
    }

    private static bool DepthPasses(uint func, float incoming, float stored) => func switch
    {
        0x0200 => false,
        0x0201 => incoming < stored,
        0x0202 => incoming == stored,
        0x0203 => incoming <= stored,
        0x0204 => incoming > stored,
        0x0205 => incoming != stored,
        0x0206 => incoming >= stored,
        _ => true,
    };

    private static bool StencilPasses(uint func, int reference, int stored) => func switch
    {
        0x0200 => false,
        0x0201 => reference < stored,
        0x0202 => reference == stored,
        0x0203 => reference <= stored,
        0x0204 => reference > stored,
        0x0205 => reference != stored,
        0x0206 => reference >= stored,
        _ => true,
    };

    private static byte ApplyStencilOp(uint op, byte stored, int reference) => op switch
    {
        0x1E00 => stored,
        0x0000 => 0,
        0x1E01 => (byte)reference,
        0x1E02 => (byte)Math.Min(255, stored + 1),
        0x1E03 => (byte)Math.Max(0, stored - 1),
        0x150A => (byte)~stored,
        0x8507 => (byte)(stored + 1),
        0x8508 => (byte)(stored - 1),
        _ => stored,
    };

    private void WriteSample(GlRenderTarget target, Span<float> depth, int index, Vector4 color, float z, bool front)
    {
        var st = _state;
        if (st.StencilTest && target.Stencil is not null)
        {
            var stored = target.Stencil[index];
            var pass = StencilPasses(st.StencilFunc, (int)(st.StencilRef & st.StencilReadMask), (int)(stored & st.StencilReadMask));
            if (!pass)
            {
                target.Stencil[index] = Masked(stored, ApplyStencilOp(st.StencilFail, stored, st.StencilRef), st.StencilWriteMask);
                return;
            }
            var depthOk = !st.DepthTest || depth.Length == 0 || DepthPasses(st.DepthFunc, z, depth[index]);
            var op = depthOk ? st.StencilDepthPass : st.StencilDepthFail;
            target.Stencil[index] = Masked(stored, ApplyStencilOp(op, stored, st.StencilRef), st.StencilWriteMask);
            if (!depthOk) return;
        }
        else if (st.DepthTest && depth.Length > 0 && !DepthPasses(st.DepthFunc, z, depth[index]))
        {
            return;
        }
        if (st.DepthMask && depth.Length > 0) depth[index] = z;

        var o = index * 4;
        var dst = new Vector4(target.Color[o], target.Color[o + 1], target.Color[o + 2], target.HasAlpha ? target.Color[o + 3] : 255) / 255f;
        var src = Vector4.Clamp(color, Vector4.Zero, Vector4.One);
        Vector4 result;
        if (st.Blend)
        {
            var sf = Factor(st.BlendSrcRgb, src, dst, st.BlendColor);
            var df = Factor(st.BlendDstRgb, src, dst, st.BlendColor);
            var saf = Factor(st.BlendSrcAlpha, src, dst, st.BlendColor);
            var daf = Factor(st.BlendDstAlpha, src, dst, st.BlendColor);
            var rgb = Combine(st.BlendEquationRgb, src * sf, dst * df);
            var alpha = Combine(st.BlendEquationAlpha, src * saf, dst * daf);
            result = new Vector4(rgb.X, rgb.Y, rgb.Z, alpha.W);
            result = Vector4.Clamp(result, Vector4.Zero, Vector4.One);
        }
        else
        {
            result = src;
        }
        if (st.MaskR) target.Color[o] = ToByte(result.X);
        if (st.MaskG) target.Color[o + 1] = ToByte(result.Y);
        if (st.MaskB) target.Color[o + 2] = ToByte(result.Z);
        if (st.MaskA) target.Color[o + 3] = target.HasAlpha ? ToByte(result.W) : (byte)255;
    }

    private static byte Masked(byte old, byte value, uint mask) => (byte)((old & ~mask) | (value & mask));

    private static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);

    private static Vector4 Combine(uint equation, Vector4 s, Vector4 d) => equation switch
    {
        0x800A => s - d,
        0x800B => d - s,
        0x8007 => Vector4.Min(s, d),
        0x8008 => Vector4.Max(s, d),
        _ => s + d,
    };

    private static Vector4 Factor(uint factor, Vector4 src, Vector4 dst, Vector4 constant)
    {
        switch (factor)
        {
            case 0: return Vector4.Zero;
            case 1: return Vector4.One;
            case 0x0300: return src;
            case 0x0301: return Vector4.One - src;
            case 0x0306: return dst;
            case 0x0307: return Vector4.One - dst;
            case 0x0302: return new Vector4(src.W);
            case 0x0303: return new Vector4(1 - src.W);
            case 0x0304: return new Vector4(dst.W);
            case 0x0305: return new Vector4(1 - dst.W);
            case 0x8001: return new Vector4(constant.X, constant.Y, constant.Z, constant.W);
            case 0x8002: return Vector4.One - constant;
            case 0x8003: return new Vector4(constant.W);
            case 0x8004: return new Vector4(1 - constant.W);
            case 0x0308:
            {
                var f = MathF.Min(src.W, 1 - dst.W);
                return new Vector4(f, f, f, 1);
            }
            default: return Vector4.One;
        }
    }
}
