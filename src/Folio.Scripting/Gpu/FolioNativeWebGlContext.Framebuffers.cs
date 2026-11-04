namespace Folio.Scripting.Gpu;

internal sealed partial class FolioNativeWebGlContext
{
    private ref uint? GetBoundFramebufferSlot(uint target, out bool valid)
    {
        valid = true;
        switch (target)
        {
            case GlFramebuffer:
            case GlDrawFramebuffer:
                return ref _boundDrawFramebuffer;
            case GlReadFramebuffer:
                return ref _boundReadFramebuffer;
            default:
                valid = false;
                return ref _boundDrawFramebuffer;
        }
    }

    public bool TryCreateFramebuffer(out WebGlFramebufferHandle framebuffer, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            framebuffer = default;
            if (!CheckAvailable(out failure)) return false;
            var id = _nextHandle++;
            _framebuffers[id] = new FramebufferEntry(id);
            framebuffer = new WebGlFramebufferHandle(id);
            return true;
        }
    }

    public bool TryDeleteFramebuffer(WebGlFramebufferHandle framebuffer, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (framebuffer.Value == 0) return true;
            if (_boundDrawFramebuffer == framebuffer.Value) _boundDrawFramebuffer = null;
            if (_boundReadFramebuffer == framebuffer.Value) _boundReadFramebuffer = null;
            if (_framebuffers.TryGetValue(framebuffer.Value, out var fb))
            {
                fb.Dispose();
                _framebuffers.Remove(framebuffer.Value);
            }
            return true;
        }
    }

    public bool TryBindFramebuffer(uint target, WebGlFramebufferHandle? framebuffer, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (target is not (GlFramebuffer or GlDrawFramebuffer or GlReadFramebuffer))
            {
                RecordGlError(GlInvalidEnum);
                return true;
            }
            if ((target is GlDrawFramebuffer or GlReadFramebuffer) && _options.Version == WebGlContextVersion.WebGl1)
            {
                RecordGlError(GlInvalidEnum);
                return true;
            }
            var id = framebuffer is { Value: > 0 } h ? (uint?)h.Value : null;
            if (id is uint fid && !_framebuffers.ContainsKey(fid))
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            if (target == GlFramebuffer)
            {
                _boundDrawFramebuffer = id;
                _boundReadFramebuffer = id;
            }
            else if (target == GlDrawFramebuffer)
            {
                _boundDrawFramebuffer = id;
            }
            else
            {
                _boundReadFramebuffer = id;
            }
            return true;
        }
    }

    public bool TryFramebufferTexture2D(uint target, uint attachment, uint textureTarget, WebGlTextureHandle? texture, int level, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            var fbId = GetBoundFramebufferSlot(target, out var valid);
            if (!valid)
            {
                RecordGlError(GlInvalidEnum);
                return true;
            }
            if (fbId is not uint fid || !_framebuffers.TryGetValue(fid, out var fb))
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            var att = texture is { Value: > 0 } th
                ? new FboAttachment(GlTexture2D, th.Value, textureTarget, level, GetCubeFaceIndex(textureTarget))
                : default;
            SetFramebufferAttachment(fb, attachment, att);
            return true;
        }
    }

    public bool TryFramebufferTextureLayer(uint target, uint attachment, WebGlTextureHandle? texture, int level, int layer, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            var fbId = GetBoundFramebufferSlot(target, out var valid);
            if (!valid)
            {
                RecordGlError(GlInvalidEnum);
                return true;
            }
            if (fbId is not uint fid || !_framebuffers.TryGetValue(fid, out var fb))
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            if (level < 0 || layer < 0)
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            var att = texture is { Value: > 0 } th
                ? new FboAttachment(GlTexture2DArray, th.Value, GlTexture2DArray, level, layer)
                : default;
            SetFramebufferAttachment(fb, attachment, att);
            return true;
        }
    }

    private void SetFramebufferAttachment(FramebufferEntry fb, uint attachment, FboAttachment att)
    {
        if (attachment >= GlColorAttachment0 && attachment < GlColorAttachment0 + MaxColorAttachments)
        {
            fb.ColorAttachments[attachment - GlColorAttachment0] = att;
        }
        else if (attachment == GlDepthAttachment)
        {
            fb.DepthAttachment = att;
        }
        else if (attachment == GlStencilAttachment)
        {
            fb.StencilAttachment = att;
        }
        else if (attachment == GlDepthStencilAttachment)
        {
            fb.DepthAttachment = att;
            fb.StencilAttachment = att;
        }
        else
        {
            RecordGlError(GlInvalidEnum);
        }
    }

    public bool TryCheckFramebufferStatus(uint target, out uint status, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            status = 0;
            if (!CheckAvailable(out failure)) return false;
            var fbId = GetBoundFramebufferSlot(target, out var valid);
            if (!valid)
            {
                RecordGlError(GlInvalidEnum);
                return true;
            }
            if (fbId is null)
            {
                status = GlFramebufferComplete;
                return true;
            }
            if (!_framebuffers.TryGetValue(fbId.Value, out var fb))
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            status = EvaluateFramebufferStatus(fb, out _, out _);
            return true;
        }
    }

    internal uint EvaluateFramebufferStatus(FramebufferEntry fb, out int fbWidth, out int fbHeight)
    {
        var w = 0;
        var h = 0;
        var hasAny = false;
        var sampleCount = -1;

        bool CheckOne(FboAttachment att, bool isColor)
        {
            if (att.AttachmentType == 0 || att.ResourceHandle == 0) return true;
            hasAny = true;
            if (att.AttachmentType == GlRenderbuffer)
            {
                if (!_renderbuffers.TryGetValue(att.ResourceHandle, out var rb) || !rb.Defined || rb.Width <= 0 || rb.Height <= 0)
                    return false;
                if (isColor && rb.IsDepth) return false;
                if (!isColor && !rb.IsDepth && !rb.IsStencil) return false;
                if (w == 0) { w = rb.Width; h = rb.Height; sampleCount = rb.Samples; }
                else if (w != rb.Width || h != rb.Height) return false;
                else if (sampleCount != rb.Samples) { sampleCount = -2; return false; }
                return true;
            }
            else
            {
                if (!_textures.TryGetValue(att.ResourceHandle, out var tex))
                    return false;
                var face = tex.Target == GlTextureCubeMap ? att.Layer : 0;
                if (!tex.Slices.TryGetValue((face, att.Level), out var slice) || !slice.Defined || slice.Width <= 0 || slice.Height <= 0)
                    return false;
                if ((tex.Target is GlTexture3D or GlTexture2DArray) && (att.Layer < 0 || att.Layer >= slice.Depth))
                    return false;
                if (w == 0) { w = slice.Width; h = slice.Height; sampleCount = 0; }
                else if (w != slice.Width || h != slice.Height) return false;
                else if (sampleCount != 0) { sampleCount = -2; return false; }
                return true;
            }
        }

        for (var i = 0; i < MaxColorAttachments; i++)
        {
            if (!CheckOne(fb.ColorAttachments[i], isColor: true))
            {
                fbWidth = w; fbHeight = h;
                return sampleCount == -2 ? GlFramebufferIncompleteMultisample : GlFramebufferIncompleteAttachment;
            }
        }
        if (!CheckOne(fb.DepthAttachment, isColor: false))
        {
            fbWidth = w; fbHeight = h;
            return sampleCount == -2 ? GlFramebufferIncompleteMultisample : GlFramebufferIncompleteAttachment;
        }
        if (fb.StencilAttachment != fb.DepthAttachment && !CheckOne(fb.StencilAttachment, isColor: false))
        {
            fbWidth = w; fbHeight = h;
            return sampleCount == -2 ? GlFramebufferIncompleteMultisample : GlFramebufferIncompleteAttachment;
        }

        fbWidth = w;
        fbHeight = h;
        if (!hasAny)
            return GlFramebufferIncompleteMissingAttachment;

        return GlFramebufferComplete;
    }

    public bool TryBlitFramebuffer(int srcX0, int srcY0, int srcX1, int srcY1, int dstX0, int dstY0, int dstX1, int dstY1, uint mask, uint filter, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if ((mask & ~(GlColorBufferBit | GlDepthBufferBit | GlStencilBufferBit)) != 0)
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            if (filter is not (GlNearest or GlLinear))
            {
                RecordGlError(GlInvalidEnum);
                return true;
            }
            if ((mask & (GlDepthBufferBit | GlStencilBufferBit)) != 0 && filter != GlNearest)
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }

            if (_boundReadFramebuffer is uint rfbId && _framebuffers.TryGetValue(rfbId, out var rfb))
            {
                if (EvaluateFramebufferStatus(rfb, out _, out _) != GlFramebufferComplete)
                {
                    RecordGlError(GlInvalidFramebufferOperation);
                    return true;
                }
            }
            if (_boundDrawFramebuffer is uint dfbId && _framebuffers.TryGetValue(dfbId, out var dfb))
            {
                if (EvaluateFramebufferStatus(dfb, out _, out _) != GlFramebufferComplete)
                {
                    RecordGlError(GlInvalidFramebufferOperation);
                    return true;
                }
            }

            if (_boundReadFramebuffer == _boundDrawFramebuffer &&
                srcX0 < dstX1 && dstX0 < srcX1 && srcY0 < dstY1 && dstY0 < srcY1)
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }

            if ((mask & GlColorBufferBit) == 0)
                return true;

            var srcMinX = Math.Min(srcX0, srcX1);
            var srcMinY = Math.Min(srcY0, srcY1);
            var srcW = Math.Abs(srcX1 - srcX0);
            var srcH = Math.Abs(srcY1 - srcY0);
            var dstMinX = Math.Min(dstX0, dstX1);
            var dstMinY = Math.Min(dstY0, dstY1);
            var dstW = Math.Abs(dstX1 - dstX0);
            var dstH = Math.Abs(dstY1 - dstY0);
            if (srcW == 0 || srcH == 0 || dstW == 0 || dstH == 0)
                return true;

            // Read float4 pixels from read framebuffer
            var srcFloats = new float[srcW * srcH * 4];
            ReadFramebufferColorFloats(_boundReadFramebuffer, srcMinX, srcMinY, srcW, srcH, srcFloats);

            // Resample and write to draw framebuffer
            var dstFloats = new float[dstW * dstH * 4];
            for (var dy = 0; dy < dstH; dy++)
            {
                var sy = Math.Clamp((int)((dy + 0.5f) * srcH / dstH), 0, srcH - 1);
                for (var dx = 0; dx < dstW; dx++)
                {
                    var sx = Math.Clamp((int)((dx + 0.5f) * srcW / dstW), 0, srcW - 1);
                    var sIdx = (sy * srcW + sx) * 4;
                    var dIdx = (dy * dstW + dx) * 4;
                    dstFloats[dIdx] = srcFloats[sIdx];
                    dstFloats[dIdx + 1] = srcFloats[sIdx + 1];
                    dstFloats[dIdx + 2] = srcFloats[sIdx + 2];
                    dstFloats[dIdx + 3] = srcFloats[sIdx + 3];
                }
            }
            WriteFramebufferColorFloats(_boundDrawFramebuffer, dstMinX, dstMinY, dstW, dstH, dstFloats);
            return true;
        }
    }

    public bool TryCreateRenderbuffer(out WebGlRenderbufferHandle renderbuffer, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            renderbuffer = default;
            if (!CheckAvailable(out failure)) return false;
            var id = _nextHandle++;
            _renderbuffers[id] = new RenderbufferEntry(id);
            renderbuffer = new WebGlRenderbufferHandle(id);
            return true;
        }
    }

    public bool TryDeleteRenderbuffer(WebGlRenderbufferHandle renderbuffer, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (renderbuffer.Value == 0) return true;
            if (_boundRenderbuffer == renderbuffer.Value) _boundRenderbuffer = null;
            if (_renderbuffers.TryGetValue(renderbuffer.Value, out var rb))
            {
                if (rb.ReservedBytes > 0)
                    TryReserveResourceBytes(-rb.ReservedBytes, out _);
                rb.Dispose();
                _renderbuffers.Remove(renderbuffer.Value);
            }
            foreach (var fb in _framebuffers.Values)
            {
                for (var i = 0; i < MaxColorAttachments; i++)
                {
                    if (fb.ColorAttachments[i].AttachmentType == GlRenderbuffer && fb.ColorAttachments[i].ResourceHandle == renderbuffer.Value)
                        fb.ColorAttachments[i] = default;
                }
                if (fb.DepthAttachment.AttachmentType == GlRenderbuffer && fb.DepthAttachment.ResourceHandle == renderbuffer.Value)
                    fb.DepthAttachment = default;
                if (fb.StencilAttachment.AttachmentType == GlRenderbuffer && fb.StencilAttachment.ResourceHandle == renderbuffer.Value)
                    fb.StencilAttachment = default;
            }
            return true;
        }
    }

    public bool TryBindRenderbuffer(uint target, WebGlRenderbufferHandle? renderbuffer, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (target != GlRenderbuffer)
            {
                RecordGlError(GlInvalidEnum);
                return true;
            }
            if (renderbuffer is null || renderbuffer.Value.Value == 0)
            {
                _boundRenderbuffer = null;
                return true;
            }
            if (!_renderbuffers.ContainsKey(renderbuffer.Value.Value))
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            _boundRenderbuffer = renderbuffer.Value.Value;
            return true;
        }
    }

    public bool TryRenderbufferStorage(uint target, uint internalFormat, int width, int height, out GpuFailure? failure) =>
        TryRenderbufferStorageMultisample(target, 0, internalFormat, width, height, out failure);

    public bool TryRenderbufferStorageMultisample(uint target, int samples, uint internalFormat, int width, int height, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (target != GlRenderbuffer)
            {
                RecordGlError(GlInvalidEnum);
                return true;
            }
            if (samples < 0 || samples > 8 || width < 0 || height < 0 || width > 4096 || height > 4096)
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            if (_boundRenderbuffer is not uint id || !_renderbuffers.TryGetValue(id, out var rb))
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            if (!ClassifyInternalFormat(internalFormat, GlRgba, GlUnsignedByte, out var bpp, out var isInt, out var isUInt, out var isDepth))
            {
                RecordGlError(GlInvalidEnum);
                return true;
            }
            if (isInt && samples > 0)
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }

            long newBytes = (long)width * height * bpp * Math.Max(1, samples);
            if (!TryReserveResourceBytes(newBytes - rb.ReservedBytes, out failure))
                return false;

            rb.ReservedBytes = newBytes;
            rb.Width = width;
            rb.Height = height;
            rb.Samples = samples;
            rb.InternalFormat = internalFormat;
            rb.IsInteger = isInt;
            rb.IsUnsigned = isUInt;
            rb.IsDepth = isDepth;
            rb.IsStencil = internalFormat is GlStencilIndex8 or GlDepth24Stencil8 or GlDepth32fStencil8 or GlDepthStencil;
            rb.Defined = true;
            rb.FloatPixels = new float[width * height * 4];
            rb.IntPixels = isInt && !isUInt ? new int[width * height * 4] : [];
            rb.UIntPixels = isUInt ? new uint[width * height * 4] : [];
            rb.DepthValues = isDepth ? new float[width * height] : [];
            if (rb.DepthValues.Length > 0) Array.Fill(rb.DepthValues, 1.0f);
            rb.StencilValues = rb.IsStencil ? new byte[width * height] : [];
            return true;
        }
    }

    public bool TryFramebufferRenderbuffer(uint target, uint attachment, uint renderbufferTarget, WebGlRenderbufferHandle? renderbuffer, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (renderbufferTarget != GlRenderbuffer)
            {
                RecordGlError(GlInvalidEnum);
                return true;
            }
            var fbId = GetBoundFramebufferSlot(target, out var valid);
            if (!valid)
            {
                RecordGlError(GlInvalidEnum);
                return true;
            }
            if (fbId is not uint fid || !_framebuffers.TryGetValue(fid, out var fb))
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            var att = renderbuffer is { Value: > 0 } rbh
                ? new FboAttachment(GlRenderbuffer, rbh.Value, GlRenderbuffer, 0, 0)
                : default;
            SetFramebufferAttachment(fb, attachment, att);
            return true;
        }
    }
}
