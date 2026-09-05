# Normalization write ownership

`NormalizedDescriptionResolutionService` owns automatic links for unresolved receipt items. It may propose a canonical ID and match score; it does not own editable item fields or a user's newer curation. Ordinary item edits retain the RECEIPTS-955 whitelist: changing raw description clears canonical identity and score, while other edits preserve them.

## Read, resolve, conditionally apply

A cycle selects up to 50 candidates, then captures each item's source values and PostgreSQL row revision in one read before matching. It groups identical raw descriptions to avoid duplicate work. Model and similarity calls run without database write locks.

The final phase uses a fresh context and a short transaction. It locks canonical targets before items, reads current state, and accepts only unchanged, live, unresolved snapshots. The revision comparison detects edit-and-revert and manual link/unlink cycles even when the visible values return to their earlier state. A competing resolver's first committed result changes that revision and protects its assignment.

Ordinary item updates also lock their item rows before the tracked read and hold those locks through the short audited commit. This protects the reverse order: an edit cannot read null metadata, let the worker fill it, and then omit its null clears because EF still sees the earlier null originals. A text edit reads the actual current link before clearing it, so both stored state and audit old values are accurate. Item edits need no canonical gate because they only preserve or clear links.

Targets must still exist, retain the resolved canonical name and remain non-Rejected. An exact-name tombstone for the trimmed source text also blocks attachment to a different fuzzy target. Display-label changes remain cosmetic and do not invalidate matching.

Only accepted items receive canonical ID and score changes. Their tracked `SaveChangesAsync` call writes the changes and automatic audit records atomically under RECEIPTS-956. Skipped snapshots add no link audit; the summary reports them as skipped. Changed unresolved items remain available for a fresh cycle, while rejected text follows the existing tombstone filter. A failure before commit rolls back accepted links and their audits together. Canonical entries created by earlier matching calls have their own existing persistence lifecycle.

## Short write coordination

`NormalizationWriteGuard` centralizes schema-qualified PostgreSQL snapshot reads and lock acquisition. The transaction-scoped advisory key `RCPT / NORM` serializes the short guarded write phases, followed by canonical locks in ID order and item locks in ID order. Canonical `FOR NO KEY UPDATE` locks allow normal foreign-key checks while excluding conflicting status changes.

The resolver and status/rejection operation acquire this gate before locking or scanning children. Rejection locks its matching child rows as it reads them, including trashed items and templates, before clearing links. Merge acquires the same gate only after similarity work and before EF writes children and deletes the discarded parent. Requeue acquires it before reading its confirmed pending set and writing children/deletions. This ordering prevents a merge holding a child from waiting on a canonical row held by rejection while rejection waits on that child.

The gate is deliberately narrow in duration and specific to canonical write coordination. It serializes these write phases even when they concern different canonical rows. Model work remains concurrent; finer locking should be justified by measured contention rather than added speculatively. This protocol does not claim serializable execution of every manual curation workflow.

## Revision and test boundaries

`xmin` is a short-lived PostgreSQL row-version observation, not a business revision, backup field or public API concurrency token. The implementation does not add entity-wide optimistic concurrency behavior or a model migration. PostgreSQL documents its row-version meaning and transaction-ID limits in [System Columns](https://www.postgresql.org/docs/18/ddl-system-columns.html).

Nonrelational unit fixtures exercise source-value and target guards. Real PostgreSQL tests establish revision, lock ordering, default-search-path, competing-writer and audit behavior. They coordinate matching and database lock waits rather than relying on timing sleeps.

## Remaining template ownership work

`ItemTemplateService` performs separate asynchronous canonical resolution. Its newer-curation checks, and explicit trusted template-derived metadata assignment in single/batch item creation, remain a focused RECEIPTS-963 follow-up. This worker change does not restore that lost create stamp or add template hints to complete-receipt creation. The issue remains open until those paths and their regressions are completed.
