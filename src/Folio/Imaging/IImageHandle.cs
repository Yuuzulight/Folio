namespace Folio.Imaging;

/// <summary>
/// Decoded image pixels a canvas draws: RGBA8 with straight (not premultiplied) alpha, row-major without padding.
/// The same handle always stands for the same pixels, so a canvas can cache what it builds from them.
/// </summary>
public interface IImageHandle
{
    int Width { get; }

    int Height { get; }

    ReadOnlyMemory<byte> Pixels { get; }
}
