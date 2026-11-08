using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Folio.Scripting.Gpu;

internal sealed partial class FolioNativeWebGlContext : INativeWebGlContext
{
    internal const uint GlFalse = 0;
    internal const uint GlTrue = 1;
    internal const uint GlNoError = 0;
    internal const uint GlInvalidEnum = 0x0500;
    internal const uint GlInvalidValue = 0x0501;
    internal const uint GlInvalidOperation = 0x0502;
    internal const uint GlOutOfMemory = 0x0505;
    internal const uint GlInvalidFramebufferOperation = 0x0506;

    internal const uint GlDepthBufferBit = 0x00000100;
    internal const uint GlStencilBufferBit = 0x00000400;
    internal const uint GlColorBufferBit = 0x00004000;

    internal const uint GlPoints = 0x0000;
    internal const uint GlLines = 0x0001;
    internal const uint GlLineLoop = 0x0002;
    internal const uint GlLineStrip = 0x0003;
    internal const uint GlTriangles = 0x0004;
    internal const uint GlTriangleStrip = 0x0005;
    internal const uint GlTriangleFan = 0x0006;

    internal const uint GlFragmentShader = 0x8B30;
    internal const uint GlVertexShader = 0x8B31;
    internal const uint GlShaderType = 0x8B4F;
    internal const uint GlDeleteStatus = 0x8B80;
    internal const uint GlCompileStatus = 0x8B81;
    internal const uint GlLinkStatus = 0x8B82;
    internal const uint GlValidateStatus = 0x8B83;
    internal const uint GlInfoLogLength = 0x8B84;
    internal const uint GlAttachedShaders = 0x8B85;
    internal const uint GlActiveUniforms = 0x8B86;
    internal const uint GlActiveAttributes = 0x8B89;
    internal const uint GlShaderSourceLength = 0x8B88;
    internal const uint GlTransformFeedbackVaryings = 0x8C83;
    internal const uint GlTransformFeedbackBufferMode = 0x8C7F;
    internal const uint GlActiveUniformBlocks = 0x8A36;

    internal const uint GlLowFloat = 0x8DF0;
    internal const uint GlMediumFloat = 0x8DF1;
    internal const uint GlHighFloat = 0x8DF2;
    internal const uint GlLowInt = 0x8DF3;
    internal const uint GlMediumInt = 0x8DF4;
    internal const uint GlHighInt = 0x8DF5;

    internal const uint GlByte = 0x1400;
    internal const uint GlUnsignedByte = 0x1401;
    internal const uint GlShort = 0x1402;
    internal const uint GlUnsignedShort = 0x1403;
    internal const uint GlInt = 0x1404;
    internal const uint GlUnsignedInt = 0x1405;
    internal const uint GlFloat = 0x1406;
    internal const uint GlHalfFloat = 0x140B;
    internal const uint GlHalfFloatOes = 0x8D61;
    internal const uint GlUnsignedShort4444 = 0x8033;
    internal const uint GlUnsignedShort5551 = 0x8034;
    internal const uint GlUnsignedShort565 = 0x8363;

    internal const uint GlFloatVec2 = 0x8B50;
    internal const uint GlFloatVec3 = 0x8B51;
    internal const uint GlFloatVec4 = 0x8B52;
    internal const uint GlIntVec2 = 0x8B53;
    internal const uint GlIntVec3 = 0x8B54;
    internal const uint GlIntVec4 = 0x8B55;
    internal const uint GlBool = 0x8B56;
    internal const uint GlBoolVec2 = 0x8B57;
    internal const uint GlBoolVec3 = 0x8B58;
    internal const uint GlBoolVec4 = 0x8B59;
    internal const uint GlFloatMat2 = 0x8B5A;
    internal const uint GlFloatMat3 = 0x8B5B;
    internal const uint GlFloatMat4 = 0x8B5C;
    internal const uint GlSampler2D = 0x8B5E;
    internal const uint GlSampler3D = 0x8B5F;
    internal const uint GlSamplerCube = 0x8B60;
    internal const uint GlSampler2DShadow = 0x8B62;
    internal const uint GlSampler2DArray = 0x8DC1;
    internal const uint GlSampler2DArrayShadow = 0x8DC4;
    internal const uint GlSamplerCubeShadow = 0x8DC5;
    internal const uint GlUnsignedIntVec2 = 0x8DC6;
    internal const uint GlUnsignedIntVec3 = 0x8DC7;
    internal const uint GlUnsignedIntVec4 = 0x8DC8;
    internal const uint GlIntSampler2D = 0x8DCA;
    internal const uint GlIntSampler3D = 0x8DCB;
    internal const uint GlIntSamplerCube = 0x8DCC;
    internal const uint GlIntSampler2DArray = 0x8DCF;
    internal const uint GlUnsignedIntSampler2D = 0x8DD2;
    internal const uint GlUnsignedIntSampler3D = 0x8DD3;
    internal const uint GlUnsignedIntSamplerCube = 0x8DD4;
    internal const uint GlUnsignedIntSampler2DArray = 0x8DD7;

    internal const uint GlArrayBuffer = 0x8892;
    internal const uint GlElementArrayBuffer = 0x8893;
    internal const uint GlCopyReadBuffer = 0x8F36;
    internal const uint GlCopyWriteBuffer = 0x8F37;
    internal const uint GlPixelPackBuffer = 0x88EB;
    internal const uint GlPixelUnpackBuffer = 0x88EC;
    internal const uint GlUniformBuffer = 0x8A11;
    internal const uint GlTransformFeedbackBuffer = 0x8C8E;
    internal const uint GlTransformFeedback = 0x8E22;
    internal const uint GlBufferSize = 0x8764;
    internal const uint GlBufferUsage = 0x8765;
    internal const uint GlStaticDraw = 0x88E4;
    internal const uint GlDynamicDraw = 0x88E8;
    internal const uint GlStreamDraw = 0x88E0;
    internal const uint GlStaticRead = 0x88E5;
    internal const uint GlDynamicRead = 0x88E9;
    internal const uint GlStreamRead = 0x88E1;
    internal const uint GlStaticCopy = 0x88E6;
    internal const uint GlDynamicCopy = 0x88EA;
    internal const uint GlStreamCopy = 0x88E2;
    internal const uint GlInterleavedAttribs = 0x8C8C;
    internal const uint GlSeparateAttribs = 0x8C8D;

