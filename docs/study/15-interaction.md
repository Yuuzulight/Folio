# Hit testing, events, focus, scrolling and text selection

## What the specs require

- [CSSOM View](https://www.w3.org/TR/cssom-view-1/): `elementFromPoint`, scrolling APIs and the scrolling box model, `scroll-behavior`, `scrollIntoView`.
- [UI Events](https://www.w3.org/TR/uievents/), [Pointer Events 3](https://www.w3.org/TR/pointerevents3/), [DOM §2 Events](https://dom.spec.whatwg.org/#events) (dispatch: capture → target → bubble, `preventDefault`), [HTML activation behaviour](https://html.spec.whatwg.org/multipage/interaction.html#activation), [focus](https://html.spec.whatwg.org/multipage/interaction.html#focus) and [sequential focus navigation](https://html.spec.whatwg.org/multipage/interaction.html#sequential-focus-navigation) (`tabindex`).
- [Selection API](https://www.w3.org/TR/selection-api/) and [DOM Ranges](https://dom.spec.whatwg.org/#ranges); [CSS UI 4](https://www.w3.org/TR/css-ui-4/) (`cursor`, `user-select`, `pointer-events`, `caret-color`); [CSS Pseudo 4 `::selection`](https://www.w3.org/TR/css-pseudo-4/#highlight-pseudos); [CSS Overscroll 1](https://www.w3.org/TR/css-overscroll-1/) (`overscroll-behavior`); [HTML `innerText` serialisation](https://html.spec.whatwg.org/multipage/dom.html#the-innertext-idl-attribute) (what "copy" should produce).
- Forms: [HTML §4.10](https://html.spec.whatwg.org/multipage/forms.html) (controls, `value`, `checked`, constraint validation) and [text editing](https://html.spec.whatwg.org/multipage/interaction.html#editing).

## Hard parts and pitfalls

- **Hit testing must match paint order exactly**, including stacking contexts, clips from the containing-block chain, transforms (inverse mapping), scroll offsets, sticky offsets, `pointer-events: none` and `visibility: hidden`.
- **Point → text position**: a hit inside a glyph run must map to a DOM (node, UTF-16 offset), respecting grapheme clusters and bidi (the visual left half of an RTL glyph is the logical *end*).
- **Selecting outside text**: dragging into padding, margins or between lines must snap to the nearest caret position sensibly.
- **Copy**: selected text must come out with line breaks between blocks, tabs between table cells, collapsed whitespace removed, but `pre` content preserved.
- **Wheel routing**: the innermost scroll container under the pointer that can still scroll in that direction takes the delta; at its edge the rest chains to the parent unless `overscroll-behavior` stops it. Touchpads send small high-precision deltas.
- **IME and text input**: composition, caret placement, candidate windows; with WinForms, a custom-drawn control must handle the IME messages itself.
- **Accessibility**: a custom-drawn control is invisible to screen readers unless it exposes an accessibility tree.

## Options

| Option | Pros | Cons |
|---|---|---|
| A. Hit test via the display list (items carry node ids) | Uses exactly what was painted | Display list must carry DOM references; transforms/clips re-derived per item |
| B. Hit test by walking the stacking tree in reverse paint order over fragments | Shares the ordering code with painting; fragments already map to boxes and nodes; natural place for text-position mapping | Must keep painting and hit testing on the same traversal code |

| Option | Pros | Cons |
|---|---|---|
| A. Native WinForms child controls for form fields (real `TextBox` etc.) | Editing, IME and accessibility for free | Can't be styled by CSS, don't scroll/clip/transform with content, can't be captured in headless rendering |
| B. Folio draws and edits its own form controls; uses system services (IME, clipboard, accessibility API) through the host control | Fully styleable; consistent in headless output | Folio implements caret, editing and selection inside controls |

## Decision

- **Hit testing: option B.** One traversal (`PaintOrderWalker`) serves both the display-list builder and the hit tester (reverse order). Result: `HitResult { Node, Box, Fragment, LocalPoint, TextPosition? }`. `elementFromPoint` (M4) uses the same call.
- **Input path**: the WinForms control converts mouse/keyboard/wheel/IME messages into Folio input events and passes them to one `InputRouter`. In M3 the router runs **default actions** directly (focus, link activation, selection, scrolling, form editing). In M4 the router first dispatches DOM events to scripts and runs the default action only if not cancelled — scripting inserts one step, the rest is unchanged.
- **M1 control**: shows the document, relayouts on resize, scrolls the root viewport with the mouse wheel and a scrollbar, and makes links clickable. Everything else below is M3.
- **Scrolling (M3)**: nested scroll containers, wheel routing and chaining with `overscroll-behavior`, high-precision touchpad deltas, horizontal wheel, keyboard scrolling (arrows, PageUp/PageDown, Home/End, Space) of the focused or hovered scroller, scrollbar dragging, `scroll-behavior: smooth`, fragment links inside nested scroll containers scroll every container needed.
- **Links (M1)**: clicking a link raises `LinkActivated(url, target, modifiers)` on the control, and the host decides what happens. If the host does not handle it, the control opens `http`, `https` and `mailto` URLs in the system browser and ignores every other scheme. Fragment links (`#id`) scroll the page in place. Folio itself never navigates; `javascript:` URLs are ignored. Keyboard activation of links arrives with focus in M3.
- **Hover and cursor (M3)**: hover state updates on mouse move with [invalidation](14-invalidation.md) limited to affected elements; `cursor` maps to Windows cursors; `title` shows a tooltip.
- **Selection (M3)**: a DOM `Range` (anchor, focus). Drag selects by caret positions; double-click selects a word (UAX #29 word boundaries); triple-click selects a paragraph (block); Shift+click extends; Ctrl+A selects all; `user-select: none/text/all/contain` honoured; highlight painted per text fragment with bidi-aware rectangles and `::selection` colours.
- **Copy (M3)**: Ctrl+C / context menu puts plain text (the `innerText` algorithm applied to the range) and an HTML fragment on the clipboard (system-provided).
- **Focus (M3)**: sequential navigation over links, form controls and `tabindex`; `:focus-visible` follows keyboard use; UA focus ring via `outline`.
- **Forms: option B (M3)**: text inputs and `textarea` with caret, selection, editing commands, undo, IME composition (via the host control's IME handling), `checkbox`, `radio`, `range`, `select` (drop-down drawn by Folio in a borderless popup window so it can extend past the control), `button`, `details`/`summary` toggling, `color`/`date` inputs as text fields. Forms never submit anywhere: submission raises `FormSubmitted(form data)` on the control for the host.
- **Accessibility (M3)**: the control exposes a basic accessibility tree (document, headings, links, lists, tables, text, form controls with names and values) through WinForms accessibility objects backed by the system's UI Automation.
