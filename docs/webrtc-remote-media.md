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

## Limits and budgets (host side)

Enforced by `RtcMediaBudget`, one instance per document, shared by all its peer connections:

- live tracks (remote and local): 16
- packets per second: 4000 in each direction; bytes per second: 4 MiB in each direction (one second of burst)

Per engine:

- a datagram over 2048 bytes, an RTP payload over 1200 bytes, malformed headers, bad padding, unknown payload types,
  unauthenticated, replayed or over-budget packets are dropped and counted, never queued
- SRTP stream state is created only after a packet authenticates, and is capped at 32 SSRCs per direction
- jitter buffer: 64 packets per stream. On overflow the oldest packet is dropped and the hole is counted as lost.
  A hole is otherwise skipped after 60 ms
- at most 16 remote streams, 16 media sections, 32 payload types and 8 SSRCs per section, 16 RTCP packets per compound
- nothing in the media path logs keys, salts or payloads

Decoded remote audio reaches the output only when the document has `Media`, `Sound` and `AllowSoundAutoplay`, because
nothing a user did started it. Capture still needs its own microphone grant and the track must be live and enabled.

## Tests

- `RtcMediaTests` (no sockets, no sleeping): SRTP/SRTCP round trips, tamper, replay, rollover, per-SSRC windows, stream
  cap; G.711; RTCP build/parse and hostile input; SDP parse bounds and round trip; a two-engine loopback through fake
  transports on a manual clock covering PCMU and PCMA, reordering, loss and the 60 ms wait, receiver and sender reports
  (including DLSR), BYE, track/packet/byte budgets, garbage and authenticated-but-unusable packets, jitter bound, and
  video being negotiated but not decoded.
- `ContentHostPolicyTests.ChildReceivesRemoteAudioOverSrtpThroughTheHostUdpBroker`: a real content-process child (no
  socket) negotiates a `recvonly` audio transceiver with an out-of-process controller, `ontrack` fires in script, SRTP
  arrives through the host broker and the child's SRTCP receiver report comes back through it. This runs over real
  loopback sockets and real time.

## What is not done

I did not overclaim these:

- **Video.** Video sections are offered `inactive` and rejected in answers. If a peer negotiates video anyway, a track
  is created and its packets are counted and reported in RTCP, but nothing is depacketized or decoded and no frame is
  produced. There is no VP8/H.264 decoder or encoder behind `IMediaDecoder` (which decodes whole bounded resources, not
  streams).
- **Opus.** Browsers prefer Opus; Folio only speaks G.711. A browser offer always lists PCMU/PCMA too, so negotiation
  succeeds at 8 kHz, but it is narrowband.
- **Interoperability is untested.** The SRTP and DTLS key layout follow RFC 7714 and RFC 5764, but I have only run Folio
  against Folio. The first run against a browser may expose a mismatch. There are no third-party test vectors in the
  suite yet.
- **Only AEAD_AES_128_GCM** is offered in `use_srtp`. A peer that supports only the AES-CM/HMAC-SHA1 profiles cannot
  connect media (data channels are unaffected).
- No MID header extension, no SSRC-less demux, no simulcast, no RTX/FEC/NACK/PLI, no congestion control or REMB.
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