    internal const uint GlTexture2D = 0x0DE1;
    internal const uint GlTexture3D = 0x806F;
    internal const uint GlTextureCubeMap = 0x8513;
    internal const uint GlTextureCubeMapPositiveX = 0x8515;
    internal const uint GlTextureCubeMapNegativeX = 0x8516;
    internal const uint GlTextureCubeMapPositiveY = 0x8517;
    internal const uint GlTextureCubeMapNegativeY = 0x8518;
    internal const uint GlTextureCubeMapPositiveZ = 0x8519;
    internal const uint GlTextureCubeMapNegativeZ = 0x851A;
    internal const uint GlTexture2DArray = 0x8C1A;
    internal const uint GlTexture0 = 0x84C0;

    internal const uint GlTextureMagFilter = 0x2800;
    internal const uint GlTextureMinFilter = 0x2801;
    internal const uint GlTextureWrapS = 0x2802;
    internal const uint GlTextureWrapT = 0x2803;
    internal const uint GlTextureWrapR = 0x8072;
    internal const uint GlTextureBaseLevel = 0x813C;
    internal const uint GlTextureMaxLevel = 0x813D;
    internal const uint GlTextureMinLod = 0x813A;
    internal const uint GlTextureMaxLod = 0x813B;
    internal const uint GlTextureCompareMode = 0x884C;
    internal const uint GlTextureCompareFunc = 0x884D;
    internal const uint GlNearest = 0x2600;
    internal const uint GlLinear = 0x2601;
    internal const uint GlNearestMipmapNearest = 0x2700;
    internal const uint GlLinearMipmapNearest = 0x2701;
    internal const uint GlNearestMipmapLinear = 0x2702;
    internal const uint GlLinearMipmapLinear = 0x2703;
    internal const uint GlRepeat = 0x2901;
    internal const uint GlClampToEdge = 0x812F;
    internal const uint GlMirroredRepeat = 0x8370;

    internal const uint GlDepthComponent = 0x1902;
    internal const uint GlAlpha = 0x1906;
    internal const uint GlRgb = 0x1907;
    internal const uint GlRgba = 0x1908;
    internal const uint GlLuminance = 0x1909;
    internal const uint GlLuminanceAlpha = 0x190A;
    internal const uint GlRed = 0x1903;
    internal const uint GlRg = 0x8227;
    internal const uint GlRedInteger = 0x8D94;
    internal const uint GlRgInteger = 0x8228;
    internal const uint GlRgbInteger = 0x8D98;
    internal const uint GlRgbaInteger = 0x8D99;
    internal const uint GlR8 = 0x8229;
    internal const uint GlRg8 = 0x822B;
    internal const uint GlRgb8 = 0x8051;
    internal const uint GlRgba8 = 0x8058;
    internal const uint GlRgba4 = 0x8056;
    internal const uint GlRgb5A1 = 0x8057;
    internal const uint GlRgb565 = 0x8D62;
    internal const uint GlR16f = 0x822D;
    internal const uint GlR32f = 0x822E;
    internal const uint GlRg16f = 0x822F;
    internal const uint GlRg32f = 0x8230;
    internal const uint GlRgb16f = 0x881B;
    internal const uint GlRgb32f = 0x8815;
    internal const uint GlRgba16f = 0x881A;
    internal const uint GlRgba32f = 0x8814;
    internal const uint GlR8ui = 0x8232;
    internal const uint GlR8i = 0x8231;
    internal const uint GlR16ui = 0x8234;
    internal const uint GlR16i = 0x8233;
    internal const uint GlR32ui = 0x8236;
    internal const uint GlR32i = 0x8235;
    internal const uint GlRg8ui = 0x8238;
    internal const uint GlRg8i = 0x8237;
    internal const uint GlRg16ui = 0x823A;
    internal const uint GlRg16i = 0x8239;
    internal const uint GlRg32ui = 0x823C;
    internal const uint GlRg32i = 0x823B;
    internal const uint GlRgb8ui = 0x8D7D;
    internal const uint GlRgb8i = 0x8D8F;
    internal const uint GlRgba8ui = 0x8D7C;
    internal const uint GlRgba8i = 0x8D8E;
    internal const uint GlRgba16ui = 0x8D76;
    internal const uint GlRgba16i = 0x8D88;
    internal const uint GlRgba32ui = 0x8D70;
    internal const uint GlRgba32i = 0x8D82;
    internal const uint GlDepthComponent16 = 0x81A5;
    internal const uint GlDepthComponent24 = 0x81A6;
    internal const uint GlDepthComponent32f = 0x8CAC;
    internal const uint GlDepth24Stencil8 = 0x88F0;
    internal const uint GlDepth32fStencil8 = 0x8CAD;
    internal const uint GlStencilIndex8 = 0x8D48;
    internal const uint GlDepthStencil = 0x84F9;

    internal const uint GlFramebuffer = 0x8D40;
    internal const uint GlReadFramebuffer = 0x8CA8;
    internal const uint GlDrawFramebuffer = 0x8CA9;
    internal const uint GlRenderbuffer = 0x8D41;
    internal const uint GlColorAttachment0 = 0x8CE0;
    internal const uint GlDepthAttachment = 0x8D00;
    internal const uint GlStencilAttachment = 0x8D20;
    internal const uint GlDepthStencilAttachment = 0x821A;
    internal const uint GlFramebufferComplete = 0x8CD5;
    internal const uint GlFramebufferIncompleteAttachment = 0x8CD6;
    internal const uint GlFramebufferIncompleteMissingAttachment = 0x8CD7;
    internal const uint GlFramebufferIncompleteDimensions = 0x8CD9;
    internal const uint GlFramebufferUnsupported = 0x8CDD;
    internal const uint GlFramebufferIncompleteMultisample = 0x8D56;

    internal const uint GlUnpackAlignment = 0x0CF5;
    internal const uint GlPackAlignment = 0x0D05;
    internal const uint GlUnpackFlipYWebgl = 0x9240;
    internal const uint GlUnpackPremultiplyAlphaWebgl = 0x9241;
    internal const uint GlUnpackColorspaceConversionWebgl = 0x9243;
    internal const uint GlUnpackRowLength = 0x0CF2;
    internal const uint GlUnpackSkipRows = 0x0CF3;
    internal const uint GlUnpackSkipPixels = 0x0CF4;
    internal const uint GlUnpackSkipImages = 0x806D;
    internal const uint GlUnpackImageHeight = 0x806E;
    internal const uint GlPackRowLength = 0x0D02;
    internal const uint GlPackSkipRows = 0x0D03;
    internal const uint GlPackSkipPixels = 0x0D04;

