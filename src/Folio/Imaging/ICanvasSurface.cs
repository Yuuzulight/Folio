namespace Folio.Imaging;

/// <summary>A canvas element's bitmap, installed by the scripting layer and painted as the element's content.</summary>
internal interface ICanvasSurface
{
    /// <summary>The bitmap's size in pixels.</summary>
    int Width { get; }

    int Height { get; }

    /// <summary>The bitmap as it is now; null when it is empty of any drawing.</summary>
    IImageHandle? Snapshot();
}
