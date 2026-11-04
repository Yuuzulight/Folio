using System.Runtime.InteropServices;
using System.Text;

namespace Folio.Scripting.Gpu;

/// <summary>
/// Native Direct3D 11, DXGI, and D3DCompiler P/Invoke interop contracts for Folio's GPU backend.
/// </summary>
internal static class FolioD3D11Interop
{
    internal const uint D3D11SdkVersion = 7;

    internal const int DriverTypeHardware = 1;
    internal const int DriverTypeWarp = 5;

    internal const uint DxgiFormatR32G32B32A32Float = 2;
    internal const uint DxgiFormatR32G32B32A32UInt = 3;
    internal const uint DxgiFormatR32G32B32A32SInt = 4;
    internal const uint DxgiFormatR16G16B16A16Float = 10;
    internal const uint DxgiFormatR8G8B8A8Unorm = 28;
    internal const uint DxgiFormatR32UInt = 42;
    internal const uint DxgiFormatD24UnormS8Uint = 45;
    internal const uint DxgiFormatR16UInt = 57;
    internal const uint DxgiFormatR8UInt = 62;

    internal const uint D3D11ResourceMiscGenerateMips = 0x1;
    internal const uint D3D11ResourceMiscTextureCube = 0x4;

    internal const uint D3D11UsageDefault = 0;
    internal const uint D3D11UsageImmutable = 1;
    internal const uint D3D11UsageDynamic = 2;
    internal const uint D3D11UsageStaging = 3;

    internal const uint D3D11BindVertexBuffer = 0x1;
    internal const uint D3D11BindIndexBuffer = 0x2;
    internal const uint D3D11BindConstantBuffer = 0x4;
    internal const uint D3D11BindShaderResource = 0x8;
    internal const uint D3D11BindStreamOutput = 0x10;
    internal const uint D3D11BindRenderTarget = 0x20;
    internal const uint D3D11BindDepthStencil = 0x40;

    internal const uint D3D11CpuAccessWrite = 0x10000;
    internal const uint D3D11CpuAccessRead = 0x20000;

    internal const uint D3D11MapRead = 1;
    internal const uint D3D11MapWrite = 2;
    internal const uint D3D11MapReadWrite = 3;
    internal const uint D3D11MapWriteDiscard = 4;
    internal const uint D3D11MapWriteNoOverwrite = 5;

    internal const uint D3D11ClearDepth = 0x1;
    internal const uint D3D11ClearStencil = 0x2;

    internal const uint D3D11SrvDimensionTexture2D = 4;
    internal const uint D3D11SrvDimensionTexture2DArray = 5;
    internal const uint D3D11SrvDimensionTexture3D = 8;
    internal const uint D3D11SrvDimensionTextureCube = 9;

    internal const uint D3D11RtvDimensionTexture2D = 4;
    internal const uint D3D11RtvDimensionTexture2DArray = 5;
    internal const uint D3D11RtvDimensionTexture3D = 8;

    // ID3D11Device Vtable Slots
    internal const int DeviceSlotCreateBuffer = 3;
    internal const int DeviceSlotCreateTexture2D = 5;
    internal const int DeviceSlotCreateTexture3D = 6;
    internal const int DeviceSlotCreateShaderResourceView = 7;
    internal const int DeviceSlotCreateRenderTargetView = 9;
    internal const int DeviceSlotCreateDepthStencilView = 10;
    internal const int DeviceSlotCreateInputLayout = 11;
    internal const int DeviceSlotCreateVertexShader = 12;
    internal const int DeviceSlotCreatePixelShader = 15;
    internal const int DeviceSlotCreateBlendState = 20;
    internal const int DeviceSlotCreateDepthStencilState = 21;
    internal const int DeviceSlotCreateRasterizerState = 22;
    internal const int DeviceSlotCreateSamplerState = 23;