    internal const uint GlCullFace = 0x0B44;
    internal const uint GlBlend = 0x0BE2;
    internal const uint GlDither = 0x0BD0;
    internal const uint GlStencilTest = 0x0B90;
    internal const uint GlDepthTest = 0x0B71;
    internal const uint GlScissorTest = 0x0C11;
    internal const uint GlPolygonOffsetFill = 0x8037;
    internal const uint GlSampleAlphaToCoverage = 0x809E;
    internal const uint GlSampleCoverage = 0x80A0;
    internal const uint GlRasterizerDiscard = 0x8C89;

    internal const uint GlNever = 0x0200;
    internal const uint GlLess = 0x0201;
    internal const uint GlEqual = 0x0202;
    internal const uint GlLequal = 0x0203;
    internal const uint GlGreater = 0x0204;
    internal const uint GlNotequal = 0x0205;
    internal const uint GlGequal = 0x0206;
    internal const uint GlAlways = 0x0207;

    internal const uint GlZero = 0;
    internal const uint GlOne = 1;
    internal const uint GlSrcColor = 0x0300;
    internal const uint GlOneMinusSrcColor = 0x0301;
    internal const uint GlSrcAlpha = 0x0302;
    internal const uint GlOneMinusSrcAlpha = 0x0303;
    internal const uint GlDstAlpha = 0x0304;
    internal const uint GlOneMinusDstAlpha = 0x0305;
    internal const uint GlDstColor = 0x0306;
    internal const uint GlOneMinusDstColor = 0x0307;
    internal const uint GlSrcAlphaSaturate = 0x0308;
    internal const uint GlConstantColor = 0x8001;
    internal const uint GlOneMinusConstantColor = 0x8002;
    internal const uint GlConstantAlpha = 0x8003;
    internal const uint GlOneMinusConstantAlpha = 0x8004;

    internal const uint GlFuncAdd = 0x8006;
    internal const uint GlBlendEquation = 0x8009;
    internal const uint GlBlendEquationRgb = 0x8009;
    internal const uint GlBlendEquationAlpha = 0x883D;
    internal const uint GlFuncSubtract = 0x800A;
    internal const uint GlFuncReverseSubtract = 0x800B;
    internal const uint GlMin = 0x8007;
    internal const uint GlMax = 0x8008;

    internal const uint GlFront = 0x0404;
    internal const uint GlBack = 0x0405;
    internal const uint GlFrontAndBack = 0x0408;
    internal const uint GlCw = 0x0900;
    internal const uint GlCcw = 0x0901;

    internal const uint GlKeep = 0x1E00;
    internal const uint GlReplace = 0x1E01;
    internal const uint GlIncr = 0x1E02;
    internal const uint GlDecr = 0x1E03;
    internal const uint GlInvert = 0x150A;
    internal const uint GlIncrWrap = 0x8507;
    internal const uint GlDecrWrap = 0x8508;

    internal const uint GlColor = 0x1800;
    internal const uint GlDepth = 0x1801;
    internal const uint GlStencil = 0x1802;

    internal const int MaxVertexAttribs = 16;
    internal const int MaxTextureUnits = 32;
    internal const int MaxUniformBufferBindings = 36;
    internal const int MaxTransformFeedbackBuffers = 4;
    internal const int MaxColorAttachments = 8;

    private readonly FolioGpuDevice _device;
    private readonly WebGlContextOptions _options;
    private IDisposable _surfaceReservation;
    private long _surfaceBytes;
    private long _resourceReservedBytes;
    private long _peakReservedBytes;
    private bool _disposed;
    private bool _isLost;
    private uint _pendingError;

    // Default backbuffer D3D11 resources (stored as R32G32B32A32_FLOAT + D24_UNORM_S8_UINT)
    private int _width;
    private int _height;
    private IntPtr _defaultColorTex;
    private IntPtr _defaultColorRtv;
    private IntPtr _defaultColorStaging;
    private IntPtr _defaultDepthTex;
    private IntPtr _defaultDepthDsv;

    // Cached D3D11 delegates
    private readonly FolioD3D11Interop.CreateTexture2DDelegate _createTexture2D;
    private readonly FolioD3D11Interop.CreateTexture3DDelegate _createTexture3D;
    private readonly FolioD3D11Interop.CreateShaderResourceViewDelegate _createSrv;
    private readonly FolioD3D11Interop.CreateShaderResourceViewDescDelegate _createSrvDesc;
    private readonly FolioD3D11Interop.CreateRenderTargetViewDelegate _createRtv;
    private readonly FolioD3D11Interop.CreateRenderTargetViewDescDelegate _createRtvDesc;
    private readonly FolioD3D11Interop.CreateDepthStencilViewDelegate _createDsv;
    private readonly FolioD3D11Interop.CreateBufferDelegate _createBuffer;
    private readonly FolioD3D11Interop.CreateInputLayoutDelegate _createInputLayout;
    private readonly FolioD3D11Interop.CreateVertexShaderDelegate _createVertexShader;
    private readonly FolioD3D11Interop.CreatePixelShaderDelegate _createPixelShader;
    private readonly FolioD3D11Interop.CreateBlendStateDelegate _createBlendState;
    private readonly FolioD3D11Interop.CreateDepthStencilStateDelegate _createDepthStencilState;
    private readonly FolioD3D11Interop.CreateRasterizerStateDelegate _createRasterizerState;
    private readonly FolioD3D11Interop.CreateSamplerStateDelegate _createSamplerState;

