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

- Complete DTLS negotiation/state/alert and adversarial acceptance beyond the
  supported profile. Certificate proofs, encrypted records, replay protection,
  bounded fragmentation/retry and OpenSSL interoperability in both roles now
  have evidence below; these focused checks do not establish full WebRTC parity.
- Complete SCTP association retry/restart, reliable message delivery,
  fragmentation, stream reset and independent-peer interoperability. Public
  strings and buffer sources now use ICE/DTLS/SCTP with a document loop;
  Blob/binaryType semantics, channel options and event budgets remain unfinished.
  Explicit empty-SDP/no-loop legacy paths still simulate local delivery.
- Verify encoded media transport, host network policy, cancellation, teardown,
  budgets and sandbox behavior with real remote peers.
- Resolve iframe/service-worker production routing against original acceptance.
  Both remain excluded from `ArtifactClassifier.ProductionFeatures`.
- Repeat all milestone, conformance, performance, deployment and release checks
  against the final reviewed commit before publishing 1.0.
- Reconcile all M0–M7 original acceptance requirements, including the mandatory
  Folio-owned replacement track. Earlier passing builds and milestone closure
  are not substitutes for that audit.

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

## SCTP association validation

Packets now require the configured SCTP ports and the local verification tag;
INIT alone requires tag zero. INIT_ACK carries an HMAC-SHA256 state cookie
binding both tags, initial TSNs, ports, creation time, lifetime and a random
nonce. Its exact bytes must be returned before the server accepts COOKIE_ECHO;
the cookie expires after 60 seconds measured by a monotonic clock.
The client requires a valid cookie parameter and its own outstanding COOKIE_ECHO
before accepting COOKIE_ACK. Verification tags use cryptographic randomness.
Pre-establishment DATA is refused. Pending stream identifiers are reserved before
packet callbacks run, and the server is ready for DATA before emitting COOKIE_ACK,
including synchronous callback delivery.

Regressions mutate ports, tags and cookie bytes while recomputing valid CRCs,
remove the mandatory cookie, send an unsolicited ACK, and then complete the
genuine handshake. Expired cookies are refused and a fresh INIT can obtain a
new cookie. Synchronous pending channels exchange messages in both directions.
The focused RTC selection passes 56/56 serialized and the project builds with
zero warnings/errors. The older capability regression now asserts that SDP
shells and candidates do not fabricate a connected transport or remote tracks;
its close-event and capability assertions also pass.

Cookie validation retains per-association state; this is not a completed RFC
9260 stateless state-restoration implementation. Association retry and restart
handling, reliability, fragmentation and independent SCTP interoperability
remain required. This work does not close #704 or qualify release 1.0.

## SCTP association retransmission

The active endpoint retains only its current INIT or COOKIE_ECHO packet, retries
with exponential delays capped at eight seconds, and stops after six retries.
Both roles have a 60-second absolute association deadline. Failure closes
pending channels and refuses new channels or incoming packets. The owned
250-millisecond timer is disposed on teardown; throwing consumer close callbacks
cannot interrupt cleanup. Handshake completion releases the retained packet.

Deterministic tests independently drop each of the four association flights,
verify recovery and DCEP opening, exhaust the retry budget, expire a passive
association and exercise the actual registered timer callback and disposal.
The focused RTC selection passes 63/63; the build has zero warnings/errors.
Before this retry change, the complete scripting suite passed 1,821 tests with
zero failures/skips under serialized execution. An unrestricted parallel run
of the final retry build exposed timing failures in GPU worker cancellation,
both service-worker registration timeout cases and one public RTC negotiation
case. These failures remain open; focused or serialized passes do not resolve
parallel/load qualification.

This closes basic association loss recovery, not reliable DATA/DCEP delivery,
congestion control, fragmentation, restart, stream reset or independent-peer
SCTP acceptance. Those and the broader release gates above remain open.

## Retry callback lock ordering

Fresh review found that timer sends could acquire SCTP then DTLS, opposite to
incoming DTLS application delivery. Retry packets are now copied under SCTP's
lock and sent after releasing it. Failure state is set under lock, with channel
close callbacks invoked afterward. A regression requires a separate thread to
enter SCTP before the retry's transport callback returns. Focused RTC tests
pass 64/64. The preceding parallel full run completed: 1,828 total, four failed,
zero skips (GPU cancellation, both registration timeout cases, one RTC deadline).
That run does not prove a deadlock; the lock-order fix follows direct code review.
Parallel/load qualification remains unresolved.

