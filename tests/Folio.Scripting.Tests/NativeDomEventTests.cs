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
  var document = Folio.Document.Parse("<button id='b'>x</button>", new FolioOptions { Scripting = ScriptingPolicy.ContentProcess });
  try
  {
   DomBindings.Install(engine, document, eventLoop: loop);
   ScriptGlobalsInstaller.Install(loop, engine);
   engine.Evaluate("globalThis.hit='no'; let b=document.getElementById('b'); b.addEventListener('click',function(e){globalThis.targetSeen=(e.target===b); globalThis.hit='listener'});");
   document.QuerySelector("#b")!.Click();
  }
  finally { document.Dispose(); }
  Assert.Empty(errors);
  Assert.Equal("listener", engine.GetGlobalProperty("hit").AsString());
  Assert.True(engine.GetGlobalProperty("targetSeen").AsBoolean());
 }

}
