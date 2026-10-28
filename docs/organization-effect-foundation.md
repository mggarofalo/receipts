# Organization effect persistence foundation (RECEIPTS-1078)

This schema is inert. No production capture, application, undo, scheduler, provider,
or UI path uses it. It supports category assignment effects only.

## Candidate portable evidence increment

`PortableTaxonomyArchive.Effects.cs` defines an internal, unwired representation
and local validator for a future archive extension. The current archive remains
v13: export, ordinary archive reading, preflight and restore do not call this code.
It does not establish portable recovery or authorize a writer.

The candidate uses strict `kind/id/payload` records for manifest, item and outcome.
Each manifest envelope carries its item count and SHA-256 content digest, sealing
the captured member set independently of deployment-local transaction stamps.
Operation reservations are derived from item operation IDs and are not serialized.
The validator rejects duplicate or orphan keys, unavailable frozen taxonomy targets,
cross-phase operation collisions (including retained historical operations), missing
prior assignments, unrecorded assignments under reserved operations, and invalid
apply/undo outcome lineage. Source IDs are frozen evidence and need not refer to
live business rows. Observed outcome revisions remain separate from capture revisions.

Decision evidence must be a JSON object with no duplicate keys, including nested
objects. Membership hashing sorts item IDs, sorts decision object keys ordinally,
retains array order, and ignores whitespace and object key order. Numbers use exact
base-10 value semantics: strip coefficient leading/trailing zeros, adjust an arbitrary
precision decimal exponent, and normalize signed zero to zero. Thus `1e2`, `100.0`
and `100` hash identically, including after PostgreSQL `jsonb` rendering. No binary
floating point or bounded .NET decimal conversion occurs. This is not RFC 8785 or
a policy contract; PostgreSQL still enforces its own numeric storage range.
The original decision JSON is retained in the payload. Request
hashes remain opaque 64-character hexadecimal values: there is no versioned request
or decision schema yet, so the validator cannot recompute or authorize policy intent.
The membership digest detects accidental modification or truncation relative to
its envelope; it is not a signature or proof of an export's global completeness.

Before this representation becomes an archive version, implement and verify export,
strict version/table validation, ordinary retained-history validation, destination
prefix/collision checks and older-archive retention, preflight, transactional restore
with trigger reconstruction, and offline inspection/normalization. Define the policy
and request hash contract before trusting decision evidence in a production writer.

Candidate SQLite export and strict reading now exist in
`PortableTaxonomyArchive.Effects.Sqlite.cs`. They remain unwired from v13. Export
seals membership and validates effect dependencies against caller-supplied history,
then creates and inserts `organization_effect_archive` atomically. The eventual
caller must provide the same source snapshot as ordinary history export and must
validate ordinary history separately. Read rejects duplicate keys even in tables
without a primary key, then validates membership and effect lineage. Both paths
reuse the exact canonical evidence codec described above.

Candidate limits are 1 MiB of UTF-8 per decision, 4,096 characters per numeric token,
and 8 MiB of UTF-8 per record payload. Decision size is checked before JSON parsing;
numeric size is checked before arbitrary-precision exponent parsing. SQLite payload
byte lengths are checked before materializing strings. Accepted values retain exact
decimal semantics. These are candidate format constraints, not policy authorization.

Remaining activation work includes a consistent export snapshot and strict version
and table integration, ordinary retained-history validation, destination retention,
prefix/collision checks, preflight, transactional restore and trigger reconstruction,
offline inspection/normalization, and older-format compatibility. Do not bump the
archive version or enable production effect writers until these are complete together.

Candidate destination preflight now exists in
`PortableTaxonomyArchive.Effects.Preflight.cs`, also unwired from v13. It validates
each side and the retained union, preserves manifests omitted by the incoming
archive (including older formats), requires shared manifests to have identical
metadata and complete canonical membership, and requires their terminal outcomes
to be an immutable prefix. Additional outcomes can append; existing outcomes cannot
be removed or rewritten. Retained effects must still close over incoming assignment
and taxonomy evidence. Reservations are checked against both retained and incoming
historical operations and across the combined effect manifests. The returned union
does not attach or write database rows. The caller must separately validate ordinary
history and hold the restore coordination locks through commit.

Candidate transactional restoration exists in
`PortableTaxonomyArchive.Effects.Restore.cs`, also unwired. It requires an existing
PostgreSQL restore transaction and saved ordinary dependencies. It loads destination
effects and applies the candidate prefix checks before attaching portable copies.
Each new manifest and its complete item set are inserted in that transaction;
PostgreSQL owns the new stamp and reconstructs both operation reservations. Apply
outcomes are saved before undo outcomes after their assignment dependencies.
Existing rows are retained without updates. A private savepoint rolls back all
candidate inserts and detaches their tracked copies on failure, even if the caller
catches the failure and commits its outer transaction. The caller still owns locks,
ordinary history validation and the final transaction commit or rollback.

