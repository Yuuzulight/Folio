# Host-mediated UDP for WebRTC (stage 1)

Part of #719 and #704. This extends the host-owned WebRTC transport seam described in
[`dependencies.md`](dependencies.md) and the process model in
[`content-process-sandbox.md`](content-process-sandbox.md).

## Problem

The content process runs in an AppContainer with no network capability, but `IceAgent` used to open its own UDP
`Socket`. Remote WebRTC therefore could not work in the sandbox, and the only way to make it work would have been to
grant the sandbox a network capability. The rule instead: the content process never owns a network socket. The host
owns every socket, applies policy and quotas, and revocation closes them.

## Who owns what

| Piece | Process | Owns |
| --- | --- | --- |
| `IIceTransport` | either | The seam `IceAgent` uses: `Bind`, `SendTo`, `Receive`, `CanSendTo`, `Dispose`. |
| `DirectIceTransport` | any non-content use (tests, in-process documents) | A real `Socket`. This is the default and behaves exactly as before. |
| `MediatedUdpClient` / mediated transport | content process | No socket. Turns the calls above into protocol messages. Installed by `Folio.ContentHost` through `IceTransportProvider.Factory`. |
| `UdpBroker` | host (`ContentProcessClient`) | Every socket, the destination policy, quotas, queues and counters. Created lazily on the first UDP command. |

`ContentProcessClient` is the only code that creates a broker, and it feeds it the host's own view of the WebRTC
capability. The child's belief about its capabilities is never consulted.

## Message flow

Two new `ContentMessageKind` values, both appended to the enum, both JSON payloads. They are read on the connection
reader thread (like resource responses), so a document that waits for a socket cannot deadlock the dispatcher.
`ContentProcessClient.SendAsync` refuses both kinds from callers, as for `GpuRestore`.

Child to host, `UdpCommand`:

- Every command carries `gen`, the document generation the host stamped into the `Load` (`hostPolicy.udpGeneration`).
  A command with any other value is refused (an `open` is answered with `failed`).
- `{op:"open", gen, token}`: ask for a socket. `token` only correlates the reply.
- `{op:"send", gen, id, host, port, data}`: `host` is a canonical IPv4 literal (no mapped, octal, hex, padded or
  whitespace forms), `data` is strict base64.
- `{op:"permit", gen, host}`: allow unsolicited inbound datagrams from this address (same address policy; see below).
- `{op:"close", gen, id}`.

Host to child, `UdpEvent`:

- `{op:"opened", token, id, port, allowLoopback, allowPrivate}`: `id` is host-assigned and never reused.
  The two flags are an advisory copy of the destination policy so ICE skips pairs the host would only drop instead of
  waiting out the check timeout. The host enforces the policy regardless.
- `{op:"failed", token, reason}`: not granted, quota exceeded, or bind failed.
- `{op:"datagram", id, host, port, data}`.
- `{op:"closed", id, reason}`: revoked, document replaced, closed by the child, or the broker was disposed.

UDP frames are small. A frame over 8 KiB is read only as far as a 32-byte prefix first: if the prefix says it is a
`UdpCommand` or `UdpEvent` (our serializer writes the kind first), or does not announce its kind that way, the
connection is closed before the payload is allocated. JSON allows a later duplicate `Kind` to override the first, so a
frame can still pass that prefix check and be allocated (bounded by the general 16 MiB frame limit, like every other
kind); after decoding, a UDP message over 8 KiB is refused whatever its framing. Every command is charged to the rate
limiter before any of its fields are read, but after the frame has been read and parsed as JSON.

The path of a packet: `IceAgent.SendDatagram` -> transport `SendTo` -> `UdpCommand` -> broker `Handle` -> per-socket
bounded outgoing queue -> host socket -> network. The reverse path: host socket receive loop -> source policy check ->
incoming token buckets -> bounded event queue -> `UdpEvent` -> child transport queue -> `IceAgent` receive loop.
Datagrams are base64 inside JSON in this stage; that costs about a third more bandwidth and is acceptable at the
quota below. A binary framing is a later optimisation.

## Policy

- The child never names a local address or port. The host binds `0.0.0.0:0` (IPv4, ephemeral) and reports the port.
  Binding specific interfaces was left out: the host does not yet report the permitted interfaces (see "Remains"),
  and inbound filtering below already limits who can reach the socket.
- **Inbound** datagrams are delivered only from addresses the child has sent to on any of its sockets (up to 256 per
  document) or has authorised with `permit` (up to 64 per document, validated by the destination policy). `IceAgent`
  permits every remote candidate address, because a peer's first check can arrive before ours leaves. Everything else
  is dropped and counted (`Unsolicited`). The sets are cleared when the document is replaced.
