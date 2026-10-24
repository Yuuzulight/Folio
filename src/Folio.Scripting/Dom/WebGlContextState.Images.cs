using System.Numerics;
using Folio.Scripting.WebGl;

namespace Folio.Scripting.Dom;

// Textures, renderbuffers and framebuffers: the images a context stores, what is attached where, and what a draw or a read resolves to.
internal sealed partial class WebGlContextState
{
    public const uint ColorAttachment0 = 0x8CE0;
    public const uint DepthAttachment = 0x8D00;
    public const uint StencilAttachment = 0x8D20;
    public const uint DepthStencilAttachment = 0x821A;
    public const int MaxColorAttachments = 4;

    public Dictionary<uint, GlTexture> Textures { get; } = [];
    public Dictionary<uint, GlRenderbuffer> Renderbuffers { get; } = [];
    public Dictionary<uint, GlFramebufferObject> Framebuffers { get; } = [];
    public HashSet<string> EnabledExtensions { get; } = new(StringComparer.OrdinalIgnoreCase);

    // ---- storage budget ----

    private bool Reserve(long bytes, long released = 0)
    {
        var next = AllocatedBytes - released + bytes;
        if (bytes < 0 || next > _maxGpuBytes)
        {
            SetError(0x0505);
            _canvas.Element.OwnerDocument.DiagnosticSink?.Invoke(new Diagnostic(DiagnosticCode.LimitExceeded, Severity.Warning, "WebGL GPU memory budget exceeded.", null, "webgl"));
            return false;
        }
        AllocatedBytes = (int)Math.Max(0, next);
        return true;
    }

    private void Release(long bytes) => AllocatedBytes = (int)Math.Max(0, AllocatedBytes - bytes);

    // ---- textures ----

    public GlTexture CreateTexture(uint handle) => Textures[handle] = new GlTexture();

    public void DeleteTexture(uint handle)
    {
        if (!Textures.Remove(handle, out var texture)) return;
        Release(texture.Levels.Sum(level => (long)(level?.ByteLength ?? 0)));
        TextureParameters.Remove(handle);
        for (var unit = 0; unit < TextureUnits.Length; unit++)
            if (TextureUnits[unit] == handle) TextureUnits[unit] = null;
        foreach (var framebuffer in Framebuffers.Values)
            foreach (var point in framebuffer.Attachments.Where(pair => pair.Value.Texture == handle).Select(pair => pair.Key).ToArray())
                framebuffer.Attachments.Remove(point);
    }

    /// <summary>
    /// Gives a texture level storage of the given size and format, zeroed, replacing what the level held. Respecifying level 0 with another size
    /// or format drops the levels above it, which could not match any more.
    /// </summary>
    public GlImage? AllocateTextureLevel(uint handle, int level, int width, int height, GlFormat format)
    {
        if (!Textures.TryGetValue(handle, out var texture)) return null;
        if (width < 0 || height < 0 || width > 4096 || height > 4096 || level < 0 || level > 15) { SetError(0x0501); return null; }
        var bytes = (long)width * height * Math.Max(format.BytesPerTexel, 1) + (format.HasStencil ? (long)width * height : 0);
        long released = texture.Level(level)?.ByteLength ?? 0;
        if (level == 0 && texture.Base is { } existing && (existing.Width != width || existing.Height != height || existing.Format != format))
            released += texture.Levels.Skip(1).Sum(l => (long)(l?.ByteLength ?? 0));
        if (!Reserve(bytes, released)) return null;
        if (level == 0 && texture.Base is { } old && (old.Width != width || old.Height != height || old.Format != format))
            texture.Levels.RemoveRange(1, texture.Levels.Count - 1);
        while (texture.Levels.Count <= level) texture.Levels.Add(null);
        var image = new GlImage(width, height, format);
        texture.Levels[level] = image;
        texture.Generated = false;
        return image;
    }

