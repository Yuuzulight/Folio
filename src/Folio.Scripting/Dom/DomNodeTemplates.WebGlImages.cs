using System.Buffers.Binary;
using System.Numerics;
using Folio.Scripting.WebGl;

namespace Folio.Scripting.Dom;

/// <summary>The WebGL calls that deal in images: textures and their levels, renderbuffers, framebuffer attachments, draw and read buffers, and reading pixels back.</summary>
internal sealed partial class DomNodeTemplates
{
    // The bytes a typed array (or an ArrayBuffer) views, whatever its element type.
    private static bool TypedBytes(IScriptEngine engine, ScriptValue value, out byte[] bytes)
    {
        if (engine.TryGetArrayBufferBytes(value, out bytes)) return true;
        var buffer = engine.GetProperty(value, "buffer");
        if (!buffer.IsUndefined && engine.TryGetArrayBufferBytes(buffer, out var all))
        {
            var offset = engine.GetProperty(value, "byteOffset") is { IsUndefined: false } o ? WebIdlConversions.ToInt32(o) : 0;
            var length = engine.GetProperty(value, "byteLength") is { IsUndefined: false } l ? WebIdlConversions.ToInt32(l) : all.Length - offset;
            if (offset >= 0 && length >= 0 && offset + length <= all.Length)
            {
                bytes = all.AsSpan(offset, length).ToArray();
                return true;
            }
        }
        bytes = [];
        return false;
    }

