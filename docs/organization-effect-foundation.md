# Organization effect persistence foundation (RECEIPTS-1078)

This schema is inert. No production capture, application, undo, scheduler, provider,
or UI path uses it. It supports category assignment effects only.

## Inactive typed capture and execution fences

`OrganizationEffectFenceContractV1` defines immutable version 1 evidence values.
Capture evidence binds the manifest's canonical request hash to
`category_assistance.Settings.Revision` and `Settings.RestoreGeneration`.
These are authoritative deployment-local fences: the settings store rotates Revision
on every settings update, and `CategoryAssistanceRestoreFence` rotates both values
while disabling assistance during restore. `organization.Runs.PolicyRevision` is
only a mutable discovery cache of Settings.Revision and is never an authority source.
The manifest's textual PolicyVersion remains a contract version, not a consent revision.

Capture hashing uses SHA-256 over the UTF-8 domain
`receipts.organization-effect.capture-fences.v1`, a NUL separator and canonical JSON.
Execution evidence separately binds that capture hash, the existing item request hash
and phase command hash under `receipts.organization-effect.execution-fences.v1`.
The values contain no mutable arrays and do not change the existing V1 request,
witness, phase or archive format.

The pure validator compares supplied captured and current authoritative projections.
Apply requires enabled valid settings with the captured Revision and restore generation,
the exact source tuple and both source business revisions, the expected assignment,
current mapping revision and taxonomy revision, selectable target and existing purchase
preservation rules. Disable then re-enable cannot revive an old capture. Reverting
source text cannot revive it after a business revision changes. Manual null assignments
remain deliberate choices. Undo compares restore generation, exact source and assignment,
taxonomy and previous-category selectability; it does not require an enabled provider,
unchanged settings Revision or a still-current mapping. Undo's authenticated applied
assignment and prior-category lineage remain separate mandatory prerequisites.

These types and hashes are **unauthenticated semantic evidence**. A null mismatch result
only means the supplied facts agree. A future database transaction must derive and seal
the capture facts from authoritative rows under the established locks, persist the
evidence durably and compare fresh authoritative execution facts before writing.
The existing `OrganizationEffectCaptureWitnessesV1` does not attest these settings or
restore fields; this contract does not retrofit it or provide that transaction.
Actor authorization, complete capture membership, mapping authority and undo lineage
still require transactional authentication. No writer, provider call, approval gate,
archive version or production activation is added. The applied-effect barrier stays closed.

A manifest stores actor, request hash, policy version, pinned taxonomy revision and
capture time. Each item freezes source identity/text, receipt and item business
revisions, the expected prior assignment (including absence), target category and
revision, policy evidence, and distinct stable operation IDs for retries.
The immutable prior assignment retains the previous category and revision.

Capture manifest and every item in one PostgreSQL transaction. A local shadow
transaction stamp seals membership on commit; callers cannot forge that stamp.
All four tables reject update, deletion and truncation. A shared operation reservation
table owns one primary key namespace across apply and undo; PostgreSQL populates
its item/phase lineage atomically with each item insert. Unique enforcement also
rejects collisions outside a Repeatable Read transaction snapshot. Outcome rows append one
terminal apply or undo result per item. Assignment references must belong to the
same item and match the reserved operation and prior assignment lineage. Undo
requires an applied result and must restore the category from the original prior
assignment, or absence when there was no assignment. Successful outcomes retain
assignment and observed source revision references separately from frozen intent.

This is evidence storage, not the conditional mutation algorithm. A future writer
must atomically compare current source revisions and current assignment against
intent before applying, and against the applied outcome before undoing. It must
serialize with existing classification/taxonomy writers, commit the assignment and
outcome together, validate policy evidence, and compare request hashes on retry.
A skipped outcome is terminal; a fresh decision requires a new manifest.

Activation prerequisite: portable backup currently enumerates explicit tables and
does not export these new rows. Before adding any production writer, introduce a
versioned archive extension with strict keys, dependency closure, collision and
prefix validation, and round-trip tests. Export portable IDs and evidence only;
exclude WriteTransaction. Restore each new manifest with all items in one
transaction so PostgreSQL restamps and reseals it, allow item triggers to reconstruct operation reservations, then append outcomes after their
assignment dependencies. Restore generation must fence any future local work.
Older archives cannot assert completeness of effect history. Do not claim portable
recovery of these rows until that extension is implemented and verified.

