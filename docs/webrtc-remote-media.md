# Remote audio over WebRTC (stage 2)

Part of #719 and #704. It builds on the host-mediated UDP broker ([`webrtc-host-udp.md`](webrtc-host-udp.md)): the content
process still owns no socket, and every media datagram crosses the same `IIceTransport` seam as ICE and DTLS.

This document says what is implemented, what was checked, and what is not there. Read the last section before relying on it.

## What works

An audio-only, G.711 peer connection with a remote peer, end to end through the sandbox:

```
remote peer <-UDP-> host UdpBroker <-UdpCommand/UdpEvent-> MediatedIceTransport -> IceAgent
    -> RtcPeerConnectionController: first byte 20..63 -> DTLS, 128..191 -> RtcMediaEngine (RFC 7983)
    -> SrtpTransport (keys from the DTLS exporter) -> RTP demux -> jitter buffer -> G.711 -> PCM frames
```

| Piece | Where | Notes |
| --- | --- | --- |
| SRTP / SRTCP | `SrtpTransport` | AEAD_AES_128_GCM per RFC 7714 with the RFC 3711 KDF. Per-SSRC rollover counter and 64-packet replay window. Header extensions are authenticated. |
| DTLS exporter | `DtlsTransport` | The key block is now 2 x (16 + 12) = 56 bytes (client key, server key, client salt, server salt). It used to split a 60 byte block with 14 byte salts, which only interoperated with itself. |
| SDP | `RtcSdp`, controller | `m=audio`/`m=video` with payload types, `a=rtpmap`, direction, `a=rtcp-mux`, `a=ssrc`, BUNDLE (rejected sections leave the group). An answer mirrors the offer's sections, order and mids; sections Folio cannot serve get port 0. |
| RTP/RTCP engine | `RtcMediaEngine` | Demux by payload type and SSRC, reordering jitter buffer, sender and receiver reports (SR/RR with loss, jitter, LSR/DLSR), SDES CNAME, BYE. |
| Codec | `G711` | PCMU (PT 0) and PCMA (PT 8), 8 kHz mono, 20 ms packets. |
| Receive to host | `WebRtcBindingState` | `RTCPeerConnection.ontrack` fires with a `MediaStreamTrack` and a one-track `MediaStream`. Decoded PCM goes to `IMediaAudioOutput`. |
| Send from capture | `WebRtcBindingState` | `addTrack(microphoneTrack)` attaches the capture reader of that track; a 20 ms pump resamples to 8 kHz and emits SRTP. |

## Limits and budgets

There are two layers, and only the second is a security boundary.

**Content process (defence in depth, not trusted).** `RtcMediaBudget` lives in the content process, one instance per
document shared by its peer connections. A compromised child can ignore it, so it bounds work and memory for honest
documents and malformed traffic, nothing more:

- live tracks (remote and local): 16
- packets per second: 4000 in each direction; bytes per second: 4 MiB in each direction (one second of burst)

**Host (the real limits).** The `UdpBroker` applies its own limits to every datagram regardless of what the child
believes: 1000 packets per second, a 128 KiB burst, and 1500-byte datagrams, plus the destination and source address
policy and socket quotas described in [`webrtc-host-udp.md`](webrtc-host-udp.md). The child budgets above are
therefore looser than the host's on packets (4000 vs 1000 per second); the host figure is the one that holds. The
host does not parse RTP, so it cannot enforce track counts.

Per engine, in the content process:

- a datagram over 2048 bytes, an RTP payload over 1200 bytes, malformed headers, bad padding, unknown payload types,
  unauthenticated, replayed or over-budget packets are dropped and counted, never queued
- SRTP stream state is created only after a packet authenticates, is capped at 32 SSRCs per direction, and a BYE
  frees that SSRC's RTP state
- jitter buffer: 64 packets per stream. On overflow the oldest packet is dropped and the hole is counted as lost.
  A hole is otherwise skipped after 60 ms
- at most 16 remote streams, 16 media sections, 32 payload types and 8 SSRCs per section, 16 RTCP packets per compound
- session descriptions over 64 KiB or with more than 64 candidates are refused, and a description that fails to
  parse makes `setLocalDescription`/`setRemoteDescription` reject with `OperationError` without changing state
- PCMU and PCMA are accepted only at 8000 Hz mono
- SRTP is created once per connection (a second context would reset the rollover counters and SRTCP index);
  derived session keys and the DTLS master key, salt and exporter block are zeroed on dispose where the runtime
  allows, and nothing in the media path logs keys, salts or payloads
- the DTLS client checks that the server chose exactly the SRTP profile it offered

