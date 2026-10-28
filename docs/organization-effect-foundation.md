# Organization effect persistence foundation (RECEIPTS-1078)

This schema is inert. No production capture, application, undo, scheduler, provider,
or UI path uses it. It supports category assignment effects only.

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