The migration refuses downgrade because dropping these tables destroys evidence.
Integration tests exercise actual PostgreSQL migration replay, sealed membership,
immutable evidence, duplicate terminal results, invalid undo, invalid outcome
references, forged stamps, cross-phase Repeatable Read collisions, same-item
operation/category/lineage mismatches, and successful separate apply/undo outcome persistence. They do not
establish conditional mutation or archive correctness; those paths do not exist.

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

## Inactive mapping authority journal replay v1

`MappingAuthorityJournalPolicyV1` is a pure candidate ordering policy in Application.
It is not registered or called by a writer, importer, archive validator, or assignment
authority gate. It adds no persistence, archive version, provider calls, or migration.

A future durable stream must start with a complete snapshot of current exact-source
mapping heads (including revocation heads), sealed while holding the existing
`taxonomy.Head` lock. Its seal must live in an independently trusted store. The v1
baseline digest is uppercase SHA-256 over the UTF-8 domain
`receipts.mapping-authority.baseline.v1`, a zero byte, and the policy's JSON baseline
projection. Heads are sorted by ordinal location, description, then item code;
version, stream identity, baseline identity, exact source tuple and revision identity
are bound. This is a candidate internal digest definition, not a portable codec.
Hashes are not signatures: recomputing a digest does not authenticate a baseline.
The caller must establish the provenance and completeness of the seal and snapshot.

The baseline authenticates the starting state for future events only. It cannot
retroactively prove when an old capture occurred, and replay emits no capture from
a baseline head alone. Existing predecessor chains and wall-clock timestamps cannot
be upgraded into this evidence. A future producer must append every head transition
and capture in the same transaction as its business change, under `taxonomy.Head`,
allocating contiguous stream positions without consuming committed positions on
rollback. Baseline creation, transition, and capture must share that lock. No such
producer exists in this increment.

Replay accepts the complete ordered prefix after the baseline and an independently
trusted terminal position. It rejects gaps, forks, repeated event identities, unknown
versions/kinds, missing mapping evidence and terminal-prefix truncation. Event IDs
and their predecessor links check ordering consistency; they do not authenticate
payloads. Trusted append-only storage and authenticated export/checkpoint provenance
remain necessary. An untrusted journal cannot supply its own trusted seal or terminal
checkpoint. Suffix omission cannot be detected without an independent terminal anchor.

Each transition must extend the replayed head for its normalized exact ordinal source
tuple and fingerprint. Null-category revisions remain heads after revocation. A capture
must reference the current head for that same tuple, and that head must have a category.
The capture identity is unique within the replayed prefix. A later revocation does not
invalidate an earlier recorded capture; a prior revocation blocks old approval reuse.
Revision timestamps, including equal timestamps, have no ordering role. Failure returns
no partial capture results.

Revision evidence must separately pass ordinary immutable mapping command/request,
actor, operation, predecessor history and approval-taxonomy semantic validation. This
policy checks source binding and ordering, not taxonomy selectability at capture,
purchase eligibility, actor authorization, item/manifest request binding, execution
head revalidation, assignment lineage, or undo authority. Its capture IDs require a
future typed durable binding to the actual frozen effects. Replay success grants no
assignment authority; the existing applied-effect barrier remains closed.

## Inactive authority journal storage candidate

Migration `20261028150000_AddMappingAuthorityJournalStorage` adds SQL-owned tables
outside the mutable EF model. It creates no baseline and installs no production
writer. Inserting the single baseline snapshots every current mapping head,
including revocations, under the existing `taxonomy.Head` transaction lock.
Baseline heads cannot be supplied by ordinary DML or extended after sealing.
Both live baseline insertion and event append require READ COMMITTED isolation;
other isolation levels fail before acquiring the lock. PostgreSQL snapshot
isolation can retain old mapping heads even after waiting for this lock, because
mapping changes need not update the taxonomy head row itself. Statement-fresh
reads after lock acquisition are part of this candidate live-write contract.
The baseline stores identities and immutable revision references. The later
anchor migration below retains its exact v1 commitment at the snapshot boundary.

Event insertion takes the same lock, derives the next ordinal and predecessor
from committed/transaction-local events, and rejects supplied gaps or forks.
Rollback consumes no ordinal. The original tip was a derived view; the later
anchor migration replaces it with an independently retained checkpoint view.
Transitions extend the journal's exact source head and must
reference the actual mapping head; captures require that same approved head.
Baseline/event rows reject update, delete and truncate. Source equality uses
ordinal C collation; timestamps and SQL sequences have no ordering role.

