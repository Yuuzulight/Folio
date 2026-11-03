#if !FOLIO_WASM_ORACLE
namespace Folio.Benchmarks;

internal static class WebAssemblyBenchmark
{
    internal static (TimeSpan Cold, TimeSpan Warm, int CachedModules) Measure(int repetitions = 5) =>
        throw new InvalidOperationException("The WebAssembly benchmark requires the explicit FolioWasmOracle configuration.");
}
#endif
