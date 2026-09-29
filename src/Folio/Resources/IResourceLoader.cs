namespace Folio.Resources;

/// <summary>Fetches every resource a document asks for; the host decides what is allowed.</summary>
/// <remarks>
/// Swap-out interface, sketched in docs/study/16-resources-and-security.md. Members are added together with the value
/// types they use when the first implementation lands.
/// </remarks>
public interface IResourceLoader
{
}
