# Third-party notices

## VP8 decoder (RFC 6386 reference decoder)

`src/Folio.Scripting/Rtc/Vp8/` (`Vp8Decoder*.cs`, `Vp8Tables.cs`) is a C# port of the reference decoder that ships with
RFC 6386 ("dixie", RFC 6386 section 20). The probability, quantizer and mode tables in `Vp8Tables.cs` were extracted from
the RFC text by script, and the prediction, transform, token and loop-filter code follows the reference source closely.
The RFC distributes that source under the following license (RFC 6386 section 20.26), reproduced in full:

```
Copyright (c) 2010, 2011, Google Inc.  All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:

  * Redistributions of source code must retain the above copyright
    notice, this list of conditions and the following disclaimer.

  * Redistributions in binary form must reproduce the above copyright
    notice, this list of conditions and the following disclaimer in
    the documentation and/or other materials provided with the
    distribution.

  * Neither the name of Google nor the names of its contributors may
    be used to endorse or promote products derived from this software
    without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED.  IN NO EVENT SHALL THE COPYRIGHT
HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT,
INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING,
BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS
OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED
AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT
LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY
WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
POSSIBILITY OF SUCH DAMAGE.
```

The RFC also carries an additional patent grant (section 20.27, the PATENTS file) for the reference source; it is not
reproduced here. See RFC 6386 for it.

## VP8 conformance test vectors

`tests/Folio.Scripting.Tests/Vp8Vectors/` holds `vp80-00-comprehensive-001.ivf` to `018.ivf` and their `.ivf.md5` files
(the MD5 of each decoded I420 frame). They were downloaded unmodified from the WebM project's libvpx test data:
`https://storage.googleapis.com/downloads.webmproject.org/test_data/libvpx/`. They are test inputs only, are not part of any
package, and are copyright the WebM project / Google. I did not find a separate license file in that download location; they
are published as test data of libvpx, which is BSD-3-Clause licensed.

## Opus codec (RFC 6716, libopus 1.5.2)

`src/Folio.Scripting/Rtc/Opus/` is a C# translation of the Opus reference implementation (libopus 1.5.2, the RFC 6716
reference decoder): the range coder, SILK, CELT (bands, PVQ, rate allocation, MDCT post-processing, concealment) and the
top-level decoder. The tables in `SilkTables.cs` and `CeltModeTables.cs` were extracted from the libopus sources by script. The
encoder reuses that band coder. The licence of libopus (its `COPYING` file, verbatim) follows:

```
Copyright 2001-2023 Xiph.Org, Skype Limited, Octasic,
                    Jean-Marc Valin, Timothy B. Terriberry,
                    CSIRO, Gregory Maxwell, Mark Borgerding,
                    Erik de Castro Lopo, Mozilla, Amazon

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:

- Redistributions of source code must retain the above copyright
notice, this list of conditions and the following disclaimer.

- Redistributions in binary form must reproduce the above copyright
notice, this list of conditions and the following disclaimer in the
documentation and/or other materials provided with the distribution.

- Neither the name of Internet Society, IETF or IETF Trust, nor the
names of specific contributors, may be used to endorse or promote
products derived from this software without specific prior written
permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
``AS IS'' AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER
OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL,
EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR
PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

Opus is subject to the royalty-free patent licenses which are
specified at:

Xiph.Org Foundation:
https://datatracker.ietf.org/ipr/1524/

Microsoft Corporation:
https://datatracker.ietf.org/ipr/1914/

Broadcom Corporation:
https://datatracker.ietf.org/ipr/1526/
```

The patent licences named in that file are not reproduced here; see the URLs it gives.

## Opus conformance test vectors

`tests/Folio.Scripting.Tests/OpusVectors.zip` holds truncated copies of `testvector01.bit` to `12.bit` and their `.dec`
reference outputs from the RFC 8251 set, downloaded from
`https://opus-codec.org/static/testvectors/opus_testvectors-rfc8251.tar.gz` (SHA-256
`6b26a22f9ba87b2b836906a9bb7afec5f8e54d49553b1200382520ee6fedfa55`). `tools/make-opus-vectors.py` records the exact truncation.
They are test inputs only, are not part of any package, and are published by the Opus project; I did not find a separate
licence file with them.

## H.264 decoder and test streams

`src/Folio.Scripting/Rtc/H264*.cs` is Folio's managed decoder for the ITU-T H.264 / ISO/IEC 14496-10 Constrained Baseline
subset and its RFC 6184 RTP depacketizer. It links no codec library and carries no ported codec source notice.

`tests/Folio.Scripting.Tests/H264Fixtures.cs` embeds a few short test streams. They were encoded with x264 (CAVLC, no B
frames, so Constrained Baseline); the x264 version string and options sit in each stream's SEI. The expected output hashes
were produced by decoding the same streams with FFmpeg's H.264 decoder. x264 and FFmpeg are not part of this repository, are
not distributed with it, and were used only to generate those fixtures. The streams are test inputs only and are not part of
any package.

H.264 is covered by patents licensed through pools such as Via LA (formerly MPEG LA). Folio does not grant a licence under
them; anyone distributing a product that decodes H.264 is responsible for the licences that apply to them.
