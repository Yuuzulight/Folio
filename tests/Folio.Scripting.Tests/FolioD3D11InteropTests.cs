using System.Runtime.InteropServices;
using System.Text;
using Folio.Scripting.Gpu;

namespace Folio.Scripting.Tests;

public sealed class FolioD3D11InteropTests
{
    [Fact]
    public void D3D11DeviceCreationSucceedsOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;

        var hr = FolioD3D11Interop.D3D11CreateDevice(
            IntPtr.Zero,
            FolioD3D11Interop.DriverTypeHardware,
            IntPtr.Zero,
            0,
            IntPtr.Zero,
            0,
            FolioD3D11Interop.D3D11SdkVersion,
            out var device,
            out var featureLevel,
            out var context);

        if (hr < 0 || device == IntPtr.Zero)
        {
            hr = FolioD3D11Interop.D3D11CreateDevice(
                IntPtr.Zero,
                FolioD3D11Interop.DriverTypeWarp,
                IntPtr.Zero,
                0,
                IntPtr.Zero,
                0,
                FolioD3D11Interop.D3D11SdkVersion,
                out device,
                out featureLevel,
                out context);
        }

        Assert.True(hr >= 0, $"D3D11CreateDevice failed with HRESULT 0x{hr:X8}");
        Assert.NotEqual(IntPtr.Zero, device);
        Assert.NotEqual(IntPtr.Zero, context);
        Assert.True(featureLevel >= 0x9100, $"Expected Direct3D 9.1 or higher feature level, got 0x{featureLevel:X8}");

        Marshal.Release(context);
        Marshal.Release(device);
    }

    [Fact]
    public void RenderTargetClearAndReadbackProducesExactColor()
    {
        if (!OperatingSystem.IsWindows()) return;

        var hr = FolioD3D11Interop.D3D11CreateDevice(
            IntPtr.Zero,
            FolioD3D11Interop.DriverTypeHardware,
            IntPtr.Zero,
            0,
            IntPtr.Zero,
            0,
            FolioD3D11Interop.D3D11SdkVersion,
            out var device,
            out _,
            out var context);

        if (hr < 0 || device == IntPtr.Zero)
        {
            hr = FolioD3D11Interop.D3D11CreateDevice(
                IntPtr.Zero,
                FolioD3D11Interop.DriverTypeWarp,
                IntPtr.Zero,
                0,
                IntPtr.Zero,
                0,
                FolioD3D11Interop.D3D11SdkVersion,
                out device,
                out _,
                out context);
        }

        Assert.True(hr >= 0);

        try
        {
            var createTexture = FolioD3D11Interop.Method<FolioD3D11Interop.CreateTexture2DDelegate>(
                device, FolioD3D11Interop.DeviceSlotCreateTexture2D);
            var createRtv = FolioD3D11Interop.Method<FolioD3D11Interop.CreateRenderTargetViewDelegate>(
                device, FolioD3D11Interop.DeviceSlotCreateRenderTargetView);
            var clearRtv = FolioD3D11Interop.Method<FolioD3D11Interop.ClearRenderTargetViewDelegate>(
                context, FolioD3D11Interop.ContextSlotClearRenderTargetView);
            var copyResource = FolioD3D11Interop.Method<FolioD3D11Interop.CopyResourceDelegate>(
                context, FolioD3D11Interop.ContextSlotCopyResource);
            var map = FolioD3D11Interop.Method<FolioD3D11Interop.MapDelegate>(
                context, FolioD3D11Interop.ContextSlotMap);
            var unmap = FolioD3D11Interop.Method<FolioD3D11Interop.UnmapDelegate>(
                context, FolioD3D11Interop.ContextSlotUnmap);

            var desc = new FolioD3D11Interop.D3D11Texture2DDesc
            {
                Width = 4,
                Height = 4,
                MipLevels = 1,
                ArraySize = 1,
                Format = FolioD3D11Interop.DxgiFormatR8G8B8A8Unorm,
                SampleDesc = new FolioD3D11Interop.DxgiSampleDesc { Count = 1, Quality = 0 },
                Usage = FolioD3D11Interop.D3D11UsageDefault,
                BindFlags = FolioD3D11Interop.D3D11BindRenderTarget,
                CPUAccessFlags = 0,
                MiscFlags = 0,
            };

            var hrTexture = createTexture(device, ref desc, IntPtr.Zero, out var rtTex);
            Assert.True(hrTexture >= 0);

            try
            {
                var hrRtv = createRtv(device, rtTex, IntPtr.Zero, out var rtv);
                Assert.True(hrRtv >= 0);

                try
                {
                    // Clear with RGBA = (0.25f, 0.5f, 0.75f, 1.0f)
                    var color = new float[] { 0.25f, 0.5f, 0.75f, 1.0f };
                    clearRtv(context, rtv, ref color[0]);

                    var stagingDesc = desc;
                    stagingDesc.Usage = FolioD3D11Interop.D3D11UsageStaging;
                    stagingDesc.BindFlags = 0;
                    stagingDesc.CPUAccessFlags = FolioD3D11Interop.D3D11CpuAccessRead;

                    var hrStaging = createTexture(device, ref stagingDesc, IntPtr.Zero, out var stagingTex);
                    Assert.True(hrStaging >= 0);

                    try
                    {
                        copyResource(context, stagingTex, rtTex);

                        var hrMap = map(context, stagingTex, 0, FolioD3D11Interop.D3D11MapRead, 0, out var mapped);
                        Assert.True(hrMap >= 0);

                        try
                        {
                            var pixel = new byte[4];
                            Marshal.Copy(mapped.Data, pixel, 0, 4);

                            // 0.25f * 255 = ~64, 0.5f * 255 = ~128, 0.75f * 255 = ~191, 1.0f * 255 = 255
                            Assert.InRange(pixel[0], 63, 65);
                            Assert.InRange(pixel[1], 127, 129);
                            Assert.InRange(pixel[2], 190, 192);
                            Assert.Equal(255, pixel[3]);
                        }
                        finally
                        {
                            unmap(context, stagingTex, 0);
                        }
                    }
                    finally
                    {
                        Marshal.Release(stagingTex);
                    }
                }
                finally
                {
                    Marshal.Release(rtv);
                }
            }
            finally
            {
                Marshal.Release(rtTex);
            }
        }
        finally
        {
            Marshal.Release(context);
            Marshal.Release(device);
        }
    }

    [Fact]
    public void D3DCompileCompilesHlslToBytecode()
    {
        if (!OperatingSystem.IsWindows()) return;

        const string hlsl = "float4 VSMain(float4 pos : POSITION) : SV_Position { return pos; }";
        var bytes = Encoding.UTF8.GetBytes(hlsl);

        var hr = FolioD3D11Interop.D3DCompile(
            bytes,
            (UIntPtr)bytes.Length,
            null,
            IntPtr.Zero,
            IntPtr.Zero,
            "VSMain",
            "vs_5_0",
            0,
            0,
            out var code,
            out var errors);

        if (errors != IntPtr.Zero) Marshal.Release(errors);

        Assert.True(hr >= 0, $"D3DCompile failed with HRESULT 0x{hr:X8}");
        Assert.NotEqual(IntPtr.Zero, code);

        Marshal.Release(code);
    }
}
