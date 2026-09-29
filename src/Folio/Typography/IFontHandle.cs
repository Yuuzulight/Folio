namespace Folio.Typography;

/// <summary>
/// A font face a canvas draws glyphs from: the bytes of an OpenType or TrueType file (or collection) and the index of
/// the face in it. Folio has already validated the data; the same handle always stands for the same face, so a
/// canvas can cache what it builds from it.
/// </summary>
public interface IFontHandle
{
    ReadOnlyMemory<byte> Data { get; }

    int FaceIndex { get; }
}
