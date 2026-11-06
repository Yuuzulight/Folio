using Folio.Rendering;
using Folio.Imaging;

var png = HeadlessRenderer.RenderPng("<body style='margin:0;color:black;background:white'>Mana ready</body>", 240);
var image = ImageDecoder.Decode(png) ?? throw new Exception("The public PNG output could not be decoded.");
if (!Enumerable.Range(0, image.Width * image.Height).Any(i => image.Pixels[i * 4] < 100 && image.Pixels[i * 4 + 3] == 255))
    throw new Exception("The default package produced blank text.");
File.WriteAllBytes(Path.Combine(AppContext.BaseDirectory, "consumer.png"), png);
var forbidden = new[] { "SkiaSharp", "HarfBuzzSharp", "Jint", "Wasmtime" };
if (AppDomain.CurrentDomain.GetAssemblies().Any(a => forbidden.Contains(a.GetName().Name)))
    throw new Exception("A differential oracle was loaded by the public renderer.");
Console.WriteLine("PASS: public Folio package, visible default text, PNG round trip, no oracle assemblies.");
