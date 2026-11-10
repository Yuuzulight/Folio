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

## Reverse-role OpenSSL verification

The probe accepts `folio-server` after the OpenSSL path to run Folio as server
against OpenSSL `s_client`. OpenSSL 3.5.6 now passes mutual authentication and
both application-data directions in this role too. The independent test exposed
the initial FEFF ClientHello header and RFC 5746 renegotiation signaling gaps.
The server now accepts the initial legacy record header specifically for
ClientHello and returns the empty renegotiation-info extension when signaled by
the peer. Verification was not disabled. Both probe roles negotiate SRTP's
AEAD-AES-128-GCM profile, and both now run in Ubuntu CI (hosted result pending).
Focused RTC tests pass 39/39; build is clean. Packet-loss recovery, fuller
negotiation/policy checks, browser API/SCTP integration and release qualification
remain open.

## Pause checkpoint: handshake flight retransmission

The transport retains a bounded outgoing handshake flight (64 records / 1 MiB),
retries with exponential backoff, and fails an uncompleted handshake after six
retries. The final server flight remains available for bounded retransmission
until authenticated application traffic confirms progress. Disposal clears the
flight and retires the timer. Deterministic-clock tests recover after dropping
the initial ClientHello, server certificate, client Finished and server Finished;
another test verifies retry exhaustion and no sends after disposal.

The five retry tests pass. The combined 44-test RTC selection initially failed
the existing TwoControllersPerformCandidateExchangeAndConnect ICE assertion
(checking rather than connected). That test passed alone, and all 44 passed in
a subsequent serialized run. The parallel failure remains unresolved evidence.
Both OpenSSL roles reran successfully with mutual authentication and encrypted
application data in both directions after the retransmission change.

Next work: investigate ICE scheduling/policy and controller-role integration;
connect public RTCPeerConnection/RTCDataChannel state and data to the transport;
qualify independent SCTP/SRTP/media and loss/reorder behavior; resolve iframe/
service-worker routing against original acceptance; then rerun the entire
milestone, production, performance, packaging, consumer and hosted CI gates.
No 1.0 tag or release has been published. PR #705 and blocker #704 remain open.

## Resume: negotiated controller roles and real UDP data

All 26 hosted checks passed for `f0252fe4` before this continuation. Controller
DTLS roles now follow the SDP `setup` attribute: active answers initiate DTLS,
passive answers accept it, and the offerer selects the opposite role. ICE
controlling/controlled roles are selected independently. Generated answers also
respect an active remote offer.

The former ICE-only controller regression now covers both active/passive answer
roles and requires ICE, mutual DTLS, SCTP association, DCEP channel opening and
a received string over real UDP. It waits for channel opening rather than
assuming remote channel creation implies that the local ACK arrived. The focused
selection passes 45/45 serialized; build is clean.

Empty-description in-page JavaScript loopback fixtures still exist and retain
their compatibility behavior. They are not network interoperability evidence.
Replacing that public JS delivery path and its fixtures remains required.

## Public transport state bridge

Public peers now register a native transport callback. ICE gathering, ICE
connection and authenticated connection events are queued onto the document
event loop; callbacks recheck capability and lease lifetime before entering the
engine. Actual nonempty SDP answers and ICE candidates no longer synthesize
connected state. Empty-SDP fixtures retain their old in-page behavior until
their replacement; local JS data-channel delivery still remains to be removed.

Native and Jint regressions observe gathering/complete events from real candidate
gathering and verify that accepting an answer without connectivity does not
report connected. The focused RTC selection passes 47/47 serialized, zero
skips; build is clean. Initial wrapper-constructor integration failed on the
native engine and was corrected by registering directly in the constructor.

## SCTP channel readiness prerequisite

Channels created before negotiation now remain connecting and queue DCEP OPEN
until association readiness. Automatically assigned stream IDs use DTLS-client
even / DTLS-server odd parity; duplicates, oversized UTF-8 labels/protocols and
creation after disposal are rejected. Pending closed channels are not advertised.
Tests create channels on both sides before negotiation, pump packet exchange,
verify opening/parity and cancellation, and check admission failures. The focused
RTC selection passes 49/49 serialized; build is clean.

This is a prerequisite for replacing public JavaScript channel delivery, which
is still unfinished. SCTP cookie/verification-tag validation, reliability and
independent-peer conformance also require further qualification before 1.0.

## Public string data-channel transport

Documents with an owner event loop now create native SCTP channel leases.
Public string sends reach SCTP/DTLS/UDP; remote channel, open and message events
are delivered through the document loop after lease/capability checks. Channel
state comes from the native channel and owned teardown bypasses monkeypatched
JavaScript close methods. Answer generation retains an offered application
m-line even without locally created channels. ICE rebuilds candidate pairs when
local gathering finishes after remote SDP, avoiding a duplicate-trickle dependency.

Native and Jint public-API regressions create a channel before negotiation,
exchange real SDP, wait for onopen, deliver a string, echo it remotely and close
both peers. The focused RTC selection passes 51/51 serialized; build is clean.
These checks exposed and repaired answer m-line, ICE pairing and native teardown
failures during development.

Remaining channel scope includes outbound binary/Blob data, partial-reliability
and negotiated options, remote stream reset/close semantics, bounded event
delivery and independent SCTP/reliability acceptance. Unsupported options are
explicitly refused. No-loop legacy fixtures still use their old in-page path;
they must not be counted as production network qualification. This delivery
does not close #704 or qualify 1.0.

## Binary buffer-source data channels

The public transport channel now copies attached ArrayBuffer/typed-array/
DataView sources through the engine's branded buffer API. Received binary data
is returned as an ArrayBuffer. Native and Jint public tests send a typed-array
subview, immediately overwrite the source, and verify byte-exact remote receipt
and echo. The focused RTC selection passes 53/53 serialized; build is clean.

Until SCTP fragmentation is implemented, the single-record payload cap is checked
before buffer allocation, UTF-8 encoding or TSN advancement. DCEP metadata
admission uses its combined encoded size. Large messages are refused explicitly;
this limit is not evidence that large-message SCTP acceptance is complete.
Blob delivery/binaryType semantics, fragmentation/reliability, event budgets,
remote reset and independent SCTP interoperability remain release work.
