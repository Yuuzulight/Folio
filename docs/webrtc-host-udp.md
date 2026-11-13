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

- `{op:"open", token}`: ask for a socket. `token` only correlates the reply.
- `{op:"send", id, host, port, data}`: `host` is a canonical IPv4 literal, `data` is base64.
- `{op:"close", id}`.

Host to child, `UdpEvent`:

- `{op:"opened", token, id, port, allowLoopback, allowPrivate}`: `id` is host-assigned and never reused.
  The two flags are an advisory copy of the destination policy so ICE skips pairs the host would only drop instead of
  waiting out the check timeout. The host enforces the policy regardless.
- `{op:"failed", token, reason}`: not granted, quota exceeded, or bind failed.
- `{op:"datagram", id, host, port, data}`.
- `{op:"closed", id, reason}`: revoked, document replaced, closed by the child, or the broker was disposed.

The path of a packet: `IceAgent.SendDatagram` -> transport `SendTo` -> `UdpCommand` -> broker `Handle` -> per-socket
bounded outgoing queue -> host socket -> network. The reverse path: host socket receive loop -> source policy check ->
incoming token buckets -> bounded event queue -> `UdpEvent` -> child transport queue -> `IceAgent` receive loop.
Datagrams are base64 inside JSON in this stage; that costs about a third more bandwidth and is acceptable at the
quota below. A binary framing is a later optimisation.

## Policy

- The child never names a local address or port. The host binds `0.0.0.0:0` (IPv4, ephemeral) and reports the port.
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
| Outgoing queue | 64 datagrams per socket; overflow drops and counts |
| Incoming queue | 256 datagrams towards the child; overflow drops and counts |

Counters (`Denied`, `Oversize`, `RateLimited`, `DroppedOutgoing`, `DroppedIncoming`, `Malformed`, `SendFailed`) are
available to the host and tests.

## Lifetimes, failure and revocation

- **Document replacement** (a `Load` with new HTML): `Retire()` bumps a generation, closes every socket, resets the
  rate limiters and discards queued events of the old generation. Socket ids are never reused, so a late datagram can
  only name a dead id, which the child ignores. At most one datagram already being handed to the pipe can be in flight.
- **Capability revocation** (`UpdateCapabilities` or a load without `WebRtc`): every socket closes at once and the
  child is sent `closed`. A send or receive that races revocation re-checks the grant and closes the sockets itself.
- **Process death or client disposal:** the broker is disposed, sockets close, queues drain.
- **Refusal in the child:** if `Bind` fails, `IceAgent` completes gathering with no candidates and moves to `failed`.
  A `closed` event ends the child's receive loop, so the peer connection ends rather than hanging.

## Remains for later stages

1. **STUN/TURN names.** Resolving an allowed STUN hostname still happens in the child. It fails inside the AppContainer
   (no DNS), so server-reflexive candidates are not gathered there yet. A host-side `resolve` that accepts only
   names in `WebRtcAllowedOrigins` would also let the host require that TURN/STUN destinations match that list.
2. **Interface candidates.** Host candidates are enumerated by the child and are advisory only. The host should report
   the interfaces it permits.
3. **mDNS and IPv6**, TURN relay allocation and TCP candidates.
4. **Binary framing** and flow control (for example back-pressure to `bufferedAmount`) instead of drop-and-count.
5. **Per-origin or per-user budgets** across documents, and an embedder-facing option for allowing private ranges.
