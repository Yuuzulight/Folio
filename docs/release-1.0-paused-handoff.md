# Paused 1.0 handoff — 2026-11-10

Folio is **not release-ready**. The user requested this pause. The goal remains
the entire original M0–M7 scope, mandatory Folio-owned replacements, full
verification and a 1.0 release. No milestone was newly signed off here.

## Repository state

- Checkout: `D:\GitHub Projects\Folio`.
- Branch: `fix/rtc-authentication-release-blockers`.
- PR: https://github.com/Yuuzulight/Folio/pull/705 — unmerged.
- Release blocker: https://github.com/Yuuzulight/Folio/issues/704 — open.
- Validated production checkpoint: `fb9228b0`; its audit update is `8ffaa55c`.
- The commit containing this handoff adds **work-in-progress fragmentation**.
  Do not describe it as fully qualified or publish 1.0 from it.

## Completed and verified before fragmentation

- DTLS certificate proofs, encrypted records, replay protection, bounded
  reassembly/retransmission and independent OpenSSL encrypted exchange in both
  roles. These are supported-profile checks, not complete DTLS/WebRTC conformance.
- Real public native/Jint data-channel strings and buffer sources over
  ICE/DTLS/SCTP, with owner-loop delivery and owned teardown.
- SCTP association tag/port checks, expiring authenticated cookies, association
  retry/deadline handling and complete-DATA reliability: per-stream sequencing,
  duplicate suppression, cumulative/gap SACKs, reneging, bounded retention and
  transport output, and outside-lock packet dispatch.
- Public buffered amount and low-threshold events driven by acknowledgements.
- Selected BUNDLE/media transport parameters, authenticated peer-reflexive ICE,
  response source/integrity checks and selected-source datagram filtering.
- Real Chrome **156.0.8078.80** connects in both Folio offerer and answerer roles;
  both peers open channels and exchange/echo three ordered strings each way.
  Default mDNS privacy remains enabled. This uses peer-reflexive discovery,
  not an implemented mDNS resolver. Ubuntu CI now includes both probes.
- Full Release solution build: zero warnings/errors. Focused RTC: **84/84**.
  Complete scripting suite: **1,849/1,849**, zero skips, **serialized**.
- Hosted run `42779691574` passed for the earlier `ec263561` checkpoint. The
  later browser/reliability changes did not yet have completed hosted evidence
  at the pause check. Do not transfer that earlier green result to later code.

## Fragmentation work checkpointed at this pause

Implemented but not fully qualified:

- 1,120-byte outgoing fragment payloads, consecutive TSNs, one stream sequence
  per message, begin/end flags and atomic admission checks.
- Reassembly with duplicate/reordering handling, metadata consistency checks,
  bounded retained bytes/messages, 60-second deadlines, and bounded-size SACKs.
- A 256 KiB local message limit and 1 MiB receive storage. These are explicit
  implementation bounds, not a claim of unlimited message support.
- SDP `max-message-size` parsing/advertising, the absent-attribute 65,536-byte
  default, zero/unbounded peer interpretation capped by local admission, public
  buffer-source size checks and remote SCTP destination-port wiring.
- Tests for fragment permutations/duplicates, TSN wrap, unordered reassembly,
  conflicting metadata, atomic admission rejection and bounded gap reports.

Evidence so far: scripting project builds with zero warnings/errors; an earlier
focused selection passed **38/38**, and the latest fragmentation/reliability
selection passed **18/18**. The 1,849 full-suite and Chrome results above
**predate fragmentation**. No new full-suite or independent large-message result
has been established for this work-in-progress commit.

## Resume here

1. Review fragmentation/reassembly and negotiated-limit/port behavior from
   first principles. Check malformed chains, missing begin/end, oversize receive,
   timeout cleanup, atomic refusal, per-stream ordering and bounded memory.
2. Add real association tests for dropped middle fragments, conflicting
   authenticated chunks and partial-chain expiry. Add default/zero/invalid SDP
   size-limit and destination-port regressions.
3. Extend the Chrome probe in both roles with large byte-exact binary, UTF-8
   and empty messages, boundary sizes and oversize refusal. The current probe
   exchanges only small strings.
4. Verify large public native/Jint buffer-source delivery and echo, including
   source mutation, limits, close/revocation and buffer events.
5. Perform the requested three fresh review passes before treating the slice
   as finished. Rerun the solution build, full scripting suite and hosted CI.

## Other required work before 1.0

- SCTP congestion/peer-window flow control, stream reset/remote close, restart
  and broader independent conformance; finish fragmentation acceptance above.
- JavaScript event count/byte budgets, Blob/binaryType behavior and remaining
  data-channel options.
- ICE role conflicts, consent freshness, relay/TURN wiring, direct mDNS
  resolution, host network-policy enforcement and network/cancellation budgets.
- Encoded remote media transport and real-device/runtime qualification.
- Iframe/service-worker production routing against original acceptance.
- Resolve the four earlier unrestricted-parallel timing failures: GPU worker
  cancellation, both service-worker registration timeout cases, and one public
  RTC negotiation deadline. Serialized passes do not resolve load qualification.
- Re-audit every original M0–M7 requirement and the mandatory replacement gate.
  Earlier milestones are not newly recertified by this RTC work. Inspect actual
  production dependency/routing state rather than assuming earlier closure or
  a passing differential corpus proves Folio-owned replacement completion.
- Final strict conformance, performance/memory/startup/frame budgets, fuzzing,
  full platform CI, packages and isolated external consumers, documentation and
  release qualification against the final reviewed commit; merge appropriate
  PRs and publish 1.0 only after all required gates are proven.

More detailed historical evidence: [release-1.0-audit.md](release-1.0-audit.md).