    // ID3D11DeviceContext Vtable Slots
    internal const int ContextSlotVSSetConstantBuffers = 7;
    internal const int ContextSlotPSSetShaderResources = 8;
    internal const int ContextSlotPSSetShader = 9;
    internal const int ContextSlotPSSetSamplers = 10;
    internal const int ContextSlotVSSetShader = 11;
    internal const int ContextSlotDrawIndexed = 12;
    internal const int ContextSlotDraw = 13;
    internal const int ContextSlotMap = 14;
    internal const int ContextSlotUnmap = 15;
    internal const int ContextSlotPSSetConstantBuffers = 16;
    internal const int ContextSlotIASetInputLayout = 17;
    internal const int ContextSlotIASetVertexBuffers = 18;
    internal const int ContextSlotIASetIndexBuffer = 19;
    internal const int ContextSlotDrawIndexedInstanced = 20;
    internal const int ContextSlotDrawInstanced = 21;
    internal const int ContextSlotIASetPrimitiveTopology = 24;
    internal const int ContextSlotVSSetShaderResources = 25;
    internal const int ContextSlotVSSetSamplers = 26;
    internal const int ContextSlotOMSetRenderTargets = 33;
    internal const int ContextSlotOMSetBlendState = 35;
    internal const int ContextSlotOMSetDepthStencilState = 36;
    internal const int ContextSlotRSSetState = 43;
    internal const int ContextSlotRSSetViewports = 44;
    internal const int ContextSlotRSSetScissorRects = 45;
    internal const int ContextSlotCopySubresourceRegion = 46;
    internal const int ContextSlotCopyResource = 47;
    internal const int ContextSlotUpdateSubresource = 48;
    internal const int ContextSlotClearRenderTargetView = 50;
    internal const int ContextSlotClearDepthStencilView = 53;
    internal const int ContextSlotGenerateMips = 54;
    internal const int ContextSlotFlush = 111;

    // ID3D10Blob Vtable Slots
    internal const int BlobSlotGetBufferPointer = 3;
    internal const int BlobSlotGetBufferSize = 4;

