using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Folio.Css;
using Folio.Imaging;
using Folio.Style;
using Folio.Typography;

namespace Folio.Painting;

/// <summary>Per-connection or per-session cache for font and image handles sent across IPC.</summary>
internal sealed class DisplayListContext
{
    // Content process (sender) tables
    public Dictionary<IFontHandle, int> FontToId { get; } = new(ReferenceEqualityComparer.Instance);
    public HashSet<string> SentWebFontHashes { get; } = new(StringComparer.Ordinal);
    public Dictionary<IImageHandle, int> ImageToId { get; } = new(ReferenceEqualityComparer.Instance);
    public HashSet<int> SentImageIds { get; } = [];

    // Host process (receiver) tables
    public Dictionary<int, IFontHandle> IdToFont { get; } = [];
    public Dictionary<string, IFontHandle> WebFontsByHash { get; } = new(StringComparer.Ordinal);
    public Dictionary<int, IImageHandle> IdToImage { get; } = [];
    public IFontSource? FontSource { get; set; }
}

/// <summary>Binary serializer and deserializer for <see cref="DisplayList"/> over IPC.</summary>
internal static class DisplayListCodec
{
    private const uint Magic = 0x54534C44; // 'DLST' little-endian
    private const ushort CurrentVersion = 1;

    public static byte[] Encode(DisplayList list, DisplayListContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(list);
        context ??= new DisplayListContext();

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        writer.Write(Magic);
        writer.Write(CurrentVersion);

        // Collect distinct items and side data
        var fontList = new List<IFontHandle>();
        var imageList = new List<IImageHandle>();
        var pathList = new List<PathData>();
        var gradientList = new List<Gradient>();
        var borderList = new List<BorderGroup>();
        var filterLists = new List<IReadOnlyList<Filter>>();
        var glyphRuns = new List<GlyphRun>();

        var fontIndex = new Dictionary<IFontHandle, int>(ReferenceEqualityComparer.Instance);
        var imageIndex = new Dictionary<IImageHandle, int>(ReferenceEqualityComparer.Instance);
        var pathIndex = new Dictionary<PathData, int>(ReferenceEqualityComparer.Instance);
        var gradientIndex = new Dictionary<Gradient, int>(ReferenceEqualityComparer.Instance);
        var borderIndex = new Dictionary<BorderGroup, int>(ReferenceEqualityComparer.Instance);
        var filterIndex = new Dictionary<IReadOnlyList<Filter>, int>(ReferenceEqualityComparer.Instance);
        var glyphIndex = new Dictionary<GlyphRun, int>(ReferenceEqualityComparer.Instance);

        foreach (var item in list.Items)
        {
            if (item.Border is { } border && !borderIndex.ContainsKey(border))
            {
                borderIndex[border] = borderList.Count;
                borderList.Add(border);
            }
            if (item.Glyphs is { } glyphs && !glyphIndex.ContainsKey(glyphs))
            {
                glyphIndex[glyphs] = glyphRuns.Count;
                glyphRuns.Add(glyphs);
                if (!fontIndex.ContainsKey(glyphs.Font))
                {
                    fontIndex[glyphs.Font] = fontList.Count;
                    fontList.Add(glyphs.Font);
                }
            }
            if (item.Image is { } image && !imageIndex.ContainsKey(image))
            {
                imageIndex[image] = imageList.Count;
                imageList.Add(image);
            }
            if (item.Path is { } path && !pathIndex.ContainsKey(path))
            {
                pathIndex[path] = pathList.Count;
                pathList.Add(path);
            }
            if (item.Gradient is { } gradient && !gradientIndex.ContainsKey(gradient))
            {
                gradientIndex[gradient] = gradientList.Count;
                gradientList.Add(gradient);
            }
            if (item.Filters is { } filters && !filterIndex.ContainsKey(filters))
            {
                filterIndex[filters] = filterLists.Count;
                filterLists.Add(filters);
            }
            if (item.Backdrop is { } backdrop && !filterIndex.ContainsKey(backdrop))
            {
                filterIndex[backdrop] = filterLists.Count;
                filterLists.Add(backdrop);
            }
        }

        // 1. Write Font Table
        writer.Write(fontList.Count);
        foreach (var font in fontList)
        {
            if (!context.FontToId.TryGetValue(font, out var id))
            {
                id = context.FontToId.Count + 1;
                context.FontToId[font] = id;
            }
            writer.Write(id);

            var faceIndex = font.FaceIndex;
            var data = font.Data;
            var isWebFont = data.Length > 0;
            writer.Write(isWebFont);

            if (isWebFont)
            {
                var hash = ComputeHash(data.Span);
                writer.Write(hash);
                writer.Write(faceIndex);

                var needsData = context.SentWebFontHashes.Add(hash);
                writer.Write(needsData);
                if (needsData)
                {
                    writer.Write(data.Length);
                    writer.Write(data.Span);
                }
            }
            else
            {
                var family = font is FontFace ff ? ff.Family : "sans-serif";
                var weight = font is FontFace ffWeight ? ffWeight.Weight : 400;
                var style = font is FontFace ffStyle ? (byte)ffStyle.Style : (byte)0;
                writer.Write(family);
                writer.Write(weight);
                writer.Write(style);
                writer.Write(faceIndex);
            }
        }

        // 2. Write Image Table
        writer.Write(imageList.Count);
        foreach (var img in imageList)
        {
            if (!context.ImageToId.TryGetValue(img, out var id))
            {
                id = context.ImageToId.Count + 1;
                context.ImageToId[img] = id;
            }
            writer.Write(id);
            writer.Write(img.Width);
            writer.Write(img.Height);

            var needsPixels = context.SentImageIds.Add(id);
            writer.Write(needsPixels);
            if (needsPixels)
            {
                var pixels = img.Pixels;
                writer.Write(pixels.Length);
                writer.Write(pixels.Span);
            }
        }

        // 3. Write Border Table
        writer.Write(borderList.Count);
        foreach (var border in borderList)
        {
            writer.Write(border.TopWidthPx);
            writer.Write(border.RightWidthPx);
            writer.Write(border.BottomWidthPx);
            writer.Write(border.LeftWidthPx);
            writer.Write((byte)border.TopStyle);
            writer.Write((byte)border.RightStyle);
            writer.Write((byte)border.BottomStyle);
            writer.Write((byte)border.LeftStyle);
            WriteCssColor(writer, border.TopColor);
            WriteCssColor(writer, border.RightColor);
            WriteCssColor(writer, border.BottomColor);
            WriteCssColor(writer, border.LeftColor);
            WriteCornerRadius(writer, border.TopLeftRadius);
            WriteCornerRadius(writer, border.TopRightRadius);
            WriteCornerRadius(writer, border.BottomRightRadius);
            WriteCornerRadius(writer, border.BottomLeftRadius);
        }

        // 4. Write Path Table
        writer.Write(pathList.Count);
        foreach (var path in pathList)
        {
            var commands = path.Commands;
            writer.Write(commands.Count);
            foreach (var cmd in commands)
            {
                writer.Write((byte)cmd.Verb);
                writer.Write(cmd.P1.X);
                writer.Write(cmd.P1.Y);
                writer.Write(cmd.P2.X);
                writer.Write(cmd.P2.Y);
                writer.Write(cmd.P3.X);
                writer.Write(cmd.P3.Y);
            }
        }

        // 5. Write Gradient Table
        writer.Write(gradientList.Count);
        foreach (var g in gradientList)
        {
            writer.Write((byte)g.Kind);
            writer.Write((byte)g.Spread);
            writer.Write(g.Stops.Count);
            foreach (var s in g.Stops)
            {
                writer.Write(s.Offset);
                writer.Write(s.Color.R);
                writer.Write(s.Color.G);
                writer.Write(s.Color.B);
                writer.Write(s.Color.A);
            }
            writer.Write(g.Start.X); writer.Write(g.Start.Y);
            writer.Write(g.End.X); writer.Write(g.End.Y);
            writer.Write(g.Center.X); writer.Write(g.Center.Y);
            writer.Write(g.Radii.X); writer.Write(g.Radii.Y);
            writer.Write(g.StartAngle);
            writer.Write(g.EndAngle);
            writer.Write(g.Focus.HasValue);
            if (g.Focus is { } focus) { writer.Write(focus.X); writer.Write(focus.Y); }
            writer.Write(g.FocusRadius);
            writer.Write(g.Transform.HasValue);
            if (g.Transform is { } m)
            {
                writer.Write(m.M11); writer.Write(m.M12);
                writer.Write(m.M21); writer.Write(m.M22);
                writer.Write(m.M31); writer.Write(m.M32);
            }
        }

        // 6. Write Filter Lists Table
        writer.Write(filterLists.Count);
        foreach (var fl in filterLists)
        {
            writer.Write(fl.Count);
            foreach (var f in fl)
            {
                writer.Write((byte)f.Kind);
                writer.Write(f.StdDeviation);
                writer.Write(f.Offset.X); writer.Write(f.Offset.Y);
                writer.Write(f.Color.R); writer.Write(f.Color.G); writer.Write(f.Color.B); writer.Write(f.Color.A);
                writer.Write((byte)f.In.Source); writer.Write(f.In.Index);
                writer.Write((byte)f.In2.Source); writer.Write(f.In2.Index);
                writer.Write(f.LinearRgb);
                writer.Write((byte)f.Operator);
                writer.Write((byte)f.Blend);
                writer.Write(f.Radius.X); writer.Write(f.Radius.Y);
                writer.Write(f.Dilate);

                writer.Write(f.Matrix is not null);
                if (f.Matrix is { } mat)
                {
                    writer.Write(mat.Count);
                    for (var i = 0; i < mat.Count; i++) writer.Write(mat[i]);
                }

                writer.Write(f.Subregion.HasValue);
                if (f.Subregion is { } sub)
                {
                    writer.Write(sub.X); writer.Write(sub.Y); writer.Write(sub.Width); writer.Write(sub.Height);
                }

                writer.Write(f.Deviations.HasValue);
                if (f.Deviations is { } dev)
                {
                    writer.Write(dev.X); writer.Write(dev.Y);
                }

                writer.Write(f.Coefficients is not null);
                if (f.Coefficients is { } coeff)
                {
                    writer.Write(coeff.Count);
                    for (var i = 0; i < coeff.Count; i++) writer.Write(coeff[i]);
                }
            }
        }

        // 7. Write Glyph Runs Table
        writer.Write(glyphRuns.Count);
        foreach (var gr in glyphRuns)
        {
            var fId = context.FontToId[gr.Font];
            writer.Write(fId);
            writer.Write(gr.Size);
            writer.Write(gr.Glyphs.Length);
            for (var i = 0; i < gr.Glyphs.Length; i++) writer.Write(gr.Glyphs[i]);
            writer.Write(gr.Origins.Length);
            for (var i = 0; i < gr.Origins.Length; i++) { writer.Write(gr.Origins[i].X); writer.Write(gr.Origins[i].Y); }
        }

        // 8. Write Display Items
        writer.Write(list.Items.Count);
        foreach (var item in list.Items)
        {
            writer.Write((byte)item.Kind);
            WriteRoundedRect(writer, item.Shape);
            WriteCssColor(writer, item.Color);
            writer.Write(item.Opacity);
            writer.Write(item.Border is not null ? borderIndex[item.Border] : -1);
            writer.Write(item.Glyphs is not null ? glyphIndex[item.Glyphs] : -1);
            writer.Write(item.Text is not null);
            if (item.Text is not null) writer.Write(item.Text);
            writer.Write((byte)item.LineStyle);
            writer.Write((byte)item.Blend);

            // Rare fields flag bitmask
            var mask = 0;
            if (item.Image is not null) mask |= 1 << 0;
            if (item.Sampling != ImageSampling.Smooth) mask |= 1 << 1;
            if (item.Blur != 0) mask |= 1 << 2;
            if (item.Inset) mask |= 1 << 3;
            if (item.Box != default) mask |= 1 << 4;
            if (item.Gradient is not null) mask |= 1 << 5;
            if (item.Transform != default) mask |= 1 << 6;
            if (item.Filters is not null) mask |= 1 << 7;
            if (item.Backdrop is not null) mask |= 1 << 8;
            if (item.Path is not null) mask |= 1 << 9;
            if (item.Rule != FillRule.NonZero) mask |= 1 << 10;
            if (item.Stroke is not null) mask |= 1 << 11;
            if (item.Projection is not null) mask |= 1 << 12;

            writer.Write(mask);
            if ((mask & (1 << 0)) != 0) writer.Write(imageIndex[item.Image!]);
            if ((mask & (1 << 1)) != 0) writer.Write((byte)item.Sampling);
            if ((mask & (1 << 2)) != 0) writer.Write(item.Blur);
            if ((mask & (1 << 4)) != 0) WriteRoundedRect(writer, item.Box);
            if ((mask & (1 << 5)) != 0) writer.Write(gradientIndex[item.Gradient!]);
            if ((mask & (1 << 6)) != 0)
            {
                var m = item.Transform;
                writer.Write(m.M11); writer.Write(m.M12);
                writer.Write(m.M21); writer.Write(m.M22);
                writer.Write(m.M31); writer.Write(m.M32);
            }
            if ((mask & (1 << 7)) != 0) writer.Write(filterIndex[item.Filters!]);
            if ((mask & (1 << 8)) != 0) writer.Write(filterIndex[item.Backdrop!]);
            if ((mask & (1 << 9)) != 0) writer.Write(pathIndex[item.Path!]);
            if ((mask & (1 << 10)) != 0) writer.Write((byte)item.Rule);
            if ((mask & (1 << 11)) != 0)
            {
                var s = item.Stroke!.Value;
                writer.Write(s.Width);
                writer.Write((byte)s.Cap);
                writer.Write((byte)s.Join);
                writer.Write(s.MiterLimit);
                writer.Write(s.DashOffset);
                writer.Write(s.Dashes is not null);
                if (s.Dashes is { } dashes)
                {
                    writer.Write(dashes.Count);
                    for (var d = 0; d < dashes.Count; d++) writer.Write(dashes[d]);
                }
            }
            if ((mask & (1 << 12)) != 0)
            {
                var p = item.Projection!.Value;
                writer.Write(p.M11); writer.Write(p.M12); writer.Write(p.M13); writer.Write(p.M14);
                writer.Write(p.M21); writer.Write(p.M22); writer.Write(p.M23); writer.Write(p.M24);
                writer.Write(p.M31); writer.Write(p.M32); writer.Write(p.M33); writer.Write(p.M34);
                writer.Write(p.M41); writer.Write(p.M42); writer.Write(p.M43); writer.Write(p.M44);
            }
        }

        writer.Flush();
        return stream.ToArray();
    }

