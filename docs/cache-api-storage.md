# Cache API storage

Folio's `CacheStorage` and `Cache` implement named caches, matching, insertion,
atomic `addAll`, deletion and request enumeration. Requests use canonical HTTP(S)
URLs, GET methods and response `Vary` headers; query options can ignore the
method, search or `Vary`. Cached responses retain independent binary bodies and
their response metadata. `add` and `addAll` use the configured resource loader.

## Host configuration

Set `FolioOptions.ArtifactStorage`, `ArtifactId` and
`Limits.MaxServiceWorkerCacheBytes`. Without a storage provider the document uses
memory only. Use the same provider instance for every context sharing a backing
store: Folio coordinates concurrent pages and content processes through that
instance. Hosts must not independently overwrite Folio's snapshot keys while
those contexts are active.

`IArtifactStorage.Write` must atomically replace one key. If it throws, the old
value must remain intact. Folio persists one complete transaction before
publishing its new in-memory state; rejected fetches, batches, quotas and storage
writes cannot publish partial cache entries.

The cap measures the UTF-8 serialized snapshot, including metadata and base64
bodies. All named stores count, including persisted stores that have not been
opened. Deleted names leave existing `Cache` handles usable, and their retained
data still counts until those handles are collected. A subsequent `open` creates
a different store. Deletion remains possible if the host lowers its cap below
the size of existing data.

An adversarial batch exceeding one million comparisons between `Vary` variants
rejects with `QuotaExceededError`; unrelated URLs use an index. Quota accounting
and transactions are enforced in native code using private Fetch state, so
replacing script globals or response getters cannot raise the host's cap.

## Content processes

`FolioView` forwards the configured storage, artifact identity and document
limits. Applications starting a process directly can pass `artifactStorage`,
`artifactId` and `documentLimits` to `ContentProcessClient.Start`. Those settings
are fixed by the parent and override corresponding fields in load payloads.
Existing three- and five-argument overloads remain available.

The child sends semantic cache operations to the same host ledger used by
in-process documents. It cannot write serialized snapshots or choose another
artifact's identity or quota. Registration metadata has a separate restricted
namespace, at most 1,024 records, 65,536 characters per value and 1,048,576
characters in aggregate including keys. Each connection permits 64 cache
contexts and 4,096 live remote handles; collected handles and document teardown
release their host leases.

Storage has a separate bounded dispatcher, so waiting document callbacks do not
block it and correlated replies can still be read. Large requests and replies
use ordered 1 MiB chunks with length and SHA-256 checks. One upload and one reply
are staged per connection; logical transfers are capped at the cache limit plus
1 MiB of protocol overhead. The existing 16 MiB frame limit remains unchanged.
Incomplete uploads never execute a cache operation.

A host storage call has a ten-second service deadline. An interrupted operation
with an uncertain commit outcome stops the requesting content process. The
host's synchronous provider may still finish that approved operation; the shared
ledger stays serialized until it finishes. A fresh context reads the committed
state. This is not reported to a live document as a recoverable failed write.

## Upgrading prototype storage

Native snapshots use the length-delimited key
`folio/cache-v2/{artifactId.Length}:{artifactId}`. When absent, Folio migrates
valid prototype `service-worker-cache` entries in one atomic replacement. The
current document's base URI resolves old relative request keys. Original entries
remain as backups; after confirming migration the host may remove those legacy
keys. They are outside the active snapshot accounting.

Invalid entries, canonicalized request collisions, or old `Vary` responses
without recoverable request headers reject with `UnknownError`, preserving the
original data. The host must resolve those records explicitly. Failed migration
can be retried after the provider recovers.

This verifies Cache API storage. Service-worker isolation, interruption and
subresource interception remain separate requirements tracked in the
[milestone audit](milestone-6-audit.md).