## Continuation checkpoint

Production code is pushed through `079b48a1` in PR #705. The complete scripting
suite for the lock-order correction passes **1,829/1,829**, zero skips, serialized
(`-parallelMode none`). The final test-source rebuild has zero warnings/errors;
all eight SCTP retransmission tests pass after that rebuild. Both OpenSSL DTLS
roles also passed encrypted bidirectional application-data probes this session.
The hosted run for `079b48a1` was still in progress when recorded; it is not
claimed green. The prior unrestricted parallel run's four failures remain open.

Next work is reliable DATA/DCEP retransmission and ordering (including pending
server channels when COOKIE_ACK is lost), bounded receive/event queues, SCTP
fragmentation/congestion/reset/restart and independent peer coverage, public
Blob/options semantics, remote encoded media and host network policy, routing
qualification, and the complete original M0–M7/replacement and release audit.
No milestone is newly signed off by this checkpoint. PR #705 is unmerged;
issue #704 remains open and release 1.0 has not been created.

## Reliable complete-DATA delivery and public buffer state

SCTP now increments per-stream message sequence numbers, retains complete DATA
until cumulative acknowledgement, suppresses duplicate TSNs and reports gap ACKs
without acknowledging missing packets. Ordered messages wait per stream while
other streams can progress. Gap-ACKed packets are retained for reneging; a later
SACK that omits them permits retransmission again. Future, stale and malformed
acknowledgements cannot release send admission. Serial arithmetic covers TSN and
stream-sequence wrap. Empty-message PPIDs carry one ignored wire-padding byte.

Send storage is capped at 1 MiB/1,024 chunks, receive payload storage at 128 KiB/
1,024 messages, and tracking at 1,024 receive streams. Retries back off, fail the
association after bounded attempts, and release payloads on failure/disposal.
Gap-retained state also has a 60-second absolute deadline. Pending transport
output is capped at 2,048 packets/4 MiB. Packets are dispatched outside SCTP's
lock, including public channel creation and sends, without recursive dispatch.
Public native/Jint channels read their actual buffered amount and queue a
`bufferedamountlow` event when acknowledgements cross the configured threshold.

Regressions drop DATA/SACK and COOKIE_ACK, recover pending channels in both
directions, reorder/duplicate messages, fill admission/receive storage, wrap
sequence numbers and exercise gap reneging. Basic complete-message reliability
is present; fragmentation, congestion and peer-window flow control, stream reset,
restart and wider SCTP conformance remain incomplete. JavaScript event budgets
are distinct from the new SCTP storage/output bounds and are still required.

## Independent browser transport acceptance

The new `rtc-chrome-probe` command negotiates with an unmodified headless Chrome
using default ICE host privacy (no mDNS-disabling flags). Both Folio offerer and
answerer roles pass against Chrome **156.0.8078.80**: ICE/DTLS/SCTP connect, both
peers open a data channel, and three ordered strings each way are echoed and
verified. This is real independent-browser transport evidence, not a shared
Folio endpoint or the empty-SDP local shortcut. It does not qualify media,
browser loss/reordering, fragmentation, TURN, or arbitrary network topology.

The probe exposed and repaired:

- Selection of media-level ICE credentials, fingerprint and DTLS setup using the
  active BUNDLE transport. Media setup/fingerprint survive serialization.
- Authenticated peer-reflexive ICE checks when a peer's SDP uses an mDNS host
  name. This is not an mDNS resolver. Incoming ICE usernames, HMACs/fingerprints,
  response source endpoints and integrity are checked; application datagrams
  must come from the selected endpoint. Controlled peers require nomination.
- Serial connectivity checks, synchronized/bounded STUN waiters and bounded
  candidate/pair admission.
- INIT and opaque-cookie parameter padding outside the declared chunk length.

Multiple unbundled transports/BUNDLE groups are explicitly refused, not silently
mixed. Full ICE role-conflict handling, consent freshness, relay wiring, direct
mDNS resolution and host network-policy qualification remain required.

The focused RTC selection passes **84/84**, zero skips; the Release solution
build has zero warnings/errors. The full serialized scripting suite passes
**1,849/1,849**, zero failures/skips, for production code through `fb9228b0`.
Ubuntu CI now runs both Chrome roles and uploads SDP/packet/report
evidence; that new CI configuration is not yet claimed green. The four earlier
parallel timing failures remain unresolved until a fresh load run proves them
fixed. #704 and the overall M0–M7/replacement/release goal remain open.

