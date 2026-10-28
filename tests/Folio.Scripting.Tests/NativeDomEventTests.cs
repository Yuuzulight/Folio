using Folio;
using Folio.Html;
using Folio.Scripting.Dom;
using Folio.Scripting.Native;

namespace Folio.Scripting.Tests;
public sealed class NativeDomEventTests
{
 [Fact] public void NativeInlineEventHandlerReceivesTheActualTarget()
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

 [Fact] public void NativeClickListenerResolvesTheLiveDomAfterDuplicateIds()
 {
  using var engine = new NativeJavaScriptEngine(); var clock = new VirtualEventLoopClock(); using var loop = new HtmlEventLoop(engine, clock);
  var document = Folio.Document.Parse("<span id='value'>0</span><button id='b'>go</button><div id='react-root'></div>", new FolioOptions { Scripting = ScriptingPolicy.ContentProcess });
  string? value;
  try
  {
   DomBindings.Install(engine, document, eventLoop: loop); ScriptGlobalsInstaller.Install(loop, engine);
   engine.Evaluate("document.getElementById('react-root').innerHTML='<span id=\"value\">hidden</span>'; document.getElementById('b').addEventListener('click',()=>{const v=document.getElementById('value');v.textContent=String(Number(v.textContent)+1)});");
   document.QuerySelector("#b")!.Click();
   value = document.QuerySelector("#value")?.TextContent;
  }
  finally { document.Dispose(); }
  Assert.Equal("1", value);
 }

 [Fact] public void NativeClickBubblesToParentListener()
 {
  using var engine = new NativeJavaScriptEngine(); using var loop = new HtmlEventLoop(engine);
  var document = Folio.Document.Parse("<div id='root'><button id='b'>go</button></div>", new FolioOptions { Scripting = ScriptingPolicy.ContentProcess });
  try
  {
   DomBindings.Install(engine, document, eventLoop: loop); ScriptGlobalsInstaller.Install(loop, engine);
   engine.Evaluate("var hit=0; document.getElementById('root').addEventListener('click',()=>hit++);");
   document.QuerySelector("#b")!.Click();
  }
  finally { document.Dispose(); }
  Assert.Equal(1, engine.Evaluate("hit").AsNumber());
 }

}