**Autoplay gate.** Whether decoded remote audio is submitted needs `Media`, `Sound` and `AllowSoundAutoplay`. That
check is made in the content process, against the child's own copy of the capabilities. The host re-checks only
`Sound`: it stops the media output when `Sound` is revoked (`ContentProcessClient`), and it does not look at `Media` or
`AllowSoundAutoplay` for WebRTC audio. A compromised child could therefore play remote audio without autoplay
permission as long as `Sound` is granted. Capture still needs its own microphone grant, and the track must be live and
enabled.

## Tests

- `RtcMediaTests` (no sockets, no sleeping): SRTP/SRTCP round trips, tamper, replay, rollover, per-SSRC windows, stream
  cap; G.711; RTCP build/parse and hostile input; SDP parse bounds and round trip; a two-engine loopback through fake
  transports on a manual clock covering PCMU and PCMA, reordering, loss and the 60 ms wait, receiver and sender reports
  (including DLSR), BYE, track/packet/byte budgets, garbage and authenticated-but-unusable packets, jitter bound, and
  video codecs Folio cannot decode (VP9) being negotiated but not decoded.
- `SrtpMatchesRfc7714KnownAnswerVectors*`: the RFC 7714 section 16.1 (RTP) and 17.1 (RTCP) AEAD_AES_128_GCM vectors, byte for byte.
- `ContentHostPolicyTests.ChildReceivesRemoteAudioOverSrtpThroughTheHostUdpBroker`: a real content-process child (no
  socket) negotiates a `recvonly` audio transceiver with an out-of-process controller, `ontrack` fires in script, SRTP
  arrives through the host broker and the child's SRTCP receiver report comes back through it. This runs over real
  loopback sockets and real time.

## What is not done

I did not overclaim these:

- **Sending video.** Not supported yet. `addTrack(videoTrack)`, `addTransceiver(videoTrack)` and a `sendonly` video transceiver throw `NotSupportedError` on a network peer connection (in-document pairs are unchanged). There is no VP8 encoder. Our offers carry video as `recvonly` and answers as `recvonly` or `inactive`.
- **Opus.** Added in stage 3 ([`webrtc-opus.md`](webrtc-opus.md)): 48 kHz Opus is preferred and G.711 remains the fallback.
- **Browser interoperability is untested.** The SRTP packet format matches the RFC 7714 vectors and the DTLS key layout
  follows RFC 5764, but I have only run Folio against Folio end to end. The first run against a browser may expose a
  mismatch. The client-side `use_srtp` validation has no negative test yet.
- **Only AEAD_AES_128_GCM** is offered in `use_srtp`. A peer that supports only the AES-CM/HMAC-SHA1 profiles cannot
  connect media (data channels are unaffected).
- No MID header extension, no SSRC-less demux, no simulcast, no RTX/FEC, no congestion control or REMB. (Video NACK/PLI/FIR exist: see Remote video below. Audio has none.)
- The jitter buffer skips holes; there is no loss concealment (a lost packet is a 20 ms gap), no clock-drift
  compensation and no playout pacing. Audio is submitted as it is released.
- The resampler is linear interpolation without an anti-alias filter, so 48 kHz capture aliases above 4 kHz.
- Release timing and RTCP run from the ICE agent's maintenance tick (about 250 ms), so a hole may wait up to
  ~300 ms instead of 60 ms when no further packet arrives to trigger a drain.
- `removeTrack`, `replaceTrack`, renegotiation that changes the number of audio senders, and `getStats` values for media
  are not wired to the engine. Only the first four audio senders of a connection are fed from capture.