    // Pixel data given as a plain array (or a typed array the engine exposes as one), turned into the bytes the upload's type describes.
    private bool PixelBytes(ScriptValue value, uint type, out byte[] bytes)
    {
        if (TypedBytes(_context.Engine, value, out bytes)) return true;
        if (!_context.Engine.TryGetArrayItems(value, out var items)) return false;
        var size = type switch { 0x1401 => 1, 0x1403 or GlFormats.HalfFloat or GlFormats.HalfFloatOes or 0x8363 or 0x8033 or 0x8034 => 2, _ => 4 };
        bytes = new byte[items.Count * size];
        for (var i = 0; i < items.Count; i++)
        {
            var span = bytes.AsSpan(i * size, size);
            switch (type)
            {
                case 0x1401: span[0] = (byte)Math.Clamp(WebIdlConversions.ToInt32(items[i]), 0, 255); break;
                case 0x1406: BinaryPrimitives.WriteSingleLittleEndian(span, (float)WebIdlConversions.ToDouble(items[i])); break;
                case GlFormats.HalfFloat or GlFormats.HalfFloatOes: BinaryPrimitives.WriteHalfLittleEndian(span, (Half)WebIdlConversions.ToDouble(items[i])); break;
                case 0x1403 or 0x8363 or 0x8033 or 0x8034: BinaryPrimitives.WriteUInt16LittleEndian(span, (ushort)WebIdlConversions.ToInt32(items[i])); break;
                default: BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)WebIdlConversions.ToDouble(items[i])); break;
            }
        }
        return true;
    }

    private static int ComponentBytes(uint type) => type switch { 0x1401 => 1, 0x1403 or GlFormats.HalfFloat or GlFormats.HalfFloatOes => 2, _ => 4 };

    private static bool IsColorPoint(uint attachment) => attachment >= WebGlContextState.ColorAttachment0 && attachment < WebGlContextState.ColorAttachment0 + 16;

    private void DefineWebGlImageMethods(HostObjectTemplate t)
    {
        t.DefineMethod("createTexture", (target, _, _) =>
        {
            var state = (WebGlContextState)target;
            var handle = state.CreateHandle();
            state.CreateTexture(handle);
            state.TextureParameters[handle] = [];
            return GlWrap(state, GlKind.Texture, handle);
        });




        // 3D textures are stored as one tall 2D image until volume textures exist.
        t.DefineMethod("bindTexture", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            if (args.Length < 2) { state.SetError(0x0502); return ScriptValue.Undefined; }
            var bindingTarget = WebIdlConversions.ToUint32(args[0]);
            if (state.TextureBindings(bindingTarget) is not { } units) { state.SetError(0x0500); return ScriptValue.Undefined; }
            if (args[1].IsNullOrUndefined) { units[state.UnitIndex] = null; return ScriptValue.Undefined; }
            var handle = WebIdlConversions.ToUint32(args[1]);
            if (!state.Textures.TryGetValue(handle, out var texture)) { state.SetError(0x0502); return ScriptValue.Undefined; }
            // A texture takes the target it is first bound to and keeps it.
            if (texture.Target == 0) texture.Target = bindingTarget;
            else if (texture.Target != bindingTarget) { state.SetError(0x0502); return ScriptValue.Undefined; }
            units[state.UnitIndex] = handle;
            return ScriptValue.Undefined;
        });

        t.DefineMethod("texParameteri", (target, _, args) => TexParameter((WebGlContextState)target, args, integer: true));
        t.DefineMethod("texParameterf", (target, _, args) => TexParameter((WebGlContextState)target, args, integer: false));
        t.DefineMethod("getTexParameter", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            if (args.Length < 2 || state.BoundTextureAt(WebIdlConversions.ToUint32(args[0])) is not { } handle) { state.SetError(args.Length < 2 ? 0x0502 : state.TextureBindings(WebIdlConversions.ToUint32(args[0])) is null ? 0x0500 : 0x0502); return ScriptValue.Null; }
            return state.TextureParameters.TryGetValue(handle, out var parameters) && parameters.TryGetValue(WebIdlConversions.ToUint32(args[1]), out var value) ? ScriptValue.FromNumber(value) : ScriptValue.Null;
        });

        t.DefineMethod("texImage2D", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            if (args.Length < 6) { state.SetError(0x0502); return ScriptValue.Undefined; }
            if (!ImageTexture(state, WebIdlConversions.ToUint32(args[0]), out var handle, out var ignoredTexture, out var face)) return ScriptValue.Undefined;
            var level = WebIdlConversions.ToInt32(args[1]);
            var internalFormat = WebIdlConversions.ToUint32(args[2]);

            // texImage2D(target, level, internalformat, format, type, source)
            if (args.Length == 6)
            {
                var format = WebIdlConversions.ToUint32(args[3]);
                var type = WebIdlConversions.ToUint32(args[4]);
                if (GlFormats.Resolve(internalFormat, format, type) is not { } resolved) { state.SetError(0x0500); return ScriptValue.Undefined; }
                if (!TryExtractImageSource(args[5], out var width, out var height, out var sourcePixels) || sourcePixels is null) { state.SetError(0x0502); return ScriptValue.Undefined; }
                if (face > 0 || state.Textures[handle].Target == WebGlContextState.TextureCubeMap)
                    if (width != height) { state.SetError(0x0501); return ScriptValue.Undefined; }
                if (state.AllocateTextureLevel(handle, level, width, height, resolved, face) is not { } image) return ScriptValue.Undefined;
                GlPixels.Write(image, 0, 0, width, height, GlPixels.FromRgba8(sourcePixels, width, height, state.UnpackFlipY, state.UnpackPremultiplyAlpha));
                return ScriptValue.Undefined;
            }

            // texImage2D(target, level, internalformat, width, height, border, format, type, pixels[, srcOffset])
            if (args.Length < 9) { state.SetError(0x0502); return ScriptValue.Undefined; }
            {
                var width = WebIdlConversions.ToInt32(args[3]);
                var height = WebIdlConversions.ToInt32(args[4]);
                var format = WebIdlConversions.ToUint32(args[6]);
                var type = WebIdlConversions.ToUint32(args[7]);
                if (WebIdlConversions.ToInt32(args[5]) != 0 || width < 0 || height < 0) { state.SetError(0x0501); return ScriptValue.Undefined; }
                if (state.Textures[handle].Target == WebGlContextState.TextureCubeMap && width != height) { state.SetError(0x0501); return ScriptValue.Undefined; }
                if (GlFormats.Resolve(internalFormat, format, type) is not { } resolved) { state.SetError(0x0500); return ScriptValue.Undefined; }
                Vector4[]? texels = null;
                byte[]? stencil = null;
                if (!args[8].IsNullOrUndefined)
                {
                    if (!PixelBytes(args[8], type, out var bytes)) { state.SetError(0x0502); return ScriptValue.Undefined; }
                    var skip = args.Length > 9 ? Math.Max(0, WebIdlConversions.ToInt32(args[9])) * ComponentBytes(type) : 0;
                    if (skip > bytes.Length) { state.SetError(0x0502); return ScriptValue.Undefined; }
                    texels = GlPixels.Decode(bytes.AsSpan(skip), width, height, format, type, state.UnpackAlignment, state.UnpackFlipY, state.UnpackPremultiplyAlpha, out stencil);
                    if (texels is null) { state.SetError(0x0502); return ScriptValue.Undefined; }
                }
                if (state.AllocateTextureLevel(handle, level, width, height, resolved, face) is not { } image) return ScriptValue.Undefined;
                if (texels is not null) GlPixels.Write(image, 0, 0, width, height, texels, stencil);
                return ScriptValue.Undefined;
            }
        });

        t.DefineMethod("texSubImage2D", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            if (args.Length != 7 && args.Length < 9) { state.SetError(0x0502); return ScriptValue.Undefined; }
            if (!ImageTexture(state, WebIdlConversions.ToUint32(args[0]), out var ignoredHandle, out var texture, out var face)) return ScriptValue.Undefined;
            var level = WebIdlConversions.ToInt32(args[1]);
            if (texture.Level(level, face) is not { } image) { state.SetError(0x0501); return ScriptValue.Undefined; }
            var x = WebIdlConversions.ToInt32(args[2]);
            var y = WebIdlConversions.ToInt32(args[3]);

            // texSubImage2D(target, level, xoffset, yoffset, format, type, source)
            if (args.Length == 7)
            {
                if (!TryExtractImageSource(args[6], out var sourceWidth, out var sourceHeight, out var sourcePixels) || sourcePixels is null) { state.SetError(0x0502); return ScriptValue.Undefined; }
                if (x < 0 || y < 0 || x + sourceWidth > image.Width || y + sourceHeight > image.Height) { state.SetError(0x0501); return ScriptValue.Undefined; }
                GlPixels.Write(image, x, y, sourceWidth, sourceHeight, GlPixels.FromRgba8(sourcePixels, sourceWidth, sourceHeight, state.UnpackFlipY, state.UnpackPremultiplyAlpha));
                return ScriptValue.Undefined;
            }

            // texSubImage2D(target, level, xoffset, yoffset, width, height, format, type, pixels[, srcOffset])
            var width = WebIdlConversions.ToInt32(args[4]);
            var height = WebIdlConversions.ToInt32(args[5]);
            var format = WebIdlConversions.ToUint32(args[6]);
            var type = WebIdlConversions.ToUint32(args[7]);
            if (width < 0 || height < 0 || x < 0 || y < 0 || x + width > image.Width || y + height > image.Height) { state.SetError(0x0501); return ScriptValue.Undefined; }
            if (args[8].IsNullOrUndefined || !PixelBytes(args[8], type, out var bytes)) { state.SetError(0x0502); return ScriptValue.Undefined; }
            var skip = args.Length > 9 ? Math.Max(0, WebIdlConversions.ToInt32(args[9])) * ComponentBytes(type) : 0;
            if (skip > bytes.Length || GlPixels.Decode(bytes.AsSpan(skip), width, height, format, type, state.UnpackAlignment, state.UnpackFlipY, state.UnpackPremultiplyAlpha, out var stencil) is not { } texels)
            {
                state.SetError(0x0502);
                return ScriptValue.Undefined;
            }
            GlPixels.Write(image, x, y, width, height, texels, stencil);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("texStorage2D", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            if (args.Length < 5) { state.SetError(0x0502); return ScriptValue.Undefined; }
            var storageTarget = WebIdlConversions.ToUint32(args[0]);
            if (storageTarget is not (WebGlContextState.Texture2D or WebGlContextState.TextureCubeMap)) { state.SetError(0x0500); return ScriptValue.Undefined; }
            if (state.BoundTextureAt(storageTarget) is not { } handle || !state.Textures.TryGetValue(handle, out var texture)) { state.SetError(0x0502); return ScriptValue.Undefined; }
            var levels = WebIdlConversions.ToInt32(args[1]);
            var width = WebIdlConversions.ToInt32(args[3]);
            var height = WebIdlConversions.ToInt32(args[4]);
            if (levels < 1 || levels > 16 || width < 1 || height < 1 || (storageTarget == WebGlContextState.TextureCubeMap && width != height)) { state.SetError(0x0501); return ScriptValue.Undefined; }
            if (GlFormats.FromSized(WebIdlConversions.ToUint32(args[2])) is not { } format) { state.SetError(0x0500); return ScriptValue.Undefined; }
            for (var face = 0; face < (storageTarget == WebGlContextState.TextureCubeMap ? 6 : 1); face++)
            {
                var levelWidth = width;
                var levelHeight = height;
                for (var level = 0; level < levels; level++)
                {
                    if (state.AllocateTextureLevel(handle, level, levelWidth, levelHeight, format, face) is null) return ScriptValue.Undefined;
                    levelWidth = Math.Max(1, levelWidth / 2);
                    levelHeight = Math.Max(1, levelHeight / 2);
                }
            }
            texture.Immutable = true;
            return ScriptValue.Undefined;
        });

        t.DefineMethod("copyTexImage2D", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            if (args.Length < 8) { state.SetError(0x0502); return ScriptValue.Undefined; }
            if (!ImageTexture(state, WebIdlConversions.ToUint32(args[0]), out var handle, out var ignoredTexture, out var face)) return ScriptValue.Undefined;
            var level = WebIdlConversions.ToInt32(args[1]);
            var internalFormat = WebIdlConversions.ToUint32(args[2]);
            var width = WebIdlConversions.ToInt32(args[5]);
            var height = WebIdlConversions.ToInt32(args[6]);
            if (level < 0 || level > 15 || width < 0 || height < 0 || width > 4096 || height > 4096 || WebIdlConversions.ToInt32(args[7]) != 0) { state.SetError(0x0501); return ScriptValue.Undefined; }
            if ((GlFormats.FromSized(internalFormat) ?? GlFormats.Resolve(internalFormat, internalFormat, 0x1401)) is not { IsColor: true } format) { state.SetError(0x0500); return ScriptValue.Undefined; }
            if (state.AllocateTextureLevel(handle, level, width, height, format, face) is null) return ScriptValue.Undefined;
            state.CopyFramebufferToTexture(handle, level, face, 0, 0, WebIdlConversions.ToInt32(args[3]), WebIdlConversions.ToInt32(args[4]), width, height);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("copyTexSubImage2D", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            if (args.Length < 8) { state.SetError(0x0502); return ScriptValue.Undefined; }
            if (!ImageTexture(state, WebIdlConversions.ToUint32(args[0]), out var handle, out var texture, out var face)) return ScriptValue.Undefined;
            var level = WebIdlConversions.ToInt32(args[1]);
            var xOffset = WebIdlConversions.ToInt32(args[2]);
            var yOffset = WebIdlConversions.ToInt32(args[3]);
            var width = WebIdlConversions.ToInt32(args[6]);
            var height = WebIdlConversions.ToInt32(args[7]);
            if (width < 0 || height < 0 || xOffset < 0 || yOffset < 0 || texture.Level(level, face) is not { } existing
                || xOffset + width > existing.Width || yOffset + height > existing.Height || width > 4096 || height > 4096)
            {
                state.SetError(0x0501);
                return ScriptValue.Undefined;
            }
            state.CopyFramebufferToTexture(handle, level, face, xOffset, yOffset, WebIdlConversions.ToInt32(args[4]), WebIdlConversions.ToInt32(args[5]), width, height);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("generateMipmap", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            var mipmapTarget = args.Length > 0 ? WebIdlConversions.ToUint32(args[0]) : 0;
            if (state.TextureBindings(mipmapTarget) is null) { state.SetError(0x0500); return ScriptValue.Undefined; }
            if (state.BoundTextureAt(mipmapTarget) is not { } handle || !state.Textures.ContainsKey(handle)) { state.SetError(0x0502); return ScriptValue.Undefined; }
            if (!state.GenerateMipmaps(handle) && state.Error == 0) state.SetError(0x0502);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("getTexLevelParameter", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            if (args.Length < 3 || !ImageTexture(state, WebIdlConversions.ToUint32(args[0]), out var ignoredHandle, out var texture, out var face) || texture.Level(WebIdlConversions.ToInt32(args[1]), face) is not { } image)
                return ScriptValue.Null;
            return WebIdlConversions.ToUint32(args[2]) switch
            {
                0x1000 => ScriptValue.FromNumber(image.Width),
                0x1001 => ScriptValue.FromNumber(image.Height),
                0x8071 => ScriptValue.FromNumber(image.Depth),
                0x1003 => ScriptValue.FromNumber(image.Format.InternalFormat),
                _ => ScriptValue.Null,
            };
        });

        t.DefineMethod("texStorage3D", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            if (state.BoundTexture is not { } handle || !state.Textures.ContainsKey(handle) || args.Length < 6) { state.SetError(0x0502); return ScriptValue.Undefined; }
            if (GlFormats.FromSized(WebIdlConversions.ToUint32(args[2])) is not { } format) { state.SetError(0x0500); return ScriptValue.Undefined; }
            var width = Math.Clamp(WebIdlConversions.ToInt32(args[3]), 0, 2048);
            var height = Math.Clamp(WebIdlConversions.ToInt32(args[4]), 0, 2048);
            var depth = Math.Clamp(WebIdlConversions.ToInt32(args[5]), 0, 256);
            state.AllocateTextureLevel(handle, 0, width, Math.Min(height * depth, 4096), format);
            return ScriptValue.Undefined;
        });





        // ---- renderbuffers ----

        t.DefineMethod("createRenderbuffer", (target, _, _) =>
        {
            var state = (WebGlContextState)target;
            var handle = state.CreateHandle();
            state.Renderbuffers[handle] = new GlRenderbuffer();
            return GlWrap(state, GlKind.Renderbuffer, handle);
        });

        t.DefineMethod("renderbufferStorage", (target, _, args) => RenderbufferStorage((WebGlContextState)target, args, 1));
        t.DefineMethod("renderbufferStorageMultisample", (target, _, args) => RenderbufferStorage((WebGlContextState)target, args, 2));

        t.DefineMethod("getRenderbufferParameter", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            if (state.BoundRenderbuffer is not { } handle || args.Length < 2 || WebIdlConversions.ToUint32(args[0]) != 0x8D41 || !state.Renderbuffers.TryGetValue(handle, out var renderbuffer))
                return ScriptValue.Null;
            var image = renderbuffer.Image;
            var format = image?.Format;
            return WebIdlConversions.ToUint32(args[1]) switch
            {
                0x8D42 => ScriptValue.FromNumber(image?.Width ?? 0),
                0x8D43 => ScriptValue.FromNumber(image?.Height ?? 0),
                0x8D44 => ScriptValue.FromNumber(renderbuffer.Format),
                0x8D50 or 0x8D51 or 0x8D52 => ScriptValue.FromNumber(format is { IsColor: true } f ? ComponentSize(f) : 0),
                0x8D53 => ScriptValue.FromNumber(format is { IsColor: true, HasAlpha: true } a ? ComponentSize(a) : 0),
                0x8D54 => ScriptValue.FromNumber(format is { IsDepth: true } ? 24 : 0),
                0x8D55 => ScriptValue.FromNumber(format is { HasStencil: true } ? 8 : 0),
                0x8CAB => ScriptValue.FromNumber(0),
                _ => ScriptValue.Null,
            };
        });

        // ---- framebuffers ----

        t.DefineMethod("createFramebuffer", (target, _, _) =>
        {
            var state = (WebGlContextState)target;
            var handle = state.CreateHandle();
            state.Framebuffers[handle] = new GlFramebufferObject();
            return GlWrap(state, GlKind.Framebuffer, handle);
        });

        t.DefineMethod("isFramebuffer", (target, _, args) => ScriptValue.FromBoolean(args.Length > 0 && ((WebGlContextState)target).Framebuffers.ContainsKey(WebIdlConversions.ToUint32(args[0]))));

        t.DefineMethod("bindFramebuffer", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            if (args.Length < 2) { state.SetError(0x0502); return ScriptValue.Undefined; }
            var fbTarget = WebIdlConversions.ToUint32(args[0]);
            var handle = args[1].IsNullOrUndefined ? (uint?)null : WebIdlConversions.ToUint32(args[1]);
            if (handle is { } h && h != 0 && !state.Framebuffers.ContainsKey(h)) { state.SetError(0x0502); return ScriptValue.Undefined; }
            var bindHandle = handle == 0 ? null : handle;
            if (fbTarget == 0x8CA8 /* READ_FRAMEBUFFER */)
                state.BoundReadFramebuffer = bindHandle;
            else if (fbTarget == 0x8CA9 /* DRAW_FRAMEBUFFER */)
                state.BoundDrawFramebuffer = bindHandle;
            else if (fbTarget == 0x8D40 /* FRAMEBUFFER */)
            {
                state.BoundFramebuffer = bindHandle;
                state.BoundReadFramebuffer = bindHandle;
                state.BoundDrawFramebuffer = bindHandle;
            }
            else state.SetError(0x0500);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("framebufferTexture2D", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            if (args.Length < 5) { state.SetError(0x0502); return ScriptValue.Undefined; }
            if (FramebufferFor(state, WebIdlConversions.ToUint32(args[0])) is not { } framebuffer) { state.SetError(WebIdlConversions.ToUint32(args[0]) is 0x8D40 or 0x8CA8 or 0x8CA9 ? 0x0502 : 0x0500); return ScriptValue.Undefined; }
            var point = WebIdlConversions.ToUint32(args[1]);
            if (!ValidAttachmentPoint(point)) { state.SetError(0x0500); return ScriptValue.Undefined; }
            if (!WebGlContextState.TryImageTarget(WebIdlConversions.ToUint32(args[2]), out var textureTarget, out var face)) { state.SetError(0x0500); return ScriptValue.Undefined; }
            var level = WebIdlConversions.ToInt32(args[4]);
            if (args[3].IsNullOrUndefined) { framebuffer.Attachments.Remove(point); return ScriptValue.Undefined; }
            var texture = WebIdlConversions.ToUint32(args[3]);
            if (level < 0 || !state.Textures.TryGetValue(texture, out var found) || found.Target != textureTarget || found.Level(level, face) is null && level != 0) { state.SetError(level < 0 ? 0x0501 : 0x0502); return ScriptValue.Undefined; }
            framebuffer.Attachments[point] = new GlAttachment(texture, null, level, face);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("framebufferRenderbuffer", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            if (args.Length < 4) { state.SetError(0x0502); return ScriptValue.Undefined; }
            if (FramebufferFor(state, WebIdlConversions.ToUint32(args[0])) is not { } framebuffer) { state.SetError(WebIdlConversions.ToUint32(args[0]) is 0x8D40 or 0x8CA8 or 0x8CA9 ? 0x0502 : 0x0500); return ScriptValue.Undefined; }
            var point = WebIdlConversions.ToUint32(args[1]);
            if (!ValidAttachmentPoint(point) || WebIdlConversions.ToUint32(args[2]) != 0x8D41) { state.SetError(0x0500); return ScriptValue.Undefined; }
            if (args[3].IsNullOrUndefined) { framebuffer.Attachments.Remove(point); return ScriptValue.Undefined; }
            var renderbuffer = WebIdlConversions.ToUint32(args[3]);
            if (!state.Renderbuffers.ContainsKey(renderbuffer)) { state.SetError(0x0502); return ScriptValue.Undefined; }
            framebuffer.Attachments[point] = new GlAttachment(null, renderbuffer, 0);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("checkFramebufferStatus", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            var framebufferTarget = args.Length > 0 ? WebIdlConversions.ToUint32(args[0]) : 0;
            if (framebufferTarget is not (0x8D40 or 0x8CA8 or 0x8CA9)) { state.SetError(0x0500); return ScriptValue.FromNumber(0); }
            var bound = framebufferTarget == 0x8CA8 ? state.BoundReadFramebuffer ?? state.BoundFramebuffer : state.BoundDrawFramebuffer ?? state.BoundFramebuffer;
            if (bound is null) return ScriptValue.FromNumber(0x8CD5);
            return ScriptValue.FromNumber(state.Framebuffers.TryGetValue(bound.Value, out var framebuffer) ? state.FramebufferStatus(framebuffer) : 0x8CDD);
        });

        t.DefineMethod("getFramebufferAttachmentParameter", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            if (args.Length < 3) return ScriptValue.Null;
            var framebuffer = FramebufferFor(state, WebIdlConversions.ToUint32(args[0]));
            var point = WebIdlConversions.ToUint32(args[1]);
            var pname = WebIdlConversions.ToUint32(args[2]);
            if (framebuffer is null)
            {
                // The canvas's own framebuffer: BACK, DEPTH and STENCIL.
                if (point is not (0x0405 or 0x1801 or 0x1802)) { state.SetError(0x0500); return ScriptValue.Null; }
                return pname switch
                {
                    0x8CD0 => ScriptValue.FromNumber(0x1701), // FRAMEBUFFER_DEFAULT
                    0x8211 => ScriptValue.FromNumber(0x8C17),
                    0x8212 or 0x8213 or 0x8214 => ScriptValue.FromNumber(point == 0x0405 ? 8 : 0),
                    0x8215 => ScriptValue.FromNumber(point == 0x0405 && state.Alpha ? 8 : 0),
                    0x8216 => ScriptValue.FromNumber(point == 0x1801 && state.Depth ? 24 : 0),
                    0x8217 => ScriptValue.FromNumber(point == 0x1802 && state.Stencil ? 8 : 0),
                    0x8210 => ScriptValue.FromNumber(0x2601),
                    _ => ScriptValue.Null,
                };
            }
            if (!ValidAttachmentPoint(point)) { state.SetError(0x0500); return ScriptValue.Null; }
            if (!framebuffer.Attachments.TryGetValue(point, out var attachment)) return pname == 0x8CD0 ? ScriptValue.FromNumber(0) : ScriptValue.Null;
            var image = state.AttachmentImage(attachment);
            return pname switch
            {
                0x8CD0 => ScriptValue.FromNumber(attachment.Texture is not null ? 0x1702 : 0x8D41),
                0x8CD1 => attachment.Texture is { } texture ? GlWrap(state, GlKind.Texture, texture) : GlWrap(state, GlKind.Renderbuffer, attachment.Renderbuffer!.Value),
                0x8CD2 when attachment.Texture is not null => ScriptValue.FromNumber(attachment.Level),
                0x8CD3 when attachment.Texture is not null => ScriptValue.FromNumber(0),
                0x8211 when image is not null => ScriptValue.FromNumber(image.Format.IsFloat || image.Format.IsDepth ? 0x1406 : 0x8C17),
                0x8212 or 0x8213 or 0x8214 when image is { Format.IsColor: true } => ScriptValue.FromNumber(ComponentSize(image.Format)),
                0x8215 when image is not null => ScriptValue.FromNumber(image.Format.IsColor && image.Format.HasAlpha ? ComponentSize(image.Format) : 0),
                0x8216 when image is not null => ScriptValue.FromNumber(image.Format.IsDepth ? 24 : 0),
                0x8217 when image is not null => ScriptValue.FromNumber(image.Format.HasStencil ? 8 : 0),
                0x8210 => ScriptValue.FromNumber(0x2601),
                _ => ScriptValue.Null,
            };
        });

        t.DefineMethod("drawBuffers", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            if (args.Length == 0 || !_context.Engine.TryGetArrayItems(args[0], out var buffers)) { state.SetError(0x0502); return ScriptValue.Undefined; }
            var wanted = buffers.Select(b => WebIdlConversions.ToUint32(b)).ToArray();
            if (wanted.Length > WebGlContextState.MaxColorAttachments) { state.SetError(0x0501); return ScriptValue.Undefined; }
            if (state.DrawFramebufferObject is { } framebuffer)
            {
                for (var i = 0; i < wanted.Length; i++)
                    if (wanted[i] != 0 && wanted[i] != WebGlContextState.ColorAttachment0 + (uint)i) { state.SetError(0x0502); return ScriptValue.Undefined; }
                framebuffer.DrawBuffers = wanted;
                return ScriptValue.Undefined;
            }
            if (wanted.Length != 1 || wanted[0] is not (0x0405 or 0)) { state.SetError(0x0502); return ScriptValue.Undefined; }
            state.DrawBuffers.Clear();
            state.DrawBuffers.Add(wanted[0]);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("readBuffer", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            if (args.Length == 0) { state.SetError(0x0502); return ScriptValue.Undefined; }
            var source = WebIdlConversions.ToUint32(args[0]);
            if (state.ReadFramebufferObject is { } framebuffer)
            {
                if (source != 0 && !(IsColorPoint(source) && source - WebGlContextState.ColorAttachment0 < WebGlContextState.MaxColorAttachments)) { state.SetError(0x0502); return ScriptValue.Undefined; }
                framebuffer.ReadBuffer = source;
                return ScriptValue.Undefined;
            }
            if (source is not (0x0405 or 0)) { state.SetError(0x0502); return ScriptValue.Undefined; }
            state.ReadBuffer = source;
            return ScriptValue.Undefined;
        });

        t.DefineMethod("clearBufferfv", (target, _, args) => ClearBufferValues((WebGlContextState)target, args, floating: true));
        t.DefineMethod("clearBufferiv", (target, _, args) => ClearBufferValues((WebGlContextState)target, args, floating: false));
        t.DefineMethod("clearBufferuiv", (target, _, args) => ClearBufferValues((WebGlContextState)target, args, floating: false));
        t.DefineMethod("clearBufferfi", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            if (args.Length < 4 || WebIdlConversions.ToUint32(args[0]) != 0x84F9) { state.SetError(0x0500); return ScriptValue.Undefined; }
            state.ClearBuffer(0x1801, 0, new Vector4((float)WebIdlConversions.ToDouble(args[2]), 0, 0, 1));
            state.ClearBuffer(0x1802, 0, new Vector4(WebIdlConversions.ToInt32(args[3]), 0, 0, 1));
            return ScriptValue.Undefined;
        });

        t.DefineMethod("readPixels", (target, _, args) => ReadPixels((WebGlContextState)target, args));
    }

    private static uint DrawBufferAt(WebGlContextState state, int index)
    {
        if (state.DrawFramebufferObject is { } framebuffer) return index < framebuffer.DrawBuffers.Length ? framebuffer.DrawBuffers[index] : 0;
        return index == 0 ? (state.DrawBuffers.Count > 0 ? state.DrawBuffers[0] : 0x0405u) : 0;
    }

    /// <summary>
    /// The texture an image call (texImage2D and the others) writes to, from its target argument: the texture bound at TEXTURE_2D, or the cube map
    /// bound at TEXTURE_CUBE_MAP for one of its faces. Raises INVALID_ENUM or INVALID_OPERATION and returns false when there is none.
    /// </summary>
    private static bool ImageTexture(WebGlContextState state, uint imageTarget, out uint handle, out GlTexture texture, out int face)
    {
        handle = 0;
        texture = null!;
        if (!WebGlContextState.TryImageTarget(imageTarget, out var bindingTarget, out face)) { state.SetError(0x0500); return false; }
        if (state.BoundTextureAt(bindingTarget) is not { } bound || !state.Textures.TryGetValue(bound, out var found)) { state.SetError(0x0502); return false; }
        handle = bound;
        texture = found;
        return true;
    }

    private static ScriptValue TexParameter(WebGlContextState state, ReadOnlySpan<ScriptValue> args, bool integer)
    {
        if (args.Length < 3) { state.SetError(0x0502); return ScriptValue.Undefined; }
        var bindingTarget = WebIdlConversions.ToUint32(args[0]);
        if (state.TextureBindings(bindingTarget) is null) { state.SetError(0x0500); return ScriptValue.Undefined; }
        if (state.BoundTextureAt(bindingTarget) is not { } handle) { state.SetError(0x0502); return ScriptValue.Undefined; }
        if (!state.TextureParameters.TryGetValue(handle, out var parameters)) state.TextureParameters[handle] = parameters = [];
        parameters[WebIdlConversions.ToUint32(args[1])] = integer ? WebIdlConversions.ToInt32(args[2]) : (int)Math.Round(WebIdlConversions.ToDouble(args[2]));
        return ScriptValue.Undefined;
    }

    private static bool ValidAttachmentPoint(uint point) =>
        (IsColorPoint(point) && point - WebGlContextState.ColorAttachment0 < WebGlContextState.MaxColorAttachments) || point is WebGlContextState.DepthAttachment or WebGlContextState.StencilAttachment or WebGlContextState.DepthStencilAttachment;

    /// <summary>The framebuffer object a framebuffer call names through its target, or null if none is bound (the canvas) or the target is not one.</summary>
    private static GlFramebufferObject? FramebufferFor(WebGlContextState state, uint framebufferTarget) => framebufferTarget switch
    {
        0x8CA8 => state.ReadFramebufferObject,
        0x8D40 or 0x8CA9 => state.DrawFramebufferObject,
        _ => null,
    };

    private static int ComponentSize(GlFormat format) => format.Storage switch { GlStorage.U8 => format.InternalFormat is 0x8056 ? 4 : format.InternalFormat is 0x8D62 ? 5 : 8, GlStorage.F16 => 16, _ => 32 };

    private ScriptValue RenderbufferStorage(WebGlContextState state, ReadOnlySpan<ScriptValue> args, int formatIndex)
    {
        // renderbufferStorage(target, internalformat, width, height) and renderbufferStorageMultisample(target, samples, internalformat, width, height)
        var minimum = formatIndex + 3;
        if (state.BoundRenderbuffer is not { } handle || args.Length < minimum || WebIdlConversions.ToUint32(args[0]) != 0x8D41) { state.SetError(0x0502); return ScriptValue.Undefined; }
        var format = WebIdlConversions.ToUint32(args[formatIndex]);
        if (format == 0x84F9) format = 0x88F0; // WebGL 1's DEPTH_STENCIL
        var width = WebIdlConversions.ToInt32(args[formatIndex + 1]);
        var height = WebIdlConversions.ToInt32(args[formatIndex + 2]);
        state.AllocateRenderbuffer(handle, format, width, height);
        return ScriptValue.Undefined;
    }

    private ScriptValue ClearBufferValues(WebGlContextState state, ReadOnlySpan<ScriptValue> args, bool floating)
    {
        if (args.Length < 3) { state.SetError(0x0502); return ScriptValue.Undefined; }
        var buffer = WebIdlConversions.ToUint32(args[0]);
        if (!_context.Engine.TryGetArrayItems(args[2], out var values)) { state.SetError(0x0502); return ScriptValue.Undefined; }
        var offset = args.Length > 3 ? Math.Max(0, WebIdlConversions.ToInt32(args[3])) : 0;
        var needed = buffer == 0x1800 ? 4 : 1;
        if (offset + needed > values.Count) { state.SetError(0x0501); return ScriptValue.Undefined; }
        float At(int i) => (float)WebIdlConversions.ToDouble(values[offset + i]);
        state.ClearBuffer(buffer, WebIdlConversions.ToInt32(args[1]), buffer == 0x1800 ? new Vector4(At(0), At(1), At(2), At(3)) : new Vector4(At(0), 0, 0, 1));
        return ScriptValue.Undefined;
    }

    private ScriptValue ReadPixels(WebGlContextState state, ReadOnlySpan<ScriptValue> args)
    {
        if (args.Length < 7) { state.SetError(0x0502); return ScriptValue.Undefined; }
        var x = WebIdlConversions.ToInt32(args[0]);
        var y = WebIdlConversions.ToInt32(args[1]);
        var width = WebIdlConversions.ToInt32(args[2]);
        var height = WebIdlConversions.ToInt32(args[3]);
        var format = WebIdlConversions.ToUint32(args[4]);
        var type = WebIdlConversions.ToUint32(args[5]);

        if (width < 0 || height < 0 || width > 4096 || height > 4096 || !_context.Engine.TryGetArrayItems(args[6], out var destination))
        {
            state.SetError(0x0501);
            return ScriptValue.Undefined;
        }
        if ((state.BoundReadFramebuffer ?? state.BoundFramebuffer) is not null && (state.ReadFramebufferObject is not { } bound || state.FramebufferStatus(bound) != 0x8CD5))
        {
            state.SetError(0x0506);
            return ScriptValue.Undefined;
        }

        if (format == 0x1902 /* DEPTH_COMPONENT */)
        {
            if (type is not (0x1406 /* FLOAT */ or 0x1403 /* UNSIGNED_SHORT */)) { state.SetError(0x0500); return ScriptValue.Undefined; }
            if (destination.Count < (long)width * height) { state.SetError(0x0501); return ScriptValue.Undefined; }
            var depthSource = state.ReadDepth() is { } read ? read : (Depth: Array.Empty<byte>(), Width: 0, Height: 0);
            for (var row = 0; row < height; row++)
                for (var column = 0; column < width; column++)
                {
                    var sourceX = x + column;
                    var sourceY = y + row;
                    var sourceIndex = sourceX >= 0 && sourceY >= 0 && sourceX < depthSource.Width && sourceY < depthSource.Height ? (sourceY * depthSource.Width + sourceX) * 4 : -1;
                    var depth = sourceIndex >= 0 && sourceIndex + 4 <= depthSource.Depth.Length ? BitConverter.ToSingle(depthSource.Depth, sourceIndex) : 1f;
                    _context.Engine.TrySetArrayItem(args[6], row * width + column, type == 0x1406
                        ? ScriptValue.FromNumber(depth)
                        : ScriptValue.FromNumber((ushort)Math.Clamp(MathF.Round(depth * 65535f), 0, 65535)));
                }
            return ScriptValue.Undefined;
        }

        var channels = format switch { 0x1908 or 0x8D99 => 4, 0x8227 => 2, 0x1903 or 0x8229 => 1, _ => 0 };
        if (channels == 0) { state.SetError(0x0500); return ScriptValue.Undefined; }
        var count = (long)width * height * channels;
        if (count > 16 * 1024 * 1024 || destination.Count < count) { state.SetError(0x0501); return ScriptValue.Undefined; }

        var image = state.ReadImage();
        // A floating-point colour buffer can be read only as floats; an 8-bit one as bytes (or, for convenience, floats).
        if (image is not null && image.Format.IsFloat && type is not (0x1406 or GlFormats.HalfFloat or GlFormats.HalfFloatOes)) { state.SetError(0x0502); return ScriptValue.Undefined; }
        if (type is not (0x1401 or 0x1406 or GlFormats.HalfFloat or GlFormats.HalfFloatOes)) { state.SetError(0x0500); return ScriptValue.Undefined; }

        for (var row = 0; row < height; row++)
            for (var column = 0; column < width; column++)
            {
                var sourceX = x + column;
                var sourceY = y + row;
                var inside = image is not null && sourceX >= 0 && sourceY >= 0 && sourceX < image.Width && sourceY < image.Height;
                var texel = inside ? image!.LoadColor(sourceY * image.Width + sourceX) : Vector4.Zero;
                var destinationBase = (row * width + column) * channels;
                for (var channel = 0; channel < channels; channel++)
                {
                    var value = channel switch { 0 => texel.X, 1 => texel.Y, 2 => texel.Z, _ => texel.W };
                    _context.Engine.TrySetArrayItem(args[6], destinationBase + channel, type switch
                    {
                        0x1401 => ScriptValue.FromNumber((byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255)),
                        0x1406 => ScriptValue.FromNumber(value),
                        _ => ScriptValue.FromNumber(BitConverter.HalfToUInt16Bits((Half)value)),
                    });
                }
            }
        return ScriptValue.Undefined;
    }
}
