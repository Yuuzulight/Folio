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


## Owned transport progress under shared-pool starvation

Hosted run `42824602362` passed at `f57010fc`; it predates this patch.
Sender pacing alone did not settle the parallel-load failure. One later run
failed before DTLS completed; another reached connected state but retained part
of the 256 KiB message. These are not reported as successful acceptance runs.
The earlier pre-checkpoint process session could not be recovered after reset,
so its terminal totals are not used as evidence here.

Controller-owned RTC transports now receive datagrams and drive DTLS/SCTP
maintenance on an owned socket thread instead of sharing CLR pool scheduling
with synchronously blocked page/test work. Standalone transport constructors
retain their timer behavior. ICE checks start directly and retry an authenticated
transaction with the same request bytes at 500 ms, 1 s and 2 s, under a bounded
3.5-second cancellation window. Unauthenticated STUN discovery keeps its original
single-attempt deadline. Public peer release no longer disposes the transport
while holding the document peer-registry lock. Diagnostic snapshots also release
that lock before inspecting transport state.

A subprocess occupies the sole CLR worker thread, then establishes real
ICE/DTLS/SCTP, delivers a byte-exact 256 KiB payload and verifies both owned
receive threads retire after disposal. The probe passes. This establishes that
path without free pool workers; asynchronous STUN retry/check continuations and
public offer/answer behavior are not claimed independent of the pool.

The same full-load investigation found the GPU cancellation harness sometimes
failed to enter its injected read within the unchanged two-second deadline.
Production GPU pipe exchange also used shared-pool work and a pool-driven timeout
continuation. #706 tracks the correction: one persistent owned blocking-I/O
thread with a capacity-one request queue, synchronous deadline/cancellation waits,
an owned fixed-buffer stderr drain, and idempotent owner disposal. Concurrent
requests are still rejected, reply validation and payload caps are preserved,
and terminal faults still kill only the owned worker. The harness call starts
on its own thread; its existing time limits are unchanged.

The GPU subprocess probe occupies the sole CLR worker thread, performs 32 real
anonymous-pipe request/reply exchanges, verifies stalled timeout and cancellation,
and verifies the owned I/O threads retire. It passes. Another regression rejects
concurrent admission without losing the active context, disposes a blocked pump,
and checks the termination and owner-disposal callbacks each run once.

Three fresh review passes covered request/packet admission and reply integrity,
thread scheduling and lock ordering, then cancellation/disposal/resource lifetime.
The Release solution builds with zero warnings/errors. The final focused GPU,
ICE-authentication and public-transport selection passes 26/26, zero skips. Both
expanded Chrome roles pass after the RTC changes. An intermediate full solution
run passed 4,948 tests, failed zero and skipped two (4,950 total); the final
owner-disposal guard and teardown regression were added after that build.
The final build's full solution run passes **4,949 tests, zero failures, two
skips** (4,951 total; 4m 42.951s). The skips are the explicit Wasmtime-oracle
benchmark and unavailable local symlink creation. Command:
`dotnet test --solution Folio.slnx -c Release --no-build --max-parallel-test-modules 1`.
Normal concurrency inside test modules is retained. Logs were captured in
`%TEMP%/folio-full-final.log`; build and focused logs are
`%TEMP%/folio-release-build-final.log` and `%TEMP%/folio-focused-final.log`.
One intermediate and one final green local run provide progress evidence;
they do not prove an intermittent failure can never recur. Hosted CI for this
patch is pending, and the native GPU worker still needs published-host acceptance
on the final head.

M6 was incorrectly closed while verified blocker #704 had no milestone. #704
is now assigned to M6, M6 is reopened, and #706 is assigned to it as well. Neither
issue is closed. PR #705 remains draft. No other milestone is newly signed off;
all original M0�M7 acceptance and mandatory replacement requirements remain in
the final release audit. Stream reset/restart, queued event/metadata budgets,
Blob/binaryType/options, ICE role/consent/relay/network-policy acceptance, remote
media, iframe/service-worker production routing and release qualification remain
required. No 1.0 tag or release has been published.


## Direct WebGL channel scheduling and published-host acceptance

Following production wiring beyond the surface broker found a second pool
reliance in `RemoteGpuChannel`: it performed command writes on the page caller,
then scheduled reply reads on `Task.Run`. A stalled write was outside its reply
deadline. Fixing `GpuWorkerBroker` alone therefore did not complete #706.

