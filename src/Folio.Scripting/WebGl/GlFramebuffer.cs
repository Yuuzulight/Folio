namespace Folio.Scripting.WebGl;

/// <summary>
/// Something the pipeline draws into: one image per fragment output (null where the output goes nowhere) with one or more samples per pixel
/// (sample-major within a pixel), rows from the bottom as GL has them, and optional depth and stencil of the same shape.
/// </summary>
internal sealed class GlRenderTarget(int width, int height, int samples, GlImage?[] colors, byte[]? depthBytes, byte[]? stencil)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public int Samples { get; } = samples;

    /// <summary>The colour image each fragment output is written to, indexed by output location.</summary>
    public GlImage?[] Colors { get; } = colors;

    /// <summary>Depth as little-endian float32 per sample; null when the target has no depth buffer.</summary>
    public byte[]? DepthBytes { get; } = depthBytes;

    public byte[]? Stencil { get; } = stencil;
}

/// <summary>The drawing buffer of a WebGL canvas: multisampled, with depth and stencil, resolved on demand into the canvas bitmap.</summary>
internal sealed class GlDefaultFramebuffer
{
    public const int SampleCount = 4;

    public int Width { get; }
    public int Height { get; }
    public GlImage Image { get; }
    public byte[] Color => Image.Data;
    public byte[] DepthBytes { get; }
    public byte[] Stencil { get; }
    private readonly bool _multisample;

    public GlDefaultFramebuffer(int width, int height, bool multisample, bool alpha = true)
    {
        Width = width;
        Height = height;
        _multisample = multisample;
        var samples = multisample ? SampleCount : 1;
        Image = new GlImage(width, height, new GlFormat(alpha ? 0x8058u : 0x8051u, GlStorage.U8, alpha ? GlLayout.RGBA : GlLayout.RGB), samples);
        DepthBytes = new byte[checked(width * height * samples * 4)];
        Stencil = new byte[width * height * samples];
        FillDepth(1f);
    }

    public int Samples => _multisample ? SampleCount : 1;

    public GlRenderTarget Target() => new(Width, Height, Samples, [Image], DepthBytes, Stencil);

    public void FillDepth(float value)
    {
        var bits = BitConverter.SingleToInt32Bits(value);
        var span = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(DepthBytes.AsSpan());
        span.Fill(bits);
    }

    /// <summary>The average of each pixel's samples as RGBA8, rows from the bottom (what <c>readPixels</c> returns).</summary>
    public byte[] Resolve()
    {
        var samples = Samples;
        var result = new byte[Width * Height * 4];
        if (samples == 1)
        {
            Array.Copy(Color, result, result.Length);
            if (!Image.Format.HasAlpha) for (var i = 3; i < result.Length; i += 4) result[i] = 255;
            return result;
        }
        for (var p = 0; p < Width * Height; p++)
        {
            var sr = 0; var sg = 0; var sb = 0; var sa = 0;
            var at = p * samples * 4;
            for (var s = 0; s < samples; s++)
            {
                sr += Color[at]; sg += Color[at + 1]; sb += Color[at + 2]; sa += Color[at + 3];
                at += 4;
            }
            result[p * 4] = (byte)((sr + samples / 2) / samples);
            result[p * 4 + 1] = (byte)((sg + samples / 2) / samples);
            result[p * 4 + 2] = (byte)((sb + samples / 2) / samples);
            result[p * 4 + 3] = Image.Format.HasAlpha ? (byte)((sa + samples / 2) / samples) : (byte)255;
        }
        return result;
    }

    /// <summary>The resolved image as straight-alpha RGBA8 with the top row first, which is what a canvas bitmap holds.</summary>
    public byte[] ToBitmap(bool premultipliedAlpha, bool alpha)
    {
        var resolved = Resolve();
        var result = new byte[resolved.Length];
        for (var y = 0; y < Height; y++)
        {
            var from = (Height - 1 - y) * Width * 4;
            var to = y * Width * 4;
            for (var x = 0; x < Width; x++)
            {
                var r = resolved[from]; var g = resolved[from + 1]; var b = resolved[from + 2]; var a = alpha ? resolved[from + 3] : (byte)255;
                if (premultipliedAlpha && alpha && a is > 0 and < 255)
                {
                    r = (byte)Math.Min(255, (r * 255 + a / 2) / a);
                    g = (byte)Math.Min(255, (g * 255 + a / 2) / a);
                    b = (byte)Math.Min(255, (b * 255 + a / 2) / a);
                }
                result[to] = r; result[to + 1] = g; result[to + 2] = b; result[to + 3] = a;
                from += 4;
                to += 4;
            }
        }
        return result;
    }
}