Real PostgreSQL tests cover candidate SQLite round-trip, destination restamping,
sealed membership, exact reservation reconstruction, unordered apply/undo input,
repeat restoration, older-format retention, invalid lineage, missing transaction
rejection, and failure after item insertion with no partial rows surviving commit.
These primitives do not establish portable recovery: version/table integration,
consistent export snapshots, ordinary retained-history integration, offline
inspection/normalization and older-format compatibility remain required together.

## Inactive v14 export/read composition

`BackupService.ExportV14CandidateAsync` is an internal integration seam, absent
from `IBackupService` and HTTP routes. It reuses the complete ordinary export,
including PostgreSQL Repeatable Read and its read-only source snapshot. It reads
and validates ordinary history from the exported SQLite tables, then seals and
exports effects from the same source snapshot into the existing SQLite transaction.
There is no nested SQLite transaction. Failure discards the candidate file; only
the owner commits the whole file. Non-PostgreSQL test contexts do not establish
concurrent snapshot guarantees.

`ReadV14CandidateAsync` requires exactly version 14 metadata, every v13 dependency
table plus the effect table, and the exact three TEXT effect columns. It validates
ordinary history before effect membership and lineage. The candidate reader is
internal; public import, inspection and normalization still reject version 14.
The public exporter and `PortableBackupFormat.CurrentVersion` remain v13.
Tests compose a full backup with a real non-genesis taxonomy snapshot and pending
manifest, then reject wrong version, missing dependency/effect tables, extra effect
columns, broken ordinary history and truncated sealed membership.

This stage does not establish effect recovery. Still required together before
activation: public version/table integration, restore preflight and retention
integration with ordinary history, effect restoration under the existing exclusive
restore locks and generation fence, offline inspection/normalization preservation,
older-version retention through actual imports, and realistic PostgreSQL full
backup/import tests (including applied/undone assignments and concurrent capture).
Ordinary assignment-authority validation must explicitly recognize validated
effect outcomes before an effect writer can create assignments; it currently accepts
only its existing classification/migration/legacy-write authorities. Do not bypass
that validation to make an effect archive pass. The existing restore generation
fence is unchanged and must continue fencing all future local organizer work.

Request hashes still have syntax validation only; no versioned request contract
exists to recompute them. Decision evidence has structural, size and canonical
membership validation only, with no policy authorization or semantic guarantee.
No production writer or archive version is enabled by this integration seam.

## Inactive assignment authority barrier

`ValidateEffectAssignmentAuthorityCandidate` now runs in the internal full v14
export and reader. It preserves the complete ordinary history validation and the
structural effect validator, then rejects every applied effect outcome. A manual
classification operation with a syntactically valid hash cannot promote matching
effect evidence into automatic assignment authority. Pending and skipped evidence
may pass, but effect apply/undo reservations cannot reuse ordinary taxonomy,
classification or migration operation IDs, including under a different actor.
This conservative candidate namespace rule is additional to the structural
effect/historical reservation checks.

This barrier is intentionally fail closed. The current manifest has an opaque
request hash, arbitrary decision JSON and no versioned assignment command contract.
It cannot prove source/reason provenance, recompute the requested action, authorize
policy evidence, or pin the undo path/revision intent. Matching operation, actor,
item, category, prior assignment and timestamps alone cannot supply those missing
semantics. Before admitting applied effects, define and verify a versioned request
and decision contract covering both phases, exact category path/revision and prior
lineage, the request hash and policy evidence, and the terminal outcome. Add
adversarial tests for every mismatched field and real full backup/import evidence.

Low-level effect codec, prefix and restore primitives still perform structural
validation only; they are inactive and must not be treated as an authority gate.
The v13 public exporter, import/historical validation and version rejection remain
unchanged. Tests cover pending/skipped acceptance, rejection of applied evidence
with and without ordinary manual authority, preservation of ordinary history
errors, both cross-actor reservation phases, and the full candidate export/read
barriers. No v14 format or production writer is activated.

## Inactive v1 typed claim codec

`OrganizationEffectContractV1` is a pure, internal candidate codec. No archive,
entity persistence, authority validator or writer calls it. The applied-outcome
barrier above remains in force. Its `approved-exact-mapping` decision kind names a
claim requiring proof; successfully reading that kind does not establish approval.
Mapping references retain the existing `CategoryMappingReuseEvidence` meanings:
mapping revision, original approval taxonomy revision and source fingerprint.
There is deliberately no new approval boolean or substitute authority.

