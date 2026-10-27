namespace Folio.Scripting.Gpu;

internal sealed partial class AngleNativeWebGlContext
{
    public bool TryGetShaderPrecisionFormat(uint shaderType, uint precisionType, out int rangeMin, out int rangeMax, out int precision, out GpuFailure? failure) => TryGetShaderPrecision(shaderType, precisionType, out rangeMin, out rangeMax, out precision, out failure);
}