The direct WebGL channel now uses `GpuWebGlPipeTransport`: one owned pump,
a capacity-one queue, one outstanding caller, bounded frame sizes, copied batch
ownership and a synchronous deadline covering both command write and reply read.
A protocol/I/O fault remains terminal and preserves its original exception.
Windows teardown uses the owned thread's handle with
[CancelSynchronousIo](https://learn.microsoft.com/en-us/windows/win32/api/ioapiset/nf-ioapiset-cancelsynchronousio),
repeating cancellation across framed-I/O boundaries within a bounded stop wait.
The pump closes its streams and native thread handle. Broken-pipe disposal
exceptions cannot escape that background cleanup. The channel's worker-exit
watch and fixed-buffer diagnostic drain also use owned threads. Channel disposal
is idempotent and avoids joining the current exit-watcher thread.

The isolated sole-pool-worker probe additionally passes 32 real framed WebGL
pipe exchanges and Windows stalled-read/stalled-write timeout, terminal reuse
refusal and pump-retirement checks. New regressions verify oversized replies
keep `InvalidDataException` rather than an aggregate wrapper, and injected
stream-disposal I/O failures do not escape the owned thread. Three fresh review
passes covered transport ownership/deadlines/bounds, cancellation and shutdown
races, then worker-exit reentrancy and cleanup.

Validation: Release solution build has zero warnings/errors; the final focused
GPU-isolation and remote-WebGL selection passes **26/26**, zero skips. A broader
solution run for the transport patch passes **4,949 tests, zero failures, two
skips** (4,951 total; 4m 49.564s); it predates the final cleanup exception guard
and its two focused regressions. Those final changes are covered by the focused
selection, not attributed to that earlier full run.

A scratch NativeAOT win-x64 publish passed the standalone/patched-compiler/font
checks. Its manifest records `ef56cc6a` plus `sourceDirty: true`, because it
included the transport patch before commit. Using that fresh executable, real
GPU-process/remote-WebGL tests pass **18/18**, zero skips, and strict native D8
conformance passes **6/6** through real GPU workers. This is local patch evidence,
not a clean release artifact. A clean post-commit publication and new-head hosted
CI still need recording. Logs: `%TEMP%/folio-webgl-pump-full.log`,
`%TEMP%/folio-webgl-pump-focused-final.log`, `%TEMP%/folio-pump-publish.log`,
`%TEMP%/folio-native-gpu-pump-tests.log`, and `%TEMP%/folio-native-gpu-pump-d8.log`.

The worker process's dispatcher still uses its own pool for command execution;
this change removes dependence on the content/page process's shared pool, not
all pool use throughout Folio. #706 and #704 remain open, PR #705 remains draft,
and original M0�M7/replacement and release acceptance remain required.


## Metadata admission slice and recurring large-message failure

On `fix/rtc-queued-delivery-budgets`, #707 now has a metadata-admission slice:
a 256 KiB per-association aggregate budget, conservatively charged at twice the
UTF-8 label/protocol byte length before remote string decoding. Local failed
OPEN admission rolls back both metadata charge and stream reservation. Failed
and disposed associations clear owned registries and charge. Closed registered
streams retain their charge until actual registry teardown; stream reset is
still separate required work. Four regressions cover Unicode/exact limits,
closed-state retention, failed-OPEN rollback/reuse, and a raw fragmented remote
metadata flood with sibling-association isolation. SCTP selection passes 56/56,
zero skips, and the Release build has zero warnings/errors. The owner-loop event
queue portion of #707 is NOT implemented; #707 must stay open.

Broader RTC qualification on this slice FAILED: 53/54 passed; the Jint 256 KiB
public round trip exceeded its unchanged ten-second deadline. Both peers were
connected. Sender diagnostics: assigned/emitted236, cumulative ACK200,
36 pending chunks, 39,840 flight bytes, cwnd2,272 and RTO8s. Receiver diagnostics:
contiguous TSN200, 199 retained fragments, no gap reports, 225,268 retained bytes.
This is a contiguous missing tail, not evidence that the metadata budget failed.
A base-head reproduction and deterministic sender loss-recovery test are still
required before assigning the cause. Earlier exact-head green full/published
runs remain valid historical evidence, not proof of eliminating intermittency.

PR #705 is held from merging again. Original #704 remains open. Do not increase
the ten-second deadline or call the reliable-delivery/load acceptance complete
from the earlier passing runs. Logs: `%TEMP%/folio-metadata-tests.log` and
`%TEMP%/folio-metadata-rtc-tests.log`.

## Reopened large-message qualification and deterministic tail recovery

Hosted run `42832983570` completed **26/26 jobs green** for `69a71d8a`.
That result does not clear the subsequent merge hold: on the metadata branch,
a broader RTC selection failed the Jint 256 KiB ten-second round trip with both
peers connected, cumulative TSN200 of236 emitted, no receiver gaps and retained
partial-message bytes. PR #705 was returned to draft. Three repetitions of the
54-test RTC selection on the unmodified `69a71d8a` base all passed; they neither
reproduce that intermittent failure nor establish the metadata patch caused it.
The failure and metadata slice are preserved on draft PR #708, stacked on #705;
#707 remains open and its owner-loop queue portion is not implemented.

A new deterministic regression DOES fail on the unmodified base sender: deliver
200 fragments of a 256 KiB message, drop the entire remaining in-flight tail,
allow one timeout retransmission, then acknowledge it. The sender does not
schedule the remaining missing DATA on the reopened congestion window, instead
waiting for another backed-off timer for each fragment. The regression fails
at the expected empty recovery-dispatch assertion before the fix.

[RFC 9260 section 6.3.3](https://www.rfc-editor.org/rfc/rfc9260.html#section-6.3.3)
requires remaining timeout DATA to be marked for retransmission as cwnd permits.
The sender now explicitly marks that bounded recovery queue, removes declared
lost chunks from flight accounting until retransmitted, and prioritizes their
redispatch over new DATA under both congestion and peer credit limits. The first
timeout still emits only one MTU-sized chunk and waits for its acknowledgement;
RTO backoff and absolute failure/admission budgets remain intact. Diagnostics
include the queued retransmission count.

The pure sender/receiver regression now passes with normal and one-fragment
peer credit. A transport regression drops the full final tail of a 256 KiB
message, restores the path and verifies byte-exact completion plus zero buffered
bytes after one timeout and acknowledgement-driven dispatch. Three fresh review
passes covered timeout/flight accounting, acknowledgement and receiver-credit
interactions, then ordering/retirement and compatibility with existing retries.
Release solution build: zero warnings/errors. Final focused SCTP/RTC selection:
**109/109**, zero skips. This proves the deterministic defect and its repair;
it does not by itself establish the cause of every intermittent UDP/DTLS timeout.
Browser/load/published-host and updated-head CI qualification are pending, and
the ten-second public-test deadline is unchanged. Do not merge from the earlier
head's green CI. Logs: `%TEMP%/folio-tail-red.log`, `%TEMP%/folio-tail-rtc-final.log`.


## Verification harness corrections after the tail repair

The `7c288c23` clean native publication succeeded (`sourceDirty: false`, executable
SHA256 `baf505d3352695742d8b928cd66a23d4b5299fb65c98e4927fbcba9721f7a53b`).
Chrome **157.0.8103.40** passed both offer/answer probes, and three 54-test RTC
repetitions passed. The full solution using that published executable completed
**4,953 passed, one failed, two skipped** (4,956 total). Its only failure was
`ServiceWorkerSafetyLifecycleTests.RevocationOrCloseCancelsAnAlreadyWaitingNonCooperatingHostLoad(close:false)`
at the post-await elapsed-time assertion, not an RTC failure. This is not a green
full run and does not establish that all intermittency is gone.

#709 records the timing-test correction. Start the blocking load on a dedicated
thread, record its monotonic completion timestamp in `finally`, and compare that
instant to cancellation. The original two-second actual-retirement bound,
bounded completion wait, cancellation-exception assertion and propagated-token
assertion remain. Test-runner continuation latency is no longer counted as load
retirement. The focused lifecycle selection passes **29/29**, zero skips. This
is a harness repair, not a claim of repairing production service-worker loading.

Hosted run `42837053404` passed Ubuntu's full suite (4,865 passed, zero failures,
51 platform/oracle skips), but FAILED its independent OpenSSL step. The log
reaches authenticated connected state, sends Folio's encrypted message, then
reports only the OpenSSL-to-Folio marker before terminating OpenSSL about two
milliseconds later. #710 records the probe's early-kill race: the receive loop
stopped when only the incoming marker appeared, killed the peer, and only then
checked its captured stdout for Folio's outgoing marker.

The probe now consumes stdout incrementally, detects markers spanning reads,
and waits for both directions before terminating the peer, within the existing
five-second application deadline. Handshake authentication, selected-source
checks, both plaintext markers and failure diagnostics remain. Three fresh
reviews covered timing/marker semantics, bounded waits and process cleanup,
then partial reads and exception/cancellation behavior. Real OpenSSL probes pass
**three times in each role (six runs)** with both encrypted-data markers. The
local OpenSSL is 3.5.6. Updated Ubuntu CI must still verify this correction; the
failed run is not reclassified as green.

The changes after `7c288c23` affect tests/docs only; production source remains
that clean published head. The new full-load run and updated-head hosted CI
are pending. PR #705 remains draft and unmerged; #704, #706, #707, #709 and #710
remain open. No milestone or 1.0 release is signed off. Logs:
`%TEMP%/folio-tail-clean-full.log`, `%TEMP%/folio-sw-timing-focused.log`,
`%TEMP%/folio-ci-7c-ubuntu.log`, `%TEMP%/folio-ssl-harness-{client,server}-{1,2,3}.log`.