This protects ordering and retention against ordinary table DML, not database
owners, DDL, disabled triggers, or superusers. Mapping revision source
normalization, fingerprints, actor/command authenticity and taxonomy semantics
remain the existing validators' responsibility. The database does not require
all mapping changes to append events: existing writers remain inactive with
respect to this journal. Consequently this is not a trusted production authority
store yet, and its tip is not an independently authenticated archive checkpoint.
Capture identities still lack typed effect bindings. A future activation must
atomically append every mapping change and frozen capture, validate trusted
baseline provenance, and bind captures to effects before granting authority.

Restoring imported journal evidence is not implemented by these live-write
triggers. A future restore path needs its own validated, atomic protocol for
baseline completeness, event lineage, independently trusted terminal anchors,
and final mapping heads; it cannot replay historical events against the live
head guard or bypass isolation/immutability guards and claim equivalent trust.

### Inactive database baseline and terminal anchors v1

Migration `20261028210000_AddMappingAuthorityAnchorsV1` retains a baseline
commitment and a terminal checkpoint outside the mutable EF model. The baseline
snapshot trigger derives exact source tuples from the stored immutable revision
rows, serializes the policy's property order and .NET JSON escaping, sorts source
tuples by UTF-16 ordinal order (including null item codes first), and retains the
canonical bytes plus the domain-separated `MappingAuthorityJournalPolicyV1`
baseline hash. No incoming hash or caller-supplied source projection is trusted.
PostgreSQL integration tests compare the retained hash to the .NET policy across
escaping, supplementary Unicode and BMP ordering.

The same baseline transaction creates checkpoint `(0, baselineId)`. Each event
append holds the existing taxonomy head lock, checks the retained checkpoint
against the event count and terminal identity, and advances it in an AFTER
trigger. Failure or rollback rolls back both event and checkpoint. Concurrent
appends serialize under the existing READ COMMITTED boundary. The public
append contract permits one event per SQL statement: a multi-row INSERT fails
the checkpoint consistency check and rolls back the entire statement. Multiple
separate append statements in one transaction are supported. The
`MappingAuthorityTip` SQL view now reads this independent checkpoint; losing an
event suffix cannot silently rewind it. Direct insert/update/delete/truncate of
anchor evidence is rejected except for the specific nested baseline/append
trigger maintenance. Database owners, DDL and disabled triggers remain outside
this protection.

Existing baselines, heads and events survive upgrade unchanged. They receive no
synthetic anchor: their original completeness cannot be established by hashing
the surviving rows. Legacy streams have no retained tip and reject new appends;
there is deliberately no automatic reseal, reset or destructive replacement.
Future activation needs an explicit migration/provenance protocol for them.

These are local structural completeness anchors, not cryptographic external
authenticity or authenticated export provenance. A portable reader must obtain
the seal and checkpoint independently of the untrusted incoming rows; recomputing
both from those rows provides no protection. Archive portability and restore
consumption remain unimplemented. Public backup version stays **13**, the applied
effect barrier remains, and production mapping/capture writers remain unactivated.

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
The existing journal's arbitrary `CaptureId` still has no durable typed witness.
No writer, schema migration, archive version or applied-effect authority gate is
changed by this contract.

A future activation must persist a versioned witness linking the journal capture
identity and stream position to manifest identity and this exact request digest,
and seal the complete ordered typed request vector together with all effect rows
in the same transaction under the existing taxonomy lock. Durable member ordinals
or the retained canonical vector must preserve order; SQL row enumeration and
wall-clock times cannot recover it. The witness must enforce its foreign-key and
mapping evidence relationships and verify the frozen row projection against the
request; this codec performs no database observation. Every mapping transition
must participate in that ordering protocol. Baseline completeness/provenance and
an independently trusted terminal checkpoint remain prerequisites. A self-supplied
digest or journal tip is not a trust anchor, and historical captures cannot be
retroactively promoted from timestamps or recomputed hashes. Until the witness,
transactional producer and trust protocol exist and are validated, replay success
and codec success grant no assignment authority; the applied barrier stays closed.

## PostgreSQL string encoding compatibility prototype

