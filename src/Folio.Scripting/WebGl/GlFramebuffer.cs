namespace Folio.Scripting.WebGl;

/// <summary>
/// Something the pipeline draws into: RGBA8 colour with one or more samples per pixel (sample-major within a pixel), rows from the bottom
/// as GL has them, and optional depth and stencil of the same shape.
/// </summary>
internal sealed class GlRenderTarget(int width, int height, int samples, byte[] color, byte[]? depthBytes, byte[]? stencil)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public int Samples { get; } = samples;
    public byte[] Color { get; } = color;

    /// <summary>Depth as little-endian float32 per sample; null when the target has no depth buffer.</summary>
    public byte[]? DepthBytes { get; } = depthBytes;

    public byte[]? Stencil { get; } = stencil;

    /// <summary>Whether alpha is stored (a canvas made with <c>alpha: false</c> reads as opaque).</summary>
    public bool HasAlpha { get; init; } = true;
}

/// <summary>The drawing buffer of a WebGL canvas: multisampled, with depth and stencil, resolved on demand into the canvas bitmap.</summary>
internal sealed class GlDefaultFramebuffer
{
    public const int SampleCount = 4;

    public int Width { get; }
    public int Height { get; }
    public byte[] Color { get; }
    public byte[] DepthBytes { get; }
    public byte[] Stencil { get; }
    private readonly bool _multisample;

    public GlDefaultFramebuffer(int width, int height, bool multisample)
    {
        Width = width;
        Height = height;
        _multisample = multisample;
        var samples = multisample ? SampleCount : 1;
        Color = new byte[checked(width * height * samples * 4)];
        DepthBytes = new byte[checked(width * height * samples * 4)];
        Stencil = new byte[width * height * samples];
        FillDepth(1f);
    }

    public int Samples => _multisample ? SampleCount : 1;

    public GlRenderTarget Target(bool alpha) => new(Width, Height, Samples, Color, DepthBytes, Stencil) { HasAlpha = alpha };

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
            result[p * 4 + 3] = (byte)((sa + samples / 2) / samples);
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
