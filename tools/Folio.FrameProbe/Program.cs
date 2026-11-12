using Folio.Benchmarks;

// Paint budgets (docs/study/18-memory-and-performance.md) measured in the build a host ships; exits 1 when one is missed.
static string Ms(TimeSpan t) => t.TotalMilliseconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
var mode = System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported ? "JIT" : "NativeAOT";
const int frames = 60;
var typical = FrameBenchmark.Measure(FrameBenchmark.Typical, frames);
var busy = FrameBenchmark.Measure(FrameBenchmark.Busy, frames);
var hover = FrameBenchmark.MeasureHover(FrameBenchmark.HoverHtml, frames);
var scroll = FrameBenchmark.MeasureScroll(FrameBenchmark.ScrollHtml, frames);
Console.WriteLine($"Frame probe ({mode}, {frames} frames, median / p95 ms, target)");
Console.WriteLine($"typical dashboard      {Ms(typical.Median),6} / {Ms(typical.P95),6}   {Ms(FrameBenchmark.Target)}{(typical.PaintOnly ? "" : "  NOT paint-only")}");
Console.WriteLine($"busy dashboard         {Ms(busy.Median),6} / {Ms(busy.P95),6}   -");
Console.WriteLine($"card hover restyle     {Ms(hover.Median),6} / {Ms(hover.P95),6}   {Ms(FrameBenchmark.HoverTarget)}");
Console.WriteLine($"viewport scroll frame  {Ms(scroll.Median),6} / {Ms(scroll.P95),6}   {Ms(FrameBenchmark.ScrollTarget)}");
return typical.Median > FrameBenchmark.Target || !typical.PaintOnly || hover.Median > FrameBenchmark.HoverTarget || scroll.Median > FrameBenchmark.ScrollTarget ? 1 : 0;