`OrganizationEffectPostgresStringPrototypeTests` contains a session-local
`pg_temp.effect_v1_string(text)` research helper. It is not installed by a migration
and has no production caller. It independently derives a quoted JSON string's
UTF-8 bytes from PostgreSQL UTF8 text. PostgreSQL `to_json`/`jsonb::text` escaping
is not interchangeable with the current default .NET writer's bytes.

The differential test compares the helper with actual default `Utf8JsonWriter`
output for every PostgreSQL-representable Unicode scalar (U+0001 through U+10FFFF,
excluding surrogate code points). Mixed strings also pass through the actual v1
canonicalizer. Cases cover quotes, backslashes, short control escapes, other
controls, HTML-sensitive ASCII, combining characters, BMP noncharacters and
supplementary pairs. The helper retains code points exactly, without normalization,
uses uppercase UTF-16 escapes, and requires UTF8 server encoding. SQL NULL remains
SQL NULL; it is not the empty string or a quoted JSON null token.

NUL, isolated surrogates, invalid UTF-8 and out-of-range scalar encodings cannot
be PostgreSQL text inputs. Tests verify database rejection and v1's managed-text
rejection before serialization can replace malformed UTF-16. These restrictions
must remain explicit at future persistence boundaries.

This establishes string-encoding parity for the runtime exercised by the tests,
not a permanent promise about future .NET encoders. Keep the differential gate
when upgrading the runtime. It does not implement object ordering, numbers,
timestamps, typed row projections, whole-request size limits or domain-separated
hashing. A future database witness must independently construct and validate the
complete v1 canonical request from frozen rows before comparing a digest. Caller
JSON or a caller-supplied hash alone cannot establish that relationship. No
witness, writer, archive version or authority activation is supplied here.


## Inactive transactional capture binding candidate v1

Migration `20261028170000_AddOrganizationEffectCaptureWitnessV1` introduces an
explicit nullable `CaptureWitnessVersion` discriminator on manifests and journal
events. Existing opaque rows retain SQL NULL and acquire no typed authority.
No writer sets this field today. This migration does not activate automatic effects,
change the archive format, or remove the applied-effect authority barrier.

A future transactional producer opts both declarations into version 1, inserts every
frozen member and the journal capture event, and inserts one
`OrganizationEffectCaptureWitnessesV1` row with manifest ID, event ID and the complete
ordered item-ID vector. The declarations and seal must share the same database
transaction. The seal supplies canonical bytes and hash itself; supplied values are
overwritten. It verifies the frozen row projection, the complete exact member set,
one normalized source and the event's single current approved mapping. It compares
the SQL-derived hash with the typed manifest's request hash and retains order durably.
The stateless SQL encoder derives complete actual v1 canonical JSON from immutable
rows without caller JSON, path arrays, hashes or mutable temporary staging tables.
The entire canonical request must fit the existing 1 MiB limit.

Typed declarations require READ COMMITTED and take the existing RCPT/NORM transaction
gate before the taxonomy lock. Seal reads after the lock are statement-fresh. It
checks current taxonomy and mapping heads, locks receipts and items in ID order,
and checks active source identity, raw tuple, business revisions, expected assignment,
and preservation of deliberate or existing category choices. Membership seals
immediately: even another insert in the same transaction rejects after sealing.
Deferred constraints on both declarations require the seal, including when a failed
seal is caught and its statement/savepoint rolled back. Both declarations must be
explicitly typed; historical events and manifests cannot be retroactively sealed.
Witness rows reject update, deletion and truncation.

This is a bounded capture-binding candidate, not complete execution authority.
Ordinary mapping command/request/source-hash and semantic fingerprint validation
remain application prerequisites. Consent, policy/restore fences, source corrections
and suppression preferences are not attested by this witness; a future producer must
validate them under their established write protocol. There is no producer requiring
every mapping transition to join the journal, no authenticated baseline/export anchor,
portable witness restore, conditional effect application or undo implementation.
The independently trusted terminal checkpoint and applied-effect barrier remain
required. Superusers and owners able to disable triggers/alter schema remain outside
the ordinary-DML protection boundary.

Verification on 2026-10-28: the Infrastructure project builds with zero warnings/errors.
All 44 `OrganizationEffectCaptureWitnessTests` passed against real PostgreSQL,
including canonical parity, complete membership, invalid context, rollback,
concurrency, legacy-choice preservation and occupied-operation regressions.
Independent review and final re-review found no remaining issue within this inactive
scope. These checks establish the bounded capture-binding candidate only; all
activation prerequisites and authority limitations below remain in force.


