# HTML tokenizing and tree building

## What the spec requires

- [WHATWG HTML, §13.2 Parsing HTML documents](https://html.spec.whatwg.org/multipage/parsing.html) defines one exact algorithm: input byte stream → encoding sniffing → input stream preprocessing (CR/LF normalisation) → tokenizer (a state machine of ~80 states) → tree construction (insertion modes, stack of open elements, list of active formatting elements).
- Every input produces a document. There are no fatal errors: "parse errors" are defined, and the spec says exactly how to recover from each one.
- Named character references: the spec's [entity table](https://html.spec.whatwg.org/multipage/named-characters.html) (2,231 entries, some without a trailing `;`).
- Encoding: [WHATWG Encoding](https://encoding.spec.whatwg.org/) and [the encoding sniffing algorithm](https://html.spec.whatwg.org/multipage/parsing.html#encoding-sniffing-algorithm) (BOM, `<meta charset>` prescan, fallback).
- Foreign content: `<svg>` and `<math>` switch namespaces, adjust attribute/tag name case, and treat `<![CDATA[` differently.

## Hard parts and pitfalls

- **Misnested formatting** (`<b><i></b></i>`, `<p><b>x<p>y`): the adoption agency algorithm and reconstruction of active formatting elements. AI output hits this more than expected (unclosed `<b>` in generated tables).
- **Tables**: "foster parenting" moves stray text and elements out of `<table>` in front of it. Missing `<tbody>`/`<tr>` are implied.
- **Implied end tags**: `<p>` closed by a following block, `<li>`/`<dd>`/`<option>` auto-closing.
- **Raw text elements**: `<script>`, `<style>`, `<textarea>`, `<title>` switch tokenizer states; `</script` inside a JS string is a classic escape case.
- **Character references** in attributes vs text differ (`&amp` without `;` followed by `=` in an attribute is not decoded).
- **Pathological inputs**: 100k nested `<div>`s, 1M attributes, huge comments. Recursive code blows the stack; quadratic code hangs.
- Case-folding: tag and attribute names are ASCII-lowercased in HTML, but SVG has camelCase names (`viewBox`, `linearGradient`) that the spec's tables restore.

## Options

| Option | Pros | Cons |
|---|---|---|
| A. Full spec tokenizer + full tree builder | Every page parses the same way authors expect; error recovery is free once written; testable against the spec text line by line | The most code of the three; many insertion modes Folio will rarely use (framesets, `<template>` in tables) |
| B. Full spec tokenizer + simplified tree builder (stack of open elements, implied end tags, no adoption agency, no foster parenting) | About half the size | Misnested markup produces different trees from what authors saw when they wrote it; bugs look like "Folio renders my page wrong" and are hard to explain |
| C. Tolerant XML-ish parser | Tiny | Breaks on ordinary HTML (`<br>`, unquoted attributes, `<li>` without end tags) |

## Decision

**Option A, minus a short list of rarely-used modes.** The tokenizer is the full spec state machine. The tree builder implements every insertion mode except:

- `in frameset` / `after frameset` (frames are never rendered; `<frameset>` is treated as an ordinary unknown element).
- Scripting-flag branches: Folio parses as if scripting is **disabled** until scripting arrives (M4), so `<noscript>` content is parsed and rendered as normal markup. From M4 the flag is on for documents the host allows to run scripts.
- `document.write` re-entrancy is never supported (scripts run after parsing).

Details:

- **Input**: the public API takes a `string` (already decoded) or a byte stream. For bytes: BOM → `<meta charset>` prescan of the first 1024 bytes → UTF-8 fallback. Supported decoders: UTF-8, UTF-16LE/BE and windows-1252 (via `System.Text.Encoding`; code pages via `CodePagesEncodingProvider` only if a host asks). Anything else decodes as UTF-8 with replacement characters.
- **Tokenizer** works over a `ReadOnlySpan<char>` / `string` with an index, emits tokens into a reused token struct (no allocation per token except the strings stored in the DOM).
- **Entity table** is generated at build time from the spec's `entities.json` into a sorted array plus a prefix lookup (longest-match rule for semicolon-less references).
- **Tree builder** is iterative (explicit stack), never recursive.
- **Limits** (configurable, see [resource loading and security](16-resources-and-security.md)): maximum input size, maximum element count, maximum nesting depth (default 512; deeper start tags are handled by attaching the new element to the depth-512 ancestor, so content still shows but nesting stops). Exceeding a limit is a diagnostic, not an exception.
- **Parse errors** are recorded as diagnostics (code + source position) only when the host asks for diagnostics, so normal parsing does not pay for them.
- **Not supported** (parsed, never executed/rendered): `<script>`, `<iframe>`, `<object>`, `<embed>`, `<frame>`, `<template>` contents (kept as an inert fragment), `<base target>`.

## Tests

- Spec-derived tokenizer and tree-construction cases written as Folio's own data files (input + expected tree dump in a simple indented format), one file per insertion mode.
- Fuzzing: random and mutated HTML must never throw, never exceed the time limit and always produce a tree that satisfies DOM invariants (see [testing](19-testing.md)).