- Destinations must be IPv4 unicast, port 1-65535, and globally routable. Denied: loopback, RFC 1918 and CGNAT
  (`100.64/10`), link-local, multicast, reserved and broadcast, `0/8`, documentation and benchmarking ranges, and all
  IPv6. Loopback and private ranges can be allowed only through `UdpBrokerOptions` (internal in this stage; the
  published-host acceptance test opts in to loopback because its peer lives there). The same check runs on the
  source address of every inbound datagram.
- A malformed or wrong-typed field is treated as absent and counted. Nothing the child sends can throw in the host.
- The `WebRtcAllowedOrigins` list still gates ICE server URLs, in the document, as today. See "Remains" for why this is
  not yet enforced on the host.
- No socket exists unless the capability is on. With the capability off, `open` is refused and nothing is created.

## Quotas (per document; the defaults)

| Limit | Value |
| --- | --- |
| Sockets | 4 |
| Datagram | 1500 bytes; larger is dropped and counted |
| Rate, each direction | 1 MiB/s with a 128 KiB burst, and 1000 packets/s with a 200-packet burst, taken together |
| Socket opens | burst of 4, refilling at 1 per second |
| Commands, any kind | burst of 500, 2000 per second, charged before its fields are read |
| Outgoing queue | 64 datagrams per socket; overflow drops and counts (the budget is refunded) |
| Incoming datagram queue | 256 datagrams towards the child; overflow drops and counts |
| All queued events | 512, control events included |

The limiters belong to the broker (the process), not to a socket or document; a replaced document receives fresh
ones at most once per second. If the child stops reading its pipe, the event queue reaches 512, the broker closes
every socket and refuses further commands until the next document (`Dead`). The broker never writes to the pipe while
holding a lock, so a stalled child blocks only its own event pump. A persistent receive error backs off and then closes
the socket (host) or fails the connection (child).

Counters (`Denied`, `Oversize`, `RateLimited`, `DroppedOutgoing`, `DroppedIncoming`, `Malformed`, `SendFailed`) are
available to the host and tests.

## Lifetimes, failure and revocation

- **Document replacement** (a `Load` with new HTML): when the `Load` is about to be sent, the host bumps the document
  generation, closes every socket, clears the permit lists and discards queued events of the old generation. The old
  document keeps running until the child handles the `Load`, but its commands carry the old generation and are
  refused, so nothing it opens survives into the new document or uses its quota (an open racing the bump is re-checked
  under the same lock). Socket ids are never reused. At most one datagram already being handed to the pipe can be in flight.
- **Capability revocation** (`UpdateCapabilities` or a load without `WebRtc`): every socket closes at once and the
  child is sent `closed`. A send or receive that races revocation re-checks the grant and closes the sockets itself.
- **Process death or client disposal:** the broker is disposed, sockets close, queues drain.
- **Refusal in the child:** if `Bind` fails, `IceAgent` completes gathering with no candidates and moves to `failed`.
  A `closed` event ends the child's receive loop and the ICE connection moves to `failed` once, so the peer connection
  ends rather than hanging.

## Threat model

The content process is untrusted. With the capability on, the page can send UDP to any globally routable IPv4
address, at up to the rates above (inherent to WebRTC: peer addresses come from remote SDP). It cannot reach private,
loopback, link-local, multicast or reserved addresses, cannot choose a local bind, cannot receive from IP addresses it
has not contacted or permitted (see the next paragraph for what that does and does not guarantee), cannot grow host memory (every queue is bounded and a non-draining child is cut off),
cannot keep a socket past revocation, document replacement or its own death, and cannot create anything with the
capability off. Residual risk: UDP volume and reflection toward public hosts within the quotas, and the 16 MiB general
frame limit for non-UDP kinds.

Inbound filtering is by source IP only. The sent-to and permit sets are keyed by address, not port, and shared by all of
a document's sockets, so a datagram from any port of a contacted or permitted IP is delivered. A third party that can
spoof a source address of a permitted IP can inject datagrams (inherent to UDP; DTLS authenticates the content). A
peer-reflexive or mDNS-hidden candidate is not permitted until the child sends to that IP, so a peer that can only
reach us by initiating from an address we never learned stays unreachable, which is a functional limit of this stage.

## Remains for later stages

1. **STUN/TURN names.** Resolving an allowed STUN hostname still happens in the child. It fails inside the AppContainer
   (no DNS), so server-reflexive candidates are not gathered there yet. A host-side `resolve` that accepts only
   names in `WebRtcAllowedOrigins` would also let the host require that TURN/STUN destinations match that list.
2. **Interface candidates.** Host candidates are enumerated by the child and are advisory only. The host should report
   the interfaces it permits.
3. **mDNS and IPv6**, TURN relay allocation and TCP candidates.
4. **Binary framing** and flow control (for example back-pressure to `bufferedAmount`) instead of drop-and-count.
5. **Per-origin or per-user budgets** across documents, and an embedder-facing option for allowing private ranges.
