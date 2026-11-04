using System.Text.RegularExpressions;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace Folio.Scripting.Gpu;

internal sealed partial class FolioNativeWebGlContext
{
    public bool TrySetViewport(int x, int y, int width, int height, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (width < 0 || height < 0)
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            _viewportX = x;
            _viewportY = y;
            _viewportW = width;
            _viewportH = height;
            return true;
        }
    }

    public bool TrySetScissor(int x, int y, int width, int height, bool enabled, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (width < 0 || height < 0)
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            _scissorX = x;
            _scissorY = y;
            _scissorW = width;
            _scissorH = height;
            _scissorTestEnabled = enabled;
            return true;
        }
    }

    public bool TrySetCapability(uint capability, bool enabled, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            switch (capability)
            {
                case GlCullFace: _cullFaceEnabled = enabled; return true;
                case GlBlend: _blendEnabled = enabled; return true;
                case GlDither: _ditherEnabled = enabled; return true;
                case GlStencilTest: _stencilTestEnabled = enabled; return true;
                case GlDepthTest: _depthTestEnabled = enabled; return true;
                case GlScissorTest: _scissorTestEnabled = enabled; return true;
                case GlPolygonOffsetFill: _polygonOffsetFillEnabled = enabled; return true;
                case GlSampleAlphaToCoverage: _sampleAlphaToCoverageEnabled = enabled; return true;
                case GlSampleCoverage: _sampleCoverageEnabled = enabled; return true;
                case GlRasterizerDiscard:
                    if (_options.Version == WebGlContextVersion.WebGl1)
                    {
                        RecordGlError(GlInvalidEnum);
                        return true;
                    }
                    _rasterizerDiscardEnabled = enabled;
                    return true;
                default:
                    RecordGlError(GlInvalidEnum);
                    return true;
            }
        }
    }

    public bool TryDrawBuffers(ReadOnlySpan<uint> buffers, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (_boundDrawFramebuffer is uint fid && _framebuffers.TryGetValue(fid, out var fb))
            {
                for (var i = 0; i < Math.Min(buffers.Length, fb.DrawBuffers.Length); i++)
                    fb.DrawBuffers[i] = buffers[i];
            }
            return true;
        }
    }

    public bool TryClearBufferfv(uint buffer, int drawBuffer, ReadOnlySpan<float> values, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (buffer == GlColor && values.Length >= 4)
            {
                return ClearTargetColorFloat(drawBuffer, values[0], values[1], values[2], values[3], out failure);
            }
            if (buffer == GlDepth && values.Length >= 1)
            {
                ClearTargetDepthStencil(clearDepth: true, values[0], clearStencil: false, 0);
                return true;
            }
            RecordGlError(GlInvalidEnum);
            return true;
        }
    }

    public bool TryClearBufferiv(uint buffer, int drawBuffer, ReadOnlySpan<int> values, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (buffer == GlColor && values.Length >= 4)
            {
                ClearTargetColorInt(drawBuffer, values[0], values[1], values[2], values[3]);
                return true;
            }
            if (buffer == GlStencil && values.Length >= 1)
            {
                ClearTargetDepthStencil(clearDepth: false, 1f, clearStencil: true, values[0]);
                return true;
            }
            RecordGlError(GlInvalidEnum);
            return true;
        }
    }

    public bool TryClearBufferuiv(uint buffer, int drawBuffer, ReadOnlySpan<uint> values, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (buffer == GlColor && values.Length >= 4)
            {
                ClearTargetColorUInt(drawBuffer, values[0], values[1], values[2], values[3]);
                return true;
            }
            RecordGlError(GlInvalidEnum);
            return true;
        }
    }

    public bool TryClearBufferfi(uint buffer, int drawBuffer, float depth, int stencil, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (buffer != GlDepthStencil)
            {
                RecordGlError(GlInvalidEnum);
                return true;
            }
            ClearTargetDepthStencil(clearDepth: true, depth, clearStencil: true, stencil);
            return true;
        }
    }

    public bool TryReadBuffer(uint buffer, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            _readBufferMode = buffer;
            return true;
        }
    }

    public bool TrySetClearColor(float red, float green, float blue, float alpha, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            _clearR = red; _clearG = green; _clearB = blue; _clearA = alpha;
            return true;
        }
    }

    public bool TryClearMask(uint mask, out GpuFailure? failure) =>
        TryClear(_clearR, _clearG, _clearB, _clearA, mask, out failure);

    public bool TryPixelStore(uint parameter, int value, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            switch (parameter)
            {
                case GlUnpackAlignment:
                    if (value is not (1 or 2 or 4 or 8)) { RecordGlError(GlInvalidValue); return true; }
                    _unpackAlignment = value;
                    return true;
                case GlPackAlignment:
                    if (value is not (1 or 2 or 4 or 8)) { RecordGlError(GlInvalidValue); return true; }
                    _packAlignment = value;
                    return true;
                case GlUnpackFlipYWebgl: _unpackFlipY = value != 0; return true;
                case GlUnpackPremultiplyAlphaWebgl: _unpackPremultiplyAlpha = value != 0; return true;
                case GlUnpackColorspaceConversionWebgl: _unpackColorspaceConversion = value; return true;
                case GlUnpackRowLength: if (value < 0) { RecordGlError(GlInvalidValue); return true; } _unpackRowLength = value; return true;
                case GlUnpackSkipRows: if (value < 0) { RecordGlError(GlInvalidValue); return true; } _unpackSkipRows = value; return true;
                case GlUnpackSkipPixels: if (value < 0) { RecordGlError(GlInvalidValue); return true; } _unpackSkipPixels = value; return true;
                case GlUnpackSkipImages: if (value < 0) { RecordGlError(GlInvalidValue); return true; } _unpackSkipImages = value; return true;
                case GlUnpackImageHeight: if (value < 0) { RecordGlError(GlInvalidValue); return true; } _unpackImageHeight = value; return true;
                case GlPackRowLength: if (value < 0) { RecordGlError(GlInvalidValue); return true; } _packRowLength = value; return true;
                case GlPackSkipRows: if (value < 0) { RecordGlError(GlInvalidValue); return true; } _packSkipRows = value; return true;
                case GlPackSkipPixels: if (value < 0) { RecordGlError(GlInvalidValue); return true; } _packSkipPixels = value; return true;
                default:
                    RecordGlError(GlInvalidEnum);
                    return true;
            }
        }
    }

    public bool TrySetActiveTexture(uint texture, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (texture < GlTexture0 || texture >= GlTexture0 + MaxTextureUnits)
            {
                RecordGlError(GlInvalidEnum);
                return true;
            }
            _activeTextureUnit = (int)(texture - GlTexture0);
            return true;
        }
    }

    public bool TrySetDepthFunction(uint function, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (function is < GlNever or > GlAlways) { RecordGlError(GlInvalidEnum); return true; }
            _depthFunc = function;
            return true;
        }
    }

    public bool TrySetDepthMask(bool enabled, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            _depthMask = enabled;
            return true;
        }
    }

    public bool TrySetClearDepth(float value, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            _clearDepth = Math.Clamp(value, 0f, 1f);
            return true;
        }
    }

    public bool TrySetStencilFunction(uint face, uint function, int reference, uint mask, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (function is < GlNever or > GlAlways) { RecordGlError(GlInvalidEnum); return true; }
            if (face is GlFront or GlFrontAndBack)
            {
                _stencilFuncFront = function;
                _stencilRefFront = reference;
                _stencilValMaskFront = mask;
            }
            if (face is GlBack or GlFrontAndBack)
            {
                _stencilFuncBack = function;
                _stencilRefBack = reference;
                _stencilValMaskBack = mask;
            }
            return true;
        }
    }

    public bool TrySetStencilMask(uint face, uint mask, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (face is GlFront or GlFrontAndBack) _stencilWritemaskFront = mask;
            if (face is GlBack or GlFrontAndBack) _stencilWritemaskBack = mask;
            return true;
        }
    }

    public bool TrySetStencilOperation(uint face, uint fail, uint depthFail, uint depthPass, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (face is GlFront or GlFrontAndBack)
            {
                _stencilFailFront = fail;
                _stencilZFailFront = depthFail;
                _stencilZPassFront = depthPass;
            }
            if (face is GlBack or GlFrontAndBack)
            {
                _stencilFailBack = fail;
                _stencilZFailBack = depthFail;
                _stencilZPassBack = depthPass;
            }
            return true;
        }
    }

    public bool TrySetCullFace(uint face, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (face is not (GlFront or GlBack or GlFrontAndBack)) { RecordGlError(GlInvalidEnum); return true; }
            _cullFaceMode = face;
            return true;
        }
    }

    public bool TrySetFrontFace(uint mode, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (mode is not (GlCw or GlCcw)) { RecordGlError(GlInvalidEnum); return true; }
            _frontFaceMode = mode;
            return true;
        }
    }

    public bool TrySetBlendColor(float red, float green, float blue, float alpha, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            _blendColorR = red; _blendColorG = green; _blendColorB = blue; _blendColorA = alpha;
            return true;
        }
    }

    public bool TrySetBlendFunction(uint sourceRgb, uint destinationRgb, uint sourceAlpha, uint destinationAlpha, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            _blendSrcRgb = sourceRgb;
            _blendDstRgb = destinationRgb;
            _blendSrcAlpha = sourceAlpha;
            _blendDstAlpha = destinationAlpha;
            return true;
        }
    }

    public bool TrySetBlendEquation(uint rgb, uint alpha, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            _blendEqRgb = rgb;
            _blendEqAlpha = alpha;
            return true;
        }
    }

    public bool TrySetColorMask(bool red, bool green, bool blue, bool alpha, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            _colorMaskR = red; _colorMaskG = green; _colorMaskB = blue; _colorMaskA = alpha;
            return true;
        }
    }

    public bool TrySetDepthState(uint function, bool writeMask, float clearValue, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            _depthFunc = function;
            _depthMask = writeMask;
            _clearDepth = Math.Clamp(clearValue, 0f, 1f);
            return true;
        }
    }

    public bool TrySetStencilState(uint face, uint function, int reference, uint compareMask, uint writeMask, uint fail, uint depthFail, uint depthPass, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            TrySetStencilFunction(face, function, reference, compareMask, out _);
            TrySetStencilMask(face, writeMask, out _);
            TrySetStencilOperation(face, fail, depthFail, depthPass, out _);
            return true;
        }
    }

    public bool TrySetStencilClear(int value, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            _clearStencil = value;
            return true;
        }
    }

    public bool TrySetCullState(uint face, uint frontFace, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            _cullFaceMode = face;
            _frontFaceMode = frontFace;
            return true;
        }
    }

    public bool TrySetDepthRange(float nearValue, float farValue, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (nearValue > farValue) { RecordGlError(GlInvalidOperation); return true; }
            _depthRangeNear = Math.Clamp(nearValue, 0f, 1f);
            _depthRangeFar = Math.Clamp(farValue, 0f, 1f);
            return true;
        }
    }

    public bool TrySetLineWidth(float width, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (width <= 0f || float.IsNaN(width)) { RecordGlError(GlInvalidValue); return true; }
            _lineWidth = width;
            return true;
        }
    }

    public bool TrySetPolygonOffset(float factor, float units, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            _polygonOffsetFactor = factor;
            _polygonOffsetUnits = units;
            return true;
        }
    }

    public bool TrySetSampleCoverage(float value, bool invert, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            _sampleCoverageValue = Math.Clamp(value, 0f, 1f);
            _sampleCoverageInvert = invert;
            return true;
        }
    }

    public bool TrySetHint(uint target, uint mode, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            return true;
        }
    }

    public bool TryFlush(out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            _flush(_device.ContextPtr);
            return true;
        }
    }

    public bool TryFinish(out GpuFailure? failure) => TryFlush(out failure);

    public bool TryClear(float red, float green, float blue, float alpha, uint mask, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if ((mask & ~(GlColorBufferBit | GlDepthBufferBit | GlStencilBufferBit)) != 0)
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            if (_boundDrawFramebuffer is uint fid && _framebuffers.TryGetValue(fid, out var fb))
            {
                if (EvaluateFramebufferStatus(fb, out _, out _) != GlFramebufferComplete)
                {
                    RecordGlError(GlInvalidFramebufferOperation);
                    return true;
                }
            }

            _clearR = red; _clearG = green; _clearB = blue; _clearA = alpha;
            if ((mask & GlColorBufferBit) != 0)
            {
                if (!ClearTargetColorFloat(0, red, green, blue, alpha, out failure))
                    return false;
            }
            if ((mask & (GlDepthBufferBit | GlStencilBufferBit)) != 0)
            {
                ClearTargetDepthStencil(
                    (mask & GlDepthBufferBit) != 0 && _depthMask,
                    _clearDepth,
                    (mask & GlStencilBufferBit) != 0 && _stencilWritemaskFront != 0,
                    _clearStencil);
            }
            return true;
        }
    }

    private bool ClearTargetColorFloat(int drawBuffer, float r, float g, float b, float a, out GpuFailure? failure)
    {
        failure = null;
        if (_boundDrawFramebuffer is null)
        {
            var targetW = _width;
            var targetH = _height;
            if (!_scissorTestEnabled && _colorMaskR && _colorMaskG && _colorMaskB && _colorMaskA)
            {
                var col = new float[] { r, g, b, Attributes.Alpha ? a : 1f };
                _clearRtv(_device.ContextPtr, _defaultColorRtv, ref col[0]);
                return true;
            }
            // Scissored or color-masked clear on default backbuffer
            var cur = new float[targetW * targetH * 4];
            ReadD3DFloatTexture(_defaultColorTex, _defaultColorStaging, targetW, targetH, cur);
            ApplyScissoredColorClear(cur, targetW, targetH, r, g, b, Attributes.Alpha ? a : 1f);
            WriteD3DFloatTexture(_defaultColorTex, targetW, targetH, cur);
            return true;
        }

        if (!_framebuffers.TryGetValue(_boundDrawFramebuffer.Value, out var fb))
            return true;
        if (drawBuffer < 0 || drawBuffer >= MaxColorAttachments)
            return true;
        var att = fb.ColorAttachments[drawBuffer];
        if (att.AttachmentType == GlRenderbuffer && _renderbuffers.TryGetValue(att.ResourceHandle, out var rb) && rb.Defined)
        {
            ApplyScissoredColorClear(rb.FloatPixels, rb.Width, rb.Height, r, g, b, a);
        }
        else if (att.AttachmentType != 0 && _textures.TryGetValue(att.ResourceHandle, out var tex))
        {
            var face = tex.Target == GlTextureCubeMap ? att.Layer : 0;
            if (tex.Slices.TryGetValue((face, att.Level), out var slice) && slice.Defined)
            {
                var layerOffset = (tex.Target is GlTexture3D or GlTexture2DArray) ? att.Layer * slice.Width * slice.Height * 4 : 0;
                ApplyScissoredColorClear(slice.FloatTexels.AsSpan(layerOffset, slice.Width * slice.Height * 4), slice.Width, slice.Height, r, g, b, a);
                tex.D3DDirty = true;
            }
        }
        return true;
    }

    private void ClearTargetColorInt(int drawBuffer, int r, int g, int b, int a)
    {
        if (_boundDrawFramebuffer is not uint fid || !_framebuffers.TryGetValue(fid, out var fb)) return;
        if (drawBuffer < 0 || drawBuffer >= MaxColorAttachments) return;
        var att = fb.ColorAttachments[drawBuffer];
        if (att.AttachmentType == GlRenderbuffer && _renderbuffers.TryGetValue(att.ResourceHandle, out var rb) && rb.Defined && rb.IntPixels.Length > 0)
        {
            for (var i = 0; i < rb.IntPixels.Length; i += 4)
            {
                if (_colorMaskR) { rb.IntPixels[i] = r; rb.FloatPixels[i] = r; }
                if (_colorMaskG) { rb.IntPixels[i + 1] = g; rb.FloatPixels[i + 1] = g; }
                if (_colorMaskB) { rb.IntPixels[i + 2] = b; rb.FloatPixels[i + 2] = b; }
                if (_colorMaskA) { rb.IntPixels[i + 3] = a; rb.FloatPixels[i + 3] = a; }
            }
        }
        else if (att.AttachmentType != 0 && _textures.TryGetValue(att.ResourceHandle, out var tex))
        {
            var face = tex.Target == GlTextureCubeMap ? att.Layer : 0;
            if (tex.Slices.TryGetValue((face, att.Level), out var slice) && slice.Defined && slice.IntTexels.Length > 0)
            {
                var layerOffset = (tex.Target is GlTexture3D or GlTexture2DArray) ? att.Layer * slice.Width * slice.Height * 4 : 0;
                for (var i = 0; i < slice.Width * slice.Height * 4; i += 4)
                {
                    if (_colorMaskR) { slice.IntTexels[layerOffset + i] = r; slice.FloatTexels[layerOffset + i] = r; }
                    if (_colorMaskG) { slice.IntTexels[layerOffset + i + 1] = g; slice.FloatTexels[layerOffset + i + 1] = g; }
                    if (_colorMaskB) { slice.IntTexels[layerOffset + i + 2] = b; slice.FloatTexels[layerOffset + i + 2] = b; }
                    if (_colorMaskA) { slice.IntTexels[layerOffset + i + 3] = a; slice.FloatTexels[layerOffset + i + 3] = a; }
                }
                tex.D3DDirty = true;
            }
        }
    }

    private void ClearTargetColorUInt(int drawBuffer, uint r, uint g, uint b, uint a)
    {
        if (_boundDrawFramebuffer is not uint fid || !_framebuffers.TryGetValue(fid, out var fb)) return;
        if (drawBuffer < 0 || drawBuffer >= MaxColorAttachments) return;
        var att = fb.ColorAttachments[drawBuffer];
        if (att.AttachmentType == GlRenderbuffer && _renderbuffers.TryGetValue(att.ResourceHandle, out var rb) && rb.Defined && rb.UIntPixels.Length > 0)
        {
            for (var i = 0; i < rb.UIntPixels.Length; i += 4)
            {
                if (_colorMaskR) { rb.UIntPixels[i] = r; rb.FloatPixels[i] = r; }
                if (_colorMaskG) { rb.UIntPixels[i + 1] = g; rb.FloatPixels[i + 1] = g; }
                if (_colorMaskB) { rb.UIntPixels[i + 2] = b; rb.FloatPixels[i + 2] = b; }
                if (_colorMaskA) { rb.UIntPixels[i + 3] = a; rb.FloatPixels[i + 3] = a; }
            }
        }
        else if (att.AttachmentType != 0 && _textures.TryGetValue(att.ResourceHandle, out var tex))
        {
            var face = tex.Target == GlTextureCubeMap ? att.Layer : 0;
            if (tex.Slices.TryGetValue((face, att.Level), out var slice) && slice.Defined && slice.UIntTexels.Length > 0)
            {
                var layerOffset = (tex.Target is GlTexture3D or GlTexture2DArray) ? att.Layer * slice.Width * slice.Height * 4 : 0;
                for (var i = 0; i < slice.Width * slice.Height * 4; i += 4)
                {
                    if (_colorMaskR) { slice.UIntTexels[layerOffset + i] = r; slice.FloatTexels[layerOffset + i] = r; }
                    if (_colorMaskG) { slice.UIntTexels[layerOffset + i + 1] = g; slice.FloatTexels[layerOffset + i + 1] = g; }
                    if (_colorMaskB) { slice.UIntTexels[layerOffset + i + 2] = b; slice.FloatTexels[layerOffset + i + 2] = b; }
                    if (_colorMaskA) { slice.UIntTexels[layerOffset + i + 3] = a; slice.FloatTexels[layerOffset + i + 3] = a; }
                }
                tex.D3DDirty = true;
            }
        }
    }

    private void ClearTargetDepthStencil(bool clearDepth, float depth, bool clearStencil, int stencil)
    {
        if (_boundDrawFramebuffer is null)
        {
            uint flags = 0;
            if (clearDepth) flags |= FolioD3D11Interop.D3D11ClearDepth;
            if (clearStencil) flags |= FolioD3D11Interop.D3D11ClearStencil;
            if (flags != 0)
                _clearDsv(_device.ContextPtr, _defaultDepthDsv, flags, Math.Clamp(depth, 0f, 1f), (byte)(stencil & 0xFF));
            return;
        }
        if (!_framebuffers.TryGetValue(_boundDrawFramebuffer.Value, out var fb)) return;
        if (clearDepth && fb.DepthAttachment.AttachmentType == GlRenderbuffer &&
            _renderbuffers.TryGetValue(fb.DepthAttachment.ResourceHandle, out var drb) && drb.DepthValues.Length > 0)
        {
            Array.Fill(drb.DepthValues, Math.Clamp(depth, 0f, 1f));
        }
        if (clearStencil && fb.StencilAttachment.AttachmentType == GlRenderbuffer &&
            _renderbuffers.TryGetValue(fb.StencilAttachment.ResourceHandle, out var srb) && srb.StencilValues.Length > 0)
        {
            Array.Fill(srb.StencilValues, (byte)(stencil & 0xFF));
        }
    }

    private void ApplyScissoredColorClear(Span<float> target, int width, int height, float r, float g, float b, float a)
    {
        var x0 = _scissorTestEnabled ? Math.Clamp(_scissorX, 0, width) : 0;
        var y0 = _scissorTestEnabled ? Math.Clamp(_scissorY, 0, height) : 0;
        var x1 = _scissorTestEnabled ? Math.Clamp(_scissorX + _scissorW, 0, width) : width;
        var y1 = _scissorTestEnabled ? Math.Clamp(_scissorY + _scissorH, 0, height) : height;

        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var idx = (y * width + x) * 4;
                if (_colorMaskR) target[idx] = r;
                if (_colorMaskG) target[idx + 1] = g;
                if (_colorMaskB) target[idx + 2] = b;
                if (_colorMaskA) target[idx + 3] = a;
            }
        }
    }

    public bool TryDrawArrays(uint mode, int first, int count, out GpuFailure? failure) =>
        TryDrawArraysInstanced(mode, first, count, 1, out failure);

    public bool TryDrawElements(uint mode, int count, uint type, int byteOffset, out GpuFailure? failure) =>
        TryDrawElementsInstanced(mode, count, type, byteOffset, 1, out failure);

    public bool TryDrawArraysInstanced(uint mode, int first, int count, int instanceCount, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (mode > GlTriangleFan)
            {
                RecordGlError(GlInvalidEnum);
                return true;
            }
            if (first < 0 || count < 0 || instanceCount < 0)
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            if (count == 0 || instanceCount == 0)
                return true;

            return ExecuteDrawCall(mode, isIndexed: false, first, count, indexType: 0, indexByteOffset: 0, instanceCount, out failure);
        }
    }

    public bool TryDrawElementsInstanced(uint mode, int count, uint type, int byteOffset, int instanceCount, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (mode > GlTriangleFan || type is not (GlUnsignedByte or GlUnsignedShort or GlUnsignedInt))
            {
                RecordGlError(GlInvalidEnum);
                return true;
            }
            if (count < 0 || byteOffset < 0 || instanceCount < 0)
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            var idxSize = type == GlUnsignedByte ? 1 : type == GlUnsignedShort ? 2 : 4;
            if (byteOffset % idxSize != 0)
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            if (CurrentVao.ElementArrayBuffer is not uint eboId || !_buffers.TryGetValue(eboId, out var ebo))
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            if ((long)byteOffset + (long)count * idxSize > ebo.Data.Length)
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            if (count == 0 || instanceCount == 0)
                return true;

            return ExecuteDrawCall(mode, isIndexed: true, first: 0, count, type, byteOffset, instanceCount, out failure);
        }
    }

    private bool ExecuteDrawCall(
        uint mode,
        bool isIndexed,
        int first,
        int count,
        uint indexType,
        int indexByteOffset,
        int instanceCount,
        out GpuFailure? failure)
    {
        failure = null;
        if (_currentProgram is not uint progId || !_programs.TryGetValue(progId, out var prog) || !prog.Linked || prog.LinkedVertexShader is null)
        {
            RecordGlError(GlInvalidOperation);
            return true;
        }

        FramebufferEntry? fb = null;
        var rtW = _width;
        var rtH = _height;
        var rtIsInt = false;
        var rtIsUInt = false;
        if (_boundDrawFramebuffer is uint fid)
        {
            if (!_framebuffers.TryGetValue(fid, out fb) || EvaluateFramebufferStatus(fb, out rtW, out rtH) != GlFramebufferComplete)
            {
                RecordGlError(GlInvalidFramebufferOperation);
                return true;
            }
            var c0 = fb.ColorAttachments[0];
            if (c0.AttachmentType == GlRenderbuffer && _renderbuffers.TryGetValue(c0.ResourceHandle, out var rb0))
            {
                rtIsInt = rb0.IsInteger;
                rtIsUInt = rb0.IsUnsigned;
            }
            else if (c0.AttachmentType != 0 && _textures.TryGetValue(c0.ResourceHandle, out var t0))
            {
                rtIsInt = t0.IsInteger;
                rtIsUInt = t0.IsUnsigned;
            }
        }

        // Validate enabled vertex attributes have bound buffers and sufficient capacity
        var vao = CurrentVao;
        int maxVertexIndex = first + count - 1;
        uint[]? resolvedIndices = null;
        if (isIndexed)
        {
            var ebo = _buffers[vao.ElementArrayBuffer!.Value];
            resolvedIndices = new uint[count];
            maxVertexIndex = 0;
            for (var i = 0; i < count; i++)
            {
                uint idx = indexType switch
                {
                    GlUnsignedByte => ebo.Data[indexByteOffset + i],
                    GlUnsignedShort => BinaryPrimitives.ReadUInt16LittleEndian(ebo.Data.AsSpan(indexByteOffset + i * 2, 2)),
                    _ => BinaryPrimitives.ReadUInt32LittleEndian(ebo.Data.AsSpan(indexByteOffset + i * 4, 4)),
                };
                resolvedIndices[i] = idx;
                if ((int)idx > maxVertexIndex) maxVertexIndex = (int)idx;
            }
        }
        else
        {
            resolvedIndices = new uint[count];
            for (var i = 0; i < count; i++) resolvedIndices[i] = (uint)(first + i);
        }

        foreach (var pattr in prog.ActiveAttribs)
        {
            ref var vattr = ref vao.Attribs[pattr.Location];
            if (!vattr.Enabled) continue;
            if (vattr.Buffer is not uint bid || !_buffers.TryGetValue(bid, out var vbuf))
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            var elemSize = vattr.Size * GetVertexComponentByteSize(vattr.Type);
            var effectiveStride = vattr.Stride > 0 ? vattr.Stride : elemSize;
            var maxReqIdx = vattr.Divisor == 0
                ? maxVertexIndex
                : (instanceCount - 1) / (int)vattr.Divisor;
            if (maxReqIdx >= 0)
            {
                var neededBytes = (long)vattr.Offset + (long)maxReqIdx * effectiveStride + elemSize;
                if (neededBytes > vbuf.Data.Length)
                {
                    RecordGlError(GlInvalidOperation);
                    return true;
                }
            }
        }

        // Handle Transform Feedback recording if active
        if (_transformFeedbackActive && !_transformFeedbackPaused && prog.LinkedTfVaryings.Count > 0)
        {
            if (!RecordTransformFeedbackOutput(prog, vao, resolvedIndices, instanceCount))
                return true;
        }

        if (_rasterizerDiscardEnabled)
            return true;

        // Convert topology to triangle list or line list or point list for D3D11
        uint d3dTopology;
        uint[] drawVertices;
        if (mode == GlTriangleFan)
        {
            d3dTopology = 4; // D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST
            if (count < 3) return true;
            var triCount = count - 2;
            drawVertices = new uint[triCount * 3];
            for (var t = 0; t < triCount; t++)
            {
                drawVertices[t * 3] = resolvedIndices[0];
                drawVertices[t * 3 + 1] = resolvedIndices[t + 1];
                drawVertices[t * 3 + 2] = resolvedIndices[t + 2];
            }
        }
        else if (mode == GlLineLoop)
        {
            d3dTopology = 3; // D3D11_PRIMITIVE_TOPOLOGY_LINESTRIP
            if (count < 2) return true;
            drawVertices = new uint[count + 1];
            Array.Copy(resolvedIndices, drawVertices, count);
            drawVertices[count] = resolvedIndices[0];
        }
        else
        {
            drawVertices = resolvedIndices;
            d3dTopology = mode switch
            {
                GlPoints => 1, // POINTLIST
                GlLines => 2, // LINELIST
                GlLineStrip => 3, // LINESTRIP
                GlTriangles => 4, // TRIANGLELIST
                GlTriangleStrip => 5, // TRIANGLESTRIP
                _ => 4,
            };
        }

        // OpenGL ES 3.0 / WebGL 2 uses the LAST vertex of each triangle as the provoking vertex for `flat` varyings,
        // whereas D3D11 uses the FIRST vertex. Rotate each triangle (v0, v1, v2) -> (v2, v0, v1) to preserve winding and match provoking vertex.
        if (d3dTopology == 4 && prog.LinkedVertexShader is { } lvs && lvs.Outputs.Exists(o => o.IsFlat))
        {
            var rotated = new uint[drawVertices.Length];
            for (var i = 0; i < drawVertices.Length; i++)
            {
                var triBase = (i / 3) * 3;
                var rem = i % 3;
                rotated[i] = triBase + 2 < drawVertices.Length
                    ? drawVertices[triBase + (rem + 2) % 3]
                    : drawVertices[i];
            }
            drawVertices = rotated;
        }

        var expandedVertexCount = drawVertices.Length;
        var totalInstances = Math.Max(1, instanceCount);

        // Build per-draw D3D11 input layout and unpacked vertex buffer (16 bytes per attribute per vertex)
        var attrCount = prog.ActiveAttribs.Count;
        IntPtr d3dInputLayout = IntPtr.Zero;
        var d3dVbs = new IntPtr[Math.Max(1, attrCount)];
        var d3dCb0 = IntPtr.Zero;
        var d3dUbos = new List<(int Slot, IntPtr Buffer)>();
        var d3dBlendState = IntPtr.Zero;
        var d3dDepthState = IntPtr.Zero;
        var d3dRastState = IntPtr.Zero;
        var tempFboColorTex = IntPtr.Zero;
        var tempFboColorRtv = IntPtr.Zero;
        var tempFboColorStaging = IntPtr.Zero;
        var tempFboDepthTex = IntPtr.Zero;
        var tempFboDepthDsv = IntPtr.Zero;

        try
        {
            var ctx = _device.ContextPtr;
            var dev = _device.DevicePtr;

            // 1. Input Layout & Vertex Buffers
            if (attrCount > 0)
            {
                var elemDescs = new FolioD3D11Interop.D3D11InputElementDesc[attrCount];
                var semanticPtrs = new IntPtr[attrCount];
                try
                {
                    for (var a = 0; a < attrCount; a++)
                    {
                        var pattr = prog.ActiveAttribs[a];
                        semanticPtrs[a] = Marshal.StringToHGlobalAnsi("ATTRIB");
                        var dxgiFmt = pattr.IsUnsigned
                            ? FolioD3D11Interop.DxgiFormatR32G32B32A32UInt
                            : pattr.IsInteger
                                ? FolioD3D11Interop.DxgiFormatR32G32B32A32SInt
                                : FolioD3D11Interop.DxgiFormatR32G32B32A32Float;
                        ref var vattr = ref vao.Attribs[pattr.Location];
                        var isInstanced = vattr.Enabled && vattr.Divisor > 0;

                        elemDescs[a] = new FolioD3D11Interop.D3D11InputElementDesc
                        {
                            SemanticName = semanticPtrs[a],
                            SemanticIndex = (uint)pattr.Location,
                            Format = dxgiFmt,
                            InputSlot = (uint)a,
                            AlignedByteOffset = 0,
                            InputSlotClass = isInstanced ? 1u : 0u,
                            InstanceDataStepRate = isInstanced ? vattr.Divisor : 0u,
                        };

                        var itemCount = isInstanced ? totalInstances : expandedVertexCount;
                        var vbBytes = BuildAttributeBufferBytes(pattr, vattr, _attribConstants[pattr.Location], drawVertices, itemCount, isInstanced);
                        d3dVbs[a] = CreateD3DBuffer(FolioD3D11Interop.D3D11BindVertexBuffer, vbBytes);
                    }

                    var hDescs = GCHandle.Alloc(elemDescs, GCHandleType.Pinned);
                    try
                    {
                        _createInputLayout(
                            dev,
                            hDescs.AddrOfPinnedObject(),
                            (uint)attrCount,
                            prog.VertexShaderBytecode,
                            (UIntPtr)prog.VertexShaderBytecode.Length,
                            out d3dInputLayout);
                    }
                    finally
                    {
                        hDescs.Free();
                    }
                }
                finally
                {
                    foreach (var p in semanticPtrs)
                        if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
                }

                var strides = new uint[attrCount];
                var offsets = new uint[attrCount];
                Array.Fill(strides, 16u);
                var hVbs = GCHandle.Alloc(d3dVbs, GCHandleType.Pinned);
                var hStrides = GCHandle.Alloc(strides, GCHandleType.Pinned);
                var hOffsets = GCHandle.Alloc(offsets, GCHandleType.Pinned);
                try
                {
                    _iaSetInputLayout(ctx, d3dInputLayout);
                    _iaSetVertexBuffers(ctx, 0, (uint)attrCount, hVbs.AddrOfPinnedObject(), hStrides.AddrOfPinnedObject(), hOffsets.AddrOfPinnedObject());
                }
                finally
                {
                    hVbs.Free();
                    hStrides.Free();
                    hOffsets.Free();
                }
            }
            else
            {
                _iaSetInputLayout(ctx, IntPtr.Zero);
            }

            // 2. Constant buffer b0 and UBOs b1..bN
            var cbBytes = MemoryMarshal.AsBytes(prog.ConstantBufferFloats.AsSpan()).ToArray();
            d3dCb0 = CreateD3DBuffer(FolioD3D11Interop.D3D11BindConstantBuffer, cbBytes);
            _vsSetConstantBuffers(ctx, 0, 1, ref d3dCb0);
            _psSetConstantBuffers(ctx, 0, 1, ref d3dCb0);

            foreach (var uboSlot in prog.ActiveUniformBlocks)
            {
                var binding = _uboBindings[uboSlot.BindingPoint];
                var uboData = new byte[Math.Max(256, ((uboSlot.ByteSize + 15) / 16) * 16)];
                if (binding.Buffer is uint ubid && _buffers.TryGetValue(ubid, out var ubuf))
                {
                    var avail = Math.Max(0, Math.Min((int)binding.Size, ubuf.Data.Length - (int)binding.Offset));
                    var copyLen = Math.Min(avail, uboData.Length);
                    if (copyLen > 0)
                        ubuf.Data.AsSpan((int)binding.Offset, copyLen).CopyTo(uboData);
                }
                var cbUbo = CreateD3DBuffer(FolioD3D11Interop.D3D11BindConstantBuffer, uboData);
                d3dUbos.Add((uboSlot.RegisterSlot, cbUbo));
                _vsSetConstantBuffers(ctx, (uint)uboSlot.RegisterSlot, 1, ref cbUbo);
                _psSetConstantBuffers(ctx, (uint)uboSlot.RegisterSlot, 1, ref cbUbo);
            }

            // 3. Samplers and SRVs
            if (prog.Samplers.Count > 0)
            {
                var srvs = new IntPtr[prog.Samplers.Count];
                var samps = new IntPtr[prog.Samplers.Count];
                for (var s = 0; s < prog.Samplers.Count; s++)
                {
                    var sampSlot = prog.Samplers[s];
                    var unitIdx = 0;
                    if (prog.UniformLocations.TryGetValue(sampSlot.UniformLocation, out var uTarget) && uTarget.Slot.IntValues.Length > 0)
                        unitIdx = Math.Clamp(uTarget.Slot.IntValues[0], 0, MaxTextureUnits - 1);

                    uint? boundTexId = sampSlot.GlType switch
                    {
                        GlSampler3D or GlIntSampler3D or GlUnsignedIntSampler3D => _boundTextures3D[unitIdx],
                        GlSamplerCube or GlSamplerCubeShadow or GlIntSamplerCube or GlUnsignedIntSamplerCube => _boundTexturesCube[unitIdx],
                        GlSampler2DArray or GlSampler2DArrayShadow or GlIntSampler2DArray or GlUnsignedIntSampler2DArray => _boundTextures2DArray[unitIdx],
                        _ => _boundTextures2D[unitIdx],
                    };

                    if (boundTexId is uint tid && _textures.TryGetValue(tid, out var texEntry))
                    {
                        // Feedback loop check: cannot sample from a texture attached to the active draw framebuffer
                        if (fb is not null)
                        {
                            for (var c = 0; c < MaxColorAttachments; c++)
                            {
                                if (fb.ColorAttachments[c].AttachmentType != 0 &&
                                    fb.ColorAttachments[c].AttachmentType != GlRenderbuffer &&
                                    fb.ColorAttachments[c].ResourceHandle == tid)
                                {
                                    RecordGlError(GlInvalidOperation);
                                    return true;
                                }
                            }
                        }
                        srvs[s] = EnsureTextureSrv(texEntry);
                        samps[s] = EnsureTextureSampler(texEntry);
                    }
                }
                var hSrvs = GCHandle.Alloc(srvs, GCHandleType.Pinned);
                var hSamps = GCHandle.Alloc(samps, GCHandleType.Pinned);
                try
                {
                    _vsSetShaderResources(ctx, 0, (uint)srvs.Length, hSrvs.AddrOfPinnedObject());
                    _psSetShaderResources(ctx, 0, (uint)srvs.Length, hSrvs.AddrOfPinnedObject());
                    _vsSetSamplers(ctx, 0, (uint)samps.Length, hSamps.AddrOfPinnedObject());
                    _psSetSamplers(ctx, 0, (uint)samps.Length, hSamps.AddrOfPinnedObject());
                }
                finally
                {
                    hSrvs.Free();
                    hSamps.Free();
                }
            }

            // 4. Render Target & Depth-Stencil setup
            IntPtr activeRtv;
            IntPtr activeDsv;
            if (fb is null)
            {
                activeRtv = _defaultColorRtv;
                activeDsv = _defaultDepthDsv;
            }
            else
            {
                var rtDxgiFormat = rtIsUInt
                    ? FolioD3D11Interop.DxgiFormatR32G32B32A32UInt
                    : rtIsInt
                        ? FolioD3D11Interop.DxgiFormatR32G32B32A32SInt
                        : FolioD3D11Interop.DxgiFormatR32G32B32A32Float;

                var cDesc = new FolioD3D11Interop.D3D11Texture2DDesc
                {
                    Width = (uint)rtW,
                    Height = (uint)rtH,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = rtDxgiFormat,
                    SampleDesc = new FolioD3D11Interop.DxgiSampleDesc { Count = 1, Quality = 0 },
                    Usage = FolioD3D11Interop.D3D11UsageDefault,
                    BindFlags = FolioD3D11Interop.D3D11BindRenderTarget,
                };
                _createTexture2D(dev, ref cDesc, IntPtr.Zero, out tempFboColorTex);
                _createRtv(dev, tempFboColorTex, IntPtr.Zero, out tempFboColorRtv);
                var stDesc = cDesc;
                stDesc.Usage = FolioD3D11Interop.D3D11UsageStaging;
                stDesc.BindFlags = 0;
                stDesc.CPUAccessFlags = FolioD3D11Interop.D3D11CpuAccessRead;
                _createTexture2D(dev, ref stDesc, IntPtr.Zero, out tempFboColorStaging);

                // Initialize tempFboColorTex from current FBO attachment 0 contents
                UploadFboAttachmentToTempTexture(fb.ColorAttachments[0], tempFboColorTex, rtW, rtH, rtIsInt, rtIsUInt);

                var dDesc = new FolioD3D11Interop.D3D11Texture2DDesc
                {
                    Width = (uint)rtW,
                    Height = (uint)rtH,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = FolioD3D11Interop.DxgiFormatD24UnormS8Uint,
                    SampleDesc = new FolioD3D11Interop.DxgiSampleDesc { Count = 1, Quality = 0 },
                    Usage = FolioD3D11Interop.D3D11UsageDefault,
                    BindFlags = FolioD3D11Interop.D3D11BindDepthStencil,
                };
                _createTexture2D(dev, ref dDesc, IntPtr.Zero, out tempFboDepthTex);
                _createDsv(dev, tempFboDepthTex, IntPtr.Zero, out tempFboDepthDsv);
                _clearDsv(ctx, tempFboDepthDsv, FolioD3D11Interop.D3D11ClearDepth | FolioD3D11Interop.D3D11ClearStencil, 1.0f, 0);

                activeRtv = tempFboColorRtv;
                activeDsv = tempFboDepthDsv;
            }

            var rtvs = new IntPtr[] { activeRtv };
            var hRtvs = GCHandle.Alloc(rtvs, GCHandleType.Pinned);
            try
            {
                _omSetRenderTargets(ctx, 1, hRtvs.AddrOfPinnedObject(), activeDsv);
            }
            finally
            {
                hRtvs.Free();
            }

            // 5. Viewport & Scissor
            // Because we negated gl_Position.y in the vertex shader so WebGL y=0 maps to D3D11 row 0,
            // WebGL viewport (x, y, w, h) maps directly to TopLeftX = x, TopLeftY = y!
            var vp = new FolioD3D11Interop.D3D11Viewport
            {
                TopLeftX = _viewportX,
                TopLeftY = _viewportY,
                Width = _viewportW,
                Height = _viewportH,
                MinDepth = _depthRangeNear,
                MaxDepth = _depthRangeFar,
            };
            _rsSetViewports(ctx, 1, ref vp);

            var sc = new FolioD3D11Interop.D3D11Rect
            {
                Left = _scissorTestEnabled ? _scissorX : 0,
                Top = _scissorTestEnabled ? _scissorY : 0,
                Right = _scissorTestEnabled ? _scissorX + _scissorW : rtW,
                Bottom = _scissorTestEnabled ? _scissorY + _scissorH : rtH,
            };
            _rsSetScissorRects(ctx, 1, ref sc);

            // 6. Blend, DepthStencil, Rasterizer states
            d3dBlendState = CreateCurrentBlendState(rtIsInt || rtIsUInt);
            var blendFactors = new float[] { _blendColorR, _blendColorG, _blendColorB, _blendColorA };
            _omSetBlendState(ctx, d3dBlendState, blendFactors, 0xFFFFFFFF);

            d3dDepthState = CreateCurrentDepthStencilState();
            _omSetDepthStencilState(ctx, d3dDepthState, (uint)_stencilRefFront);

            d3dRastState = CreateCurrentRasterizerState();
            _rsSetState(ctx, d3dRastState);

            // 7. Shaders, Topology, Draw
            _vsSetShader(ctx, prog.VertexShader, IntPtr.Zero, 0);
            _psSetShader(ctx, prog.PixelShader, IntPtr.Zero, 0);
            _iaSetPrimitiveTopology(ctx, d3dTopology);

            if (totalInstances > 1)
                _drawInstanced(ctx, (uint)expandedVertexCount, (uint)totalInstances, 0, 0);
            else
                _draw(ctx, (uint)expandedVertexCount, 0);

            // Unbind SRVs so textures aren't left bound
            if (prog.Samplers.Count > 0)
            {
                var emptySrvs = new IntPtr[prog.Samplers.Count];
                var hEmpty = GCHandle.Alloc(emptySrvs, GCHandleType.Pinned);
                try
                {
                    _vsSetShaderResources(ctx, 0, (uint)emptySrvs.Length, hEmpty.AddrOfPinnedObject());
                    _psSetShaderResources(ctx, 0, (uint)emptySrvs.Length, hEmpty.AddrOfPinnedObject());
                }
                finally
                {
                    hEmpty.Free();
                }
            }

            // Copy back FBO result if drawing to custom FBO
            if (fb is not null)
            {
                DownloadTempTextureToFboAttachment(fb.ColorAttachments[0], tempFboColorTex, tempFboColorStaging, rtW, rtH, rtIsInt, rtIsUInt);
            }

            return true;
        }
        finally
        {
            if (tempFboDepthDsv != IntPtr.Zero) Marshal.Release(tempFboDepthDsv);
            if (tempFboDepthTex != IntPtr.Zero) Marshal.Release(tempFboDepthTex);
            if (tempFboColorStaging != IntPtr.Zero) Marshal.Release(tempFboColorStaging);
            if (tempFboColorRtv != IntPtr.Zero) Marshal.Release(tempFboColorRtv);
            if (tempFboColorTex != IntPtr.Zero) Marshal.Release(tempFboColorTex);
            if (d3dRastState != IntPtr.Zero) Marshal.Release(d3dRastState);
            if (d3dDepthState != IntPtr.Zero) Marshal.Release(d3dDepthState);
            if (d3dBlendState != IntPtr.Zero) Marshal.Release(d3dBlendState);
            foreach (var (_, ub) in d3dUbos)
                if (ub != IntPtr.Zero) Marshal.Release(ub);
            if (d3dCb0 != IntPtr.Zero) Marshal.Release(d3dCb0);
            foreach (var vb in d3dVbs)
                if (vb != IntPtr.Zero) Marshal.Release(vb);
            if (d3dInputLayout != IntPtr.Zero) Marshal.Release(d3dInputLayout);
        }
    }

    private IntPtr CreateD3DBuffer(uint bindFlags, byte[] data)
    {
        var byteLen = Math.Max(16, ((data.Length + 15) / 16) * 16);
        var padded = data.Length == byteLen ? data : new byte[byteLen];
        if (!ReferenceEquals(padded, data))
            Buffer.BlockCopy(data, 0, padded, 0, data.Length);

        var desc = new FolioD3D11Interop.D3D11BufferDesc
        {
            ByteWidth = (uint)byteLen,
            Usage = FolioD3D11Interop.D3D11UsageDefault,
            BindFlags = bindFlags,
            CPUAccessFlags = 0,
            MiscFlags = 0,
            StructureByteStride = 0,
        };
        var hData = GCHandle.Alloc(padded, GCHandleType.Pinned);
        try
        {
            var init = new FolioD3D11Interop.D3D11SubresourceData
            {
                SysMem = hData.AddrOfPinnedObject(),
                SysMemPitch = (uint)byteLen,
                SysMemSlicePitch = 0,
            };
            var hInit = GCHandle.Alloc(init, GCHandleType.Pinned);
            try
            {
                _createBuffer(_device.DevicePtr, ref desc, hInit.AddrOfPinnedObject(), out var buf);
                return buf;
            }
            finally
            {
                hInit.Free();
            }
        }
        finally
        {
            hData.Free();
        }
    }

    private byte[] BuildAttributeBufferBytes(
        ProgramAttribSlot pattr,
        VertexAttribState vattr,
        VertexAttribConstant constant,
        uint[] drawVertices,
        int itemCount,
        bool isInstanced)
    {
        var result = new byte[Math.Max(1, itemCount) * 16];
        var vbuf = vattr.Enabled && vattr.Buffer is uint bid && _buffers.TryGetValue(bid, out var b) ? b : null;

        for (var i = 0; i < itemCount; i++)
        {
            var dstSpan = result.AsSpan(i * 16, 16);
            if (!vattr.Enabled || vbuf is null)
            {
                if (pattr.IsUnsigned)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(dstSpan[0..4], constant.U0);
                    BinaryPrimitives.WriteUInt32LittleEndian(dstSpan[4..8], constant.U1);
                    BinaryPrimitives.WriteUInt32LittleEndian(dstSpan[8..12], constant.U2);
                    BinaryPrimitives.WriteUInt32LittleEndian(dstSpan[12..16], constant.U3);
                }
                else if (pattr.IsInteger)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(dstSpan[0..4], constant.I0);
                    BinaryPrimitives.WriteInt32LittleEndian(dstSpan[4..8], constant.I1);
                    BinaryPrimitives.WriteInt32LittleEndian(dstSpan[8..12], constant.I2);
                    BinaryPrimitives.WriteInt32LittleEndian(dstSpan[12..16], constant.I3);
                }
                else
                {
                    BinaryPrimitives.WriteSingleLittleEndian(dstSpan[0..4], constant.F0);
                    BinaryPrimitives.WriteSingleLittleEndian(dstSpan[4..8], constant.F1);
                    BinaryPrimitives.WriteSingleLittleEndian(dstSpan[8..12], constant.F2);
                    BinaryPrimitives.WriteSingleLittleEndian(dstSpan[12..16], constant.F3);
                }
                continue;
            }

            var srcIndex = isInstanced ? i : (int)drawVertices[i];
            var compBytes = GetVertexComponentByteSize(vattr.Type);
            var stride = vattr.Stride > 0 ? vattr.Stride : vattr.Size * compBytes;
            var baseOffset = vattr.Offset + srcIndex * stride;

            if (pattr.IsUnsigned)
            {
                Span<uint> uvals = [0u, 0u, 0u, 1u];
                for (var c = 0; c < vattr.Size; c++)
                {
                    var off = baseOffset + c * compBytes;
                    if (off + compBytes > vbuf.Data.Length) break;
                    uvals[c] = ReadUIntComponent(vbuf.Data.AsSpan(off, compBytes), vattr.Type);
                }
                for (var c = 0; c < 4; c++)
                    BinaryPrimitives.WriteUInt32LittleEndian(dstSpan.Slice(c * 4, 4), uvals[c]);
            }
            else if (pattr.IsInteger)
            {
                Span<int> ivals = [0, 0, 0, 1];
                for (var c = 0; c < vattr.Size; c++)
                {
                    var off = baseOffset + c * compBytes;
                    if (off + compBytes > vbuf.Data.Length) break;
                    ivals[c] = ReadIntComponent(vbuf.Data.AsSpan(off, compBytes), vattr.Type);
                }
                for (var c = 0; c < 4; c++)
                    BinaryPrimitives.WriteInt32LittleEndian(dstSpan.Slice(c * 4, 4), ivals[c]);
            }
            else
            {
                Span<float> fvals = [0f, 0f, 0f, 1f];
                for (var c = 0; c < vattr.Size; c++)
                {
                    var off = baseOffset + c * compBytes;
                    if (off + compBytes > vbuf.Data.Length) break;
                    fvals[c] = ReadFloatComponent(vbuf.Data.AsSpan(off, compBytes), vattr.Type, vattr.Normalized);
                }
                for (var c = 0; c < 4; c++)
                    BinaryPrimitives.WriteSingleLittleEndian(dstSpan.Slice(c * 4, 4), fvals[c]);
            }
        }
        return result;
    }

    private static float ReadFloatComponent(ReadOnlySpan<byte> s, uint type, bool normalized) => type switch
    {
        GlFloat => BinaryPrimitives.ReadSingleLittleEndian(s),
        GlHalfFloat or GlHalfFloatOes => (float)BinaryPrimitives.ReadHalfLittleEndian(s),
        GlByte => normalized ? Math.Max(-1f, (sbyte)s[0] / 127f) : (sbyte)s[0],
        GlUnsignedByte => normalized ? s[0] / 255f : s[0],
        GlShort => normalized ? Math.Max(-1f, BinaryPrimitives.ReadInt16LittleEndian(s) / 32767f) : BinaryPrimitives.ReadInt16LittleEndian(s),
        GlUnsignedShort => normalized ? BinaryPrimitives.ReadUInt16LittleEndian(s) / 65535f : BinaryPrimitives.ReadUInt16LittleEndian(s),
        GlInt => normalized ? Math.Max(-1f, BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483647f) : BinaryPrimitives.ReadInt32LittleEndian(s),
        GlUnsignedInt => normalized ? BinaryPrimitives.ReadUInt32LittleEndian(s) / (float)uint.MaxValue : BinaryPrimitives.ReadUInt32LittleEndian(s),
        _ => 0f,
    };

    private static int ReadIntComponent(ReadOnlySpan<byte> s, uint type) => type switch
    {
        GlByte => (sbyte)s[0],
        GlUnsignedByte => s[0],
        GlShort => BinaryPrimitives.ReadInt16LittleEndian(s),
        GlUnsignedShort => BinaryPrimitives.ReadUInt16LittleEndian(s),
        GlInt => BinaryPrimitives.ReadInt32LittleEndian(s),
        GlUnsignedInt => (int)BinaryPrimitives.ReadUInt32LittleEndian(s),
        _ => 0,
    };

    private static uint ReadUIntComponent(ReadOnlySpan<byte> s, uint type) => type switch
    {
        GlUnsignedByte => s[0],
        GlByte => (uint)(sbyte)s[0],
        GlUnsignedShort => BinaryPrimitives.ReadUInt16LittleEndian(s),
        GlShort => (uint)BinaryPrimitives.ReadInt16LittleEndian(s),
        GlUnsignedInt => BinaryPrimitives.ReadUInt32LittleEndian(s),
        GlInt => (uint)BinaryPrimitives.ReadInt32LittleEndian(s),
        _ => 0u,
    };

    private bool RecordTransformFeedbackOutput(
        ProgramEntry prog,
        VertexArrayEntry vao,
        uint[] resolvedIndices,
        int instanceCount)
    {
        // Evaluate simple vertex shader transform-feedback expressions for each vertex
        var vs = prog.LinkedVertexShader!;
        var totalVertices = resolvedIndices.Length * Math.Max(1, instanceCount);

        // Verify TF buffer bindings and capacity
        for (var vIdx = 0; vIdx < prog.LinkedTfVaryings.Count; vIdx++)
        {
            var slotIdx = prog.TransformFeedbackBufferMode == GlSeparateAttribs ? vIdx : 0;
            var binding = _tfBindings[slotIdx];
            if (binding.Buffer is not uint tfBid || !_buffers.TryGetValue(tfBid, out var tfBuf))
            {
                RecordGlError(GlInvalidOperation);
                return false;
            }
            var comps = GetGlTypeComponents(prog.LinkedTfVaryings[vIdx].Type);
            var bytesPerVert = prog.TransformFeedbackBufferMode == GlSeparateAttribs
                ? comps * 4
                : prog.LinkedTfVaryings.Sum(x => GetGlTypeComponents(x.Type) * 4);
            var available = binding.Size > 0 ? Math.Min(binding.Size, tfBuf.Data.Length - binding.Offset) : tfBuf.Data.Length - binding.Offset;
            if (totalVertices * bytesPerVert > available)
            {
                RecordGlError(GlInvalidOperation);
                return false;
            }
        }

        // Compute and write TF varyings
        var interleavedOffset = (int)_tfBindings[0].Offset;
        var separateOffsets = new int[MaxTransformFeedbackBuffers];
        for (var i = 0; i < MaxTransformFeedbackBuffers; i++)
            separateOffsets[i] = (int)_tfBindings[i].Offset;

        for (var inst = 0; inst < Math.Max(1, instanceCount); inst++)
        {
            for (var vi = 0; vi < resolvedIndices.Length; vi++)
            {
                var vertIdx = (int)resolvedIndices[vi];
                var attrValues = new Dictionary<string, float[]>(StringComparer.Ordinal)
                {
                    ["gl_VertexID"] = [vertIdx, 0f, 0f, 0f],
                    ["gl_InstanceID"] = [inst, 0f, 0f, 0f],
                };
                foreach (var pattr in prog.ActiveAttribs)
                {
                    ref var vattr = ref vao.Attribs[pattr.Location];
                    var vals = new float[] { 0f, 0f, 0f, 1f };
                    if (vattr.Enabled && vattr.Buffer is uint bid && _buffers.TryGetValue(bid, out var vbuf))
                    {
                        var compBytes = GetVertexComponentByteSize(vattr.Type);
                        var stride = vattr.Stride > 0 ? vattr.Stride : vattr.Size * compBytes;
                        var srcIdx = vattr.Divisor == 0 ? vertIdx : inst / (int)vattr.Divisor;
                        var baseOff = vattr.Offset + srcIdx * stride;
                        for (var c = 0; c < vattr.Size; c++)
                        {
                            var off = baseOff + c * compBytes;
                            if (off + compBytes <= vbuf.Data.Length)
                                vals[c] = ReadFloatComponent(vbuf.Data.AsSpan(off, compBytes), vattr.Type, vattr.Normalized);
                        }
                    }
                    attrValues[pattr.Name] = vals;
                }

                for (var vIdx = 0; vIdx < prog.LinkedTfVaryings.Count; vIdx++)
                {
                    var tfVar = prog.LinkedTfVaryings[vIdx];
                    var comps = GetGlTypeComponents(tfVar.Type);
                    var outFloats = EvaluateTfVaryingExpression(vs.MainBody, tfVar.Name, comps, attrValues, prog);

                    var slotIdx = prog.TransformFeedbackBufferMode == GlSeparateAttribs ? vIdx : 0;
                    var tfBuf = _buffers[_tfBindings[slotIdx].Buffer!.Value];
                    var writePos = prog.TransformFeedbackBufferMode == GlSeparateAttribs ? separateOffsets[slotIdx] : interleavedOffset;

                    for (var c = 0; c < comps; c++)
                    {
                        if (writePos + 4 <= tfBuf.Data.Length)
                            BinaryPrimitives.WriteSingleLittleEndian(tfBuf.Data.AsSpan(writePos, 4), outFloats[c]);
                        writePos += 4;
                    }

                    if (prog.TransformFeedbackBufferMode == GlSeparateAttribs)
                        separateOffsets[slotIdx] = writePos;
                    else
                        interleavedOffset = writePos;
                }
            }
        }
        return true;
    }

    private static float[] EvaluateTfVaryingExpression(
        string mainBody,
        string varyingName,
        int comps,
        Dictionary<string, float[]> attrValues,
        ProgramEntry prog)
    {
        var res = new float[comps];
        // Find `<varyingName> = <expr>;` in mainBody
        var m = Regex.Match(mainBody, $@"\b{Regex.Escape(varyingName)}\s*=\s*([^;]+);");
        if (!m.Success) return res;
        var expr = m.Groups[1].Value.Trim();

        for (var c = 0; c < comps; c++)
        {
            res[c] = EvaluateScalarGlslExpr(expr, c, attrValues, prog);
        }
        return res;
    }

    private static float EvaluateScalarGlslExpr(
        string expr,
        int comp,
        Dictionary<string, float[]> attrValues,
        ProgramEntry prog)
    {
        var s = expr.Trim();
        // Strip outer vec2/vec3/vec4(...) if present
        var vecMatch = Regex.Match(s, @"^(?:vec[234]|float)\s*\((.+)\)$");
        if (vecMatch.Success)
        {
            var args = SplitTopLevelCommas(vecMatch.Groups[1].Value);
            if (comp < args.Count)
                s = args[comp].Trim();
        }

        // Evaluate simple binary + / - / * / /
        var plusIdx = FindTopLevelAddSub(s);
        if (plusIdx > 0)
        {
            var op = s[plusIdx];
            var lhs = EvaluateScalarGlslExpr(s[..plusIdx], comp, attrValues, prog);
            var rhs = EvaluateScalarGlslExpr(s[(plusIdx + 1)..], comp, attrValues, prog);
            return op == '+' ? lhs + rhs : lhs - rhs;
        }

        var mulIdx = FindTopLevelMulDiv(s);
        if (mulIdx > 0)
        {
            var op = s[mulIdx];
            var lhs = EvaluateScalarGlslExpr(s[..mulIdx], comp, attrValues, prog);
            var rhs = EvaluateScalarGlslExpr(s[(mulIdx + 1)..], comp, attrValues, prog);
            return op == '*' ? lhs * rhs : (rhs != 0f ? lhs / rhs : 0f);
        }

        // Check swizzle e.g. a_val.x
        var dotIdx = s.IndexOf('.');
        var varName = dotIdx >= 0 ? s[..dotIdx].Trim() : s;
        var swiz = dotIdx >= 0 ? s[(dotIdx + 1)..].Trim() : string.Empty;
        var swizComp = comp < swiz.Length
            ? swiz[comp] switch { 'x' or 'r' => 0, 'y' or 'g' => 1, 'z' or 'b' => 2, 'w' or 'a' => 3, _ => comp }
            : comp;

        if (attrValues.TryGetValue(varName, out var av))
            return av[Math.Clamp(swizComp, 0, av.Length - 1)];

        var uSlot = prog.ActiveUniforms.Find(u => u.BaseName == varName);
        if (uSlot is not null && uSlot.FloatValues.Length > 0)
            return uSlot.FloatValues[Math.Clamp(swizComp, 0, uSlot.FloatValues.Length - 1)];

        if (float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lit))
            return lit;

        return 0f;
    }

    private static List<string> SplitTopLevelCommas(string s)
    {
        var list = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')') depth--;
            else if (s[i] == ',' && depth == 0)
            {
                list.Add(s[start..i]);
                start = i + 1;
            }
        }
        list.Add(s[start..]);
        return list;
    }

    private static int FindTopLevelAddSub(string s)
    {
        var depth = 0;
        for (var i = s.Length - 1; i > 0; i--)
        {
            if (s[i] == ')') depth++;
            else if (s[i] == '(') depth--;
            else if (depth == 0 && (s[i] == '+' || s[i] == '-'))
                return i;
        }
        return -1;
    }

    private static int FindTopLevelMulDiv(string s)
    {
        var depth = 0;
        for (var i = s.Length - 1; i > 0; i--)
        {
            if (s[i] == ')') depth++;
            else if (s[i] == '(') depth--;
            else if (depth == 0 && (s[i] == '*' || s[i] == '/'))
                return i;
        }
        return -1;
    }

    private static int GetGlTypeComponents(uint glType) => glType switch
    {
        GlFloat or GlInt or GlUnsignedInt or GlBool => 1,
        GlFloatVec2 or GlIntVec2 or GlUnsignedIntVec2 or GlBoolVec2 => 2,
        GlFloatVec3 or GlIntVec3 or GlUnsignedIntVec3 or GlBoolVec3 => 3,
        GlFloatVec4 or GlIntVec4 or GlUnsignedIntVec4 or GlBoolVec4 or GlFloatMat2 => 4,
        GlFloatMat3 => 9,
        GlFloatMat4 => 16,
        _ => 4,
    };

    private IntPtr CreateCurrentBlendState(bool isIntegerRt)
    {
        byte mask = 0;
        if (_colorMaskR) mask |= 1;
        if (_colorMaskG) mask |= 2;
        if (_colorMaskB) mask |= 4;
        if (_colorMaskA) mask |= 8;

        var rt0 = new FolioD3D11Interop.D3D11RenderTargetBlendDesc
        {
            BlendEnable = (_blendEnabled && !isIntegerRt) ? 1 : 0,
            SrcBlend = MapBlendFactor(_blendSrcRgb, isAlpha: false),
            DestBlend = MapBlendFactor(_blendDstRgb, isAlpha: false),
            BlendOp = MapBlendOp(_blendEqRgb),
            SrcBlendAlpha = MapBlendFactor(_blendSrcAlpha, isAlpha: true),
            DestBlendAlpha = MapBlendFactor(_blendDstAlpha, isAlpha: true),
            BlendOpAlpha = MapBlendOp(_blendEqAlpha),
            RenderTargetWriteMask = mask,
        };
        var desc = new FolioD3D11Interop.D3D11BlendDesc
        {
            AlphaToCoverageEnable = _sampleAlphaToCoverageEnabled ? 1 : 0,
            IndependentBlendEnable = 0,
            RenderTarget0 = rt0,
        };
        _createBlendState(_device.DevicePtr, ref desc, out var state);
        return state;
    }

    private static int MapBlendFactor(uint factor, bool isAlpha) => factor switch
    {
        GlZero => 1, // D3D11_BLEND_ZERO
        GlOne => 2, // D3D11_BLEND_ONE
        GlSrcColor => isAlpha ? 5 : 3, // SRC_COLOR / SRC_ALPHA
        GlOneMinusSrcColor => isAlpha ? 6 : 4,
        GlSrcAlpha => 5, // D3D11_BLEND_SRC_ALPHA
        GlOneMinusSrcAlpha => 6,
        GlDstAlpha => 7,
        GlOneMinusDstAlpha => 8,
        GlDstColor => isAlpha ? 7 : 9,
        GlOneMinusDstColor => isAlpha ? 8 : 10,
        GlSrcAlphaSaturate => 11,
        GlConstantColor or GlConstantAlpha => 14, // BLEND_FACTOR
        GlOneMinusConstantColor or GlOneMinusConstantAlpha => 15, // INV_BLEND_FACTOR
        _ => 2,
    };

    private static int MapBlendOp(uint eq) => eq switch
    {
        GlFuncSubtract => 2,
        GlFuncReverseSubtract => 3,
        GlMin => 4,
        GlMax => 5,
        _ => 1, // ADD
    };

    private IntPtr CreateCurrentDepthStencilState()
    {
        var desc = new FolioD3D11Interop.D3D11DepthStencilDesc
        {
            DepthEnable = _depthTestEnabled ? 1 : 0,
            DepthWriteMask = _depthMask ? 1 : 0,
            DepthFunc = MapCompareFunc(_depthFunc),
            StencilEnable = _stencilTestEnabled ? 1 : 0,
            StencilReadMask = (byte)(_stencilValMaskFront & 0xFF),
            StencilWriteMask = (byte)(_stencilWritemaskFront & 0xFF),
            FrontFace = new FolioD3D11Interop.D3D11DepthStencilOpDesc
            {
                StencilFailOp = MapStencilOp(_stencilFailFront),
                StencilDepthFailOp = MapStencilOp(_stencilZFailFront),
                StencilPassOp = MapStencilOp(_stencilZPassFront),
                StencilFunc = MapCompareFunc(_stencilFuncFront),
            },
            BackFace = new FolioD3D11Interop.D3D11DepthStencilOpDesc
            {
                StencilFailOp = MapStencilOp(_stencilFailBack),
                StencilDepthFailOp = MapStencilOp(_stencilZFailBack),
                StencilPassOp = MapStencilOp(_stencilZPassBack),
                StencilFunc = MapCompareFunc(_stencilFuncBack),
            },
        };
        _createDepthStencilState(_device.DevicePtr, ref desc, out var state);
        return state;
    }

    private static int MapCompareFunc(uint func) => func switch
    {
        GlNever => 1,
        GlLess => 2,
        GlEqual => 3,
        GlLequal => 4,
        GlGreater => 5,
        GlNotequal => 6,
        GlGequal => 7,
        GlAlways => 8,
        _ => 2,
    };

    private static int MapStencilOp(uint op) => op switch
    {
        GlZero => 2,
        GlReplace => 3,
        GlIncr => 4,
        GlDecr => 5,
        GlInvert => 6,
        GlIncrWrap => 7,
        GlDecrWrap => 8,
        _ => 1, // KEEP
    };

    private IntPtr CreateCurrentRasterizerState()
    {
        // Note: Because we negated gl_Position.y in the vertex shader, winding order in D3D11 screen space is inverted relative to WebGL.
        // Therefore WebGL CCW corresponds to FrontCounterClockwise = 0 in D3D11!
        int cullMode = 1; // NONE
        if (_cullFaceEnabled)
        {
            cullMode = _cullFaceMode switch
            {
                GlFront => 2, // FRONT
                GlBack => 3, // BACK
                _ => 3,
            };
        }
        var desc = new FolioD3D11Interop.D3D11RasterizerDesc
        {
            FillMode = 3, // SOLID
            CullMode = cullMode,
            FrontCounterClockwise = _frontFaceMode == GlCw ? 1 : 0,
            DepthBias = _polygonOffsetFillEnabled ? (int)_polygonOffsetUnits : 0,
            DepthBiasClamp = 0f,
            SlopeScaledDepthBias = _polygonOffsetFillEnabled ? _polygonOffsetFactor : 0f,
            DepthClipEnable = 1,
            ScissorEnable = _scissorTestEnabled ? 1 : 0,
            MultisampleEnable = 0,
            AntialiasedLineEnable = 0,
        };
        _createRasterizerState(_device.DevicePtr, ref desc, out var state);
        return state;
    }

    private void UploadFboAttachmentToTempTexture(FboAttachment att, IntPtr dstTex, int w, int h, bool isInt, bool isUInt)
    {
        var rowPitch = (uint)(w * 16);
        if (att.AttachmentType == GlRenderbuffer && _renderbuffers.TryGetValue(att.ResourceHandle, out var rb) && rb.Defined)
        {
            if (isUInt && rb.UIntPixels.Length > 0)
            {
                var hPin = GCHandle.Alloc(rb.UIntPixels, GCHandleType.Pinned);
                try { _updateSubresourceFull(_device.ContextPtr, dstTex, 0, IntPtr.Zero, hPin.AddrOfPinnedObject(), rowPitch, 0); }
                finally { hPin.Free(); }
            }
            else if (isInt && rb.IntPixels.Length > 0)
            {
                var hPin = GCHandle.Alloc(rb.IntPixels, GCHandleType.Pinned);
                try { _updateSubresourceFull(_device.ContextPtr, dstTex, 0, IntPtr.Zero, hPin.AddrOfPinnedObject(), rowPitch, 0); }
                finally { hPin.Free(); }
            }
            else if (rb.FloatPixels.Length > 0)
            {
                var hPin = GCHandle.Alloc(rb.FloatPixels, GCHandleType.Pinned);
                try { _updateSubresourceFull(_device.ContextPtr, dstTex, 0, IntPtr.Zero, hPin.AddrOfPinnedObject(), rowPitch, 0); }
                finally { hPin.Free(); }
            }
        }
        else if (att.AttachmentType != 0 && _textures.TryGetValue(att.ResourceHandle, out var tex))
        {
            var face = tex.Target == GlTextureCubeMap ? att.Layer : 0;
            if (tex.Slices.TryGetValue((face, att.Level), out var slice) && slice.Defined)
            {
                var layerByteOffset = (tex.Target is GlTexture3D or GlTexture2DArray) ? att.Layer * w * h * 16 : 0;
                if (isUInt && slice.UIntTexels.Length > 0)
                {
                    var hPin = GCHandle.Alloc(slice.UIntTexels, GCHandleType.Pinned);
                    try { _updateSubresourceFull(_device.ContextPtr, dstTex, 0, IntPtr.Zero, IntPtr.Add(hPin.AddrOfPinnedObject(), layerByteOffset), rowPitch, 0); }
                    finally { hPin.Free(); }
                }
                else if (isInt && slice.IntTexels.Length > 0)
                {
                    var hPin = GCHandle.Alloc(slice.IntTexels, GCHandleType.Pinned);
                    try { _updateSubresourceFull(_device.ContextPtr, dstTex, 0, IntPtr.Zero, IntPtr.Add(hPin.AddrOfPinnedObject(), layerByteOffset), rowPitch, 0); }
                    finally { hPin.Free(); }
                }
                else
                {
                    var hPin = GCHandle.Alloc(slice.FloatTexels, GCHandleType.Pinned);
                    try { _updateSubresourceFull(_device.ContextPtr, dstTex, 0, IntPtr.Zero, IntPtr.Add(hPin.AddrOfPinnedObject(), layerByteOffset), rowPitch, 0); }
                    finally { hPin.Free(); }
                }
            }
        }
    }

    private void DownloadTempTextureToFboAttachment(FboAttachment att, IntPtr srcTex, IntPtr stagingTex, int w, int h, bool isInt, bool isUInt)
    {
        _copyResource(_device.ContextPtr, stagingTex, srcTex);
        var hr = _map(_device.ContextPtr, stagingTex, 0, FolioD3D11Interop.D3D11MapRead, 0, out var mapped);
        if (hr < 0 || mapped.Data == IntPtr.Zero) return;
        try
        {
            var rowFloats = w * 4;
            if (att.AttachmentType == GlRenderbuffer && _renderbuffers.TryGetValue(att.ResourceHandle, out var rb) && rb.Defined)
            {
                CopyMappedToBuffers(mapped, w, h, isInt, isUInt, rb.FloatPixels, rb.IntPixels, rb.UIntPixels, 0);
            }
            else if (att.AttachmentType != 0 && _textures.TryGetValue(att.ResourceHandle, out var tex))
            {
                var face = tex.Target == GlTextureCubeMap ? att.Layer : 0;
                if (tex.Slices.TryGetValue((face, att.Level), out var slice) && slice.Defined)
                {
                    var layerOffset = (tex.Target is GlTexture3D or GlTexture2DArray) ? att.Layer * w * h * 4 : 0;
                    CopyMappedToBuffers(mapped, w, h, isInt, isUInt, slice.FloatTexels, slice.IntTexels, slice.UIntTexels, layerOffset);
                    tex.D3DDirty = true;
                }
            }
        }
        finally
        {
            _unmap(_device.ContextPtr, stagingTex, 0);
        }
    }

    private static void CopyMappedToBuffers(
        FolioD3D11Interop.D3D11MappedSubresource mapped,
        int w,
        int h,
        bool isInt,
        bool isUInt,
        float[] floatDst,
        int[] intDst,
        uint[] uintDst,
        int dstOffset)
    {
        var rowFloats = w * 4;
        var tempInts = new int[rowFloats];
        for (var r = 0; r < h; r++)
        {
            var srcPtr = IntPtr.Add(mapped.Data, checked((int)(r * mapped.RowPitch)));
            var rowBase = dstOffset + r * rowFloats;
            if (isUInt && uintDst.Length >= rowBase + rowFloats)
            {
                Marshal.Copy(srcPtr, tempInts, 0, rowFloats);
                for (var i = 0; i < rowFloats; i++)
                {
                    var uv = (uint)tempInts[i];
                    uintDst[rowBase + i] = uv;
                    floatDst[rowBase + i] = uv;
                }
            }
            else if (isInt && intDst.Length >= rowBase + rowFloats)
            {
                Marshal.Copy(srcPtr, intDst, rowBase, rowFloats);
                for (var i = 0; i < rowFloats; i++)
                    floatDst[rowBase + i] = intDst[rowBase + i];
            }
            else
            {
                Marshal.Copy(srcPtr, floatDst, rowBase, rowFloats);
            }
        }
    }

    private void ReadD3DFloatTexture(IntPtr tex, IntPtr staging, int w, int h, float[] dst)
    {
        _copyResource(_device.ContextPtr, staging, tex);
        var hr = _map(_device.ContextPtr, staging, 0, FolioD3D11Interop.D3D11MapRead, 0, out var mapped);
        if (hr < 0 || mapped.Data == IntPtr.Zero) return;
        try
        {
            var rowFloats = w * 4;
            for (var r = 0; r < h; r++)
            {
                var srcPtr = IntPtr.Add(mapped.Data, checked((int)(r * mapped.RowPitch)));
                Marshal.Copy(srcPtr, dst, r * rowFloats, rowFloats);
            }
        }
        finally
        {
            _unmap(_device.ContextPtr, staging, 0);
        }
    }

    private void WriteD3DFloatTexture(IntPtr tex, int w, int h, float[] src)
    {
        var hPin = GCHandle.Alloc(src, GCHandleType.Pinned);
        try
        {
            _updateSubresourceFull(_device.ContextPtr, tex, 0, IntPtr.Zero, hPin.AddrOfPinnedObject(), (uint)(w * 16), 0);
        }
        finally
        {
            hPin.Free();
        }
    }

    private void ReadFramebufferColorFloats(uint? fbId, int x, int y, int width, int height, Span<float> dst)
    {
        if (fbId is null)
        {
            var bw = Math.Max(1, _width);
            var bh = Math.Max(1, _height);
            var full = new float[bw * bh * 4];
            ReadD3DFloatTexture(_defaultColorTex, _defaultColorStaging, bw, bh, full);
            CopySubrectFloats(full, bw, bh, x, y, width, height, dst);
            return;
        }
        if (!_framebuffers.TryGetValue(fbId.Value, out var fb)) return;
        var att = fb.ColorAttachments[0];
        if (att.AttachmentType == GlRenderbuffer && _renderbuffers.TryGetValue(att.ResourceHandle, out var rb) && rb.Defined)
        {
            CopySubrectFloats(rb.FloatPixels, rb.Width, rb.Height, x, y, width, height, dst);
        }
        else if (att.AttachmentType != 0 && _textures.TryGetValue(att.ResourceHandle, out var tex))
        {
            var face = tex.Target == GlTextureCubeMap ? att.Layer : 0;
            if (tex.Slices.TryGetValue((face, att.Level), out var slice) && slice.Defined)
            {
                var layerOffset = (tex.Target is GlTexture3D or GlTexture2DArray) ? att.Layer * slice.Width * slice.Height * 4 : 0;
                CopySubrectFloats(slice.FloatTexels.AsSpan(layerOffset, slice.Width * slice.Height * 4), slice.Width, slice.Height, x, y, width, height, dst);
            }
        }
    }

    private void WriteFramebufferColorFloats(uint? fbId, int x, int y, int width, int height, ReadOnlySpan<float> src)
    {
        if (fbId is null)
        {
            var bw = Math.Max(1, _width);
            var bh = Math.Max(1, _height);
            var full = new float[bw * bh * 4];
            ReadD3DFloatTexture(_defaultColorTex, _defaultColorStaging, bw, bh, full);
            WriteSubrectFloats(src, width, height, full, bw, bh, x, y);
            WriteD3DFloatTexture(_defaultColorTex, bw, bh, full);
            return;
        }
        if (!_framebuffers.TryGetValue(fbId.Value, out var fb)) return;
        var att = fb.ColorAttachments[0];
        if (att.AttachmentType == GlRenderbuffer && _renderbuffers.TryGetValue(att.ResourceHandle, out var rb) && rb.Defined)
        {
            WriteSubrectFloats(src, width, height, rb.FloatPixels, rb.Width, rb.Height, x, y);
        }
        else if (att.AttachmentType != 0 && _textures.TryGetValue(att.ResourceHandle, out var tex))
        {
            var face = tex.Target == GlTextureCubeMap ? att.Layer : 0;
            if (tex.Slices.TryGetValue((face, att.Level), out var slice) && slice.Defined)
            {
                var layerOffset = (tex.Target is GlTexture3D or GlTexture2DArray) ? att.Layer * slice.Width * slice.Height * 4 : 0;
                WriteSubrectFloats(src, width, height, slice.FloatTexels.AsSpan(layerOffset, slice.Width * slice.Height * 4), slice.Width, slice.Height, x, y);
                tex.D3DDirty = true;
            }
        }
    }

    private static void CopySubrectFloats(ReadOnlySpan<float> src, int srcW, int srcH, int x, int y, int w, int h, Span<float> dst)
    {
        for (var r = 0; r < h; r++)
        {
            var sy = y + r;
            if (sy < 0 || sy >= srcH) continue;
            for (var c = 0; c < w; c++)
            {
                var sx = x + c;
                if (sx < 0 || sx >= srcW) continue;
                var sIdx = (sy * srcW + sx) * 4;
                var dIdx = (r * w + c) * 4;
                dst[dIdx] = src[sIdx];
                dst[dIdx + 1] = src[sIdx + 1];
                dst[dIdx + 2] = src[sIdx + 2];
                dst[dIdx + 3] = src[sIdx + 3];
            }
        }
    }

    private static void WriteSubrectFloats(ReadOnlySpan<float> src, int w, int h, Span<float> dst, int dstW, int dstH, int x, int y)
    {
        for (var r = 0; r < h; r++)
        {
            var dy = y + r;
            if (dy < 0 || dy >= dstH) continue;
            for (var c = 0; c < w; c++)
            {
                var dx = x + c;
                if (dx < 0 || dx >= dstW) continue;
                var sIdx = (r * w + c) * 4;
                var dIdx = (dy * dstW + dx) * 4;
                dst[dIdx] = src[sIdx];
                dst[dIdx + 1] = src[sIdx + 1];
                dst[dIdx + 2] = src[sIdx + 2];
                dst[dIdx + 3] = src[sIdx + 3];
            }
        }
    }

    private bool ReadCurrentFramebufferPixelsRgba8(int x, int y, int width, int height, Span<byte> dst, out GpuFailure? failure)
    {
        failure = null;
        var floats = new float[width * height * 4];
        ReadFramebufferColorFloats(_boundReadFramebuffer, x, y, width, height, floats);
        for (var i = 0; i < floats.Length; i++)
            dst[i] = (byte)Math.Clamp((int)Math.Round(floats[i] * 255f), 0, 255);
        return true;
    }

    public bool TryReadPixels(int x, int y, int width, int height, Span<byte> destination, uint format, uint type, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (width < 0 || height < 0)
            {
                RecordGlError(GlInvalidValue);
                failure = new(GpuFailureKind.NativeError, "The readback dimensions are invalid.", GlInvalidValue);
                return false;
            }
            if (_boundReadFramebuffer is uint rfbId)
            {
                if (!_framebuffers.TryGetValue(rfbId, out var rfb) || EvaluateFramebufferStatus(rfb, out _, out _) != GlFramebufferComplete)
                {
                    RecordGlError(GlInvalidFramebufferOperation);
                    failure = new(GpuFailureKind.NativeError, "The read framebuffer is incomplete.", GlInvalidFramebufferOperation);
                    return false;
                }
            }
            if (_packRowLength > 0 && _packRowLength < width)
            {
                RecordGlError(GlInvalidValue);
                failure = new(GpuFailureKind.NativeError, "The pack row length is smaller than the read width.", GlInvalidValue);
                return false;
            }
            if (width == 0 || height == 0) return true;

            var pixelBytes = GetSourcePixelByteSize(format, type);
            var rowPixels = _packRowLength > 0 ? _packRowLength : width;
            var rawRowBytes = rowPixels * pixelBytes;
            var align = Math.Max(1, _packAlignment);
            var alignedRowStride = ((rawRowBytes + align - 1) / align) * align;
            var baseOffset = _packSkipRows * alignedRowStride + _packSkipPixels * pixelBytes;
            var needed = baseOffset + (height - 1) * alignedRowStride + width * pixelBytes;
            if (destination.Length < needed)
            {
                RecordGlError(GlInvalidOperation);
                failure = new(GpuFailureKind.NativeError, "The readback destination buffer is too small.", GlInvalidOperation);
                return false;
            }

            var floats = new float[width * height * 4];
            ReadFramebufferColorFloats(_boundReadFramebuffer, x, y, width, height, floats);

            for (var r = 0; r < height; r++)
            {
                var rowStart = baseOffset + r * alignedRowStride;
                for (var c = 0; c < width; c++)
                {
                    var sIdx = (r * width + c) * 4;
                    var pxDst = destination.Slice(rowStart + c * pixelBytes, pixelBytes);
                    PackOnePixel(floats.AsSpan(sIdx, 4), format, type, pxDst);
                }
            }
            return true;
        }
    }

    private static void PackOnePixel(ReadOnlySpan<float> rgba, uint format, uint type, Span<byte> dst)
    {
        if (type == GlUnsignedShort565)
        {
            var r5 = (ushort)Math.Clamp((int)Math.Round(rgba[0] * 31f), 0, 31);
            var g6 = (ushort)Math.Clamp((int)Math.Round(rgba[1] * 63f), 0, 63);
            var b5 = (ushort)Math.Clamp((int)Math.Round(rgba[2] * 31f), 0, 31);
            BinaryPrimitives.WriteUInt16LittleEndian(dst, (ushort)((r5 << 11) | (g6 << 5) | b5));
            return;
        }
        if (type == GlUnsignedShort4444)
        {
            var r4 = (ushort)Math.Clamp((int)Math.Round(rgba[0] * 15f), 0, 15);
            var g4 = (ushort)Math.Clamp((int)Math.Round(rgba[1] * 15f), 0, 15);
            var b4 = (ushort)Math.Clamp((int)Math.Round(rgba[2] * 15f), 0, 15);
            var a4 = (ushort)Math.Clamp((int)Math.Round(rgba[3] * 15f), 0, 15);
            BinaryPrimitives.WriteUInt16LittleEndian(dst, (ushort)((r4 << 12) | (g4 << 8) | (b4 << 4) | a4));
            return;
        }
        if (type == GlUnsignedShort5551)
        {
            var r5 = (ushort)Math.Clamp((int)Math.Round(rgba[0] * 31f), 0, 31);
            var g5 = (ushort)Math.Clamp((int)Math.Round(rgba[1] * 31f), 0, 31);
            var b5 = (ushort)Math.Clamp((int)Math.Round(rgba[2] * 31f), 0, 31);
            var a1 = (ushort)(rgba[3] >= 0.5f ? 1 : 0);
            BinaryPrimitives.WriteUInt16LittleEndian(dst, (ushort)((r5 << 11) | (g5 << 6) | (b5 << 1) | a1));
            return;
        }
        if (type == GlFloat)
        {
            var comps = format == GlRed ? 1 : format == GlRg ? 2 : format == GlRgb ? 3 : 4;
            for (var i = 0; i < comps; i++)
                BinaryPrimitives.WriteSingleLittleEndian(dst.Slice(i * 4, 4), rgba[i]);
            return;
        }
        if (type == GlInt || format == GlRgbaInteger)
        {
            for (var i = 0; i < 4 && (i + 1) * 4 <= dst.Length; i++)
                BinaryPrimitives.WriteInt32LittleEndian(dst.Slice(i * 4, 4), (int)Math.Round(rgba[i]));
            return;
        }
        if (type == GlUnsignedInt)
        {
            for (var i = 0; i < 4 && (i + 1) * 4 <= dst.Length; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(i * 4, 4), (uint)Math.Max(0f, Math.Round(rgba[i])));
            return;
        }
        var bComps = format == GlAlpha ? 1 : format == GlRgb ? 3 : 4;
        if (bComps == 1)
        {
            dst[0] = (byte)Math.Clamp((int)Math.Round(rgba[3] * 255f), 0, 255);
            return;
        }
        for (var i = 0; i < bComps; i++)
            dst[i] = (byte)Math.Clamp((int)Math.Round(rgba[i] * 255f), 0, 255);
    }

    public bool TryReadPixelsPresentation(int x, int y, int width, int height, Span<byte> destination, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            var expected = width * height * 4;
            if (destination.Length < expected)
            {
                failure = new(GpuFailureKind.NativeError, "The presentation destination span is too small.");
                return false;
            }
            var floats = new float[width * height * 4];
            ReadFramebufferColorFloats(null, x, y, width, height, floats);

            // Flip vertically for HTML <canvas> top-down presentation (row 0 = top of canvas = WebGL y = height - 1)
            for (var r = 0; r < height; r++)
            {
                var srcRow = height - 1 - r;
                for (var c = 0; c < width; c++)
                {
                    var sIdx = (srcRow * width + c) * 4;
                    var dIdx = (r * width + c) * 4;
                    destination[dIdx] = (byte)Math.Clamp((int)Math.Round(floats[sIdx] * 255f), 0, 255);
                    destination[dIdx + 1] = (byte)Math.Clamp((int)Math.Round(floats[sIdx + 1] * 255f), 0, 255);
                    destination[dIdx + 2] = (byte)Math.Clamp((int)Math.Round(floats[sIdx + 2] * 255f), 0, 255);
                    destination[dIdx + 3] = Attributes.Alpha
                        ? (byte)Math.Clamp((int)Math.Round(floats[sIdx + 3] * 255f), 0, 255)
                        : (byte)255;
                }
            }
            return true;
        }
    }

    public bool TryGetParameter(uint parameter, out WebGlParameterValue value, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            value = default;
            if (!CheckAvailable(out failure)) return false;
            switch (parameter)
            {
                case 0x1F00: // VENDOR
                    value = new(WebGlParameterKind.String, String: "Folio");
                    return true;
                case 0x1F01: // RENDERER
                    value = new(WebGlParameterKind.String, String: $"Folio WebGL ({_device.RendererIdentity})");
                    return true;
                case 0x1F02: // VERSION
                    value = new(WebGlParameterKind.String, String: _options.Version == WebGlContextVersion.WebGl2 ? "WebGL 2.0 (Folio D3D11)" : "WebGL 1.0 (Folio D3D11)");
                    return true;
                case 0x8B8C: // SHADING_LANGUAGE_VERSION
                    value = new(WebGlParameterKind.String, String: _options.Version == WebGlContextVersion.WebGl2 ? "WebGL GLSL ES 3.00 (Folio D3D11)" : "WebGL GLSL ES 1.0 (Folio D3D11)");
                    return true;
                case 0x9245: // UNMASKED_VENDOR_WEBGL
                    value = new(WebGlParameterKind.String, String: "Folio");
                    return true;
                case 0x9246: // UNMASKED_RENDERER_WEBGL
                    value = new(WebGlParameterKind.String, String: _device.RendererIdentity);
                    return true;
                case 0x0BA2: // VIEWPORT
                    value = new(WebGlParameterKind.Int32Array, Int32Array: [_viewportX, _viewportY, _viewportW, _viewportH]);
                    return true;
                case 0x0C10: // SCISSOR_BOX
                    value = new(WebGlParameterKind.Int32Array, Int32Array: [_scissorX, _scissorY, _scissorW, _scissorH]);
                    return true;
                case 0x0C22: // COLOR_CLEAR_VALUE
                    value = new(WebGlParameterKind.Float32Array, Float32Array: [_clearR, _clearG, _clearB, _clearA]);
                    return true;
                case 0x0C23: // COLOR_WRITEMASK
                    value = new(WebGlParameterKind.BooleanArray, Int32Array: [_colorMaskR ? 1 : 0, _colorMaskG ? 1 : 0, _colorMaskB ? 1 : 0, _colorMaskA ? 1 : 0]);
                    return true;
                case 0x0B70: // DEPTH_RANGE
                    value = new(WebGlParameterKind.Float32Array, Float32Array: [_depthRangeNear, _depthRangeFar]);
                    return true;
                case 0x0B72: // DEPTH_WRITEMASK
                    value = new(WebGlParameterKind.Boolean, Boolean: _depthMask, Int32: _depthMask ? 1 : 0);
                    return true;
                case 0x0B73: // DEPTH_CLEAR_VALUE
                    value = new(WebGlParameterKind.Float32, Float32: _clearDepth);
                    return true;
                case 0x0B74: // DEPTH_FUNC
                    value = new(WebGlParameterKind.UInt32, UInt32: _depthFunc, Int32: (int)_depthFunc);
                    return true;
                case 0x0B21: // LINE_WIDTH
                    value = new(WebGlParameterKind.Float32, Float32: _lineWidth);
                    return true;
                case 0x8038: // POLYGON_OFFSET_FACTOR
                    value = new(WebGlParameterKind.Float32, Float32: _polygonOffsetFactor);
                    return true;
                case 0x2A00: // POLYGON_OFFSET_UNITS
                    value = new(WebGlParameterKind.Float32, Float32: _polygonOffsetUnits);
                    return true;
                case 0x80AA: // SAMPLE_COVERAGE_VALUE
                    value = new(WebGlParameterKind.Float32, Float32: _sampleCoverageValue);
                    return true;
                case 0x80AB: // SAMPLE_COVERAGE_INVERT
                    value = new(WebGlParameterKind.Boolean, Boolean: _sampleCoverageInvert, Int32: _sampleCoverageInvert ? 1 : 0);
                    return true;
                case 0x84E0: // ACTIVE_TEXTURE
                    value = new(WebGlParameterKind.UInt32, UInt32: GlTexture0 + (uint)_activeTextureUnit, Int32: (int)(GlTexture0 + (uint)_activeTextureUnit));
                    return true;
                case 0x8B8D: // CURRENT_PROGRAM
                    value = new(WebGlParameterKind.UInt32, UInt32: _currentProgram ?? 0u, Int32: (int)(_currentProgram ?? 0u));
                    return true;
                case 0x8894: // ARRAY_BUFFER_BINDING
                    value = new(WebGlParameterKind.UInt32, UInt32: _boundArrayBuffer ?? 0u, Int32: (int)(_boundArrayBuffer ?? 0u));
                    return true;
                case 0x8895: // ELEMENT_ARRAY_BUFFER_BINDING
                    value = new(WebGlParameterKind.UInt32, UInt32: CurrentVao.ElementArrayBuffer ?? 0u, Int32: (int)(CurrentVao.ElementArrayBuffer ?? 0u));
                    return true;
                case 0x8CA6: // FRAMEBUFFER_BINDING / DRAW_FRAMEBUFFER_BINDING
                    value = new(WebGlParameterKind.UInt32, UInt32: _boundDrawFramebuffer ?? 0u, Int32: (int)(_boundDrawFramebuffer ?? 0u));
                    return true;
                case 0x8CAA: // READ_FRAMEBUFFER_BINDING
                    value = new(WebGlParameterKind.UInt32, UInt32: _boundReadFramebuffer ?? 0u, Int32: (int)(_boundReadFramebuffer ?? 0u));
                    return true;
                case 0x8CA7: // RENDERBUFFER_BINDING
                    value = new(WebGlParameterKind.UInt32, UInt32: _boundRenderbuffer ?? 0u, Int32: (int)(_boundRenderbuffer ?? 0u));
                    return true;
                case 0x85B5: // VERTEX_ARRAY_BINDING
                    value = new(WebGlParameterKind.UInt32, UInt32: _boundVertexArray ?? 0u, Int32: (int)(_boundVertexArray ?? 0u));
                    return true;
                case 0x8069: // TEXTURE_BINDING_2D
                    value = new(WebGlParameterKind.UInt32, UInt32: _boundTextures2D[_activeTextureUnit] ?? 0u, Int32: (int)(_boundTextures2D[_activeTextureUnit] ?? 0u));
                    return true;
                case 0x8514: // TEXTURE_BINDING_CUBE_MAP
                    value = new(WebGlParameterKind.UInt32, UInt32: _boundTexturesCube[_activeTextureUnit] ?? 0u, Int32: (int)(_boundTexturesCube[_activeTextureUnit] ?? 0u));
                    return true;
                case 0x806A: // TEXTURE_BINDING_3D
                    value = new(WebGlParameterKind.UInt32, UInt32: _boundTextures3D[_activeTextureUnit] ?? 0u, Int32: (int)(_boundTextures3D[_activeTextureUnit] ?? 0u));
                    return true;
                case 0x8C1D: // TEXTURE_BINDING_2D_ARRAY
                    value = new(WebGlParameterKind.UInt32, UInt32: _boundTextures2DArray[_activeTextureUnit] ?? 0u, Int32: (int)(_boundTextures2DArray[_activeTextureUnit] ?? 0u));
                    return true;
                case GlUnpackAlignment: value = new(WebGlParameterKind.Int32, Int32: _unpackAlignment); return true;
                case GlPackAlignment: value = new(WebGlParameterKind.Int32, Int32: _packAlignment); return true;
                case GlUnpackFlipYWebgl: value = new(WebGlParameterKind.Boolean, Boolean: _unpackFlipY, Int32: _unpackFlipY ? 1 : 0); return true;
                case GlUnpackPremultiplyAlphaWebgl: value = new(WebGlParameterKind.Boolean, Boolean: _unpackPremultiplyAlpha, Int32: _unpackPremultiplyAlpha ? 1 : 0); return true;
                case GlUnpackColorspaceConversionWebgl: value = new(WebGlParameterKind.UInt32, UInt32: (uint)_unpackColorspaceConversion, Int32: _unpackColorspaceConversion); return true;
                case GlUnpackRowLength: value = new(WebGlParameterKind.Int32, Int32: _unpackRowLength); return true;
                case GlUnpackSkipRows: value = new(WebGlParameterKind.Int32, Int32: _unpackSkipRows); return true;
                case GlUnpackSkipPixels: value = new(WebGlParameterKind.Int32, Int32: _unpackSkipPixels); return true;
                case GlUnpackSkipImages: value = new(WebGlParameterKind.Int32, Int32: _unpackSkipImages); return true;
                case GlUnpackImageHeight: value = new(WebGlParameterKind.Int32, Int32: _unpackImageHeight); return true;
                case GlPackRowLength: value = new(WebGlParameterKind.Int32, Int32: _packRowLength); return true;
                case GlPackSkipRows: value = new(WebGlParameterKind.Int32, Int32: _packSkipRows); return true;
                case GlPackSkipPixels: value = new(WebGlParameterKind.Int32, Int32: _packSkipPixels); return true;
                case GlCullFace: value = new(WebGlParameterKind.Boolean, Boolean: _cullFaceEnabled, Int32: _cullFaceEnabled ? 1 : 0); return true;
                case GlBlend: value = new(WebGlParameterKind.Boolean, Boolean: _blendEnabled, Int32: _blendEnabled ? 1 : 0); return true;
                case GlDither: value = new(WebGlParameterKind.Boolean, Boolean: _ditherEnabled, Int32: _ditherEnabled ? 1 : 0); return true;
                case GlStencilTest: value = new(WebGlParameterKind.Boolean, Boolean: _stencilTestEnabled, Int32: _stencilTestEnabled ? 1 : 0); return true;
                case GlDepthTest: value = new(WebGlParameterKind.Boolean, Boolean: _depthTestEnabled, Int32: _depthTestEnabled ? 1 : 0); return true;
                case GlScissorTest: value = new(WebGlParameterKind.Boolean, Boolean: _scissorTestEnabled, Int32: _scissorTestEnabled ? 1 : 0); return true;
                case GlPolygonOffsetFill: value = new(WebGlParameterKind.Boolean, Boolean: _polygonOffsetFillEnabled, Int32: _polygonOffsetFillEnabled ? 1 : 0); return true;
                case GlSampleAlphaToCoverage: value = new(WebGlParameterKind.Boolean, Boolean: _sampleAlphaToCoverageEnabled, Int32: _sampleAlphaToCoverageEnabled ? 1 : 0); return true;
                case GlSampleCoverage: value = new(WebGlParameterKind.Boolean, Boolean: _sampleCoverageEnabled, Int32: _sampleCoverageEnabled ? 1 : 0); return true;
                case GlRasterizerDiscard: value = new(WebGlParameterKind.Boolean, Boolean: _rasterizerDiscardEnabled, Int32: _rasterizerDiscardEnabled ? 1 : 0); return true;
                case 0x0B45: // CULL_FACE_MODE
                    value = new(WebGlParameterKind.UInt32, UInt32: _cullFaceMode, Int32: (int)_cullFaceMode);
                    return true;
                case 0x0B46: // FRONT_FACE
                    value = new(WebGlParameterKind.UInt32, UInt32: _frontFaceMode, Int32: (int)_frontFaceMode);
                    return true;
                case 0x8005: // BLEND_COLOR
                    value = new(WebGlParameterKind.Float32Array, Float32Array: [_blendColorR, _blendColorG, _blendColorB, _blendColorA]);
                    return true;
                case 0x80C8: // BLEND_DST_RGB
                    value = new(WebGlParameterKind.UInt32, UInt32: _blendDstRgb, Int32: (int)_blendDstRgb);
                    return true;
                case 0x80C9: // BLEND_SRC_RGB
                    value = new(WebGlParameterKind.UInt32, UInt32: _blendSrcRgb, Int32: (int)_blendSrcRgb);
                    return true;
                case 0x80CA: // BLEND_DST_ALPHA
                    value = new(WebGlParameterKind.UInt32, UInt32: _blendDstAlpha, Int32: (int)_blendDstAlpha);
                    return true;
                case 0x80CB: // BLEND_SRC_ALPHA
                    value = new(WebGlParameterKind.UInt32, UInt32: _blendSrcAlpha, Int32: (int)_blendSrcAlpha);
                    return true;
                case GlBlendEquationRgb:
                    value = new(WebGlParameterKind.UInt32, UInt32: _blendEqRgb, Int32: (int)_blendEqRgb);
                    return true;
                case GlBlendEquationAlpha:
                    value = new(WebGlParameterKind.UInt32, UInt32: _blendEqAlpha, Int32: (int)_blendEqAlpha);
                    return true;
                case 0x8869: // MAX_VERTEX_ATTRIBS
                    value = new(WebGlParameterKind.Int32, Int32: MaxVertexAttribs);
                    return true;
                case 0x8872: // MAX_TEXTURE_IMAGE_UNITS
                case 0x8B4C: // MAX_VERTEX_TEXTURE_IMAGE_UNITS
                case 0x8B4D: // MAX_COMBINED_TEXTURE_IMAGE_UNITS
                    value = new(WebGlParameterKind.Int32, Int32: MaxTextureUnits);
                    return true;
                case 0x0D33: // MAX_TEXTURE_SIZE
                case 0x851C: // MAX_CUBE_MAP_TEXTURE_SIZE
                case 0x84E8: // MAX_RENDERBUFFER_SIZE
                    value = new(WebGlParameterKind.Int32, Int32: 4096);
                    return true;
                case 0x8073: // MAX_3D_TEXTURE_SIZE
                case 0x88FF: // MAX_ARRAY_TEXTURE_LAYERS
                    value = new(WebGlParameterKind.Int32, Int32: 2048);
                    return true;
                case 0x8CDF: // MAX_COLOR_ATTACHMENTS
                case 0x8824: // MAX_DRAW_BUFFERS
                    value = new(WebGlParameterKind.Int32, Int32: MaxColorAttachments);
                    return true;
                case 0x8D57: // MAX_SAMPLES
                    value = new(WebGlParameterKind.Int32, Int32: 4);
                    return true;
                case 0x0D3A: // MAX_VIEWPORT_DIMS
                    value = new(WebGlParameterKind.Int32Array, Int32Array: [4096, 4096]);
                    return true;
                case 0x846D: // ALIASED_POINT_SIZE_RANGE
                case 0x846E: // ALIASED_LINE_WIDTH_RANGE
                    value = new(WebGlParameterKind.Float32Array, Float32Array: [1f, 1f]);
                    return true;
                default:
                    value = new(WebGlParameterKind.Int32, Int32: 0);
                    return true;
            }
        }
    }

    public bool TryGetError(out uint error, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            error = GlNoError;
            if (!CheckAvailable(out failure)) return false;
            error = _pendingError;
            _pendingError = GlNoError;
            return true;
        }
    }

    public bool TryGetExtensions(out IReadOnlyList<string> extensions, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            extensions = [];
            if (!CheckAvailable(out failure)) return false;
            extensions =
            [
                "OES_texture_float",
                "OES_texture_float_linear",
                "OES_texture_half_float",
                "OES_texture_half_float_linear",
                "OES_element_index_uint",
                "OES_standard_derivatives",
                "OES_vertex_array_object",
                "ANGLE_instanced_arrays",
                "EXT_color_buffer_float",
                "EXT_color_buffer_half_float",
                "WEBGL_depth_texture",
                "WEBGL_lose_context",
                "WEBGL_debug_renderer_info",
            ];
            return true;
        }
    }

    public bool TryResize(int width, int height, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (width < 0 || height < 0 || width > 4096 || height > 4096)
            {
                failure = new(GpuFailureKind.NativeError, "The WebGL surface dimensions are invalid.");
                return false;
            }
            if (width == _width && height == _height)
                return true;

            long newSurfaceBytes = (long)Math.Max(1, width) * Math.Max(1, height) * 4;
            var delta = newSurfaceBytes - _surfaceBytes;
            if (_options.MaxBytes > 0 && newSurfaceBytes + _resourceReservedBytes > _options.MaxBytes)
            {
                failure = new(GpuFailureKind.BudgetExceeded, "The WebGL context budget was exceeded during resize.");
                return false;
            }
            if (delta > 0 && !_device.TryReserve(delta, out var extraRes))
            {
                failure = new(GpuFailureKind.BudgetExceeded, "The GPU device budget was exceeded during resize.");
                return false;
            }
            else if (delta < 0)
            {
                _device.Release(-delta);
            }

            ReleaseBackbufferResources();
            _width = width;
            _height = height;
            _surfaceBytes = newSurfaceBytes;
            if (_surfaceBytes + _resourceReservedBytes > _peakReservedBytes)
                _peakReservedBytes = _surfaceBytes + _resourceReservedBytes;

            AllocateBackbufferResources(Math.Max(1, width), Math.Max(1, height));
            return true;
        }
    }
}