    /// <summary>Builds the mip chain of level 0 by averaging 2x2 blocks. A depth or stencil texture has none.</summary>
    public bool GenerateMipmaps(uint handle)
    {
        if (!Textures.TryGetValue(handle, out var texture) || texture.Base is not { } baseLevel || baseLevel.Format.IsDepth || !baseLevel.Format.IsColor)
            return false;
        var width = baseLevel.Width;
        var height = baseLevel.Height;
        if (width <= 0 || height <= 0) return true;
        var previous = baseLevel;
        var levels = new List<GlImage?> { baseLevel };
        long total = 0;
        while (width > 1 || height > 1)
        {
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
            total += (long)width * height * baseLevel.Format.BytesPerTexel;
        }
        var released = texture.Levels.Skip(1).Sum(l => (long)(l?.ByteLength ?? 0));
        if (!Reserve(total, released)) return false;
        width = baseLevel.Width;
        height = baseLevel.Height;
        while (width > 1 || height > 1)
        {
            var nextWidth = Math.Max(1, width / 2);
            var nextHeight = Math.Max(1, height / 2);
            var next = new GlImage(nextWidth, nextHeight, baseLevel.Format);
            for (var y = 0; y < nextHeight; y++)
            {
                var y0 = Math.Min(y * 2, height - 1);
                var y1 = Math.Min(y0 + 1, height - 1);
                for (var x = 0; x < nextWidth; x++)
                {
                    var x0 = Math.Min(x * 2, width - 1);
                    var x1 = Math.Min(x0 + 1, width - 1);
                    var sum = previous.Load(y0 * width + x0) + previous.Load(y0 * width + x1) + previous.Load(y1 * width + x0) + previous.Load(y1 * width + x1);
                    next.Store(y * nextWidth + x, sum * 0.25f);
                }
            }
            levels.Add(next);
            previous = next;
            width = nextWidth;
            height = nextHeight;
        }
        texture.Levels.Clear();
        texture.Levels.AddRange(levels);
        texture.Generated = true;
        return true;
    }

    /// <summary>Writes RGBA8 pixels (a decoded image, a canvas, ImageData) into a texture level, flipped and premultiplied as the unpack state says.</summary>
    public void UploadRgba8(uint handle, int level, int x, int y, int width, int height, ReadOnlySpan<byte> pixels)
    {
        if (!Textures.TryGetValue(handle, out var texture) || texture.Level(level) is not { } image) return;
        GlPixels.Write(image, x, y, width, height, GlPixels.FromRgba8(pixels, width, height, UnpackFlipY, UnpackPremultiplyAlpha));
    }

    /// <summary>
    /// Copies a region of the read framebuffer's colour into a level of a texture (copyTexImage2D, copyTexSubImage2D). Pixels outside the
    /// framebuffer come out as transparent black; a format without alpha reads as opaque.
    /// </summary>
    public void CopyFramebufferToTexture(uint handle, int level, int xOffset, int yOffset, int x, int y, int width, int height)
    {
        if (!Textures.TryGetValue(handle, out var texture) || texture.Level(level) is not { } target) return;
        var texels = new Vector4[width * height];
        if (ReadImage() is { } source)
        {
            for (var row = 0; row < height; row++)
                for (var column = 0; column < width; column++)
                {
                    var sx = x + column;
                    var sy = y + row;
                    if (sx < 0 || sy < 0 || sx >= source.Width || sy >= source.Height) continue;
                    texels[row * width + column] = source.LoadColor(sy * source.Width + sx);
                }
        }
        GlPixels.Write(target, xOffset, yOffset, width, height, texels);
    }

    // ---- renderbuffers ----

    public void DeleteRenderbuffer(uint handle)
    {
        if (!Renderbuffers.Remove(handle, out var renderbuffer)) return;
        Release(renderbuffer.Image?.ByteLength ?? 0);
        if (BoundRenderbuffer == handle) BoundRenderbuffer = null;
        foreach (var framebuffer in Framebuffers.Values)
            foreach (var point in framebuffer.Attachments.Where(pair => pair.Value.Renderbuffer == handle).Select(pair => pair.Key).ToArray())
                framebuffer.Attachments.Remove(point);
    }

    public bool AllocateRenderbuffer(uint handle, uint internalFormat, int width, int height)
    {
        if (!Renderbuffers.TryGetValue(handle, out var renderbuffer)) return false;
        var format = GlFormats.FromSized(internalFormat);
        if (format is not { } resolved) { SetError(0x0500); return false; }
        if (width < 0 || height < 0 || width > 4096 || height > 4096) { SetError(0x0501); return false; }
        var bytes = (long)width * height * Math.Max(resolved.BytesPerTexel, 1) + (resolved.HasStencil ? (long)width * height : 0);
        if (!Reserve(bytes, renderbuffer.Image?.ByteLength ?? 0)) return false;
        renderbuffer.Image = new GlImage(width, height, resolved);
        renderbuffer.Format = internalFormat;
        if (resolved.IsDepth) FillDepth(renderbuffer.Image, 1f);
        return true;
    }

    public static void FillDepth(GlImage image, float value)
    {
        var span = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(image.Data.AsSpan());
        span.Fill(value);
    }

    // ---- framebuffers ----