- Remote tracks have no `ended` event in script yet (the engine raises it on BYE and close).
- Sending is not gated on the DTLS peer being the same one ICE selected after an ICE restart; restarts are not handled.
- TURN relays (#782) are not involved; media uses whatever pair ICE selects.

## Remote video (VP8 and H.264)

Part of #719 and #704. A remote peer can send VP8 or H.264 (Constrained Baseline, see below); it is depacketized, decoded
and shown in `<video srcObject>`, and `canvas.drawImage(video)` reads the same frame. Before the first decoded frame the
element is black. The two codecs share one receiver base (`RtpVideoReceiver`: reorder buffer, NACK, key-frame requests, pixel
rate cap), so everything in the table below applies to both unless a row says otherwise.

```
SRTP -> RTP demux -> Vp8Receiver (RFC 7741 reassembly, loss timers) -> Vp8Decoder (RFC 6386) -> RGBA
          \-> H264Receiver (reorder buffer, loss timers) -> H264Depacketizer (RFC 6184) -> H264Decoder -> RGBA
     -> RtcRemoteTrack.VideoFrame -> host sink -> media element (polled at paint, like a capture frame)
     <- RTCP NACK / PLI / FIR from the receiver
```

| Piece | Where | Notes |
| --- | --- | --- |
| Decoder | `Rtc/Vp8/Vp8Decoder*.cs` | Key and inter frames, golden/altref with sign bias and copy flags, segmentation, token partitions, normal and simple loop filter, six-tap and bilinear prediction, versions 0-3. Ported from the RFC 6386 reference decoder; no unsafe code. Bounds-checked: a hostile frame returns a status (`Corrupt`, `Unsupported`, `TooLarge`, `NoReference`), never throws, and the decoder then needs a key frame. |
| Size caps | `RtcMediaBudget` | `MaxVideoPixels` (1920x1088) per picture and `maxVideoBytes` (64 MiB) per document, charged at each key frame that changes the size. The charge covers the four reference frames, mode info, one row of coefficients and an allowance of two RGBA output frames. **It does not cover** the receiver's packet buffer (up to 4 MiB per stream), the frame being assembled (up to 2 MiB per stream), the per-decoder copy of the compressed frame it decodes from (up to the frame size), or RGBA frames the host still holds; those are bounded per stream but not charged to the document. When a size change does not fit, the old buffers are freed before the new reservation is tried, so nothing uncharged is retained. A failed decode at an unchanged size keeps its buffers. |
| Work bound | `Vp8Receiver` | Each stream may reconstruct 40 full-size pictures per second; pixels are charged by the picture a frame produces, not its byte size, so a 10-byte key frame at 1080p costs 2 million pixels. Frames over the allowance are dropped (an inter frame dropped this way asks for a key frame). A single call decodes at most 8 frames and converts only the newest one to RGBA. Decoding still runs under the connection's media lock, bounded by these limits, not outside it. |
| `IMediaDecoder` | `Vp8MediaDecoder` | Decodes an IVF file (`video/x-ivf`, `video/ivf`, `video/vp8`) to timestamped frames under the caller's output budget. WebM/MP4 demuxing is not implemented. |
| Depacketizer (VP8) | `Vp8Receiver` | Payload descriptor with PictureID/TL0PICIDX/TID/KEYIDX, frames from packets by sequence and marker, 512 packets / 4 MiB buffered, 2 MiB per frame. |
| Loss repair | `RtpVideoReceiver`, `RtcpCodec` | A hole older than 20 ms is NACKed (RFC 4585), at most 4 times 100 ms apart, 128 sequence numbers tracked, window 256. A hole older than 400 ms, a restart or overflow drops the frame, discards inter frames and requests a key frame with PLI (else FIR, RFC 5104), at most every 500 ms per stream (measured from the last request, a decoded key frame does not reset it) and at most one per 100 ms per document. All repair packets share a 50 per second document allowance so they cannot starve audio. A stream with no key frame 100 ms after its first packet asks for one. For H.264 the key frame is an IDR picture. |
| SDP | `RtcSdp`, controller | `m=video` with `rtcp-mux`, BUNDLE and `a=rtcp-fb` (`nack`, `nack pli`, `ccm fir`; others ignored). Folio offers VP8 (96), then H.264 `packetization-mode=1` with `profile-level-id` 42e01f (97) and 42001f (98), each with the same feedback list. Feedback is sent only when both descriptions list it for the payload type the stream uses. An answer picks one codec: the offered VP8 payload type when there is one (VP8 is preferred, whatever order the offer lists them in), otherwise the best decodable H.264 payload type (mode 1 before mode 0, then offer order), answered with the offered `profile-level-id` and mode. RTX, VP9 and undecodable H.264 payload types are never picked; a section with nothing decodable is rejected (port 0, out of the BUNDLE group). |
| H.264 receiver | `H264Receiver`, `H264Depacketizer` | Packets are put back in order by the shared reorder buffer (512 packets / 4 MiB; reordering within the window costs nothing), fed in sequence to the depacketizer (single NAL, STAP-A, FU-A; 2 MiB per access unit) and every complete access unit is decoded. A picture that lost a packet is dropped whole, no later picture is shown until an IDR picture arrives, and a key frame is requested under the rules above (shared 100 ms and 50 per second document caps). The same pixel rate cap applies (40 pictures per second at the document's picture cap; a picture is charged at the last decoded size, or the cap before the first one, and any excess is charged afterwards). At most 8 access units per call; only the newest decoded picture of a call is converted to RGBA and delivered. A decode that drops a NAL unit, stops at a decoder budget, hits the decoder's catch-all or yields a concealed picture discards that picture, counts it in `VideoDecodeErrors` and waits for an IDR picture. While waiting, an access unit that holds only SPS and PPS still reaches the decoder. After a restart or buffer overflow the first picture is dropped as well, because its head may be gone. |
| H.264 memory | `H264Decoder`, `RtcMediaBudget` | The decoder reserves against the same per-document video budget when an SPS is activated: `macroblocks * (384 * (reference frames + 6) + 3 * 384 + 1024)` bytes, which covers the reference pictures, the recycled picture pool and the current one, three output frames in flight and the RGBA conversion. It is released on a size change, BYE, close, `inactive`/`recvonly` and port 0. A stream that does not fit is refused the way an oversized one is: nothing decodes, nothing stays reserved. `H264DecoderLimits` bound each decode call (frames, output bytes, NAL units, macroblock work, input bytes); a call cut short is counted in `BudgetStops`, and `UnexpectedErrors` counts decoder bugs. They are exposed only as internal debug counters on `RtcRemoteTrack` (`VideoBudgetStops`, `VideoUnexpectedErrors`, and `VideoDecodeErrors` for dropped or concealed pictures), never to script. |

### Releasing decoders

A decoder is released (and its bytes returned to the document budget) when its track ends (BYE), when the connection closes,
when a renegotiated description stops the peer sending on it (`inactive` or `recvonly`), or when the section is rejected with
port 0. A later `recvonly`/`sendrecv` section starts a new decoder at the next key frame.

### Verification

The decoder passes all 18 libvpx conformance vectors `vp80-00-comprehensive-001` to `018` bit-exactly (per-frame MD5 of
the I420 output). They and their `.md5` files come from the WebM project's official test data,
`https://storage.googleapis.com/downloads.webmproject.org/test_data/libvpx/`, and are checked in under
`tests/Folio.Scripting.Tests/Vp8Vectors`. Other tests: engine loopback with packet loss, NACK and retransmission, PLI/FIR,
budgets, BYE, fuzzed frames and RTP, RTCP round trips, SDP, the IVF adapter, and a sandboxed content-process test where
VP8 arrives through the host UDP broker, appears in a `<video>` and is read back with `drawImage` and `getImageData`.

H.264 (`RtcH264Tests`, `H264DecoderTests`, `H264DepacketizerTests`): the decoder is checked against checked-in x264 streams
whose expected output was produced by FFmpeg's independent decoder (`H264Fixtures`). `RtcH264Tests` runs those streams
through the engine over SRTP with a fake clock (no sleeping), at several MTUs with STAP-A and FU-A, and compares every
delivered frame with a direct decode. It also covers reordering and duplicates, NACK with retransmission, an unrecovered
loss followed by PLI and the next IDR picture, FIR-only and no-feedback negotiation, both codecs on one section, SDP
negotiation with both codecs, one codec and none, release of the decoder memory on inactive/port 0/BYE/close, refusal by the
memory and picture caps, the pixel rate cap, the decoder call budgets, and hostile RTP (FU-A without a start, truncated and
oversized units, forbidden bits, random payloads, endless fragments, sequence jumps). `ContentHostPolicyTests` has the same
broker round trip as VP8 with H.264; it needs the sandboxed content host (`dotnet build tools/Folio.ContentHost` first) and
runs in CI.

### H.264 support

Decodable: **Constrained Baseline** (and Baseline without FMO/ASO): CAVLC, I and P slices, 4:2:0 8-bit, progressive frame
coding, several slices per picture, several reference frames with list modification and sliding-window or adaptive reference
marking, deblocking, cropping. Negotiated profiles are those whose `profile-level-id` names Baseline or Constrained Baseline
(profile_idc 0x42, or 0x4d / 0x58 with constraint_set0); the level is not checked, Folio's limits are host policy.

Not decodable. A NAL unit using any of these is dropped, the picture is discarded, an IDR picture is requested and the
decoder keeps running:

- CABAC (entropy_coding_mode 1), so Main and High streams.
- B slices (and SP/SI slices), so no bidirectional prediction.
- The High profiles and what only they add (8x8 transform, scaling matrices, monochrome, 4:2:2/4:4:4, bit depths above 8): an SPS whose profile_idc is not 66, 77 or 88 is refused. Weighted prediction is refused in the PPS.
- Interlaced coding (field pictures, MBAFF).
- FMO and ASO (slice groups, arbitrary slice order), redundant pictures.
- Output reordering: output order is decode order, which is exact for Constrained Baseline.
- Packetization mode 2 (STAP-B, MTAP, FU-B).
- Showing concealed pictures: the decoder fills macroblocks that no slice delivered (from the previous picture, or grey), but the receiver never shows such a picture. It drops it, counts it as a decode error and requests an IDR picture. This also covers a stream joined in the middle of an IDR picture, so the first picture shown is always a complete IDR.

### Not done

- Sending video (above), simulcast, SVC, VP9, H.264 beyond Constrained Baseline (above), RTX, REMB/transport-cc, header extensions beyond skipping them.
- No playout delay or frame pacing: a frame is delivered when complete. Hosts repaint on their own schedule; there is no
  frame-arrived invalidation yet. The picture's scaling bits (display upscaling hint) are ignored.
- Decoding is single-threaded and unoptimised C#; large pictures cost CPU on the content process.
- Not tested against a browser sender; no real-world RTP capture is in the suite.
- Colour conversion is BT.601 limited range.