    private readonly FolioD3D11Interop.VSSetConstantBuffersDelegate _vsSetConstantBuffers;
    private readonly FolioD3D11Interop.PSSetConstantBuffersDelegate _psSetConstantBuffers;
    private readonly FolioD3D11Interop.VSSetShaderDelegate _vsSetShader;
    private readonly FolioD3D11Interop.PSSetShaderDelegate _psSetShader;
    private readonly FolioD3D11Interop.VSSetShaderResourcesDelegate _vsSetShaderResources;
    private readonly FolioD3D11Interop.PSSetShaderResourcesDelegate _psSetShaderResources;
    private readonly FolioD3D11Interop.VSSetSamplersDelegate _vsSetSamplers;
    private readonly FolioD3D11Interop.PSSetSamplersDelegate _psSetSamplers;
    private readonly FolioD3D11Interop.IASetInputLayoutDelegate _iaSetInputLayout;
    private readonly FolioD3D11Interop.IASetVertexBuffersDelegate _iaSetVertexBuffers;
    private readonly FolioD3D11Interop.IASetIndexBufferDelegate _iaSetIndexBuffer;
    private readonly FolioD3D11Interop.IASetPrimitiveTopologyDelegate _iaSetPrimitiveTopology;
    private readonly FolioD3D11Interop.OMSetRenderTargetsDelegate _omSetRenderTargets;
    private readonly FolioD3D11Interop.OMSetBlendStateDelegate _omSetBlendState;
    private readonly FolioD3D11Interop.OMSetDepthStencilStateDelegate _omSetDepthStencilState;
    private readonly FolioD3D11Interop.RSSetStateDelegate _rsSetState;
    private readonly FolioD3D11Interop.RSSetViewportsDelegate _rsSetViewports;
    private readonly FolioD3D11Interop.RSSetScissorRectsDelegate _rsSetScissorRects;
    private readonly FolioD3D11Interop.DrawDelegate _draw;
    private readonly FolioD3D11Interop.DrawIndexedDelegate _drawIndexed;
    private readonly FolioD3D11Interop.DrawInstancedDelegate _drawInstanced;
    private readonly FolioD3D11Interop.DrawIndexedInstancedDelegate _drawIndexedInstanced;
    private readonly FolioD3D11Interop.ClearRenderTargetViewDelegate _clearRtv;
    private readonly FolioD3D11Interop.ClearDepthStencilViewDelegate _clearDsv;
    private readonly FolioD3D11Interop.CopyResourceDelegate _copyResource;
    private readonly FolioD3D11Interop.CopySubresourceRegionDelegate _copySubresourceRegion;
    private readonly FolioD3D11Interop.UpdateSubresourceDelegate _updateSubresource;
    private readonly FolioD3D11Interop.UpdateSubresourceFullDelegate _updateSubresourceFull;
    private readonly FolioD3D11Interop.GenerateMipsDelegate _generateMips;
    private readonly FolioD3D11Interop.MapDelegate _map;
    private readonly FolioD3D11Interop.UnmapDelegate _unmap;
    private readonly FolioD3D11Interop.FlushDelegate _flush;

    // Resource handle tables
    private uint _nextHandle = 1;
    private readonly Dictionary<uint, ShaderEntry> _shaders = [];
    private readonly Dictionary<uint, ProgramEntry> _programs = [];
    private readonly Dictionary<uint, BufferEntry> _buffers = [];
    private readonly Dictionary<uint, TextureEntry> _textures = [];
    private readonly Dictionary<uint, RenderbufferEntry> _renderbuffers = [];
    private readonly Dictionary<uint, FramebufferEntry> _framebuffers = [];
    private readonly Dictionary<uint, VertexArrayEntry> _vertexArrays = [];
    private readonly Dictionary<uint, TransformFeedbackEntry> _transformFeedbacks = [];
    private readonly VertexArrayEntry _defaultVao = new(0);

    // State
    private uint? _currentProgram;
    private uint? _boundArrayBuffer;
    private uint? _boundCopyReadBuffer;
    private uint? _boundCopyWriteBuffer;
    private uint? _boundPixelPackBuffer;
    private uint? _boundPixelUnpackBuffer;
    private uint? _boundUniformBuffer;
    private uint? _boundTransformFeedbackBuffer;
    private uint? _boundVertexArray;
    private uint? _boundTransformFeedback;
    private bool _transformFeedbackActive;
    private bool _transformFeedbackPaused;
    private uint _transformFeedbackMode;
    private uint? _boundDrawFramebuffer;
    private uint? _boundReadFramebuffer;
    private uint? _boundRenderbuffer;

    private int _activeTextureUnit;
    private readonly uint?[] _boundTextures2D = new uint?[MaxTextureUnits];
    private readonly uint?[] _boundTexturesCube = new uint?[MaxTextureUnits];
    private readonly uint?[] _boundTextures3D = new uint?[MaxTextureUnits];
    private readonly uint?[] _boundTextures2DArray = new uint?[MaxTextureUnits];

    private readonly BufferRangeBinding[] _uboBindings = new BufferRangeBinding[MaxUniformBufferBindings];
    private readonly BufferRangeBinding[] _tfBindings = new BufferRangeBinding[MaxTransformFeedbackBuffers];

    private readonly VertexAttribConstant[] _attribConstants = new VertexAttribConstant[MaxVertexAttribs];

    // Pipeline state
    private int _viewportX;
    private int _viewportY;
    private int _viewportW;
    private int _viewportH;
    private int _scissorX;
    private int _scissorY;
    private int _scissorW;
    private int _scissorH;
    private bool _scissorTestEnabled;
    private bool _depthTestEnabled;
    private bool _stencilTestEnabled;
    private bool _blendEnabled;
    private bool _cullFaceEnabled;
    private bool _ditherEnabled = true;
    private bool _polygonOffsetFillEnabled;
    private bool _sampleAlphaToCoverageEnabled;
    private bool _sampleCoverageEnabled;
    private bool _rasterizerDiscardEnabled;

    private float _clearR;
    private float _clearG;
    private float _clearB;
    private float _clearA;
    private float _clearDepth = 1.0f;
    private int _clearStencil;

    private uint _depthFunc = GlLess;
    private bool _depthMask = true;
    private float _depthRangeNear;
    private float _depthRangeFar = 1.0f;
    private float _lineWidth = 1.0f;
    private float _polygonOffsetFactor;
    private float _polygonOffsetUnits;
    private float _sampleCoverageValue = 1.0f;
    private bool _sampleCoverageInvert;

    private uint _cullFaceMode = GlBack;
    private uint _frontFaceMode = GlCcw;

    private float _blendColorR;
    private float _blendColorG;
    private float _blendColorB;
    private float _blendColorA;
    private uint _blendSrcRgb = GlOne;
    private uint _blendDstRgb = GlZero;
    private uint _blendSrcAlpha = GlOne;
    private uint _blendDstAlpha = GlZero;
    private uint _blendEqRgb = GlFuncAdd;
    private uint _blendEqAlpha = GlFuncAdd;
    private bool _colorMaskR = true;
    private bool _colorMaskG = true;
    private bool _colorMaskB = true;
    private bool _colorMaskA = true;

    private uint _stencilFuncFront = GlAlways;
    private int _stencilRefFront;
    private uint _stencilValMaskFront = 0xFFFFFFFF;
    private uint _stencilWritemaskFront = 0xFFFFFFFF;
    private uint _stencilFailFront = GlKeep;
    private uint _stencilZFailFront = GlKeep;
    private uint _stencilZPassFront = GlKeep;

