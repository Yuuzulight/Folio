# DOM representation

## What the spec requires

- [WHATWG DOM](https://dom.spec.whatwg.org/): node tree (Document, DocumentType, Element, Text, Comment, DocumentFragment), tree order, attributes with namespaces, `classList`/`id` reflection, ranges (used by selection), mutation algorithms (insert, remove, replace) with their pre-insertion validity checks.
- [HTML §3 and §4](https://html.spec.whatwg.org/multipage/dom.html): element semantics that matter to rendering (which elements are replaced, `hidden`, `lang`, `dir`), and the [UA stylesheet in the Rendering section](https://html.spec.whatwg.org/multipage/rendering.html).
- Static rendering (M1–M2) needs the tree, attributes and a few queries. Interaction and scripting (M3–M5) need the full mutation API, ranges, live collections, events and mutation observers. The representation must not block them.

## Hard parts and pitfalls

- **Memory**: a node per element and per text run is unavoidable, but per-node overhead adds up fast in .NET (object header 16 B + fields). A 10k-element artifact with 3 attributes each must stay in low single-digit MB.
- **Strings**: tag names, attribute names and class names repeat constantly. Comparing them as strings in selector matching is slow.
- **Child lists**: `List<Node>` per element makes insert/remove O(n) and allocates on every element with children; linked siblings make index access O(n).
- **Engine data hanging off nodes**: style, box, dirty flags. Putting everything on the node bloats it; keeping it in side dictionaries makes lookups slow.
- **Live collections** (`getElementsByClassName`, `children`) and ranges must update on mutation (M4).

## Options

| Option | Pros | Cons |
|---|---|---|
| A. Classic object tree: `Node` class with parent, first/last child, previous/next sibling references | Direct mapping to DOM spec; O(1) insert/remove; natural for scripting; easy to debug | One object per node; pointer-chasing |
| B. Struct-of-arrays arena: nodes are integer ids into parallel arrays | Compact, cache-friendly, cheap to serialise across a process boundary | Every API takes `(doc, id)`; mutation needs free lists; scripting wrappers need a handle table; harder to read |
| C. Immutable tree rebuilt on change | Trivially thread-safe | Mutation (scripts, hover state) costs O(depth) allocations per change; poor fit for the DOM API |

## Decision

**Option A with compact nodes and interned names.**

- `Node` (abstract) → `ContainerNode` → `Element`, `Document`, `DocumentFragment`; `CharacterData` → `Text`, `Comment`. Links: `Parent`, `FirstChild`, `LastChild`, `PreviousSibling`, `NextSibling`. No child list objects.
- **Atoms**: tag names, attribute names, class names, ids and CSS property names are interned into a per-process `AtomTable` and stored as `Atom` (a 4-byte struct wrapping an int). Equality is an int compare. The table is append-only and bounded (see limits); uncommon strings beyond the cap stay as plain strings and match by string compare.
- **Element name**: one `QualifiedName` atom pair (namespace + local name). HTML elements also get a small `ElementKind` enum (Div, P, Img, Table, …) for fast switch-based behaviour.
- **Attributes**: an `Attribute[]` array per element (struct of name atom + string value), exact-size, linear scan (elements rarely have more than 5). `id` and `class` are parsed on set and cached: `Atom Id`, `Atom[] Classes`.
- **Text**: `Text.Data` is a `string`. Adjacent text nodes are merged by the parser as the spec requires.
- **Engine data**: each `Element` has exactly three engine fields: `NodeFlags Flags` (dirty bits, hover/active/focus state, "has ::before" etc.), `ComputedStyle? Style`, `Box? Box`. Everything else lives in the style/layout modules.
- **Child index**: not stored. `:nth-child` computes indices by walking siblings, with a per-restyle cache (see [selectors](03-css-parsing-and-selectors.md)).
- **Mutation API** exists from M1 (the parser and the host use it: `AppendChild`, `InsertBefore`, `RemoveChild`, `SetAttribute`). Every mutation calls one internal hook, `OnMutated(node, kind)`, which sets dirty flags for [invalidation](14-invalidation.md). Selection (M3) adds ranges; scripting (M4–M5) adds live collections and mutation observers on top of the same hook.
- **Ownership/threading**: a `Document` and its nodes belong to one thread at a time (see [architecture](../architecture.md#threading-model)); debug builds assert it.
- **Public surface**: M1 exposes a read-only view for hosts (`QuerySelector`, `GetElementById`, `TextContent`, attribute reads) plus a small mutation API. Script-facing wrappers (M4) are separate objects created on demand, so plain rendering never allocates them.

Rationale: B would save perhaps 30–40% of DOM memory, but the DOM is not where the memory goes for typical artifacts (fonts, glyph caches and the raster surface dominate; see [memory](18-memory-and-performance.md)). A keeps the code readable and maps 1:1 onto the DOM spec for scripting.
