# Plan: Folio-Owned GPU Backend and Shader Translation (Issue #682)

This plan specifies the architecture, interfaces, and differential acceptance gate to replace `Avalonia.Angle.Windows.Natives` (ANGLE) with Folio's own native GPU backend and shader pipeline.

---

## 1. Motivation and Scope

ANGLE is a third-party dependency providing OpenGL ES 2.0/3.0 emulation over Direct3D 11 on Windows. Under the mandatory M7 all-Folio replacement track ([dependencies.md](../dependencies.md), [replacement-gate.md](../replacement-gate.md)), third-party rendering engines and native runtimes must be eliminated from production builds and published packages.

### Scope
1. **Folio-Owned GPU Device and Surface Pipe**:
   - Direct integration with Windows Direct3D 11 (`d3d11.dll`) and DXGI (`dxgi.dll`) through Folio-owned P/Invoke contracts.
   - Hardware device creation (`D3D_DRIVER_TYPE_HARDWARE`) with explicit WARP fallback (`D3D_DRIVER_TYPE_WARP`) matching the existing renderer preference hierarchy.
   - Off-screen render targets, multi-sample renderbuffers, swap chains, and staging textures for bounded pixel readback (`TryReadPixels`).
2. **GLSL Translation and Shader Compilation**:
   - Folio-owned GLSL ES 1.00 and 3.00 parsing and translation into Direct3D High Level Shading Language (HLSL SM 5.0).
   - HLSL compilation to DXBC bytecode using Windows' standard `d3dcompiler_47.dll` (`D3DCompile`).
   - Reflection of active attributes, uniforms, and uniform buffer blocks.
3. **WebGL 1 and 2 Context Architecture**:
   - Direct implementation of `INativeWebGlContext` and `INativeWebGlFactory` replacing `AngleNativeWebGlContext`.
   - Full support for standard WebGL 1/2 buffer targets, texture formats (RGBA8, RGB8, float, depth/stencil), framebuffers, blits, draw arrays, and instanced draw calls.
   - Strict resource accounting against the host's `MaxGpuBytes` budget.
   - Clean context loss handling, watchdog interruption, and sandboxed content-process safety.
4. **Dependency Removal**:
   - Remove `Avalonia.Angle.Windows.Natives` from `src/Folio.Scripting/Folio.Scripting.csproj`.
   - Update pack and publish rules to verify zero ANGLE DLLs (`av_libglesv2.dll`, `av_libegl.dll`) in published production artifacts.

---

## 2. Architecture and Component Mapping

```
+-------------------------------------------------------------------+
|               Folio Scripting / DOM WebGL Layer                   |
|                   (DomNodeTemplates.WebGl.cs)                     |
+-------------------------------------------------------------------+
                                  |
                                  v
+-------------------------------------------------------------------+
|                      INativeWebGlContext                          |
|         (Engine-independent boundary, WebGlContracts.cs)          |
+-------------------------------------------------------------------+
                                  |
         +------------------------+------------------------+
         |                                                 |
         v                                                 v
+-----------------------------+               +---------------------+
|    FolioNativeWebGlContext   |               |   FolioGpuDevice    |
|   (Direct3D 11 WebGL Exec)  |               | (D3D11 / DXGI Core) |
+-----------------------------+               +---------------------+
         |                                                 |
         +-----------------------+-------------------------+
                                 |
                                 v
+-------------------------------------------------------------------+
|               Direct3D 11 / DXGI Native Windows API               |
|            (d3d11.dll, dxgi.dll, d3dcompiler_47.dll)             |
+-------------------------------------------------------------------+
```

### Direct3D 11 Mapping Table

| WebGL Primitive / State | Direct3D 11 Equivalent | Notes |
|---|---|---|
| `WebGLTexture` | `ID3D11Texture2D` + `ID3D11ShaderResourceView` | 2D, Cube, 3D textures |
| `WebGLRenderbuffer` | `ID3D11Texture2D` + `ID3D11RenderTargetView` / `DepthStencilView` | RGBA8, Depth24Stencil8 |
| `WebGLFramebuffer` | Render Target and Depth Stencil Views binding set | MRT via `OMSetRenderTargets` |
| `WebGLBuffer` | `ID3D11Buffer` (`D3D11_BIND_VERTEX_BUFFER`, `INDEX_BUFFER`, `CONSTANT_BUFFER`) | Mapped via `D3D11_MAP_WRITE_DISCARD` |
| `WebGLProgram` | `ID3D11VertexShader` + `ID3D11PixelShader` + `ID3D11InputLayout` | Compiled via `D3DCompile` |
| `Clear` | `ClearRenderTargetView`, `ClearDepthStencilView` | Bounded rect scissor |
| `DrawArrays`, `DrawElements` | `Draw`, `DrawIndexed`, `DrawInstanced`, `DrawIndexedInstanced` | Topology mapped |
| `ReadPixels` | Staging `ID3D11Texture2D` with `CopySubresourceRegion` + `Map` | Row-pitch aligned |

---

## 3. Shader Translation Pipeline

GLSL ES input is validated, parsed, and translated to HLSL SM 5.0:
1. **Built-in Variable Mapping**:
   - `gl_Position` $\to$ `SV_Position` (with Y-flip correction for screen-space orientation).
   - `gl_FragColor` / `out vec4` $\to$ `SV_Target0` (`SV_Target[n]` for MRT).
   - `gl_FragCoord` $\to$ `SV_Position` input in pixel shader.
   - `gl_VertexID` $\to$ `SV_VertexID`.
   - `gl_InstanceID` $\to$ `SV_InstanceID`.
2. **Type and Matrix Handling**:
   - Vector constructors (`vec2`, `vec3`, `vec4`) $\to$ `float2`, `float3`, `float4`.
   - Matrix math: GLSL column-major conventions preserved with HLSL matrix layout annotations.
   - Sampler functions (`texture2D`, `texture`, `textureLod`) $\to$ `Texture.Sample(Sampler, uv)`.
3. **Precision Qualifiers**:
   - `lowp`, `mediump`, `highp` stripped or mapped to `min16float` / `float`.

---

## 4. Differential Verification and Acceptance Gate

To satisfy the replacement gate, the Folio-owned backend must meet the following criteria before ANGLE is deleted:
1. **Conformance Suite**:
   - Pass 100% of Category D8 (WebGL acceptance tests) with zero visual divergence.
   - Pass the full WebGL conformance test harness in `tests/Folio.RenderTests/`.
2. **Differential Parity with ANGLE**:
   - Compare pixel readback against ANGLE across synthetic tests, buffer updates, float targets, cube maps, and MRT.
3. **Limits, Sandbox, and Resource Accounting**:
   - Strict adherence to `MaxGpuBytes` and immediate refusal when allocations exceed budget.
   - Full recovery on device loss and watchdog interruption in sandboxed content processes.
4. **Publish Verification**:
   - Production publish output (`Folio.ContentHost.exe`) contains zero references to `av_libglesv2.dll` or `av_libegl.dll`.
