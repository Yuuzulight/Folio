# 1.0 release audit — 2026-11-10

Release is blocked. Main `ee1aab15` builds cleanly and its full local suite
passes 4,843 tests with two documented skips (4,845 discovered). Four production
packages and isolated portable/Windows package consumers pass. Hosted run
42609667213 passes all 26 jobs. These checks do not establish completion.

## Reproduced authentication failure

A fresh `DtlsTransport` accepted a 25-byte record: handshake content type 22,
version FEFD, epoch/sequence zero, record payload length 12, handshake type 20
(Finished), and zero body/fragment length. `State` became `connected` without
certificates, keys or verification data. Plaintext application data was also
delivered before authentication.

The initial mitigation rejects Finished without a connecting state, master
secret and matching 12-byte transcript verification data, excludes the incoming
Finished from its own transcript, and rejects application data before connection
authentication. Regression tests exercise fresh and started transports and both
send/receive plaintext paths. This is not full DTLS qualification.

## Remaining required work

- Complete DTLS certificate/key-exchange signature checks, encrypted handshake
  records, correct transcript/flight ordering, epochs, replay rejection,
  retransmission and independent-peer interoperability.
- Connect public peer state and data channels to ICE/DTLS/SCTP. The current
  JavaScript binding declares connected on an answer and delivers messages
  between local objects; those paths do not prove remote transport.
- Verify encoded media transport, host network policy, cancellation, teardown,
  budgets and sandbox behavior with real remote peers.
- Resolve iframe/service-worker production routing against original acceptance.
  Both remain excluded from `ArtifactClassifier.ProductionFeatures`.
- Repeat all milestone, conformance, performance, deployment and release checks
  against the final reviewed commit before publishing 1.0.

`TwoControllersPerformCandidateExchangeAndConnect` currently asserts ICE state
only, despite its DTLS comment. Full transport acceptance needs tests beyond it.

## Key-exchange repair

The server ephemeral key is now verified against the pinned ECDSA certificate
over the client random, server random and complete curve parameters. Missing
fingerprint pins are rejected. P-256 keys use the RFC 8422 uncompressed ECPoint
wire encoding and DER ECDSA signatures; ECDH now supplies the raw shared secret
to the TLS PRF rather than a separately hashed key. Tests cover a valid signature,
wrong signing key, changed client/server randoms, changed point, corrupted or
truncated signature and invalid point encoding. The focused group passes 13/13.
Mutual client authentication, protected handshake records, transcript/state
ordering and independent-peer interoperability still block release.

## Protected handshake record repair

Handshake records in epoch 1 now use AES-128-GCM and authenticated record
headers. The wire version is corrected to FEFD. The reader rejects duplicate
authenticated records with a 64-record sliding window, accepts valid reordered
records within that window, and only advances it after authentication succeeds.
Plaintext Finished records are rejected. Focused record/remote-RTC tests pass
17/17 with zero skips; the project build has zero warnings/errors.

This implements protected handshake record framing, not a complete DTLS
handshake: flight/transcript/state ordering, mutual client authentication,
fragment reassembly and retransmission remain required.

Application-data send/receive now uses the same authenticated record cipher and
replay window as handshake records. The duplicate application-data decryptor and
redundant key objects were removed. A post-handshake transport-boundary fixture
verifies corruption rejection, duplicate rejection, shared handshake/application
sequence space, and outgoing encrypted data. It supplies keys directly and must
not be counted as handshake interoperability evidence. Focused tests pass 18/18
and the project build has zero warnings/errors.

## Handshake ordering repair

The server now waits for the client's authenticated Finished before sending its
final flight. The authenticated client Finished enters the transcript used to
verify the server Finished. Incoming handshake sequence numbers and complete
fragment lengths are checked, and Finished requires receipt of ChangeCipherSpec.
Key expansion consistently uses server-random then client-random; SRTP export
consistently uses client-random then server-random for both roles.

