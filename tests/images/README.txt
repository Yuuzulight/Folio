Small generated images for the decoder tests.

PNG (13x11): every colour type and bit depth, tRNS, Adam7 interlacing and image data split over several IDAT
chunks; rows cycle through all five filter types.
JPEG (37x29, so MCUs are partial): baseline 4:4:4, 4:2:2 and 4:2:0, greyscale, progressive, restart intervals and
optimised Huffman tables.

The expected pixels come from SkiaSharp's codecs when the tests run (Folio.RenderTests/DecoderReferenceTests.cs).