    public static DisplayList Decode(ReadOnlySpan<byte> bytes, DisplayListContext? context = null)
    {
        if (!TryDecode(bytes, out var list, context) || list is null)
            throw new InvalidDataException("Invalid display list binary payload.");
        return list;
    }

    public static bool TryDecode(ReadOnlySpan<byte> bytes, out DisplayList? list, DisplayListContext? context = null)
    {
        list = null;
        if (bytes.Length < 6) return false;

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (magic != Magic) return false;
        var version = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
        if (version != CurrentVersion) return false;

        context ??= new DisplayListContext();

        try
        {
            using var stream = new MemoryStream(bytes[6..].ToArray(), writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8);

            // 1. Read Font Table
            var fontCount = reader.ReadInt32();
            if (fontCount < 0 || fontCount > 100_000) return false;
            var fontsInFrame = new Dictionary<int, IFontHandle>();
            for (var i = 0; i < fontCount; i++)
            {
                var id = reader.ReadInt32();
                var isWebFont = reader.ReadBoolean();
                if (isWebFont)
                {
                    var hash = reader.ReadString();
                    var faceIndex = reader.ReadInt32();
                    var hasData = reader.ReadBoolean();
                    if (hasData)
                    {
                        var dataLen = reader.ReadInt32();
                        if (dataLen < 0 || dataLen > 64 * 1024 * 1024) return false;
                        var fontBytes = reader.ReadBytes(dataLen);
                        var handle = (IFontHandle?)FontFace.Parse(fontBytes, faceIndex)
                            ?? new EmbeddedFontHandle(fontBytes, faceIndex);
                        context.WebFontsByHash[hash] = handle;
                        context.IdToFont[id] = handle;
                        fontsInFrame[id] = handle;
                    }
                    else
                    {
                        if (context.WebFontsByHash.TryGetValue(hash, out var cached))
                        {
                            context.IdToFont[id] = cached;
                            fontsInFrame[id] = cached;
                        }
                        else
                        {
                            var empty = new EmbeddedFontHandle(Array.Empty<byte>(), faceIndex);
                            context.IdToFont[id] = empty;
                            fontsInFrame[id] = empty;
                        }
                    }
                }
                else
                {
                    var family = reader.ReadString();
                    var weight = reader.ReadInt32();
                    var style = (FaceStyle)reader.ReadByte();
                    var faceIndex = reader.ReadInt32();
                    IFontHandle? handle = null;
                    if (context.FontSource is { } fs)
                    {
                        var faces = fs.OpenFamily(family);
                        handle = faces.OfType<FontFace>().FirstOrDefault(f => f.FaceIndex == faceIndex)
                            ?? faces.FirstOrDefault();
                    }
                    handle ??= new SystemFontHandle(family, weight, style, faceIndex);
                    context.IdToFont[id] = handle;
                    fontsInFrame[id] = handle;
                }
            }

            // 2. Read Image Table
            var imageCount = reader.ReadInt32();
            if (imageCount < 0 || imageCount > 100_000) return false;
            var imagesInFrame = new List<IImageHandle>(imageCount);
            for (var i = 0; i < imageCount; i++)
            {
                var id = reader.ReadInt32();
                var width = reader.ReadInt32();
                var height = reader.ReadInt32();
                var hasPixels = reader.ReadBoolean();
                if (hasPixels)
                {
                    var pixelLen = reader.ReadInt32();
                    if (pixelLen < 0 || pixelLen > 128 * 1024 * 1024) return false;
                    var pixels = reader.ReadBytes(pixelLen);
                    var img = new DecodedImage(width, height, pixels);
                    context.IdToImage[id] = img;
                    imagesInFrame.Add(img);
                }
                else
                {
                    if (context.IdToImage.TryGetValue(id, out var cached))
                        imagesInFrame.Add(cached);
                    else
                        imagesInFrame.Add(new DecodedImage(width, height, new byte[width * height * 4]));
                }
            }

            // 3. Read Border Table
            var borderCount = reader.ReadInt32();
            if (borderCount < 0 || borderCount > 100_000) return false;
            var borders = new List<BorderGroup>(borderCount);
            for (var i = 0; i < borderCount; i++)
            {
                var tw = reader.ReadSingle(); var rw = reader.ReadSingle(); var bw = reader.ReadSingle(); var lw = reader.ReadSingle();
                var ts = (BorderStyle)reader.ReadByte(); var rs = (BorderStyle)reader.ReadByte(); var bs = (BorderStyle)reader.ReadByte(); var ls = (BorderStyle)reader.ReadByte();
                var tc = ReadCssColor(reader); var rc = ReadCssColor(reader); var bc = ReadCssColor(reader); var lc = ReadCssColor(reader);
                var tlr = ReadCornerRadius(reader); var trr = ReadCornerRadius(reader); var brr = ReadCornerRadius(reader); var blr = ReadCornerRadius(reader);
                borders.Add(new BorderGroup(tw, rw, bw, lw, ts, rs, bs, ls, tc, rc, bc, lc, tlr, trr, brr, blr));
            }

            // 4. Read Path Table
            var pathCount = reader.ReadInt32();
            if (pathCount < 0 || pathCount > 100_000) return false;
            var paths = new List<PathData>(pathCount);
            for (var i = 0; i < pathCount; i++)
            {
                var cmdCount = reader.ReadInt32();
                var path = new PathData(cmdCount);
                for (var c = 0; c < cmdCount; c++)
                {
                    var verb = (PathVerb)reader.ReadByte();
                    var p1 = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                    var p2 = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                    var p3 = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                    switch (verb)
                    {
                        case PathVerb.MoveTo: path.MoveTo(p1.X, p1.Y); break;
                        case PathVerb.LineTo: path.LineTo(p1.X, p1.Y); break;
                        case PathVerb.CubicTo: path.CubicTo(p1, p2, p3); break;
                        case PathVerb.Close: path.Close(); break;
                    }
                }
                paths.Add(path);
            }

            // 5. Read Gradient Table
            var gradCount = reader.ReadInt32();
            if (gradCount < 0 || gradCount > 100_000) return false;
            var gradients = new List<Gradient>(gradCount);
            for (var i = 0; i < gradCount; i++)
            {
                var kind = (GradientKind)reader.ReadByte();
                var spread = (GradientSpread)reader.ReadByte();
                var stopCount = reader.ReadInt32();
                var stops = new List<GradientStop>(stopCount);
                for (var s = 0; s < stopCount; s++)
                {
                    stops.Add(new GradientStop(reader.ReadSingle(), new Rgba(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle())));
                }
                var start = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                var end = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                var center = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                var radii = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                var startAngle = reader.ReadSingle();
                var endAngle = reader.ReadSingle();
                Vector2? focus = reader.ReadBoolean() ? new Vector2(reader.ReadSingle(), reader.ReadSingle()) : null;
                var focusRadius = reader.ReadSingle();
                Matrix3x2? transform = reader.ReadBoolean()
                    ? new Matrix3x2(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle())
                    : null;
                gradients.Add(new Gradient(kind, stops, spread, start, end, center, radii, startAngle, endAngle, focus, focusRadius, transform));
            }

            // 6. Read Filter Table
            var filterListCount = reader.ReadInt32();
            if (filterListCount < 0 || filterListCount > 100_000) return false;
            var filterListsRead = new List<IReadOnlyList<Filter>>(filterListCount);
            for (var i = 0; i < filterListCount; i++)
            {
                var fCount = reader.ReadInt32();
                var fl = new List<Filter>(fCount);
                for (var f = 0; f < fCount; f++)
                {
                    var kind = (FilterKind)reader.ReadByte();
                    var stdDev = reader.ReadSingle();
                    var offset = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                    var col = new Rgba(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    var inSrc = (FilterSource)reader.ReadByte(); var inIdx = reader.ReadInt32();
                    var in2Src = (FilterSource)reader.ReadByte(); var in2Idx = reader.ReadInt32();
                    var linearRgb = reader.ReadBoolean();
                    var op = (CompositeOperator)reader.ReadByte();
                    var blend = (BlendMode)reader.ReadByte();
                    var rad = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                    var dilate = reader.ReadBoolean();

                    float[]? matrix = null;
                    if (reader.ReadBoolean())
                    {
                        var mCount = reader.ReadInt32();
                        matrix = new float[mCount];
                        for (var mi = 0; mi < mCount; mi++) matrix[mi] = reader.ReadSingle();
                    }

                    RectF? subregion = null;
                    if (reader.ReadBoolean())
                    {
                        subregion = new RectF(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    }

                    Vector2? deviations = null;
                    if (reader.ReadBoolean())
                    {
                        deviations = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                    }

                    float[]? coeff = null;
                    if (reader.ReadBoolean())
                    {
                        var cCount = reader.ReadInt32();
                        coeff = new float[cCount];
                        for (var ci = 0; ci < cCount; ci++) coeff[ci] = reader.ReadSingle();
                    }

                    fl.Add(new Filter(kind, stdDev, matrix, offset, col)
                    {
                        In = new FilterInput(inSrc, inIdx),
                        In2 = new FilterInput(in2Src, in2Idx),
                        LinearRgb = linearRgb,
                        Operator = op,
                        Blend = blend,
                        Radius = rad,
                        Dilate = dilate,
                        Subregion = subregion,
                        Deviations = deviations,
                        Coefficients = coeff
                    });
                }
                filterListsRead.Add(fl);
            }

            // 7. Read Glyph Runs
            var grCount = reader.ReadInt32();
            if (grCount < 0 || grCount > 100_000) return false;
            var runs = new List<GlyphRun>(grCount);
            for (var i = 0; i < grCount; i++)
            {
                var fId = reader.ReadInt32();
                var size = reader.ReadSingle();
                var gLen = reader.ReadInt32();
                var glyphArr = new ushort[gLen];
                for (var g = 0; g < gLen; g++) glyphArr[g] = reader.ReadUInt16();
                var oLen = reader.ReadInt32();
                var originArr = new Vector2[oLen];
                for (var o = 0; o < oLen; o++) originArr[o] = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                var font = fontsInFrame.TryGetValue(fId, out var fHandle) ? fHandle : context.IdToFont[fId];
                runs.Add(new GlyphRun(font, size, glyphArr, originArr));
            }

            // 8. Read Items
            var itemCount = reader.ReadInt32();
            if (itemCount < 0 || itemCount > 1_000_000) return false;
            var result = new DisplayList();
            for (var i = 0; i < itemCount; i++)
            {
                var kind = (DisplayItemKind)reader.ReadByte();
                var shape = ReadRoundedRect(reader);
                var color = ReadCssColor(reader);
                var opacity = reader.ReadSingle();
                var borderIdx = reader.ReadInt32();
                var glyphIdx = reader.ReadInt32();
                string? text = reader.ReadBoolean() ? reader.ReadString() : null;
                var lineStyle = (TextDecorationStyle)reader.ReadByte();
                var blend = (BlendMode)reader.ReadByte();

                var border = borderIdx >= 0 && borderIdx < borders.Count ? borders[borderIdx] : null;
                var glyphs = glyphIdx >= 0 && glyphIdx < runs.Count ? runs[glyphIdx] : null;

                var mask = reader.ReadInt32();
                IImageHandle? image = null;
                var sampling = ImageSampling.Smooth;
                float blur = 0;
                var inset = (mask & (1 << 3)) != 0;
                var box = default(RoundedRect);
                Gradient? gradient = null;
                var transform = default(Matrix3x2);
                IReadOnlyList<Filter>? fl = null;
                IReadOnlyList<Filter>? backdrop = null;
                PathData? path = null;
                var rule = FillRule.NonZero;
                Stroke? stroke = null;
                Matrix4x4? proj = null;

                if ((mask & (1 << 0)) != 0)
                {
                    var imgIdx = reader.ReadInt32();
                    image = imgIdx >= 0 && imgIdx < imagesInFrame.Count ? imagesInFrame[imgIdx] : null;
                }
                if ((mask & (1 << 1)) != 0) sampling = (ImageSampling)reader.ReadByte();
                if ((mask & (1 << 2)) != 0) blur = reader.ReadSingle();
                if ((mask & (1 << 4)) != 0) box = ReadRoundedRect(reader);
                if ((mask & (1 << 5)) != 0)
                {
                    var gIdx = reader.ReadInt32();
                    gradient = gIdx >= 0 && gIdx < gradients.Count ? gradients[gIdx] : null;
                }
                if ((mask & (1 << 6)) != 0)
                {
                    transform = new Matrix3x2(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                }
                if ((mask & (1 << 7)) != 0)
                {
                    var flIdx = reader.ReadInt32();
                    fl = flIdx >= 0 && flIdx < filterListsRead.Count ? filterListsRead[flIdx] : null;
                }
                if ((mask & (1 << 8)) != 0)
                {
                    var bdIdx = reader.ReadInt32();
                    backdrop = bdIdx >= 0 && bdIdx < filterListsRead.Count ? filterListsRead[bdIdx] : null;
                }
                if ((mask & (1 << 9)) != 0)
                {
                    var pIdx = reader.ReadInt32();
                    path = pIdx >= 0 && pIdx < paths.Count ? paths[pIdx] : null;
                }
                if ((mask & (1 << 10)) != 0) rule = (FillRule)reader.ReadByte();
                if ((mask & (1 << 11)) != 0)
                {
                    var w = reader.ReadSingle();
                    var cap = (LineCap)reader.ReadByte();
                    var join = (LineJoin)reader.ReadByte();
                    var miter = reader.ReadSingle();
                    var dashOffset = reader.ReadSingle();
                    float[]? dashes = null;
                    if (reader.ReadBoolean())
                    {
                        var dLen = reader.ReadInt32();
                        dashes = new float[dLen];
                        for (var d = 0; d < dLen; d++) dashes[d] = reader.ReadSingle();
                    }
                    stroke = new Stroke(w, cap, dashes, join, miter, dashOffset);
                }
                if ((mask & (1 << 12)) != 0)
                {
                    proj = new Matrix4x4(
                        reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                        reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                        reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                        reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                }

                result.Items.Add(new DisplayItem(kind, shape, color, opacity, border, glyphs, lineStyle, image, sampling, blur, inset, box, gradient, transform, fl, backdrop, blend, path, rule, stroke, proj, text));
            }

            list = result;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool AreEqual(DisplayList expected, DisplayList actual, out string? difference)
    {
        difference = null;
        if (expected.Items.Count != actual.Items.Count)
        {
            difference = $"Item count mismatch: expected {expected.Items.Count}, got {actual.Items.Count}";
            return false;
        }

        for (var i = 0; i < expected.Items.Count; i++)
        {
            var e = expected.Items[i];
            var a = actual.Items[i];

            if (e.Kind != a.Kind)
            {
                difference = $"Item {i} Kind mismatch: expected {e.Kind}, got {a.Kind}";
                return false;
            }
            if (!e.Shape.Equals(a.Shape))
            {
                difference = $"Item {i} Shape mismatch: expected {e.Shape}, got {a.Shape}";
                return false;
            }
            if (!e.Color.Equals(a.Color))
            {
                difference = $"Item {i} Color mismatch: expected {e.Color}, got {a.Color}";
                return false;
            }
            if (Math.Abs(e.Opacity - a.Opacity) > 0.0001f)
            {
                difference = $"Item {i} Opacity mismatch: expected {e.Opacity}, got {a.Opacity}";
                return false;
            }
            if (e.LineStyle != a.LineStyle)
            {
                difference = $"Item {i} LineStyle mismatch: expected {e.LineStyle}, got {a.LineStyle}";
                return false;
            }
            if (e.Blend != a.Blend)
            {
                difference = $"Item {i} Blend mismatch: expected {e.Blend}, got {a.Blend}";
                return false;
            }
            if (!string.Equals(e.Text, a.Text, StringComparison.Ordinal))
            {
                difference = $"Item {i} Text mismatch: expected {e.Text}, got {a.Text}";
                return false;
            }
            if (!Equals(e.Border, a.Border))
            {
                difference = $"Item {i} Border mismatch: expected {e.Border}, got {a.Border}";
                return false;
            }
            if (e.Glyphs is not null || a.Glyphs is not null)
            {
                if (e.Glyphs is null || a.Glyphs is null)
                {
                    difference = $"Item {i} Glyphs null mismatch";
                    return false;
                }
                if (Math.Abs(e.Glyphs.Size - a.Glyphs.Size) > 0.001f)
                {
                    difference = $"Item {i} Glyphs.Size mismatch: expected {e.Glyphs.Size}, got {a.Glyphs.Size}";
                    return false;
                }
                if (!e.Glyphs.Glyphs.AsSpan().SequenceEqual(a.Glyphs.Glyphs))
                {
                    difference = $"Item {i} Glyphs array mismatch";
                    return false;
                }
                if (!e.Glyphs.Origins.AsSpan().SequenceEqual(a.Glyphs.Origins))
                {
                    difference = $"Item {i} Glyphs.Origins mismatch";
                    return false;
                }
            }
            if (e.Path is not null || a.Path is not null)
            {
                if (e.Path is null || a.Path is null || e.Path.Commands.Count != a.Path.Commands.Count)
                {
                    difference = $"Item {i} Path mismatch";
                    return false;
                }
                for (var p = 0; p < e.Path.Commands.Count; p++)
                {
                    if (!e.Path.Commands[p].Equals(a.Path.Commands[p]))
                    {
                        difference = $"Item {i} Path command {p} mismatch";
                        return false;
                    }
                }
            }
            if (e.Gradient is not null || a.Gradient is not null)
            {
                if (e.Gradient is null || a.Gradient is null)
                {
                    difference = $"Item {i} Gradient null mismatch";
                    return false;
                }
                if (e.Gradient.Kind != a.Gradient.Kind || e.Gradient.Spread != a.Gradient.Spread
                    || e.Gradient.Stops.Count != a.Gradient.Stops.Count)
                {
                    difference = $"Item {i} Gradient properties mismatch";
                    return false;
                }
                for (var s = 0; s < e.Gradient.Stops.Count; s++)
                {
                    if (!e.Gradient.Stops[s].Equals(a.Gradient.Stops[s]))
                    {
                        difference = $"Item {i} Gradient stop {s} mismatch";
                        return false;
                    }
                }
            }
            if (e.Image is not null || a.Image is not null)
            {
                if (e.Image is null || a.Image is null
                    || e.Image.Width != a.Image.Width || e.Image.Height != a.Image.Height
                    || !e.Image.Pixels.Span.SequenceEqual(a.Image.Pixels.Span))
                {
                    difference = $"Item {i} Image mismatch";
                    return false;
                }
            }
            if (e.Sampling != a.Sampling)
            {
                difference = $"Item {i} Sampling mismatch";
                return false;
            }
            if (Math.Abs(e.Blur - a.Blur) > 0.001f)
            {
                difference = $"Item {i} Blur mismatch";
                return false;
            }
            if (e.Inset != a.Inset)
            {
                difference = $"Item {i} Inset mismatch";
                return false;
            }
            if (!e.Box.Equals(a.Box))
            {
                difference = $"Item {i} Box mismatch";
                return false;
            }
            if (!e.Transform.Equals(a.Transform))
            {
                difference = $"Item {i} Transform mismatch";
                return false;
            }
            if (e.Rule != a.Rule)
            {
                difference = $"Item {i} FillRule mismatch";
                return false;
            }
            if (!StrokeEquals(e.Stroke, a.Stroke))
            {
                difference = $"Item {i} Stroke mismatch";
                return false;
            }
            if (!Equals(e.Projection, a.Projection))
            {
                difference = $"Item {i} Projection mismatch";
                return false;
            }
        }
        return true;
    }

    private static bool StrokeEquals(Stroke? a, Stroke? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        var s1 = a.Value;
        var s2 = b.Value;
        if (s1.Width != s2.Width || s1.Cap != s2.Cap || s1.Join != s2.Join || s1.MiterLimit != s2.MiterLimit || s1.DashOffset != s2.DashOffset)
            return false;
        if (s1.Dashes is null && s2.Dashes is null) return true;
        if (s1.Dashes is null || s2.Dashes is null) return false;
        return s1.Dashes.SequenceEqual(s2.Dashes);
    }

    private static void WriteRoundedRect(BinaryWriter writer, in RoundedRect r)
    {
        writer.Write(r.Rect.X); writer.Write(r.Rect.Y); writer.Write(r.Rect.Width); writer.Write(r.Rect.Height);
        writer.Write(r.Radii.TopLeft.X); writer.Write(r.Radii.TopLeft.Y);
        writer.Write(r.Radii.TopRight.X); writer.Write(r.Radii.TopRight.Y);
        writer.Write(r.Radii.BottomRight.X); writer.Write(r.Radii.BottomRight.Y);
        writer.Write(r.Radii.BottomLeft.X); writer.Write(r.Radii.BottomLeft.Y);
    }

    private static RoundedRect ReadRoundedRect(BinaryReader reader)
    {
        var rect = new RectF(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        var tl = new Vector2(reader.ReadSingle(), reader.ReadSingle());
        var tr = new Vector2(reader.ReadSingle(), reader.ReadSingle());
        var br = new Vector2(reader.ReadSingle(), reader.ReadSingle());
        var bl = new Vector2(reader.ReadSingle(), reader.ReadSingle());
        return new RoundedRect(rect, new CornerRadii(tl, tr, br, bl));
    }

    private static void WriteLengthPercentage(BinaryWriter writer, in LengthPercentage lp)
    {
        writer.Write(lp.Px);
        writer.Write(lp.Percent);
    }

    private static LengthPercentage ReadLengthPercentage(BinaryReader reader)
    {
        return new LengthPercentage(reader.ReadSingle(), reader.ReadSingle());
    }

    private static void WriteCornerRadius(BinaryWriter writer, in CornerRadius cr)
    {
        WriteLengthPercentage(writer, cr.X);
        WriteLengthPercentage(writer, cr.Y);
    }

    private static CornerRadius ReadCornerRadius(BinaryReader reader)
    {
        return new CornerRadius(ReadLengthPercentage(reader), ReadLengthPercentage(reader));
    }

    private static void WriteCssColor(BinaryWriter writer, in CssColor color)
    {
        writer.Write(color.R);
        writer.Write(color.G);
        writer.Write(color.B);
        writer.Write(color.A);
        writer.Write(color.IsCurrentColor);
    }

    private static CssColor ReadCssColor(BinaryReader reader)
    {
        return new CssColor(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadBoolean());
    }

    private static string ComputeHash(ReadOnlySpan<byte> bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private sealed class EmbeddedFontHandle(ReadOnlyMemory<byte> data, int faceIndex) : IFontHandle
    {
        public ReadOnlyMemory<byte> Data => data;
        public int FaceIndex => faceIndex;
    }

    private sealed class SystemFontHandle(string family, int weight, FaceStyle style, int faceIndex) : IFontHandle
    {
        public string Family => family;
        public int Weight => weight;
        public FaceStyle Style => style;
        public ReadOnlyMemory<byte> Data => ReadOnlyMemory<byte>.Empty;
        public int FaceIndex => faceIndex;
    }
}