    private uint _stencilFuncBack = GlAlways;
    private int _stencilRefBack;
    private uint _stencilValMaskBack = 0xFFFFFFFF;
    private uint _stencilWritemaskBack = 0xFFFFFFFF;
    private uint _stencilFailBack = GlKeep;
    private uint _stencilZFailBack = GlKeep;
    private uint _stencilZPassBack = GlKeep;

    // Pixel store parameters
    private int _unpackAlignment = 4;
    private int _packAlignment = 4;
    private bool _unpackFlipY;
    private bool _unpackPremultiplyAlpha;
    private int _unpackColorspaceConversion = 0x9244;
    private int _unpackRowLength;
    private int _unpackSkipRows;
    private int _unpackSkipPixels;
    private int _unpackSkipImages;
    private int _unpackImageHeight;
    private int _packRowLength;
    private int _packSkipRows;
    private int _packSkipPixels;

    private uint _readBufferMode = 0x0405; // BACK

    public WebGlContextAttributes Attributes { get; }
    public int DrawingBufferWidth => _width;
    public int DrawingBufferHeight => _height;
    public int Width => _width;
    public int Height => _height;
    public bool IsLost => _isLost || _disposed || _device.IsDisposed || _device.IsRemoved;
    public long ReservedBytes { get { lock (_device.Gate) return _disposed ? 0 : _surfaceBytes + _resourceReservedBytes; } }
    public long PeakReservedBytes { get { lock (_device.Gate) return _peakReservedBytes; } }

