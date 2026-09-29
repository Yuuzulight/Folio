Box tree cases, written from CSS Display 3, CSS 2.2 section 9.2, CSS Text 3 section 4, CSS Lists 3, CSS Content 3 and
CSS Tables 3 section 3.

Format: "=== name", the HTML document, "---", then the tree from the root element's box: one box per line as
"<kind> <element or ::pseudo or (anonymous)> [float] [absolute]", children indented two spaces. A block container
with inline content lists its items instead: "text", <box> and </box> (with + for the continuation parts of an
inline split by a block), br, wbr, and atomic, floated or out-of-flow boxes in place. Outside list markers appear
as marker "text".
