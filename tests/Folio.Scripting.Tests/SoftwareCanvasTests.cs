using Folio.Painting;

namespace Folio.Scripting.Tests;

/// <summary>The canvas tests again, drawn by Folio's own raster backend instead of Skia (#569: canvas strokes among them).</summary>
public class SoftwareCanvasTests : CanvasTests
{
    protected override IRasterBackend CreateRaster() => SoftwareRasterBackend.Instance;
}
