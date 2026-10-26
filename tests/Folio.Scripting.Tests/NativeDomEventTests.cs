using Folio;
using Folio.Html;
using Folio.Scripting.Dom;
using Folio.Scripting.Native;

namespace Folio.Scripting.Tests;
public sealed class NativeDomEventTests
{
 [Fact] public void Probe()
 {
  using var engine = new NativeJavaScriptEngine(); var clock = new VirtualEventLoopClock(); using var loop = new HtmlEventLoop(engine, clock); var errors = new List<Exception>(); loop.UncaughtException += errors.Add;
  ScriptGlobalsInstaller.Install(loop, engine); DomBindings.Install(engine, TreeBuilder.Parse("<button id='b'>x</button>"), eventLoop: loop);
  engine.Evaluate("globalThis.hit='no'; let b=document.getElementById('b'); b.addEventListener('click',function(){globalThis.hit='listener'}); b.dispatchEvent(new Event('click'));");
  Assert.Empty(errors);
  Assert.Equal("listener", engine.GetGlobalProperty("hit").AsString());
 }
}
