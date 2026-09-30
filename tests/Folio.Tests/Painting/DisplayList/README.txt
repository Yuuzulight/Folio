Display list cases, written from CSS 2.2 Appendix E (paint order) and section 9.9 (z-index), CSS Backgrounds 3
(canvas background, background-clip, border radii and their overlap rule), CSS Overflow 3 and CSS Color 4 (opacity).

Format: "=== name", the HTML document (laid out in an 800x600 viewport), "---", then one line per display item:
"fill <x>,<y> <w>x<h>[ radius ...] <colour>", "border <rect>[ radius ...] <width> <style> <colour>" (or the four sides
top / right / bottom / left when they differ), "clip <rect>[ radius ...]", "opacity <value>", "decoration <rect> <line style> <colour>" (the rectangle's height is the line's thickness)
and "pop". A radius is
one value for equal circular corners, "x,y" for equal elliptical ones, or four corners from the top left.