    public void DeleteFramebuffer(uint handle)
    {
        Framebuffers.Remove(handle);
        if (BoundFramebuffer == handle) BoundFramebuffer = null;
        if (BoundReadFramebuffer == handle) BoundReadFramebuffer = null;
        if (BoundDrawFramebuffer == handle) BoundDrawFramebuffer = null;
    }

    public GlImage? AttachmentImage(GlAttachment attachment)
    {
        if (attachment.Texture is { } texture) return Textures.TryGetValue(texture, out var t) ? t.Level(attachment.Level) : null;
        if (attachment.Renderbuffer is { } renderbuffer) return Renderbuffers.TryGetValue(renderbuffer, out var r) ? r.Image : null;
        return null;
    }

    private bool ColorRenderable(GlFormat format)
    {
        if (!format.IsColor || format.Layout is GlLayout.L or GlLayout.LA or GlLayout.A) return false;
        return format.Storage switch
        {
            GlStorage.U8 => true,
            GlStorage.F16 => EnabledExtensions.Contains("EXT_color_buffer_float") || EnabledExtensions.Contains("EXT_color_buffer_half_float"),
            _ => EnabledExtensions.Contains("EXT_color_buffer_float") || EnabledExtensions.Contains("WEBGL_color_buffer_float"),
        };
    }

    /// <summary>The completeness of a framebuffer object: COMPLETE, or the reason it is not.</summary>
    public uint FramebufferStatus(GlFramebufferObject framebuffer)
    {
        if (framebuffer.Attachments.Count == 0) return 0x8CD7;
        int? width = null, height = null;
        foreach (var (point, attachment) in framebuffer.Attachments)
        {
            if (AttachmentImage(attachment) is not { } image) return 0x8CD6;
            var format = image.Format;
            var valid = point switch
            {
                >= ColorAttachment0 and < ColorAttachment0 + 16 => point - ColorAttachment0 < MaxColorAttachments && ColorRenderable(format),
                DepthAttachment => format.IsDepth,
                StencilAttachment => format.HasStencil,
                DepthStencilAttachment => format.Layout == GlLayout.DepthStencil,
                _ => false,
            };
            if (!valid) return 0x8CD6;
            if (width is null) { width = image.Width; height = image.Height; }
            else if (width != image.Width || height != image.Height) return 0x8CD9;
            if (image.Width == 0 || image.Height == 0) return 0x8CD6;
        }
        return 0x8CD5;
    }

    /// <summary>The framebuffer a draw goes to, or null when it is the canvas's.</summary>
    public GlFramebufferObject? DrawFramebufferObject => (BoundDrawFramebuffer ?? BoundFramebuffer) is { } handle && Framebuffers.TryGetValue(handle, out var fb) ? fb : null;

    /// <summary>The framebuffer a read comes from, or null when it is the canvas's.</summary>
    public GlFramebufferObject? ReadFramebufferObject => (BoundReadFramebuffer ?? BoundFramebuffer) is { } handle && Framebuffers.TryGetValue(handle, out var fb) ? fb : null;

    private GlImage? AttachedImage(GlFramebufferObject framebuffer, uint point) =>
        framebuffer.Attachments.TryGetValue(point, out var attachment) ? AttachmentImage(attachment) : null;

    /// <summary>The depth image of a framebuffer object: a depth attachment, or the depth half of a combined one.</summary>
    public GlImage? DepthImage(GlFramebufferObject framebuffer) => AttachedImage(framebuffer, DepthAttachment) ?? AttachedImage(framebuffer, DepthStencilAttachment);

    public GlImage? StencilImage(GlFramebufferObject framebuffer) => AttachedImage(framebuffer, StencilAttachment) ?? AttachedImage(framebuffer, DepthStencilAttachment);

    /// <summary>The colour image a read takes from: the read buffer of the bound framebuffer, or the resolved canvas drawing buffer.</summary>
    public GlImage? ReadImage()
    {
        if (ReadFramebufferObject is { } framebuffer)
        {
            var index = framebuffer.ReadBuffer - ColorAttachment0;
            return framebuffer.ReadBuffer is >= ColorAttachment0 and < ColorAttachment0 + 16 ? AttachedImage(framebuffer, framebuffer.ReadBuffer) : null;
        }
        if (ReadBuffer == 0 || BeginDrawingBufferUse() is not { } buffer) return null;
        var resolved = new GlImage(buffer.Width, buffer.Height, buffer.Image.Format);
        buffer.Resolve().CopyTo(resolved.Data, 0);
        return resolved;
    }
}
