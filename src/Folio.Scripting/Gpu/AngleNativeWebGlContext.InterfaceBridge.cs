namespace Folio.Scripting.Gpu;

internal sealed partial class AngleNativeWebGlContext
{
    public bool TryGetError(out uint error, out GpuFailure? failure)
    {
        error = 0; failure = null;
        if (!TryGetError(out error)) { failure = new(GpuFailureKind.Unavailable, "The WebGL context is unavailable."); return false; }
        return true;
    }
}
