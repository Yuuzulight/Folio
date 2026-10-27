using System.Diagnostics;
using Folio.Scripting;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;

namespace Folio.Benchmarks;

internal static class WebAssemblyBenchmark
{
    private static readonly byte[] Module =
    [
        0, 97, 115, 109, 1, 0, 0, 0,
        1, 5, 1, 96, 0, 1, 127,
        3, 2, 1, 0,
        7, 7, 1, 3, (byte)'r', (byte)'u', (byte)'n', 0, 0,
        10, 6, 1, 4, 0, 65, 42, 11
    ];

    internal static (TimeSpan Cold, TimeSpan Warm, int CachedModules) Measure(int repetitions = 5)
    {
        using var runtime = new NativeWebAssemblyRuntime(64 * 1024);
        using var engine = new JintScriptEngine();
        var coldStart = Stopwatch.GetTimestamp();
        if (!runtime.TryInstantiate(engine, Module, ScriptValue.Undefined, [], out var cold) || cold is null)
            throw new InvalidOperationException("Native WebAssembly benchmark could not instantiate its module.");
        using (cold)
            cold.Invoke(engine, "run", []);
        var coldTime = Stopwatch.GetElapsedTime(coldStart);

        var warmTimes = new List<TimeSpan>(repetitions);
        for (var i = 0; i < repetitions; i++)
        {
            var start = Stopwatch.GetTimestamp();
            if (!runtime.TryInstantiate(engine, Module, ScriptValue.Undefined, [], out var warm) || warm is null)
                throw new InvalidOperationException("Native WebAssembly benchmark cache instantiation failed.");
            using (warm)
                warm.Invoke(engine, "run", []);
            warmTimes.Add(Stopwatch.GetElapsedTime(start));
        }
        warmTimes.Sort();
        return (coldTime, warmTimes[warmTimes.Count / 2], runtime.CachedModuleCount);
    }
}
