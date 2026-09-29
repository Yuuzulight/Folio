Tree construction cases, written from https://html.spec.whatwg.org/multipage/parsing.html#tree-construction,
one file per insertion mode or algorithm.

Format:
  === case name
  input lines, with \uXXXX escapes
  ---
  the document: "#document" (plus "quirks" or "limited-quirks"), then one node per line, indented two
  spaces per level. Elements show their attributes in order; SVG and MathML elements are prefixed "svg:" and
  "math:"; a template's contents appear under "#content".