Capture witness operation checks reject apply/undo identities already visible in
ordinary taxonomy, assignment, mapping, historical/legacy, category assistance,
normalization catalog and receipt request history at sealing. This is an
existing-visible-history check, not durable cross-table uniqueness: ordinary
writers that do not share the capture locks can race, and later writes can reuse
an identity. Before activation, every participating writer must coordinate through
a durable global operation namespace protocol. The applied-effect barrier remains
closed; this candidate does not supply that protocol.

A future producer must acquire the RCPT/NORM gate at transaction entry before
any baseline, journal event, mapping or taxonomy operation that takes
`taxonomy.Head`. Typed declaration triggers cannot repair an inverted order when
a transaction already holds Head from an earlier untyped action. No global
deadlock-proof ordering is claimed for mixed caller protocols.

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
outcomes. Only the bounded structural evidence candidate is supported; typed
capture witness/journal portability and execution authority remain unimplemented.
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
normalization preservation is covered by the inactive seam below; typed witnesses, independent journal trust anchors,
applied assignment authority and broader version compatibility remain prerequisites.


## Inactive SQL capture policy fence v1

Migration `20261028190000_AddOrganizationEffectCaptureFencesV1` adds a separate
SQL-owned policy attestation, explicitly opted into with nullable
`OrganizationEffectCaptureWitnessesV1.CaptureFenceVersion = 1`. Historical and
ordinary v1 witnesses retain NULL; they cannot be updated or retroactively granted
this attestation. There is no production producer. Portable format remains v13;
the v14 candidate, UI/approval gates, provider calls and applied-effect barrier
remain unchanged.

Before the existing typed seal locks receipt/item rows, the opted-in seal acquires
RCPT/NORM, taxonomy Head and category_assistance.Settings in that order. A producer
must acquire the gate at transaction entry, before baseline, event or mapping work
that can lock Head; nested triggers cannot repair an order already inverted by
caller work. The Head -> Settings order agrees with restore, enrollment, dispatch
and discovery. READ COMMITTED ensures statement-fresh policy after waiting. The
attestation derives Revision and RestoreGeneration from the locked Settings row,
never organization.Runs or other discovery cache. Disabled/invalid settings fail
closed. Settings updates and restore generation transitions serialize with this
lock until transaction completion.

After successful typed witness insertion, a trigger inserts one immutable
OrganizationEffectCaptureFencesV1 row, referencing that witness. Canonical bytes
and the domain-separated hash match OrganizationEffectFenceContractV1.CaptureEvidence;
the manifest hash comes from the SQL-derived witness. Direct DML cannot supply
arbitrary policy facts. A failure or outer rollback removes declarations, witness
and fence atomically. Ordinary table owners/superusers capable of changing trigger
or schema definitions remain outside the DML trust boundary.

This attests policy at capture only. It does not authenticate arbitrary future
execution facts, actor permissions, undo lineage, mapping command/source-hash
semantics, suppression/disposition checks or an independent journal checkpoint.
Every future execution must recheck authoritative current settings/generation and
source/assignment/taxonomy under the established locks. Every mapping transition
must still join the journal, every ordinary writer must join the global operation
registry, and portable witness restoration must be implemented before activation.
The registry foundation alone does not establish global enforcement. No effect
application or automatic categorization is enabled.

Verification (2026-10-28): all 53 OrganizationEffectCaptureWitnessTests passed on real PostgreSQL (zero skipped), including SQL/.NET canonical parity, disabled/generation lock races, gate-before-baseline composition, immutable/retroactive-fact rejection and outer rollback. Build completed with zero warnings/errors. Independent review found no blocker in the SQL fence foundation. Additional reverse-contention evidence uses the actual settings update store and CategoryAssistanceRestoreFence inside a gate-protected restore transaction, confirming that capture blocks later policy changes and that restore rotation, disable and audit commit or roll back together while captured evidence remains unchanged. This exercises the restore fence transaction, not a complete portable archive import.


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
with a refreshed lineage checksum. This does not activate a format or complete
portable journal checkpoints, typed capture witnesses/fences or global registry
portability. Those remain prerequisites to restorable execution authority.

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
