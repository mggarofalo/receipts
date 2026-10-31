# Explicit category mapping foundation (RECEIPTS-1049)

`ICategoryMappingService` owns reusable category approvals independently of product
normalization. Product links, templates, suggestions, model confidence, and historical
purchase categories never constitute approval. The service does not rewrite purchases.

An approval identifies an exact normalized `(location, description, itemCode)` source.
It reuses `NormalizationCatalogPolicy.Normalize/Hash`: only surrounding U+0020 is
trimmed; an empty code becomes null; case, tabs, Unicode, different codes, different
descriptions, and different locations remain distinct. The stored values are checked
alongside the SHA-256 fingerprint when resolving.

`ChangeAsync` requires a persisted enabled actor, unique operation ID, reason,
current expected taxonomy revision, and expected mapping revision (null only when
there is no mapping). A non-null category explicitly approves; null explicitly
revokes. Every change appends evidence with the actor, timestamp, request hash,
previous mapping revision, category identity, approval taxonomy revision, and semantic
fingerprint. Actor/operation ID retries return the original immutable result, even
after a later revocation. A different payload with the same operation ID conflicts.
PostgreSQL changes first lock `taxonomy.Head`, sharing taxonomy's lock order;
competing approvals and taxonomy edits cannot pass stale revision checks together.
The mapping head additionally uses optimistic EF concurrency and a unique source key.

Resolution uses the current taxonomy revision and active category identity. Names
are presentation: renames and unrelated changes preserve approval. A changed definition
on the category or any ancestor, or a changed identity ancestry, requires renewed
approval. Missing or archived targets/ancestors are unavailable. Mapping provenance
retains the original approval revision while a new purchase uses the current revision.
Explicit selections, including clearing the category, take precedence; an invalid
explicit selection returns a rejection disposition without falling back to a mapping.

Aggregate receipt saves now reuse approvals for genuinely new items with no explicit
classification intent and empty legacy category/subcategory labels, when classification
writes are enabled and the legacy migration is complete. Existing assignments, including
manual clear, survive incidental financial or source edits. Nonempty legacy labels retain
the compatibility writer's behavior; they may be deliberate or imported evidence and do
not constitute a request to apply a mapping. Future contextual defaults must preserve this
distinction.

The aggregate writer holds `taxonomy.Head` before reading the mapping through its own
DbContext and transaction. Approval, revocation and taxonomy changes take the same lock.
It checks normalized source values as well as the fingerprint and current semantic
validity, then appends classification and operation evidence atomically with receipt
facts. A preview alone never authorizes a write. Missing, revoked, stale or unavailable
mappings retain the existing `legacy_write` fallback (including unresolved null), without
calling a model/provider.

Mapping assignments use source `approved_mapping`, the current taxonomy revision, and a
JSON `CategoryMappingReuseEvidence` reason containing the immutable mapping revision,
original approval taxonomy revision and exact source fingerprint. The assignment actor
is the receipt writer; the referenced mapping retains the original approving actor,
operation, reason and timestamp. A deterministic item/mapping operation ID and the
aggregate receipt request replay prevent duplicate assignment evidence. Format 11
preserves the reason and authoritative classification operation unchanged.

Authenticated inspect/history and Admin approval/revocation HTTP contracts are available.
The review UI remains required before the feature is enabled or released.

## Required before enabling or releasing

- Portable format 11 now preserves the approval history and heads described below.
- Add review UI for the explicit approval/revocation HTTP contracts, conflict handling
  and current mapping provenance. The API uses one required nullable category field.
  Inspect/history require authentication; changes require Admin. Category paths use
  the existing immutable taxonomy revision endpoint. Inspection combines advisory reads;
  writes always recheck the supplied source, mapping and taxonomy revisions.
- Add frontend tests covering the eventual approval and entry user flow. The internal
  aggregate reuse path has PostgreSQL tests for precedence, revocation races, atomic
  rollback, retries and portable provenance. Keep model/provider adapters outside these
  application-owned rules.

## Portable history (format 11)

`category_mapping_archive` stores every revision, including revocations, and each
source head using strict versioned JSON records. Original IDs, timestamps, actors,
operation IDs, request hashes, source values/fingerprints, category and taxonomy
revision IDs, previous revision links and semantic fingerprints survive unchanged.
Inspection and import validate normalized sources, recompute request and semantic
hashes, require a single acyclic unbranched chain per source and its latest head,
and reject unknown or missing taxonomy evidence and duplicate actor/operation IDs.

Restore holds the taxonomy head lock and appends taxonomy snapshots before mapping
history in the same transaction as domain upserts. Existing immutable mapping rows
must be an exact subset of the incoming history; an older or divergent format 11
archive is rejected. History is never updated or deleted and immutable PostgreSQL
triggers remain enabled. Heads advance only to the validated archived chain tip.

Formats 10 and earlier explicitly bring no category approvals. Their domain upserts
and existing taxonomy/history compatibility rules still apply; existing destination
mapping history and heads are preserved. Offline inspection includes mapping rows
in factual digests, and offline normalization preserves format 11 and all approval
evidence unchanged. Release enablement still requires the entry-flow requirements
above; API approval is always an explicit Admin operation, independent of purchase acceptance.

## Background reuse of approved mappings (RECEIPTS-1078)

`ApprovedMappingBackgroundWorker` implements bounded deterministic reuse for historical
purchases. The long-running API host registers it alongside the Classification
workspace's automatic activity and conditional undo surface. There is no new setting
or consent toggle, and this worker
never resolves a suggestion provider. Existing classification-write configuration and
migration readiness still apply. This activates reuse of explicit approvals. Strong Jev
matches are written by the category assistance worker instead (see
[the Jev adapter](jev-suggestion-adapter.md)); they share this activity list and undo, and
never create or approve a mapping.

Each pass advances at most one 100-purchase frozen sweep page. A rotating scan of
current mapping heads revisits sources to find later purchases, including item IDs
below a previous sweep cursor. A restart resumes an unfinished page from persisted
operation scope and its original restore generation. Restore invalidates local sweep
checkpoints; imported pending operations do not acquire new execution authority.
Revoked mappings, changed taxonomy semantics and saved review choices are rechecked
by the existing capture/executor transactions. Manual classifications and clears are
preserved. Committed background changes publish a silent receipt-item invalidation.

Operations belong to the mapping approver and appear in that actor's automatic
classification activity list. The public undo route now dispatches approved-mapping
operations through conditional undo, capturing the restore generation at request time.
Undo writes a manual correction, including an explicit null, so subsequent mapping
scans cannot immediately reapply it. New automatic capture is not a user preview
requiring acceptance; its frozen evidence exists to make execution and correction
recoverable. The Classification workspace exposes that activity and undo alongside
the active worker.