The previous checkpoint `ec263561` completed hosted CI successfully (run
`42779691574`). That result predates the reliable-delivery/browser changes and
is not evidence that the new tip's CI has passed.

## Resumed fragmentation acceptance and ICE ordering repair

The paused `b372bdb0` checkpoint has been resumed. The Chrome probe now checks
byte-exact 65,536-byte binary messages, 60,000-byte UTF-8 strings, empty strings
and binary messages, and oversize-send refusal, in both directions and in both
offer/answer roles. All checks pass locally against Chrome 156.0.8078.80 using
default ICE host privacy. These replace the earlier small-string-only probe;
they do not prove arbitrary-size, lossy-network or congestion acceptance.

Public native and Jint tests also pass byte-exact 64 KiB and 256 KiB round trips
after source-buffer mutation. Packet tests cover a lost middle fragment,
conflicting metadata with a valid packet checksum, partial-chain expiry and an
unaffected sibling association. SDP tests cover absent/zero/explicit/invalid
message limits and the remote SCTP destination port. Pending channels that
cannot fit a newly negotiated limit close rather than remaining connecting;
failed immediate channel admission rolls back its registry/identifier reservation.

The checkpoint's hosted run `42785213081` failed on Windows because
`TwoControllersPerformCandidateExchangeAndConnect(passiveAnswer: False)` found
DTLS still connecting; ICE was already connected. Review found that a validated
STUN response scheduled endpoint selection asynchronously, allowing a following
DTLS packet to be discarded before selection. Authenticated response handling
now selects the endpoint before returning to socket delivery. Response and
cancellation compete atomically, so an expired check cannot be revived by a
late response. A socket regression sends a DTLS-shaped datagram immediately
after the valid response. The original two-second controller deadline is retained.

Final local build: Release solution, **zero warnings/errors**. Final focused
RTC selection: **111/111**, zero skips. Both expanded Chrome roles pass after
the final guards. A broader solution run during this continuation finished
**4,933 passed, one failed, two skipped** (4,936 total); its sole failure was the
native 256 KiB public round trip exceeding its ten-second deadline under normal
test parallelism. The previously observed timing failures were not reproduced
in that run; one passing run is not proof that every intermittent issue is gone.
The broader run predates the final cancellation/admission guards and is not
reported as a green run for the final commit.

**Next required work:** sender congestion/peer-window control and load
qualification. The current sender emits all fragments of a large message in a
burst. The new large-message failure remains unresolved; do not relax the
deadline or count focused/serialized results as resolving it. Fragmentation
qualification, hosted CI and the entire original M0–M7/replacement/release scope
remain open. PR #705 stays draft and #704 is not closed.

## SCTP sender congestion and peer-window control

The sender now distinguishes assigned/queued TSNs from emitted TSNs. SACKs
cannot free unsent payloads. New DATA leaves the bounded queue only when peer
credit and congestion capacity allow it, with a four-packet dispatch cap and
limited one-packet congestion-window overbooking. Initial peer credit comes
from INIT/INIT_ACK and later credit from validated SACKs.

The sender implements slow start, congestion avoidance, partial-byte accounting,
fast retransmit after fresh gap reports, a fast-recovery boundary, one-packet
timeout recovery, exponential timeout backoff and idle-window decay. RTT samples
update the retransmission timeout; retransmitted data, including zero-window
probes, cannot contribute an ambiguous RTT sample. Gap-acknowledged payloads
remain available for reneging, and marked retransmissions precede new DATA.

Closed receive windows use bounded exponential probes. Responsive zero-window
peers can hold the existing bounded send queue past the ordinary stalled-send
deadline; reopening credit starts a fresh progress budget. Silent peers and
unfinished receive chains still have bounded retirement behavior. This does
not expand the existing payload/count admission limits.

Tests now exercise the actual dequeue/emission/acknowledgement lifecycle rather
than treating queue admission as transmission. Ten new sender-window tests cover
credit, pacing, probe behavior, recovery, duplicate/stale ACKs, RTT/backoff,
congestion avoidance and idle decay. The Release solution builds with zero
warnings/errors. Expanded Chrome probes still pass in both roles. The final
broader solution test run is in progress; the prior 256 KiB parallel-load
failure is not declared resolved until that run provides evidence.
