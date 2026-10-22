Small generated images for the decoder tests.

PNG (13x11): every colour type and bit depth, tRNS, Adam7 interlacing and image data split over several IDAT
chunks; rows cycle through all five filter types.
JPEG (37x29, so MCUs are partial): baseline 4:4:4, 4:2:2 and 4:2:0, greyscale, progressive, restart intervals and
optimised Huffman tables.

The expected pixels come from SkiaSharp's codecs when the tests run (Folio.RenderTests/DecoderReferenceTests.cs).

GIF (13x11 unless named otherwise): GIF87a with a global palette, interlaced with a local palette, transparent, interlaced and
transparent, three frames (the first a smaller, offset frame with its own palette), two colours (9x9), a 64x64 noise image that fills
the LZW code table, one pixel, and a truncated file. The first frame is the picture, as with SkiaSharp's codec.

WebP (lossless, `lossless-*.webp`): made by a real encoder from generated pictures at different effort levels so that every predictor,
the cross-colour, subtract-green and colour-indexing transforms, the colour cache, meta prefix groups and backward references
are used: two, sixteen and two hundred colours, alpha, noise, a Mandelbrot set, blurred noise, hard edges, cells, colour
bars, a test pattern, one pixel, a single row and a single column, and a three-frame animation (the first frame is the picture).

WebP (lossy, `lossy-*.webp`): VP8 key frames from two encoders so that every intra mode, the normal and simple loop filters, one, two,
four and eight token partitions, skipped macroblocks, segments, the loop-filter deltas, odd sizes down to one pixel, the lowest and
highest quality, and an alpha plane (ALPH, lossless-compressed and filtered) are used.