    [StructLayout(LayoutKind.Sequential)]
    internal struct DxgiSampleDesc
    {
        public uint Count;
        public uint Quality;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11Texture2DDesc
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public DxgiSampleDesc SampleDesc;
        public uint Usage;
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11Texture3DDesc
    {
        public uint Width;
        public uint Height;
        public uint Depth;
        public uint MipLevels;
        public uint Format;
        public uint Usage;
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11ShaderResourceViewDesc
    {
        public uint Format;
        public uint ViewDimension;
        public uint Param0;
        public uint Param1;
        public uint Param2;
        public uint Param3;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11RenderTargetViewDesc
    {
        public uint Format;
        public uint ViewDimension;
        public uint Param0;
        public uint Param1;
        public uint Param2;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11MappedSubresource
    {
        public IntPtr Data;
        public uint RowPitch;
        public uint DepthPitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11Viewport
    {
        public float TopLeftX;
        public float TopLeftY;
        public float Width;
        public float Height;
        public float MinDepth;
        public float MaxDepth;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11Box
    {
        public uint Left;
        public uint Top;
        public uint Front;
        public uint Right;
        public uint Bottom;
        public uint Back;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11BufferDesc
    {
        public uint ByteWidth;
        public uint Usage;
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;
        public uint StructureByteStride;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11SubresourceData
    {
        public IntPtr SysMem;
        public uint SysMemPitch;
        public uint SysMemSlicePitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11InputElementDesc
    {
        public IntPtr SemanticName;
        public uint SemanticIndex;
        public uint Format;
        public uint InputSlot;
        public uint AlignedByteOffset;
        public uint InputSlotClass;
        public uint InstanceDataStepRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11RasterizerDesc
    {
        public int FillMode;
        public int CullMode;
        public int FrontCounterClockwise;
        public int DepthBias;
        public float DepthBiasClamp;
        public float SlopeScaledDepthBias;
        public int DepthClipEnable;
        public int ScissorEnable;
        public int MultisampleEnable;
        public int AntialiasedLineEnable;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11RenderTargetBlendDesc
    {
        public int BlendEnable;
        public int SrcBlend;
        public int DestBlend;
        public int BlendOp;
        public int SrcBlendAlpha;
        public int DestBlendAlpha;
        public int BlendOpAlpha;
        public byte RenderTargetWriteMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11BlendDesc
    {
        public int AlphaToCoverageEnable;
        public int IndependentBlendEnable;
        public D3D11RenderTargetBlendDesc RenderTarget0;
        public D3D11RenderTargetBlendDesc RenderTarget1;
        public D3D11RenderTargetBlendDesc RenderTarget2;
        public D3D11RenderTargetBlendDesc RenderTarget3;
        public D3D11RenderTargetBlendDesc RenderTarget4;
        public D3D11RenderTargetBlendDesc RenderTarget5;
        public D3D11RenderTargetBlendDesc RenderTarget6;
        public D3D11RenderTargetBlendDesc RenderTarget7;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11DepthStencilOpDesc
    {
        public int StencilFailOp;
        public int StencilDepthFailOp;
        public int StencilPassOp;
        public int StencilFunc;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11DepthStencilDesc
    {
        public int DepthEnable;
        public int DepthWriteMask;
        public int DepthFunc;
        public int StencilEnable;
        public byte StencilReadMask;
        public byte StencilWriteMask;
        public D3D11DepthStencilOpDesc FrontFace;
        public D3D11DepthStencilOpDesc BackFace;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11SamplerDesc
    {
        public int Filter;
        public int AddressU;
        public int AddressV;
        public int AddressW;
        public float MipLODBias;
        public uint MaxAnisotropy;
        public int ComparisonFunc;
        public float BorderColor0;
        public float BorderColor1;
        public float BorderColor2;
        public float BorderColor3;
        public float MinLOD;
        public float MaxLOD;
    }

    internal static T Method<T>(IntPtr instance, int slot) where T : class
    {
        var vtable = Marshal.ReadIntPtr(instance);
        var methodPtr = Marshal.ReadIntPtr(vtable, checked(slot * IntPtr.Size));
        return Marshal.GetDelegateForFunctionPointer<T>(methodPtr);
    }

    internal static byte[] ReadBlobBytes(IntPtr blob)
    {
        if (blob == IntPtr.Zero) return [];
        var getPtr = Method<BlobGetBufferPointerDelegate>(blob, BlobSlotGetBufferPointer);
        var getSize = Method<BlobGetBufferSizeDelegate>(blob, BlobSlotGetBufferSize);
        var ptr = getPtr(blob);
        var size = checked((int)(ulong)getSize(blob));
        if (ptr == IntPtr.Zero || size <= 0) return [];
        var bytes = new byte[size];
        Marshal.Copy(ptr, bytes, 0, size);
        return bytes;
    }

    internal static string ReadBlobString(IntPtr blob)
    {
        var bytes = ReadBlobBytes(blob);
        if (bytes.Length == 0) return string.Empty;
        var len = Array.IndexOf(bytes, (byte)0);
        if (len < 0) len = bytes.Length;
        return Encoding.UTF8.GetString(bytes, 0, len).Trim();
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int D3D11CreateDevice(
        IntPtr pAdapter,
        int driverType,
        IntPtr software,
        uint flags,
        IntPtr pFeatureLevels,
        uint featureLevels,
        uint sdkVersion,
        out IntPtr ppDevice,
        out int pFeatureLevel,
        out IntPtr ppImmediateContext);

    [DllImport("d3dcompiler_47.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int D3DCompile(
        [In] byte[] pSrcData,
        UIntPtr srcDataLen,
        [In] string? pSourceName,
        [In] IntPtr pDefines,
        [In] IntPtr pInclude,
        [In] string pEntrypoint,
        [In] string pTarget,
        uint flags1,
        uint flags2,
        out IntPtr ppCode,
        out IntPtr ppErrorMsgs);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate IntPtr BlobGetBufferPointerDelegate(IntPtr blob);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate UIntPtr BlobGetBufferSizeDelegate(IntPtr blob);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int CreateTexture2DDelegate(IntPtr device, ref D3D11Texture2DDesc desc, IntPtr initialData, out IntPtr texture);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int CreateTexture3DDelegate(IntPtr device, ref D3D11Texture3DDesc desc, IntPtr initialData, out IntPtr texture);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int CreateShaderResourceViewDelegate(IntPtr device, IntPtr resource, IntPtr desc, out IntPtr srv);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int CreateShaderResourceViewDescDelegate(IntPtr device, IntPtr resource, ref D3D11ShaderResourceViewDesc desc, out IntPtr srv);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int CreateRenderTargetViewDelegate(IntPtr device, IntPtr resource, IntPtr desc, out IntPtr rtv);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int CreateRenderTargetViewDescDelegate(IntPtr device, IntPtr resource, ref D3D11RenderTargetViewDesc desc, out IntPtr rtv);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int CreateDepthStencilViewDelegate(IntPtr device, IntPtr resource, IntPtr desc, out IntPtr dsv);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int CreateBufferDelegate(IntPtr device, ref D3D11BufferDesc desc, IntPtr initialData, out IntPtr buffer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int CreateInputLayoutDelegate(IntPtr device, IntPtr pInputElementDescs, uint numElements, byte[] pShaderBytecodeWithInputSignature, UIntPtr bytecodeLength, out IntPtr ppInputLayout);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int CreateVertexShaderDelegate(IntPtr device, byte[] pShaderBytecode, UIntPtr bytecodeLength, IntPtr pClassLinkage, out IntPtr ppVertexShader);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int CreatePixelShaderDelegate(IntPtr device, byte[] pShaderBytecode, UIntPtr bytecodeLength, IntPtr pClassLinkage, out IntPtr ppPixelShader);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int CreateBlendStateDelegate(IntPtr device, ref D3D11BlendDesc pBlendStateDesc, out IntPtr ppBlendState);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int CreateDepthStencilStateDelegate(IntPtr device, ref D3D11DepthStencilDesc pDepthStencilDesc, out IntPtr ppDepthStencilState);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int CreateRasterizerStateDelegate(IntPtr device, ref D3D11RasterizerDesc pRasterizerDesc, out IntPtr ppRasterizerState);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int CreateSamplerStateDelegate(IntPtr device, ref D3D11SamplerDesc pSamplerDesc, out IntPtr ppSamplerState);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void VSSetConstantBuffersDelegate(IntPtr context, uint startSlot, uint numBuffers, ref IntPtr ppConstantBuffers);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void PSSetConstantBuffersDelegate(IntPtr context, uint startSlot, uint numBuffers, ref IntPtr ppConstantBuffers);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void VSSetShaderDelegate(IntPtr context, IntPtr pVertexShader, IntPtr ppClassInstances, uint numClassInstances);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void PSSetShaderDelegate(IntPtr context, IntPtr pPixelShader, IntPtr ppClassInstances, uint numClassInstances);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void VSSetShaderResourcesDelegate(IntPtr context, uint startSlot, uint numViews, IntPtr ppShaderResourceViews);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void PSSetShaderResourcesDelegate(IntPtr context, uint startSlot, uint numViews, IntPtr ppShaderResourceViews);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void VSSetSamplersDelegate(IntPtr context, uint startSlot, uint numSamplers, IntPtr ppSamplers);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void PSSetSamplersDelegate(IntPtr context, uint startSlot, uint numSamplers, IntPtr ppSamplers);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void IASetInputLayoutDelegate(IntPtr context, IntPtr pInputLayout);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void IASetVertexBuffersDelegate(IntPtr context, uint startSlot, uint numBuffers, IntPtr ppVertexBuffers, IntPtr pStrides, IntPtr pOffsets);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void IASetIndexBufferDelegate(IntPtr context, IntPtr pIndexBuffer, uint format, uint offset);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void IASetPrimitiveTopologyDelegate(IntPtr context, uint topology);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void OMSetRenderTargetsDelegate(IntPtr context, uint numViews, IntPtr ppRenderTargetViews, IntPtr pDepthStencilView);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void OMSetBlendStateDelegate(IntPtr context, IntPtr pBlendState, float[] blendFactor, uint sampleMask);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void OMSetDepthStencilStateDelegate(IntPtr context, IntPtr pDepthStencilState, uint stencilRef);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void RSSetStateDelegate(IntPtr context, IntPtr pRasterizerState);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void RSSetViewportsDelegate(IntPtr context, uint numViewports, ref D3D11Viewport pViewports);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void RSSetScissorRectsDelegate(IntPtr context, uint numRects, ref D3D11Rect pRects);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void DrawDelegate(IntPtr context, uint vertexCount, uint startVertexLocation);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void DrawIndexedDelegate(IntPtr context, uint indexCount, uint startIndexLocation, int baseVertexLocation);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void DrawInstancedDelegate(IntPtr context, uint vertexCountPerInstance, uint instanceCount, uint startVertexLocation, uint startInstanceLocation);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void DrawIndexedInstancedDelegate(IntPtr context, uint indexCountPerInstance, uint instanceCount, uint startIndexLocation, int baseVertexLocation, uint startInstanceLocation);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void ClearRenderTargetViewDelegate(IntPtr context, IntPtr rtv, ref float color);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void ClearDepthStencilViewDelegate(IntPtr context, IntPtr dsv, uint clearFlags, float depth, byte stencil);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void CopyResourceDelegate(IntPtr context, IntPtr dst, IntPtr src);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void CopySubresourceRegionDelegate(IntPtr context, IntPtr dstResource, uint dstSubresource, uint dstX, uint dstY, uint dstZ, IntPtr srcResource, uint srcSubresource, IntPtr pSrcBox);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void UpdateSubresourceDelegate(IntPtr context, IntPtr dstResource, uint dstSubresource, ref D3D11Box pDstBox, IntPtr pSrcData, uint srcRowPitch, uint srcDepthPitch);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void UpdateSubresourceFullDelegate(IntPtr context, IntPtr dstResource, uint dstSubresource, IntPtr pDstBox, IntPtr pSrcData, uint srcRowPitch, uint srcDepthPitch);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void GenerateMipsDelegate(IntPtr context, IntPtr pShaderResourceView);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int MapDelegate(IntPtr context, IntPtr resource, uint subresource, uint mapType, uint mapFlags, out D3D11MappedSubresource mapped);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void UnmapDelegate(IntPtr context, IntPtr resource, uint subresource);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void FlushDelegate(IntPtr context);
}
