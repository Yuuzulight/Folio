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
| RTP receive | `RtcMediaEngine` | Opus packets go through the same reordering jitter buffer. A gap is concealed with frames as long as the packet before the gap (at most 3 frames, at most 120 ms), and the last lost frame is recovered from the in-band redundancy of the next packet when its duration matches. Decoded PCM is 48 kHz and reaches `IMediaAudioOutput` through the existing track path. Output is mono unless the peer declares `sprop-stereo=1`. |
| RTP send | `RtcMediaEngine` | A capture track resamples to 48 kHz (a 48 kHz capture passes through unchanged), cuts 20 ms frames, encodes at 32 kbit/s (81 byte packets) and sends them as SRTP. Marker bit on the first packet, timestamp step 960. |

## CPU ceiling

Decoding is the expensive part of a hostile stream, so it has its own limits on top of the existing 4000 packets/s and 64 packet
jitter limits:

- decoded plus concealed audio per stream is capped at 1.25 times real time (token bucket, burst 1500 ms, so a full jitter buffer
  still plays); a packet that does not fit is dropped and counted in `PacketsDropped`;
- one concealment pass per gap, at most 3 frames and 120 ms;
- at most 16 packets are decoded per stream per pass under the engine lock; the rest of a buffered burst drains on the next
  datagram or tick.

Worst case I measured (release build, random bytes with a fixed TOC, which exercises every symbol path; stereo 48 kHz output, one
core of a desktop CPU, JIT warm-up included): 24 ms of CPU per second of audio for 1275 byte CELT 20 ms packets, 10 ms for CELT
2.5 ms, 10 ms for 60 ms SILK wideband, 8 ms for hybrid, 4 ms per second of audio for concealment. At the cap that is at most about
30 ms of CPU per second per stream, and with the existing limit of 16 tracks per document at most about half a core
(16 x 1.25 x 24 ms) if every track is hostile. I did not measure a phone. A decoder's memory is allocated once per stream
(a few hundred kilobytes, never sized from stream data); each packet also copies its own bytes once.

## Hostile input

Every length in the packet framing is checked against the packet before use. Entropy decoding clamps table indices to bounded
alphabets, the PVQ codeword decoder is only reachable with the (N, K) pairs that the allocation cache allows, and buffers are
sized once. `OpusDecoder.Decode` clamps its output to [-1, 1] and maps NaN to zero, because a corrupt stream can overshoot.
An unexpected exception inside the media plane only drops the packet and restarts that stream's decoder.

Tests: 6000 random packets plus every TOC byte with five body lengths (nothing throws, the run is deterministic, and after a
`Reset()` the decoder behaves like a new one), bit-flipped and truncated real packets from all twelve vectors, undersized output buffers, malformed framing
for every frame-count code, hostile `a=fmtp` lines, and authenticated-but-junk Opus payloads through the media engine.

## Conformance

Source: the RFC 8251 set, <https://opus-codec.org/static/testvectors/opus_testvectors-rfc8251.tar.gz> (SHA-256
`6b26a22f9ba87b2b836906a9bb7afec5f8e54d49553b1200382520ee6fedfa55`), `testvector01..12` with their `.bit` input and `.dec`
reference output. The bitstreams are the same as in the original RFC 6716 set (`opus_testvectors.tar.gz`, SHA-256
`94ac78ca4f74c4e43bc9fe4ec1ad0aa36f38ab90f45b0727c40dd1e96096e767`), but the reference outputs differ. My first run used the
original set and appeared to need a different hybrid band folding; against the RFC 8251 set the 2017 folding behaviour that
libopus 1.5.2 has is the right one (`i == start + 1` in the lowband offset and the special hybrid folding), and that is what the
port does.

Results of the opt-in run of the complete files (`FOLIO_OPUS_VECTORS`; about 20 000 packets covering SILK-only, hybrid and
CELT-only, mono and stereo, all bandwidths, 2.5 to 60 ms frames). These numbers come from that run only, not from the checked-in
subset:

- the final range coder state equals the reference decoder's after every packet. That is strong evidence the entropy decoding
  and parse are right; it says nothing about the synthesis, so it does not by itself show the output is exact;
- output against the reference `.dec`: SILK-only vectors 02, 03 and 04 are bit-identical; the others are at 65 to 83 dB SNR
  (vector 05 65.6, 06 66.1, 07 66.3, 08 and 09 66.0, 10 74.4, 11 82.7, 12 73.1, 01 79.8). The reference is fixed point and mine is
  the float path, so bit-exactness is not expected there. I did not run `opus_compare`.

What the repository checks on every run: the first 150 packets of every vector and the reference samples of the first 20, in
`tests/Folio.Scripting.Tests/OpusVectors.zip` (about 1 MB), asserting identical final range on every packet and an output error
of at most max(4 LSB, 1 percent of the signal RMS) (`TruncatedOfficialVectorsDecodeWithMatchingRangeCoderStateAndSamples`).
The zip is built by `tools/make-opus-vectors.py` from the extracted tarball; the recipe (first 150 packets of each `.bit`
unchanged, `.dec` cut after the samples of the first 20 packets, deflate level 9) is in the script header.
`CompleteOfficialVectorsWhenAvailable` runs the complete files with a 60 dB floor when `FOLIO_OPUS_VECTORS` points at the
extracted `opus_newvectors` directory; when it is unset the test is reported as skipped, not passed.

There are no official vectors for encoders. The encoder is checked by closing the loop: its packets decode with my decoder and
the encoder's range coder state equals the decoder's final range on every frame, at 16, 32 and 64 kbit/s, including silence,
clipping and non-finite input. That shows self-consistency; I have not decoded its output with libopus or any other decoder.

## What is approximate or missing

- **Encoder quality is below libopus.** It is mono CELT with one long MDCT per frame and a fixed allocation: no transient
  detection (attacks will pre-echo), no pitch pre-filter, no tf switching, no dynamic allocation, no stereo, no VBR, no band
  skipping, no SILK or hybrid (so no speech-optimised low bitrate mode), no in-band FEC, no DTX. Measured on a three tone test
  signal the decoded SNR is about 11 dB at 16 kbit/s, 17 dB at 32 and 23 dB at 64. CELT is perceptual, so SNR understates how it
  sounds, but I did not run a listening test or PESQ. They follow the RFC 6716 packet format and my decoder accepts them; I have
  not tried another decoder. Expect intelligible wideband voice at best, not libopus quality.
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

Because it derives from libopus, the Opus files carry its BSD licence. The full text (checked equal to libopus 1.5.2's `COPYING`) is in
[`THIRD-PARTY-NOTICES.md`](../THIRD-PARTY-NOTICES.md), which is packed into every NuGet package (`Directory.Build.props`). The source
headers of the Opus files repeat the copyright holders of the corresponding reference files.
