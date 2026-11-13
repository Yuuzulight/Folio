# Opus audio over WebRTC (stage 3)

Part of #719 and #704, on top of remote audio over SRTP ([`webrtc-remote-media.md`](webrtc-remote-media.md)). WebRTC audio is now
48 kHz Opus when the peer offers it, instead of 8 kHz G.711. G.711 stays as the fallback.

Read the last two sections before relying on this: what is approximate, and where the code comes from.

## What works

| Piece | Where | Notes |
| --- | --- | --- |
| Range coder | `Opus/RangeCoder.cs` | RFC 6716 section 4.1 decoder and encoder. Reads past the end return zeros. |
| SILK decoder | `Opus/Silk*.cs` | NB, MB and WB, 10/20/40/60 ms, mono and stereo (mid/side prediction), LBRR (in-band FEC) frames, packet loss concealment and comfort noise, resampling to 48 kHz. Fixed point, as in the reference. |
| CELT decoder | `Opus/Celt*.cs`, `Opus/Mdct.cs` | 2.5, 5, 10 and 20 ms frames, long and short blocks, mono and stereo (intensity, dual, mid/side), pitch post-filter, anti-collapse, concealment. Float arithmetic. |
| Hybrid and mode switching | `Opus/OpusDecoder.cs` | SILK + CELT in one frame, redundancy frames, CELT/SILK transitions with the reference's cross-fades, multi-frame packets (codes 0-3, padding, VBR/CBR). |
| Encoder | `Opus/OpusEncoder.cs` | Mono, 48 kHz, CELT only, fullband, 20 ms, constant bitrate. See the limits below. |
| SDP | `RtcSdp`, controller | `opus/48000/2` is offered first, with `a=fmtp:111 minptime=10;useinbandfec=1;stereo=0;sprop-stereo=0`, ahead of PCMU and PCMA. `a=fmtp` is parsed (bounded: 32 entries of at most 256 characters) and written. An answer to an offer that lists Opus picks Opus; otherwise G.711 as before. `red/48000/2` and other `/2` codecs are not mistaken for Opus. |
| RTP receive | `RtcMediaEngine` | Opus packets go through the same reordering jitter buffer. A gap is concealed: one 20 ms frame of concealment per lost packet (at most 6 per gap), and the last lost frame is recovered from the in-band redundancy of the next packet when it has any. Decoded PCM is 48 kHz and reaches `IMediaAudioOutput` through the existing track path. Output is mono unless the peer declares `sprop-stereo=1`. |
| RTP send | `RtcMediaEngine` | A capture track resamples to 48 kHz (a 48 kHz capture passes through unchanged), cuts 20 ms frames, encodes at 32 kbit/s (81 byte packets) and sends them as SRTP. Marker bit on the first packet, timestamp step 960. |

Opus support adds no new budget: decode work is bounded by the existing 4000 packets/s and 64 packet jitter limits, a packet
describes at most 120 ms, and a decoder's memory is allocated once per stream (a few hundred kilobytes, never from stream data).

## Hostile input

Every length in the packet framing is checked against the packet before use. Entropy decoding clamps table indices to bounded
alphabets, the PVQ codeword decoder is only reachable with the (N, K) pairs that the allocation cache allows, and buffers are
sized once. `OpusDecoder.Decode` clamps its output to [-1, 1] and maps NaN to zero, because a corrupt stream can overshoot.
An unexpected exception inside the media plane only drops the packet and restarts that stream's decoder.

Tests: 6000 random packets plus every TOC byte with five body lengths (nothing throws, memory does not grow, the decoder still
works afterwards), bit-flipped and truncated real packets from all twelve vectors, undersized output buffers, malformed framing
for every frame-count code, hostile `a=fmtp` lines, and authenticated-but-junk Opus payloads through the media engine.

## Conformance

I fetched the official test vectors and ran them. Source: <https://opus-codec.org/testvectors/opus_testvectors.tar.gz>
(the RFC 8251 set `testvector01..12`, `.bit` input and `.dec` reference output).

- All twelve vectors, every packet (about 20 000 packets: SILK-only, hybrid and CELT-only, mono and stereo, all bandwidths,
  2.5 to 60 ms frames): the **final range coder state equals the reference decoder's after every packet**. That proves the whole
  bitstream parse (modes, allocation, energies, pulses, LBRR, redundancy) is exact.
