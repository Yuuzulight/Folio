Display list cases, written from CSS 2.2 Appendix E (paint order) and section 9.9 (z-index), CSS Backgrounds 3
(canvas background, background-clip, border radii and their overlap rule), CSS Overflow 3, CSS Color 4 (opacity), CSS Transforms 1 and 2, Filter Effects 1 and 2, Compositing 2, CSS Masking 1 and CSS Shapes 1.

Format: "=== name", the HTML document (laid out in an 800x600 viewport), "---", then one line per display item:
"fill <x>,<y> <w>x<h>[ radius ...] <colour>[ blend <mode>]", "border <rect>[ radius ...] <width> <style> <colour>" (or the four sides
top / right / bottom / left when they differ), "clip <rect>[ radius ...]" or "clip path[ evenodd] <commands>" (M, L, C with their points, and Z), a layer as "[opacity <value>][ filter <primitives>][ backdrop <clip rect> <primitives>][ blend <mode>]" (or "layer"
for a plain isolated group)
(primitives: "blur(<standard deviation>)", "shadow(<dx>,<dy>,<standard deviation>,<r>,<g>,<b>,<a>)" and
"matrix(<20 values>)", colours from 0 to 1), "transform <a>,<b>,<c>,<d>,<e>,<f>" (as matrix(), in canvas coordinates), "decoration <rect> <line style> <colour>[ skip-ink]" (the rectangle's height is the line's thickness; skip-ink
when the line leaves gaps around the glyphs)
"image <rect> <pixel width>x<pixel height> <smooth|pixelated>", "fill path[ evenodd] <commands> <colour>",
"stroke path <commands> <width>[ <round|square> cap][ <round|bevel> join| miter <limit other than 4>][ dashes <lengths>[ offset <length>]] <colour>", "shadow <rect>[ radius ...] <colour> blur <standard deviation> <inside|outside> <clip rect>", and "pop". Glyphs
drawn blurred (text shadows) end with "blur <standard deviation>". A radius is
one value for equal circular corners, "x,y" for equal elliptical ones, or four corners from the top left.
