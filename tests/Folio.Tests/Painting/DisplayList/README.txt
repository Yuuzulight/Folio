Display list cases, written from CSS 2.2 Appendix E (paint order) and section 9.9 (z-index), CSS Backgrounds 3
(canvas background, background-clip, border radii and their overlap rule), CSS Overflow 3 and CSS Color 4 (opacity).

Format: "=== name", the HTML document (laid out in an 800x600 viewport), "---", then one line per display item:
"fill <x>,<y> <w>x<h>[ radius ...] <colour>", "border <rect>[ radius ...] <width> <style> <colour>" (or the four sides
top / right / bottom / left when they differ), "clip <rect>[ radius ...]", "opacity <value>", "decoration <rect> <line style> <colour>[ skip-ink]" (the rectangle's height is the line's thickness; skip-ink
when the line leaves gaps around the glyphs)
"image <rect> <pixel width>x<pixel height> <smooth|pixelated>", "shadow <rect>[ radius ...] <colour> blur <standard deviation> <inside|outside> <clip rect>", and "pop". Glyphs
drawn blurred (text shadows) end with "blur <standard deviation>". A radius is
one value for equal circular corners, "x,y" for equal elliptical ones, or four corners from the top left.