    internal FolioNativeWebGlContext(
        FolioGpuDevice device,
        WebGlContextOptions options,
        IDisposable surfaceReservation,
        long surfaceBytes)
    {
        _device = device;
        _options = options;
        Attributes = options.RequestedAttributes;
        _surfaceReservation = surfaceReservation;
        _surfaceBytes = surfaceBytes;
        _peakReservedBytes = surfaceBytes;
        _width = options.Width;
        _height = options.Height;
        _viewportW = options.Width;
        _viewportH = options.Height;
        _scissorW = options.Width;
        _scissorH = options.Height;

        for (var i = 0; i < MaxVertexAttribs; i++)
            _attribConstants[i] = VertexAttribConstant.DefaultFloat();

        var dev = device.DevicePtr;
        var ctx = device.ContextPtr;

        _createTexture2D = FolioD3D11Interop.Method<FolioD3D11Interop.CreateTexture2DDelegate>(dev, FolioD3D11Interop.DeviceSlotCreateTexture2D);
        _createTexture3D = FolioD3D11Interop.Method<FolioD3D11Interop.CreateTexture3DDelegate>(dev, FolioD3D11Interop.DeviceSlotCreateTexture3D);
        _createSrv = FolioD3D11Interop.Method<FolioD3D11Interop.CreateShaderResourceViewDelegate>(dev, FolioD3D11Interop.DeviceSlotCreateShaderResourceView);
        _createSrvDesc = FolioD3D11Interop.Method<FolioD3D11Interop.CreateShaderResourceViewDescDelegate>(dev, FolioD3D11Interop.DeviceSlotCreateShaderResourceView);
        _createRtv = FolioD3D11Interop.Method<FolioD3D11Interop.CreateRenderTargetViewDelegate>(dev, FolioD3D11Interop.DeviceSlotCreateRenderTargetView);
        _createRtvDesc = FolioD3D11Interop.Method<FolioD3D11Interop.CreateRenderTargetViewDescDelegate>(dev, FolioD3D11Interop.DeviceSlotCreateRenderTargetView);
        _createDsv = FolioD3D11Interop.Method<FolioD3D11Interop.CreateDepthStencilViewDelegate>(dev, FolioD3D11Interop.DeviceSlotCreateDepthStencilView);
        _createBuffer = FolioD3D11Interop.Method<FolioD3D11Interop.CreateBufferDelegate>(dev, FolioD3D11Interop.DeviceSlotCreateBuffer);
        _createInputLayout = FolioD3D11Interop.Method<FolioD3D11Interop.CreateInputLayoutDelegate>(dev, FolioD3D11Interop.DeviceSlotCreateInputLayout);
        _createVertexShader = FolioD3D11Interop.Method<FolioD3D11Interop.CreateVertexShaderDelegate>(dev, FolioD3D11Interop.DeviceSlotCreateVertexShader);
        _createPixelShader = FolioD3D11Interop.Method<FolioD3D11Interop.CreatePixelShaderDelegate>(dev, FolioD3D11Interop.DeviceSlotCreatePixelShader);
        _createBlendState = FolioD3D11Interop.Method<FolioD3D11Interop.CreateBlendStateDelegate>(dev, FolioD3D11Interop.DeviceSlotCreateBlendState);
        _createDepthStencilState = FolioD3D11Interop.Method<FolioD3D11Interop.CreateDepthStencilStateDelegate>(dev, FolioD3D11Interop.DeviceSlotCreateDepthStencilState);
        _createRasterizerState = FolioD3D11Interop.Method<FolioD3D11Interop.CreateRasterizerStateDelegate>(dev, FolioD3D11Interop.DeviceSlotCreateRasterizerState);
        _createSamplerState = FolioD3D11Interop.Method<FolioD3D11Interop.CreateSamplerStateDelegate>(dev, FolioD3D11Interop.DeviceSlotCreateSamplerState);

        _vsSetConstantBuffers = FolioD3D11Interop.Method<FolioD3D11Interop.VSSetConstantBuffersDelegate>(ctx, FolioD3D11Interop.ContextSlotVSSetConstantBuffers);
        _psSetConstantBuffers = FolioD3D11Interop.Method<FolioD3D11Interop.PSSetConstantBuffersDelegate>(ctx, FolioD3D11Interop.ContextSlotPSSetConstantBuffers);
        _vsSetShader = FolioD3D11Interop.Method<FolioD3D11Interop.VSSetShaderDelegate>(ctx, FolioD3D11Interop.ContextSlotVSSetShader);
        _psSetShader = FolioD3D11Interop.Method<FolioD3D11Interop.PSSetShaderDelegate>(ctx, FolioD3D11Interop.ContextSlotPSSetShader);
        _vsSetShaderResources = FolioD3D11Interop.Method<FolioD3D11Interop.VSSetShaderResourcesDelegate>(ctx, FolioD3D11Interop.ContextSlotVSSetShaderResources);
        _psSetShaderResources = FolioD3D11Interop.Method<FolioD3D11Interop.PSSetShaderResourcesDelegate>(ctx, FolioD3D11Interop.ContextSlotPSSetShaderResources);
        _vsSetSamplers = FolioD3D11Interop.Method<FolioD3D11Interop.VSSetSamplersDelegate>(ctx, FolioD3D11Interop.ContextSlotVSSetSamplers);
        _psSetSamplers = FolioD3D11Interop.Method<FolioD3D11Interop.PSSetSamplersDelegate>(ctx, FolioD3D11Interop.ContextSlotPSSetSamplers);
        _iaSetInputLayout = FolioD3D11Interop.Method<FolioD3D11Interop.IASetInputLayoutDelegate>(ctx, FolioD3D11Interop.ContextSlotIASetInputLayout);
        _iaSetVertexBuffers = FolioD3D11Interop.Method<FolioD3D11Interop.IASetVertexBuffersDelegate>(ctx, FolioD3D11Interop.ContextSlotIASetVertexBuffers);
        _iaSetIndexBuffer = FolioD3D11Interop.Method<FolioD3D11Interop.IASetIndexBufferDelegate>(ctx, FolioD3D11Interop.ContextSlotIASetIndexBuffer);
        _iaSetPrimitiveTopology = FolioD3D11Interop.Method<FolioD3D11Interop.IASetPrimitiveTopologyDelegate>(ctx, FolioD3D11Interop.ContextSlotIASetPrimitiveTopology);
        _omSetRenderTargets = FolioD3D11Interop.Method<FolioD3D11Interop.OMSetRenderTargetsDelegate>(ctx, FolioD3D11Interop.ContextSlotOMSetRenderTargets);
        _omSetBlendState = FolioD3D11Interop.Method<FolioD3D11Interop.OMSetBlendStateDelegate>(ctx, FolioD3D11Interop.ContextSlotOMSetBlendState);
        _omSetDepthStencilState = FolioD3D11Interop.Method<FolioD3D11Interop.OMSetDepthStencilStateDelegate>(ctx, FolioD3D11Interop.ContextSlotOMSetDepthStencilState);
        _rsSetState = FolioD3D11Interop.Method<FolioD3D11Interop.RSSetStateDelegate>(ctx, FolioD3D11Interop.ContextSlotRSSetState);
        _rsSetViewports = FolioD3D11Interop.Method<FolioD3D11Interop.RSSetViewportsDelegate>(ctx, FolioD3D11Interop.ContextSlotRSSetViewports);
        _rsSetScissorRects = FolioD3D11Interop.Method<FolioD3D11Interop.RSSetScissorRectsDelegate>(ctx, FolioD3D11Interop.ContextSlotRSSetScissorRects);
        _draw = FolioD3D11Interop.Method<FolioD3D11Interop.DrawDelegate>(ctx, FolioD3D11Interop.ContextSlotDraw);
        _drawIndexed = FolioD3D11Interop.Method<FolioD3D11Interop.DrawIndexedDelegate>(ctx, FolioD3D11Interop.ContextSlotDrawIndexed);
        _drawInstanced = FolioD3D11Interop.Method<FolioD3D11Interop.DrawInstancedDelegate>(ctx, FolioD3D11Interop.ContextSlotDrawInstanced);
        _drawIndexedInstanced = FolioD3D11Interop.Method<FolioD3D11Interop.DrawIndexedInstancedDelegate>(ctx, FolioD3D11Interop.ContextSlotDrawIndexedInstanced);
        _clearRtv = FolioD3D11Interop.Method<FolioD3D11Interop.ClearRenderTargetViewDelegate>(ctx, FolioD3D11Interop.ContextSlotClearRenderTargetView);
        _clearDsv = FolioD3D11Interop.Method<FolioD3D11Interop.ClearDepthStencilViewDelegate>(ctx, FolioD3D11Interop.ContextSlotClearDepthStencilView);
        _copyResource = FolioD3D11Interop.Method<FolioD3D11Interop.CopyResourceDelegate>(ctx, FolioD3D11Interop.ContextSlotCopyResource);
        _copySubresourceRegion = FolioD3D11Interop.Method<FolioD3D11Interop.CopySubresourceRegionDelegate>(ctx, FolioD3D11Interop.ContextSlotCopySubresourceRegion);
        _updateSubresource = FolioD3D11Interop.Method<FolioD3D11Interop.UpdateSubresourceDelegate>(ctx, FolioD3D11Interop.ContextSlotUpdateSubresource);
        _updateSubresourceFull = FolioD3D11Interop.Method<FolioD3D11Interop.UpdateSubresourceFullDelegate>(ctx, FolioD3D11Interop.ContextSlotUpdateSubresource);
        _generateMips = FolioD3D11Interop.Method<FolioD3D11Interop.GenerateMipsDelegate>(ctx, FolioD3D11Interop.ContextSlotGenerateMips);
        _map = FolioD3D11Interop.Method<FolioD3D11Interop.MapDelegate>(ctx, FolioD3D11Interop.ContextSlotMap);
        _unmap = FolioD3D11Interop.Method<FolioD3D11Interop.UnmapDelegate>(ctx, FolioD3D11Interop.ContextSlotUnmap);
        _flush = FolioD3D11Interop.Method<FolioD3D11Interop.FlushDelegate>(ctx, FolioD3D11Interop.ContextSlotFlush);

        AllocateBackbufferResources(Math.Max(1, _width), Math.Max(1, _height));
    }

    private void AllocateBackbufferResources(int width, int height)
    {
        var colorDesc = new FolioD3D11Interop.D3D11Texture2DDesc
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = FolioD3D11Interop.DxgiFormatR32G32B32A32Float,
            SampleDesc = new FolioD3D11Interop.DxgiSampleDesc { Count = 1, Quality = 0 },
            Usage = FolioD3D11Interop.D3D11UsageDefault,
            BindFlags = FolioD3D11Interop.D3D11BindRenderTarget | FolioD3D11Interop.D3D11BindShaderResource,
            CPUAccessFlags = 0,
            MiscFlags = 0,
        };

