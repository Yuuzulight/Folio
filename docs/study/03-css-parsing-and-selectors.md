# CSS parsing and selector matching

## What the spec requires

- [CSS Syntax 3](https://www.w3.org/TR/css-syntax-3/): tokenizer (idents, functions, at-keywords, hash, strings, urls, numbers, dimensions, percentages, unicode-range, CDO/CDC), then "consume a list of rules / declarations / component values". Error recovery is defined: a bad declaration is dropped up to the next `;`, a bad rule up to the matching `}`.
- [CSS Nesting 1](https://www.w3.org/TR/css-nesting-1/): nested style rules and `&`.
- [Selectors 4](https://www.w3.org/TR/selectors-4/): type/universal/class/id/attribute selectors, combinators (descendant, `>`, `+`, `~`), pseudo-classes, pseudo-elements, specificity.
- At-rules that matter for artifacts: `@media` ([Media Queries 4](https://www.w3.org/TR/mediaqueries-4/)), `@supports` ([Conditional 3](https://www.w3.org/TR/css-conditional-3/)), `@layer` ([Cascade 5](https://www.w3.org/TR/css-cascade-5/#layering)), `@font-face` ([Fonts 4](https://www.w3.org/TR/css-fonts-4/#font-face-rule)), `@import`, `@keyframes` ([Animations 1](https://www.w3.org/TR/css-animations-1/)), `@property` ([Properties and Values API](https://www.w3.org/TR/css-properties-values-api-1/)).
- Sources: `<style>` elements, `<link rel=stylesheet>` (through the host loader), `style=""` attributes, the UA stylesheet from [HTML's Rendering section](https://html.spec.whatwg.org/multipage/rendering.html).

## Hard parts and pitfalls

- **Property grammars** are the bulk of the work: ~150 properties, each with its own value grammar and shorthand expansion (`background`, `font`, `grid-template`, `border`, `flex`, `inset`, `place-*`).
- **`var()` defers parsing**: a declaration containing `var()` cannot be validated at parse time; it is stored as a token list and parsed after substitution at computed-value time ("invalid at computed-value time" → property becomes `unset`, not the previous declaration).
- **Unknown properties/values must be dropped silently** so the cascade falls back to an earlier declaration. AI output often includes vendor prefixes and newer properties; this is correct behaviour, not a failure.
- **Selector matching cost**: naive "for each element, for each rule, match right-to-left" is O(elements × rules). Utility-class CSS (thousands of single-class rules) makes this the hottest loop in style.
- **`:has()`** matches against descendants/siblings and invalidates upward; expensive and complex to invalidate.
- **`:nth-child()`** needs sibling indices; recomputing per match is quadratic.
- **Specificity of `:is()`/`:not()`/`:has()`** is the max of their arguments; `:where()` is zero.

## Options

**Parsing**

| Option | Pros | Cons |
|---|---|---|
| A. Spec tokenizer + generic component-value tree, then per-property typed parsers | Error recovery is exact; `var()` and unknown at-rules fall out naturally; one place per property | Two passes over each declaration |
| B. Hand-written direct parser straight to typed values | One pass | Error recovery diverges from spec; `var()` needs a second parser anyway |

**Matching**

| Option | Pros | Cons |
|---|---|---|
| A. Linear scan of all rules per element | Simplest | Too slow for utility-class stylesheets (thousands of rules) |
| B. Rule buckets by rightmost compound (id / class / tag / universal), plus an ancestor Bloom filter for descendant combinators | Standard technique; cuts candidates by 10–100×; filter rejects most descendant selectors without walking ancestors | Moderate complexity |
| C. Compile selectors to a decision tree / generated code | Fastest | Complex; poor fit for dynamic stylesheets |

## Decision

- **Parsing: option A.** Tokenizer over the source string → component values (small struct array, strings as spans into the source where possible) → rules. Each longhand has a typed parser registered in one generated table (`PropertyId` enum → parser, initial value, inherited flag, animation type, "affects layout / paint only" flag used by [invalidation](14-invalidation.md)). Shorthands expand to longhands at parse time. Declarations containing `var()` are stored as an unparsed token list for their longhand(s) (shorthands with `var()` store a "pending-substitution" value on each longhand, per spec).
- **Nesting** is supported by the parser; nested selectors are desugared into ordinary complex selectors (`&` → `:is(parent-list)`), so the matcher never sees nesting.
- **Selectors: option B.** Each rule is inserted into one bucket keyed by its rightmost compound: id atom, else first class atom, else tag atom, else the universal bucket. Matching an element: gather candidates from its id bucket, each class bucket, its tag bucket and the universal bucket; run a 2-hash ancestor Bloom filter check; then match right-to-left. Matched rules are sorted by (origin/layer/importance order, specificity, source order).
- **`:nth-*`**: sibling indices cached per parent during a restyle pass.
- **M1 selector set**: type, universal, class, id, all attribute operators (`=`, `~=`, `|=`, `^=`, `$=`, `*=`, `i`/`s` flags), all four combinators, `:root`, `:empty`, `:first-child`, `:last-child`, `:only-child`, `:nth-child(An+B)`, `:nth-last-child`, `:*-of-type`, `:not()`, `:is()`, `:where()`, `:link`, `:any-link`, `:hover`, `:active`, `:focus`, `:focus-visible`, `:focus-within`, `:checked`, `:disabled`, `:enabled`, `:lang()`, `:dir()`; pseudo-elements `::before`, `::after`, `::marker`, `::selection`, `::placeholder` (parsed; rendered when form controls arrive), `::first-line`/`::first-letter` (parsed, ignored until M2). User-action and form-state pseudo-classes match against state flags that stay false until the control updates them in M3. Unsupported pseudo-classes make the whole selector invalid, as the spec requires.
- **`:has()`**: matched from M2 (static documents only need it evaluated once per full restyle). Each argument is a relative selector anchored at the element being tested; a leading descendant or child combinator limits the search to that element's subtree (`:has(> x)` to its children), a leading sibling combinator to its following siblings and, when a descendant or child combinator follows, their subtrees. Arguments are unforgiving and `:has()` may not nest. There is no result cache yet: a full restyle costs one bounded walk per candidate element. Upward invalidation arrives with incremental restyle in M3: a DOM or state change marks the ancestors (and preceding siblings, for sibling anchors) of the changed element whose rules contain `:has()` for restyle, filtered by the features the `:has()` arguments mention. In M1 a selector with `:has()` was treated as invalid (dropped).
- **`@media`**: `width`, `height`, `min-/max-` forms and range syntax, `orientation`, `aspect-ratio`, `resolution`, `prefers-color-scheme` (from the host), `prefers-reduced-motion` (always `reduce` in M1, where animations snap to their end state; from the OS setting once animations run in M2), `hover`/`pointer` (fine), media types `screen`/`all` true, `print` false.
- **`@supports`**: answered from the same property/value parser, so it is truthful by construction.
- **`@layer`**: supported (compiled utility-class CSS depends on it).
- **`@import`**: through the host loader, depth-limited (default 4), cycles detected.
- **`@keyframes`**: parsed and stored; see [cascade](04-cascade-and-computed-values.md) for how M1 and M2 use them.
- **`@property`**: a rule registers only with a valid `syntax` string and `inherits`, plus an initial value that matches the syntax and uses no relative units or `var()` (unless the syntax is `*`). Registered values compute for their syntax once the element's font size and colour are known (lengths to px, angles to deg, times to s, colours to `rgb()`), so descendants inherit the computed value; a value that does not match is invalid at computed-value time and falls back to the inherited or initial value. Interpolating registered values arrives with the animation timeline.
- Every dropped declaration or rule can be reported as a diagnostic ("unsupported property `x`"), which Mana can feed back to the model that wrote the artifact.