The item request binds manifest identity, actor, policy version, capture time,
raw source tuple and source revisions, explicit prior assignment identity or null,
target category/revision and ordered category-to-root identity path, both reserved
operations, and typed mapping references. A separate phase command binds the item
request hash, phase operation, expected assignment, category/revision/path and
recording time. These are candidate claims, not an executable assignment command
or a complete manifest-level request contract. Undo may express an absent category
with an empty path, and must name an expected applied assignment.

All constructor fields are required, including nullable fields. Reading rejects
unknown fields, duplicate fields, unknown versions and kinds, malformed identities,
non-uppercase SHA-256 strings, and unpaired UTF-16 surrogates before JSON encoding
can replace them. Object keys use the existing ordinal canonical
JSON codec; nulls are emitted explicitly and path array order is retained. Hashes
are uppercase SHA-256 of UTF-8 domain, a zero byte, and canonical JSON. Domains are
`receipts.organization-effect.item-request.v1`,
`receipts.organization-effect.apply.v1`, and
`receipts.organization-effect.undo.v1`. Timestamps must be UTC at PostgreSQL's
microsecond precision; JSON requires exactly six fractional digits and `Z`.
Submicrosecond input is rejected rather than rounded or silently truncated.

Before using these claims as authority, reconstruct mapping revision operation and
request authority using the existing mapping command semantics, source fingerprint
and exact source tuple, approval taxonomy and current semantic fingerprint, and
revocation/head history at the relevant decision. Verify category paths against
immutable taxonomy snapshots. Bind each phase to its request's reserved operation,
actor, source, expected prior lineage and terminal outcome, and reconstruct undo's
prior category and revision from its original assignment. Define the complete
manifest request and its item membership hash, and its relationship to persisted
`RequestHash`. None of these checks are supplied by this codec. Existing arbitrary
decision JSON and opaque request hashes cannot be upgraded or reinterpreted as v1
authority. New typed persistence/portable envelopes, adversarial semantic tests,
and real full backup/import evidence remain required before changing the barrier.

## Inactive mapping claim consistency

`ValidateEffectMappingClaimCandidate` is an unwired pure consistency validator.
It first runs ordinary history validation, including reconstruction of mapping
commands, request hashes, predecessor chains and approval semantic fingerprints.
It binds the typed request to the frozen manifest and item (including raw source
text, revisions, nullable prior assignment, both operations and stored typed
decision), then checks the referenced mapping's normalized exact source tuple,
fingerprint, category and original approval taxonomy. The target path must match
its immutable taxonomy snapshot and its semantic fingerprint must still match the
approval. Approval and target taxonomy cannot postdate capture. No self-asserted
approval field is accepted.

Success establishes reference consistency only. It does not establish that the
referenced mapping was the eligible head at capture or execution. Mapping chains
order their revisions but capture stores no observed mapping-head revision or
shared ordering token; equal timestamps cannot order capture against revocation.
This increment deliberately supplies no historical eligibility check. It also
does not prove source eligibility, prior-assignment lineage, manifest membership
request hashing, phase command binding, outcome authority or undo intent. The
manifest's opaque `RequestHash` is not compared to the item request hash: those
are different scopes and their relationship remains undefined. No persisted
arbitrary JSON is upgraded automatically. No archive caller invokes this method,
and the applied-effect authority barrier remains fail closed.

## Inactive manifest capture intent contract v1

`OrganizationEffectContractV1.ManifestRequest` is a pure, unwired declaration of
the complete ordered membership of one capture. Its version, manifest identity,
actor, policy version, pinned target taxonomy revision and capture timestamp are
bound together with every complete typed `ItemRequest` in `Members`. Members must
repeat that same manifest context, including capture time. Item identities must
be unique and all apply/undo operation identities share one collision namespace.
An empty vector is a valid declaration of zero members. The vector's order is
explicit intent: neither timestamps nor item IDs sort it. Equal capture times
are expected and do not establish any causal order.

`WriteManifestRequest` emits canonical JSON using the existing v1 contract's
ordinal object-key ordering, explicit nulls, exact Unicode strings and strict
UTC microsecond timestamps. It preserves member and category-path array order.
`ReadManifestRequest` rejects unknown, duplicate or missing fields and validates
every nested typed item. `ManifestRequestHash` is uppercase SHA-256 over UTF-8
`receipts.organization-effect.manifest-request.v1`, a zero byte, then that JSON.
It binds full member requests, rather than only their IDs or item hashes, and is
domain-separated from item request and archive membership digests. Removing,
adding or reordering a member changes the declaration and digest. Approval
taxonomy revision in mapping evidence is historical evidence and need not equal
the manifest's pinned target taxonomy revision; this codec does not evaluate it.