        var hr = _createTexture2D(_device.DevicePtr, ref colorDesc, IntPtr.Zero, out _defaultColorTex);
        if (hr < 0 || _defaultColorTex == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to create D3D11 color texture (0x{hr:X8}).");

        hr = _createRtv(_device.DevicePtr, _defaultColorTex, IntPtr.Zero, out _defaultColorRtv);
        if (hr < 0 || _defaultColorRtv == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to create D3D11 color RTV (0x{hr:X8}).");

        var stagingDesc = colorDesc;
        stagingDesc.Usage = FolioD3D11Interop.D3D11UsageStaging;
        stagingDesc.BindFlags = 0;
        stagingDesc.CPUAccessFlags = FolioD3D11Interop.D3D11CpuAccessRead;
        hr = _createTexture2D(_device.DevicePtr, ref stagingDesc, IntPtr.Zero, out _defaultColorStaging);
        if (hr < 0 || _defaultColorStaging == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to create D3D11 color staging texture (0x{hr:X8}).");

        var depthDesc = new FolioD3D11Interop.D3D11Texture2DDesc
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = FolioD3D11Interop.DxgiFormatD24UnormS8Uint,
            SampleDesc = new FolioD3D11Interop.DxgiSampleDesc { Count = 1, Quality = 0 },
            Usage = FolioD3D11Interop.D3D11UsageDefault,
            BindFlags = FolioD3D11Interop.D3D11BindDepthStencil,
            CPUAccessFlags = 0,
            MiscFlags = 0,
        };

        hr = _createTexture2D(_device.DevicePtr, ref depthDesc, IntPtr.Zero, out _defaultDepthTex);
        if (hr < 0 || _defaultDepthTex == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to create D3D11 depth-stencil texture (0x{hr:X8}).");

        hr = _createDsv(_device.DevicePtr, _defaultDepthTex, IntPtr.Zero, out _defaultDepthDsv);
        if (hr < 0 || _defaultDepthDsv == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to create D3D11 depth-stencil view (0x{hr:X8}).");

        var zeroColor = new float[] { 0f, 0f, 0f, Attributes.Alpha ? 0f : 1f };
        _clearRtv(_device.ContextPtr, _defaultColorRtv, ref zeroColor[0]);
        _clearDsv(_device.ContextPtr, _defaultDepthDsv, FolioD3D11Interop.D3D11ClearDepth | FolioD3D11Interop.D3D11ClearStencil, 1.0f, 0);
    }

    private void ReleaseBackbufferResources()
    {
        if (_defaultDepthDsv != IntPtr.Zero) { Marshal.Release(_defaultDepthDsv); _defaultDepthDsv = IntPtr.Zero; }
        if (_defaultDepthTex != IntPtr.Zero) { Marshal.Release(_defaultDepthTex); _defaultDepthTex = IntPtr.Zero; }
        if (_defaultColorStaging != IntPtr.Zero) { Marshal.Release(_defaultColorStaging); _defaultColorStaging = IntPtr.Zero; }
        if (_defaultColorRtv != IntPtr.Zero) { Marshal.Release(_defaultColorRtv); _defaultColorRtv = IntPtr.Zero; }
        if (_defaultColorTex != IntPtr.Zero) { Marshal.Release(_defaultColorTex); _defaultColorTex = IntPtr.Zero; }
    }

    private bool CheckAvailable(out GpuFailure? failure)
    {
        if (_disposed || _device.IsDisposed || _isLost)
        {
            failure = new(GpuFailureKind.Unavailable, "The WebGL context is lost or disposed.");
            return false;
        }
        failure = null;
        return true;
    }

    private void RecordGlError(uint error)
    {
        if (_pendingError == GlNoError && error != GlNoError)
            _pendingError = error;
    }

    private bool TryReserveResourceBytes(long deltaBytes, out GpuFailure? failure)
    {
        failure = null;
        if (deltaBytes <= 0)
        {
            if (deltaBytes < 0)
            {
                var release = -deltaBytes;
                _resourceReservedBytes = Math.Max(0, _resourceReservedBytes - release);
                _device.Release(release);
            }
            return true;
        }

        if (_options.MaxBytes > 0 && _surfaceBytes + _resourceReservedBytes + deltaBytes > _options.MaxBytes)
        {
            RecordGlError(GlOutOfMemory);
            failure = new(GpuFailureKind.BudgetExceeded, "The WebGL context memory budget was exceeded.");
            return false;
        }

        if (!_device.TryReserve(deltaBytes, out _))
        {
            RecordGlError(GlOutOfMemory);
            failure = new(GpuFailureKind.BudgetExceeded, "The GPU device memory budget was exceeded.");
            return false;
        }

        _resourceReservedBytes += deltaBytes;
        var total = _surfaceBytes + _resourceReservedBytes;
        if (total > _peakReservedBytes)
            _peakReservedBytes = total;
        return true;
    }

    private VertexArrayEntry CurrentVao =>
        _boundVertexArray is uint id && _vertexArrays.TryGetValue(id, out var vao) ? vao : _defaultVao;

    public void Dispose()
    {
        lock (_device.Gate)
        {
            if (_disposed) return;
            _disposed = true;
            _isLost = true;
            _device.ForgetWebGlContext(this);

            foreach (var prog in _programs.Values) prog.Dispose();
            _programs.Clear();
            foreach (var tex in _textures.Values) tex.Dispose();
            _textures.Clear();
            foreach (var rb in _renderbuffers.Values) rb.Dispose();
            _renderbuffers.Clear();
            foreach (var fb in _framebuffers.Values) fb.Dispose();
            _framebuffers.Clear();
            _shaders.Clear();
            _buffers.Clear();
            _vertexArrays.Clear();
            _transformFeedbacks.Clear();

            ReleaseBackbufferResources();

            if (_resourceReservedBytes > 0)
            {
                _device.Release(_resourceReservedBytes);
                _resourceReservedBytes = 0;
            }
            _surfaceReservation.Dispose();
            _surfaceBytes = 0;
        }
    }

    // Internal convenience overloads for tests (matching AngleNativeWebGlContext's internal surface)
    internal bool TryCreateShader(uint shaderType, out uint shader, out GpuFailure? failure)
    {
        var ok = TryCreateShader(shaderType, out WebGlShaderHandle h, out failure);
        shader = h.Value;
        return ok;
    }

    internal bool TrySetShaderSource(uint shader, ReadOnlySpan<byte> utf8Source, out GpuFailure? failure) =>
        TrySetShaderSource(new WebGlShaderHandle(shader), utf8Source, out failure);

    internal bool TryCompileShader(uint shader, out bool compiled, out string infoLog, out GpuFailure? failure) =>
        TryCompileShader(new WebGlShaderHandle(shader), out compiled, out infoLog, out failure);

    internal bool TryCreateProgram(out uint program, out GpuFailure? failure)
    {
        var ok = TryCreateProgram(out WebGlProgramHandle h, out failure);
        program = h.Value;
        return ok;
    }

    internal bool TryAttachShader(uint program, uint shader, out GpuFailure? failure) =>
        TryAttachShader(new WebGlProgramHandle(program), new WebGlShaderHandle(shader), out failure);

    internal bool TryLinkProgram(uint program, out bool linked, out string infoLog, out GpuFailure? failure) =>
        TryLinkProgram(new WebGlProgramHandle(program), out linked, out infoLog, out failure);

    internal bool TryUseProgram(uint? program, out GpuFailure? failure) =>
        TryUseProgram(program is uint id and > 0 ? new WebGlProgramHandle(id) : null, out failure);

    internal bool TryGetUniformLocation(uint program, ReadOnlySpan<byte> utf8Name, out int location, out GpuFailure? failure)
    {
        var ok = TryGetUniformLocation(new WebGlProgramHandle(program), Encoding.UTF8.GetString(utf8Name), out var loc, out failure);
        location = loc.Value;
        return ok;
    }

    internal bool TryUniformFloats(int location, int componentCount, int elementCount, ReadOnlySpan<float> values, out GpuFailure? failure)
    {
        if (elementCount <= 0 || values.Length < componentCount * elementCount)
        {
            failure = new(GpuFailureKind.NativeError, "The uniform value span is too small.");
            return false;
        }
        return TryUniformFloat(new WebGlUniformLocation(location), componentCount, values.Slice(0, componentCount * elementCount), out failure);
    }

    internal bool TryGetUniformFloats(uint program, int location, Span<float> destination, out int written, out GpuFailure? failure)
    {
        written = 0;
        if (!TryGetUniform(new WebGlProgramHandle(program), new WebGlUniformLocation(location), out var val, out failure))
            return false;
        if (val.Float32 is { } floats)
        {
            if (destination.Length < floats.Count)
            {
                failure = new(GpuFailureKind.NativeError, "The uniform destination span is too small.");
                return false;
            }
            for (var i = 0; i < floats.Count; i++) destination[i] = floats[i];
            written = floats.Count;
            return true;
        }
        if (val.Int32 is { } ints)
        {
            if (destination.Length < ints.Count)
            {
                failure = new(GpuFailureKind.NativeError, "The uniform destination span is too small.");
                return false;
            }
            for (var i = 0; i < ints.Count; i++) destination[i] = ints[i];
            written = ints.Count;
            return true;
        }
        failure = new(GpuFailureKind.NativeError, "Uniform could not be read as floats.");
        return false;
    }

    internal bool TryCreateVertexArray(out uint vertexArray, out GpuFailure? failure)
    {
        var ok = TryCreateVertexArray(out WebGlVertexArrayHandle h, out failure);
        vertexArray = h.Value;
        return ok;
    }

    internal bool TryBindVertexArray(uint? vertexArray, out GpuFailure? failure) =>
        TryBindVertexArray(vertexArray is uint id and > 0 ? new WebGlVertexArrayHandle(id) : null, out failure);

    internal bool TryCreateBuffer(out uint buffer, out GpuFailure? failure)
    {
        var ok = TryCreateBuffer(out WebGlBufferHandle h, out failure);
        buffer = h.Value;
        return ok;
    }

    internal bool TryBindBuffer(uint target, uint? buffer, out GpuFailure? failure) =>
        TryBindBuffer(target, buffer is uint id and > 0 ? new WebGlBufferHandle(id) : null, out failure);

    internal bool TryVertexAttribPointer(uint index, int size, uint type, bool normalized, int stride, int byteOffset, out GpuFailure? failure) =>
        TryVertexAttribPointer(new WebGlAttribLocation((int)index), size, type, normalized, stride, byteOffset, out failure);

    internal bool TryEnableVertexAttrib(uint index, bool enabled, out GpuFailure? failure) =>
        enabled
            ? TryEnableVertexAttrib(new WebGlAttribLocation((int)index), out failure)
            : TryDisableVertexAttrib(new WebGlAttribLocation((int)index), out failure);

    internal bool TryReadPixels(int x, int y, int width, int height, Span<byte> destination, out GpuFailure? failure) =>
        TryReadPixels(x, y, width, height, destination, GlRgba, GlUnsignedByte, out failure);

    // Nested records and entries
    internal readonly record struct BufferRangeBinding(uint? Buffer, long Offset, long Size);

    internal struct VertexAttribState
    {
        public bool Enabled;
        public int Size;
        public uint Type;
        public bool Normalized;
        public bool IsInteger;
        public int Stride;
        public int Offset;
        public uint Divisor;
        public uint? Buffer;

        public static VertexAttribState CreateDefault() => new()
        {
            Enabled = false,
            Size = 4,
            Type = GlFloat,
            Normalized = false,
            IsInteger = false,
            Stride = 0,
            Offset = 0,
            Divisor = 0,
            Buffer = null,
        };
    }

    internal struct VertexAttribConstant
    {
        public WebGlParameterKind Kind;
        public float F0, F1, F2, F3;
        public int I0, I1, I2, I3;
        public uint U0, U1, U2, U3;

        public static VertexAttribConstant DefaultFloat() => new()
        {
            Kind = WebGlParameterKind.Float32Array,
            F0 = 0f, F1 = 0f, F2 = 0f, F3 = 1f,
        };
    }

    internal sealed class VertexArrayEntry
    {
        public uint Handle { get; }
        public uint? ElementArrayBuffer;
        public readonly VertexAttribState[] Attribs = new VertexAttribState[MaxVertexAttribs];

        public VertexArrayEntry(uint handle)
        {
            Handle = handle;
            for (var i = 0; i < MaxVertexAttribs; i++)
                Attribs[i] = VertexAttribState.CreateDefault();
        }
    }

    internal sealed class TransformFeedbackEntry(uint handle)
    {
        public uint Handle { get; } = handle;
        public bool Active;
        public bool Paused;
        public uint Mode;
    }

    internal sealed class ShaderEntry(uint handle, uint shaderType)
    {
        public uint Handle { get; } = handle;
        public uint ShaderType { get; } = shaderType;
        public string Source = string.Empty;
        public bool Compiled;
        public bool DeletePending;
        public string InfoLog = string.Empty;
        public GlslParsedShader? Parsed;
    }

    internal sealed class BufferEntry(uint handle)
    {
        public uint Handle { get; } = handle;
        public byte[] Data = [];
        public uint Usage = GlStaticDraw;
        public uint FirstTarget;
        public long ReservedBytes;
    }
}
