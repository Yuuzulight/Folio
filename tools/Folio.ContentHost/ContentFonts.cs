using Folio.Typography;

namespace Folio.ContentHost;

// The SDK and standalone host share the same lazy, licensed font defaults.
internal static class ContentFonts
{
    internal static readonly FontSettings Settings = BundledFontSource.Settings;
}
