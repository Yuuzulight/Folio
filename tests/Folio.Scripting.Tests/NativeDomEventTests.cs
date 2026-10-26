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
   engine.Evaluate("globalThis.hit='no'; globalThis.targetSeen=false; let b=document.getElementById('b'); b.onclick=function(e){globalThis.hit='onclick'; globalThis.targetSeen=(e.target===b)};");
   document.QuerySelector("#b")!.Click();
  }
  finally { document.Dispose(); }
  Assert.Empty(errors);
  Assert.Equal("onclick", engine.GetGlobalProperty("hit").AsString());
  Assert.True(engine.GetGlobalProperty("targetSeen").AsBoolean());
 }

}