The existing canonicalizer imposes a 1 MiB (1,048,576 byte) limit on the entire
manifest JSON, including all nested members. On read, both UTF-16 code-unit length
and UTF-8 byte count of the supplied JSON, including whitespace, must be within
that limit. On write, the serialized whole request must fit before canonicalizing;
JSON escaping contributes to its size. This is not a per-member allowance or a
fixed member-count limit. Oversized declarations fail without producing a hash.

This digest is a claim binding, not proof that a capture observed a mapping head,
that the declared vector is the sealed database membership, or that any operation
is authorized. No current manifest hash is reinterpreted, compared or upgraded.
No producer or authority gate is activated by this contract. The applied-effect
barrier remains closed until execution authority is designed and validated.

## Inactive full candidate import composition

The internal BackupImportService.ImportV14CandidateAsync seam now composes the
strict candidate reader with the ordinary business import. It is absent from the
public service interface and endpoints. Public import, inspection and offline
normalization still reject v14; PortableBackupFormat.CurrentVersion remains 13.
Offline normalization must not rewrite or strip this candidate extension.

Under the ordinary local/database restore locks and taxonomy head lock, the seam
runs ordinary ValidateDestination and source-revision preflight, then validates
existing effects, incoming effects and their retained union before business
writes. The authority barrier rejects applied effects, including retained applied
outcomes. Only the bounded structural evidence candidate is supported; portable
execution authority remains unimplemented.
Opaque decision/request evidence is never upgraded into authority.

Business dependencies, ordinary immutable history, effect restoration and the
normalization/category-assistance generation fences share the outer transaction.
A later failure rolls all of them back. Effect restoration uses its existing
savepoint, PostgreSQL stamps and operation reservation triggers. Repeating an
archive is idempotent for evidence, while each successful restore advances the
local generations. Candidate imports of v10-v13 retain omitted local effects and
validate their dependency closure; pre-v10 archives are explicitly rejected by
this seam because their legacy migration composition has not been validated for
effect retention. The ordinary public legacy import behavior is unchanged.

The PostgreSQL integration case covers full export to a fresh destination,
repeat restore, retention through older v10-v13 archives, skipped terminal results,
destination manifest conflict rejection, and invalid-input rollback of business
rows and the assistance generation. A fault after effect item insertion checks
that restored business/assignment dependencies really exist at the fault point
and that the outer rollback removes them, effect manifests, members, operations
and outcomes while preserving the original generation. Pre-v12 imports lack the
archived source-revision evidence; a later replay of the original newer archive
can therefore fail ordinary source-revision prefix validation.
This is bounded integration evidence, not archive activation. Offline inspection/
normalization preservation is covered by the inactive seam below. Applied assignment
authority and broader version compatibility remain unresolved.


## Inactive candidate offline preservation

Internal candidate constructors on OfflinePortableBackupInspection and
PortableBackupNormalizationService now compose the strict v14 reader with the
ordinary offline workflow. Public constructors, manifest parsing, current-format
export and import remain v13. The internal manifest reader permits only the v14
version extension and delegates all ordinary review, identity, duplicate JSON,
source binding and decision validation to the existing policy.

Candidate inspection and normalization compare the complete exported v13 schema
plus the exact effect archive schema, reject unsupported objects, and run ordinary
catalog/history/deletion checks plus sealed-effect membership and assignment
authority checks. Applied effects remain rejected. The transformer copies the
original artifact, changes only the ordinary normalization state/lineage, retains
version 14 and never rewrites effect rows. Every effect column and its SQLite
storage class contributes to the factual baseline. Verification rejects a changed
payload even when its JSON meaning and the refreshed whole-file checksum agree.

Synthetic SQLite coverage includes manifest/item/skipped-outcome content bytes,
source immutability, candidate-only access, repeated apply/verify, and corruption
with a refreshed lineage checksum. This does not activate a format or establish
portable execution authority; that work remains unresolved.

The inactive effect reader requires TEXT storage and decodes raw SQLite bytes
with strict UTF-8 before parsing. Malformed TEXT cannot be normalized by provider
replacement fallback into identical history facts. A regression replaces a valid
literal U+FFFD in a skipped outcome with CAST(X'FF' AS TEXT), refreshes lineage,
and requires both inspection and verification to reject the artifact. Direct
reader cases cover malformed UTF-8 and BLOB storage in each envelope column.

Real PostgreSQL composition evidence now runs the four candidate-import cases
with a normalized artifact: export v14, apply a reviewed canonical creation,
verify the source bytes and complete effect-table digest, restore into a fresh
migrated database, and compare exported manifest/item/skipped-outcome content.
The cases retain repeated import, immutable conflict rejection, atomic rollback
and older-format v10/v11/v12/v13 evidence retention checks. PostgreSQL jsonb
formatting is compared against the original exported evidence, while SQLite
normalization preserves the exported payload bytes exactly.