A queued Folio-to-Folio datagram test now completes a server-pinned handshake,
checks matching directional SRTP keys, delivers encrypted application data, and
rejects duplicate delivery. Corrupting the client's encrypted Finished leaves
both transports unconnected and application sends refused. The focused group
passes 20/20. This is same-implementation evidence; mutual client authentication,
independent-peer interoperability, fragmentation/retransmission and public API
wiring remain required before release.

## Mutual client certificate repair

The server now requests an ECDSA/SHA-256 client certificate. The client sends
its certificate and a CertificateVerify signature over the preceding transcript.
The server pins that certificate to the expected SDP fingerprint, verifies the
signature, and requires this proof before accepting Finished. The client also
requires the server's signed key-exchange proof before sending its final flight.
The queued handshake regression passes with mutual proof; corrupting the client
CertificateVerify signature prevents connection. This currently accepts the
specific supported ECDSA request shape; general extension negotiation and
independent-peer interoperability remain unqualified. Public API wiring and
fragmentation/retransmission still block 1.0.

## Bounded handshake fragment reassembly

Handshake fragments are now buffered by message sequence, reassembled before
transcript processing, and consumed in sequence order. Overlaps must contain
identical bytes; conflicting headers/overlaps and malformed ranges are rejected.
Payload and coverage storage is charged against a 256 KiB aggregate cap, with
at most 16 pending messages. Accepted-message retransmissions are ignored.

The queued mutual-handshake test now also splits the server certificate and
delivers its fragments in reverse order, then verifies encrypted application
delivery. Reassembly tests cover agreeing/conflicting overlap and allocation/
message-count refusal. The focused RTC selection passes 36/36 with zero skips;
build has zero warnings/errors. Flight retransmission, broader peer negotiation,
independent interoperability and public API wiring still block release.

## Independent-peer probe and framing repairs

A local OpenSSL 3.5.6 `s_server -dtls1_2` probe returned a 48-byte
HelloVerifyRequest using record version FEFF. Folio remained connecting because
it discarded that version. This is direct interoperability failure evidence,
not a completed independent handshake.

The receive path now parses multiple DTLS records in one UDP datagram. A
truncated record is not authenticated or admitted to the replay window. The
client accepts FEFF specifically for the cookie challenge, echoes the cookie in
a new ClientHello, and removes the initial ClientHello/challenge from its
Finished transcript. A regression verifies cookie echo and ignores an old
duplicate challenge. The focused RTC selection passes 37/37; build is clean.
The full OpenSSL handshake has not yet been qualified.

## Independent OpenSSL mutual handshake

The reproducible command is now:

```
dotnet tests/Folio.Scripting.Tests/bin/Release/net10.0/Folio.Scripting.Tests.dll dtls-openssl-probe <openssl-path>
```

OpenSSL 3.5.6 successfully completed a mutual DTLS 1.2 handshake with Folio as
client, using P-256 ECDHE/ECDSA and AES-128-GCM. OpenSSL's verification log
confirms client certificate, CertificateVerify and Finished acceptance; Folio
reports connected after verifying the pinned server and protected Finished.
The probe generates temporary identities, retains diagnostic logs/certificates,
removes its private-key file, and terminates the owned server process.

The probe exposed a fixed-shape CertificateRequest parser. It now negotiates
SHA-256/ECDSA from variable certificate-type/signature lists and validates the
bounded CA-name vector. ClientHello explicitly advertises SHA-256/ECDSA.
Focused RTC tests pass 38/38; build is clean. This qualifies this handshake
configuration only: reverse-role interoperability, independent application-data
exchange, retransmission, public API wiring and release-wide checks remain open.

The OpenSSL probe now also requires encrypted application-data delivery in both
directions. OpenSSL receives `folio-to-openssl-probe`; Folio receives
`openssl-to-folio-probe`. A fresh local OpenSSL 3.5.6 run passes both directions
and mutual authentication with no OpenSSL error in the captured diagnostics.
The Ubuntu CI build now runs this command using `/usr/bin/openssl`; hosted
confirmation is pending. This is still one client-role/cipher configuration;
reverse-role coverage, packet-loss recovery, browser API/SCTP integration and
the remaining release checks are not established by it.
