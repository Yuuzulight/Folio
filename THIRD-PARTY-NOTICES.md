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