- Output samples against the reference `.dec` files: SILK-only vectors 02, 03 and 04 are **bit-identical**. The rest sit at 65 to
  83 dB SNR: the reference output is fixed point and mine is the float CELT path with 16-bit rounding, so a bit-exact match is not
  expected there. That is far inside what `opus_compare` accepts, but I did not run `opus_compare` itself.
- The repository carries the first 150 packets of every vector (the first 20 with reference samples), zipped, in
  `tests/Folio.Scripting.Tests/OpusVectors.zip` (about 1 MB), and checks range state and samples on them in
  `TruncatedOfficialVectorsDecodeWithMatchingRangeCoderStateAndSamples`. Set `FOLIO_OPUS_VECTORS` to a directory with the complete
  files to run all packets with a 60 dB floor (`CompleteOfficialVectorsWhenAvailable`); I ran that here and it passes.

One thing the vectors taught me: their hybrid frames follow the band folding of libopus built with the 2017 decoder update
disabled. My first port followed the update and diverged in a few hybrid frames; the vectors pin the behaviour, so the port uses it.

There are no official vectors for encoders. The encoder is checked by closing the loop: its packets decode with my decoder, and
the encoder's range coder state equals the decoder's final range on every frame, at 16, 32 and 64 kbit/s, including silence,
clipping and non-finite input.

## What is approximate or missing

- **Encoder quality is below libopus.** It is mono CELT with one long MDCT per frame and a fixed allocation: no transient
  detection (attacks will pre-echo), no pitch pre-filter, no tf switching, no dynamic allocation, no stereo, no VBR, no band
  skipping, no SILK or hybrid (so no speech-optimised low bitrate mode), no in-band FEC, no DTX. Measured on a three tone test
  signal the decoded SNR is about 11 dB at 16 kbit/s, 17 dB at 32 and 23 dB at 64. CELT is perceptual, so SNR understates how it
  sounds, but I did not run a listening test or PESQ. Packets are valid and every decoder accepts them. Expect intelligible
  wideband voice, not libopus quality.
- **Concealment** is a port of the reference for SILK and for CELT noise substitution. CELT pitch repetition uses a plain
  normalised cross-correlation pitch search instead of the reference's multi-stage search, so it can pick a different lag on
  hard material. Neural concealment (DRED/LPCNet) is not implemented. The vectors contain no lost packets, so concealment is
  tested for plausibility (finite, bounded, non-silent for every mode), not against reference output.
- The decoder runs at 48 kHz only (what WebRTC negotiates). Decoder gain (`OPUS_SET_GAIN`) and soft clipping are not applied:
  output is hard-limited to [-1, 1].
- The sender does not react to receiver reports, loss, `maxaveragebitrate` or `maxplaybackrate`, and ignores `ptime`: it sends
  20 ms packets at 32 kbit/s.
- Interoperability with a real browser is untested, as in stage 2. The bitstreams are conformant, but I have not connected
  Folio to Chrome or Firefox.
- Recovery from in-band redundancy needs a sender that writes LBRR; this encoder does not, though the decoder handles it.
- Performance: managed float code, one decoder per remote stream, no SIMD. The vectors (about 20 000 packets) decode in about two
  seconds including file reading, but I did not benchmark phones.
- `ContentHostPolicyTests.ChildReceivesRemoteAudioOverSrtpThroughTheHostUdpBroker` expects the new offer (`111 0 8`) but could
  not run on my machine: every content-process test fails at the AppContainer setup (`AppContainerSecurity.Grant`) here, before
  it reaches any media code. The in-process engine tests cover the same path.

## Where the code comes from

The decoder is a translation to managed C# of the Opus reference implementation (RFC 6716, libopus 1.5.2), file by file: the
range coder, SILK, CELT (bands, PVQ, rate allocation, MDCT, post-filter, concealment) and the top-level decoder. The static
tables (SILK entropy and codebook tables, the CELT pulse cache and window) were extracted from the reference sources by script.
The FFT under the MDCT is a small mixed-radix transform written for this port. The encoder reuses the decoder's band coder; its
analysis-free front end is new. There is no third-party package and nothing native: the engine still ships no codec library, but
this code is a derivative of libopus, not an independent design.

Because it derives from libopus, the Opus files carry its BSD licence, reproduced here as required:

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

The source headers of the Opus files repeat the copyright holders of the corresponding reference files.
