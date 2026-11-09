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
fragment reassembly and retransmission remain required. Application-data
readback still uses its older decryptor and needs the same replay protection.
