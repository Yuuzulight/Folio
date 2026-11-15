#!/usr/bin/env python3
"""Builds tests/Folio.Scripting.Tests/OpusVectors.zip from the RFC 8251 Opus test vectors.

Source: https://opus-codec.org/static/testvectors/opus_testvectors-rfc8251.tar.gz
        SHA-256 6b26a22f9ba87b2b836906a9bb7afec5f8e54d49553b1200382520ee6fedfa55 (extracts to opus_newvectors/)

Recipe: for each testvector01..12 keep the first 150 packets of the .bit file unchanged (8 byte header per packet: length and
final range, big endian), and the first 20 packets' worth of samples of the .dec reference output (stereo, 16 bit little endian,
48 kHz; packet durations are taken from the TOC byte and the frame count code). Entries are deflated at level 9.
Usage: make-opus-vectors.py <extracted opus_newvectors dir> <output zip>
"""
import struct, sys, zipfile

def frame_samples(toc):
    if toc & 0x80: return (48000 << ((toc >> 3) & 3)) // 400
    if (toc & 0x60) == 0x60: return 480 if toc & 8 == 0 else 960
    s = (toc >> 3) & 3
    return 2880 if s == 3 else (48000 << s) // 100

def frame_count(p):
    c = p[0] & 3
    return 1 if c == 0 else 2 if c in (1, 2) else p[1] & 0x3f

src, out = sys.argv[1], sys.argv[2]
PACKETS, REFERENCE_PACKETS = 150, 20
with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for v in range(1, 13):
        bit = open(f'{src}/testvector{v:02d}.bit', 'rb').read()
        dec = open(f'{src}/testvector{v:02d}.dec', 'rb').read()
        pos = n = samples = ref_samples = 0
        keep = bytearray()
        while pos + 8 <= len(bit) and n < PACKETS:
            length, _ = struct.unpack('>II', bit[pos:pos + 8])
            packet = bit[pos + 8:pos + 8 + length]
            keep += bit[pos:pos + 8 + length]
            pos += 8 + length
            n += 1
            samples += frame_samples(packet[0]) * frame_count(packet)
            if n == REFERENCE_PACKETS: ref_samples = samples
        z.writestr(f'testvector{v:02d}.bit', bytes(keep))
        z.writestr(f'testvector{v:02d}.dec', dec[:ref_samples * 4])
