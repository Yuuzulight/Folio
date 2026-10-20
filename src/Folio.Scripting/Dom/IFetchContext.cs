using Folio.Resources;

namespace Folio.Scripting.Dom;

/// <summary>The body store and resource broker required by fetch, without a Window or document.</summary>
internal interface IFetchContext
{
    Uri? BaseUri { get; }
    (ResourceResponse? Response, string? Error) Fetch(string url);
    int StoreFetchBody(byte[] data);
    byte[]? FetchBody(int handle);
    void FreeFetchBody(int handle);
}
