# Folio Browser

A lightweight HTML/CSS rendering engine for .NET, written from scratch in C#. No Chromium, WebKit or Gecko inside.

Folio is being built for [Mana](https://github.com/Yuuzulight/Mana), a personal AI companion whose native Windows launcher avoids Chromium to keep its memory footprint small. Mana writes HTML "artifacts" (reports, tables, small interactive pages); Folio's first job is to show them inside Mana's own window. It's a standalone project so other .NET apps can use it too.

> A *folio* is a single page of a book: what this engine produces.

**Status:** study phase. No engine code yet.

## Goals
- Modern CSS layout: flexbox, grid, custom properties (`var()`), with correct text layout and font fallback.
- Draws natively with SkiaSharp; a WinForms control first, other hosts later.
- An interactive on-screen control: hover, clicks, scrolling, text selection, relayout on resize. This is the gap no free .NET engine fills today.
- Small: tens of MB at most, not a browser process.
- Safe by default: no network access unless the host allows it.

## Non-goals
- Browsing the web. No address bar, tabs, logins or extensions.
- Full web compatibility. Pages that need big frameworks can still open in a real browser.

## Plan
0. **Study** how existing engines work and write it up in `docs/study/`: PeachPDF (modern CSS in pure C#), Rend (C# + SkiaSharp), litehtml (layout/draw separation), HtmlRenderer (WinForms hosting), Blitz with Stylo and Taffy (styling and flex/grid), RmlUi (interactive HTML/CSS for games), plus Servo and Ladybird for architecture. Then write the architecture doc.
1. **Static renderer:** parse, cascade, layout (block, inline, flex, grid), paint to SkiaSharp. Replaces HtmlRenderer in Mana's "Open in Mana" view.
2. **Sandboxed scripting:** a JavaScript engine (e.g. Jint) with strict time/memory limits and no host access, likely out of process.
3. **Interaction:** events and hit testing, forms and text input, canvas 2D, incremental restyle/relayout.
4. **Watch list:** if Blitz or Servo becomes embeddable from .NET first, reconsider.

Background and research: [Yuuzulight/Mana#798](https://github.com/Yuuzulight/Mana/issues/798).

## Licence
Apache-2.0, same as Mana. When studying other engines we read and learn from them; if any code is ever adapted, its original licence notice is kept alongside it.
