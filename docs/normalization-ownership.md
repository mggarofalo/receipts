# Normalization write ownership

`NormalizedDescriptionResolutionService` owns automatic links for unresolved receipt items. It may propose a canonical ID and match score; it does not own editable item fields or a user's newer curation. Ordinary item edits retain the RECEIPTS-955 whitelist: changing raw description clears canonical identity and score, while other edits preserve them.

## Read, resolve, conditionally apply

A cycle selects up to 50 candidates, then captures each item's source values and PostgreSQL row revision in one read before matching. It groups identical raw descriptions to avoid duplicate work. Model and similarity calls run without database write locks.

The final phase uses a fresh context and a short transaction. It locks canonical targets before items, reads current state, and accepts only unchanged, live, unresolved snapshots. The revision comparison detects edit-and-revert and manual link/unlink cycles even when the visible values return to their earlier state. A competing resolver's first committed result changes that revision and protects its assignment.

Ordinary item updates also lock their item rows before the tracked read and hold those locks through the short audited commit. This protects the reverse order: an edit cannot read null metadata, let the worker fill it, and then omit its null clears because EF still sees the earlier null originals. A text edit reads the actual current link before clearing it, so both stored state and audit old values are accurate. Item edits need no canonical gate because they only preserve or clear links.

Targets must still exist, retain the resolved canonical name and remain non-Rejected. An exact-name tombstone for the trimmed source text also blocks attachment to a different fuzzy target. Display-label changes remain cosmetic and do not invalidate matching.

Only accepted items receive canonical ID and score changes. Their tracked `SaveChangesAsync` call writes the changes and automatic audit records atomically under RECEIPTS-956. After the guarded transaction commits at least one link, the worker publishes one broad, toast-suppressed receipt-item projection repair; it publishes nothing for an empty, skipped or failed batch. Skipped snapshots add no link audit; the summary reports them as skipped. Changed unresolved items remain available for a fresh cycle, while rejected text follows the existing tombstone filter. A failure before commit rolls back accepted links and their audits together. Canonical entries created by earlier matching calls have their own existing persistence lifecycle.

## Short write coordination

`NormalizationWriteGuard` centralizes schema-qualified PostgreSQL snapshot reads and lock acquisition. The transaction-scoped advisory key `RCPT / NORM` serializes the short guarded write phases, followed by canonical locks in ID order and item locks in ID order. Canonical `FOR NO KEY UPDATE` locks allow normal foreign-key checks while excluding conflicting status changes.

The resolver and status/rejection operation acquire this gate before locking or scanning children. Rejection locks its matching child rows as it reads them, including trashed items and templates, before clearing links. Merge acquires the same gate only after similarity work and before EF writes children and deletes the discarded parent. Requeue acquires it before reading its confirmed pending set and writing children/deletions. This ordering prevents a merge holding a child from waiting on a canonical row held by rejection while rejection waits on that child.

The gate is deliberately narrow in duration and specific to canonical write coordination. It serializes these write phases even when they concern different canonical rows. Model work remains concurrent; finer locking should be justified by measured contention rather than added speculatively. This protocol does not claim serializable execution of every manual curation workflow.

## Revision and test boundaries

`xmin` is a short-lived PostgreSQL row-version observation, not a business revision, backup field or public API concurrency token. The implementation does not add entity-wide optimistic concurrency behavior or a model migration. PostgreSQL documents its row-version meaning and transaction-ID limits in [System Columns](https://www.postgresql.org/docs/18/ddl-system-columns.html).

Nonrelational unit fixtures exercise source-value and target guards. Real PostgreSQL tests establish revision, lock ordering, default-search-path, competing-writer and audit behavior. They coordinate matching and database lock waits rather than relying on timing sleeps.

## Template declarations and updates

`ItemTemplateService` resolves the requested template name through `GetOrCreateForTemplateAsync` before committing template fields. It captures an opaque per-template revision first. The repository then takes the short canonical write gate, locks proposed targets and templates, and validates every requested source revision before assigning any tracked values. A changed, deleted or missing source conflicts the whole update; no requested template fields or update audits are partially saved.

The repository applies an explicit editable-field whitelist and only a valid server-resolved canonical result. A target that was removed, renamed or rejected during resolution is not attached. The existing classifier-failure fallback remains an unlinked template, and cancellation during canonical work still happens before template fields are saved. Template fields, their accepted canonical link and audits share one final commit.

`PUT /api/item-templates/{id}` returns a typed 409 RFC 9457 problem with the reason in `detail` when this source revision changed. The client keeps the dialog and draft open and displays that reason; retry is deliberate. Initial missing templates still return 404. This protects the server's asynchronous resolution window, not every stale browser draft: requests do not carry a public version or ETag.

Creating or updating a template deliberately declares its name and may reinstate a previously rejected canonical entry through the existing resolver policy. A later rejection is respected at the final template write. Canonical registry creation or reinstatement uses its pre-existing separate context and can commit before a later template conflict or cancellation; it is not included in the template-field atomicity claim.

## Receipt items entered from templates

Single and batch receipt-item creation carry optional, positionally aligned template hints through an explicit service/repository overload. The generic item mapper continues to ignore caller canonical IDs and scores. The create handler keeps the parent-existence check and passes provenance onward; it no longer mutates a domain item with metadata that the mapper discards.

The repository resolves current active templates and valid canonical targets inside the short write transaction. It locks canonical targets before hinted templates and rereads the actual template FK after a lock wait. If the target set changed, it releases the transaction and retries, up to three attempts, without reversing that lock order. Repeated churn falls back to unlinked only for hints whose current target cannot be validated; it does not prevent saving the receipt. Unknown, deleted and unlinked templates retain their existing fallback. Hint alignment is validated at direct service and repository boundaries too.

Accepted hints stamp the canonical ID with a null score: a template declaration is not a measured similarity. Canonical display/name equality with the template is not required on this entry path, because a deliberate canonical merge can leave the template linked to a differently named survivor. Current locked template identity wins. Ordinary no-hint creation is unchanged, and complete-receipt creation does not gain template hints.

Real PostgreSQL creation-chain and controlled matching/lock-wait tests cover these boundaries. The frontend conflict test uses the actual page, mutation hook, app query client, error presentation and a controlled HTTP response, including same-draft retry after 409.
