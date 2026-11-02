using System.Runtime.InteropServices;

namespace Folio.Scripting.Gpu;

/// <summary>
/// Native Direct3D 11, DXGI, and D3DCompiler P/Invoke interop contracts for Folio's GPU backend.
/// </summary>
internal static class FolioD3D11Interop
{
    internal const uint D3D11SdkVersion = 7;

    internal const int DriverTypeHardware = 1;
    internal const int DriverTypeWarp = 5;

    internal const uint DxgiFormatR8G8B8A8Unorm = 28;
    internal const uint DxgiFormatD24UnormS8Uint = 45;
    internal const uint DxgiFormatR32G32B32A32Float = 2;

    internal const uint D3D11UsageDefault = 0;
    internal const uint D3D11UsageImmutable = 1;
    internal const uint D3D11UsageDynamic = 2;
    internal const uint D3D11UsageStaging = 3;

    internal const uint D3D11BindVertexBuffer = 0x1;
    internal const uint D3D11BindIndexBuffer = 0x2;
    internal const uint D3D11BindConstantBuffer = 0x4;
    internal const uint D3D11BindShaderResource = 0x8;
    internal const uint D3D11BindRenderTarget = 0x20;
    internal const uint D3D11BindDepthStencil = 0x40;

    internal const uint D3D11CpuAccessWrite = 0x10000;
    internal const uint D3D11CpuAccessRead = 0x20000;

    internal const uint D3D11MapRead = 1;
    internal const uint D3D11MapWrite = 2;
    internal const uint D3D11MapReadWrite = 3;
    internal const uint D3D11MapWriteDiscard = 4;
    internal const uint D3D11MapWriteNoOverwrite = 5;

    // ID3D11Device Vtable Slots
    internal const int DeviceSlotCreateBuffer = 3;
    internal const int DeviceSlotCreateTexture2D = 5;
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
    internal const int ContextSlotRSSetViewports = 44;
    internal const int ContextSlotRSSetScissorRects = 45;
    internal const int ContextSlotCopySubresourceRegion = 46;
    internal const int ContextSlotCopyResource = 47;
    internal const int ContextSlotUpdateSubresource = 48;
    internal const int ContextSlotClearRenderTargetView = 50;
    internal const int ContextSlotClearDepthStencilView = 53;
    internal const int ContextSlotFlush = 111;

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

    internal static T Method<T>(IntPtr instance, int slot) where T : class
    {
        var vtable = Marshal.ReadIntPtr(instance);
        var methodPtr = Marshal.ReadIntPtr(vtable, checked(slot * IntPtr.Size));
        return Marshal.GetDelegateForFunctionPointer<T>(methodPtr);
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
    internal delegate int CreateTexture2DDelegate(IntPtr device, ref D3D11Texture2DDesc desc, IntPtr initialData, out IntPtr texture);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int CreateRenderTargetViewDelegate(IntPtr device, IntPtr resource, IntPtr desc, out IntPtr rtv);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int CreateBufferDelegate(IntPtr device, ref D3D11BufferDesc desc, IntPtr initialData, out IntPtr buffer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void ClearRenderTargetViewDelegate(IntPtr context, IntPtr rtv, ref float color);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void CopyResourceDelegate(IntPtr context, IntPtr dst, IntPtr src);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void UpdateSubresourceDelegate(IntPtr context, IntPtr dstResource, uint dstSubresource, ref D3D11Box pDstBox, IntPtr pSrcData, uint srcRowPitch, uint srcDepthPitch);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int MapDelegate(IntPtr context, IntPtr resource, uint subresource, uint mapType, uint mapFlags, out D3D11MappedSubresource mapped);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void UnmapDelegate(IntPtr context, IntPtr resource, uint subresource);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void FlushDelegate(IntPtr context);
}
