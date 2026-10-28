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
