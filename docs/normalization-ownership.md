# Normalization write ownership

`NormalizedDescriptionResolutionService` only reuses reviewed exact scoped mappings for unresolved receipt items. It does not create identities or accept global exact-name or similarity matches. Application policies own verification and eligibility decisions; infrastructure owns factual reads and guarded writes. Resolution does not own editable purchase facts or a user's newer curation. Changing raw description clears canonical identity and score. For an approved scoped mapping, changing the item code or parent receipt location also invalidates the assignment because those fields are part of its source key. Financial-only edits preserve classification.

## Receipt entry verification

`ReceiptNormalizationPolicy` owns intent validation, decision precedence and whether existing source/assignment/identity evidence remains valid. The EF save adapter enforces it for complete receipts, standalone rows, parent scope edits, restore and reviewed catalog writes. It batches local facts and acquires the normalization gate before admission and row locks; no model or embedding call occurs inside the receipt transaction.

Each optional row intent is `knownSource`, `confirmExisting`, `confirmNew`, `exclude` or `defer`. Explicit choices carry a stable draft-row ID and server-computed source fingerprint; edits additionally carry the expected item revision. Existing choices carry the target revision, and explicit `knownSource` requires the previewed mapping revision. A remembered mapping can also carry its revision. Confirming a new product creates it in the same transaction as the receipt, purchase, audit and outstanding-review registration. Remembering a source is separate from confirming one purchase; conflicting persisted or staged source identities reject the whole write.

Verification is `NeedsReview`, `Verified` or `Excluded`, independently of canonical status and suggestion/operation state. Proof binds the source fingerprint, target identity and assignment token, together with reviewer and review time. Mapping-dependent proof additionally binds the approved mapping revision. A display-label or financial edit does not invalidate identity proof. A source change reevaluates it. Revoking a mapping downgrades dependent proof while preserving historical links and independently reviewed purchases.

Guarded application soft-delete and restore preserve a still-current independent purchase review or exclusion. Trash controls availability in queries and review work; it does not erase an unchanged user decision. The adapter validates the same source, assignment and active-target evidence before preserving it. Changed or already downgraded evidence is never revived, including source or target changes while trashed. Mapping-dependent and unknown purchases retain conservative unavailable/review behavior. Direct SQL bypasses still downgrade evidence through the database guards.

Old clients can omit intents. A current approved scoped mapping is applied during the atomic save; otherwise the valid financial receipt is saved as NeedsReview. Existing template hints remain convenience provenance and cannot independently verify a purchase. NeedsReview rows create durable `NormalizationReviewWork` in the same transaction. This table is registration for the later inbox workflow, not a claim that the inbox UI or historical backfill is implemented.

Complete creation and standalone create/edit accept an optional caller request ID. An identical retry reuses its result without new identities, mappings or audits; a changed payload with the same actor/request ID conflicts. Caller-generated draft IDs and intent facts are hashed, while server-generated creation IDs and read-only proof metadata are excluded. Replay tombstones survive deletion to prevent accidental recreation.

Portable backup version 8 includes validated receipt verification and replay evidence. Version 7 and older links remain unverified during import, even when the archive includes active mappings. Restored mappings can apply to future saves. Existing persisted links migrate to NeedsReview without inventing historical reviewers; population of the complete historical review queue and its acceptance workflow remains RECEIPTS-1026.

Database guards downgrade evidence after direct SQL source, assignment, mapping or target-status changes; they never manufacture verification. They register a durable dirty observation with a nullable source hash when SQL cannot reconstruct the application fingerprint. The application recomputes that fingerprint before making a decision. A transaction-local list of application-prepared item IDs permits the validated atomic write and is cleared immediately after that save. Financial-only and display-label changes do not downgrade evidence.

## Reviewed catalog decisions

`Application.Normalization.NormalizationCatalogPolicy.Classify` is the authoritative decision over `CatalogAssignmentFacts`: item/parent liveness, reviewed verification evidence, rejection, historical links and ownership by a revoked mapping. Preview, historical application, group suggestion admission and retry consume that decision. Infrastructure projects eligibility facts and parent revisions in bounded batches, and retains SQL grouping/filtering for efficient source pages and coverage; parity tests must keep these read projections consistent with the policy.

`RequireAction` owns the operation-state rules for approval, continuation, cancellation and undo. Restored observations cannot resume or undo. These are concrete catalog policies, not a generic workflow framework.

A source key is exact store-location text, raw description and optional item code, with outer ASCII spaces (U+0020) trimmed consistently by Application and PostgreSQL. Null and space-only codes share the missing-code key; tabs, nonbreaking spaces and other Unicode characters remain significant. Case, punctuation, brand and size text are not silently collapsed. Approved source mappings are separate from canonical product identities and from mutable historical-operation progress.

Existing links migrate as `legacy_unknown`; an Active canonical or null similarity score is not evidence of review. New writes record assignment provenance separately from verification. Origin strings such as manual, template and reviewed, and a template FK, are not proof. Independently verified purchases remain protected. Any unverified linked history requires explicit inclusion, regardless of its old origin string. A still-owned assignment from a revoked mapping becomes eligible for a new explicit preview; revocation does not silently rewrite purchases outside an earlier preview.

Preview captures historical membership, prior assignments, item revisions and parent receipt revisions. Approval creates the mapping and processes a bounded chunk. The catalog worker continues approved membership in chunks of 100 without provider calls, checking the approving administrator and mapping before each chunk. Changed members become visible conflicts; they are not overwritten. A failed chunk rolls back and pauses with a safe error; continuation is deliberate. Cancellation revokes the mapping and stops future chunks while retaining committed changes.

Undo revokes the mapping and restores only snapshotted assignments whose item revision, assignment token and parent revision still match. Revocation downgrades mapping-dependent verification. Under the same locks, only unchanged operation members carry that revocation-induced revision forward into their undo observations; intervening purchase changes are never rebased. Later edits and manual decisions survive. Future purchases resolved from the mapping are not silently added to the original operation's undo scope; a new preview is required to correct those purchases.

## Candidate evidence and model suggestions

Catalog retrieval is the one candidate search an administrator sees. It queries Active targets before limiting results, combines exact names, lexical matches, human-approved examples and optional semantic neighbors, and reserves candidate capacity for the closest semantic results. Automatic links are not training examples. Retrieval is bounded and is not proof that a missing candidate represents a new product.

`NormalizationCatalogAiPolicy` owns the versioned group prompt, payload allowlist and strict output validation. Identity decisions are `same`, `different` or `insufficient`; proposing a new display name is separate from asserting identity. Approved examples disclose their description and whether their store/code scope matches the source, without sending separate store or code fields. Other-scope examples are weak evidence, and all source/example text is untrusted data. Structural validation cannot establish semantic correctness; human approval remains required.

Group suggestions feed the same fresh catalog preview used for manual decisions. Optional run/proposal/revision references retain suggestion provenance and complete reviewed proposals without invalidating unchanged sibling previews. Fresh preview may change historical inclusion independently of the original suggestion. A human identity override is recorded as `overridden`, retaining the original suggestion alongside the actual catalog operation; accepting the same identity is `applied`. Both retain provenance. The retired per-item apply endpoint rejects every request: group suggestions with `catalog_review_required` and saved per-purchase runs with `workflow_retired`. Saved legacy requests retain their original protocol; unknown protocol versions fail explicitly.

## Read, resolve, conditionally apply

Approved operations reserve their still-pending snapshotted members from automatic resolution, including remaining members after cancellation. Reservation holds only while item and parent revisions still match. Unapproved previews and newly entered purchases are not reserved. This prevents automatic resolution from bypassing historical progress and undo ownership.

A cycle selects a bounded batch of unresolved observations with current approved exact scoped mappings. It runs without embeddings or ChatGPT and never creates identities, accepts global exact-name matches or performs similarity matching. Explicitly deferred purchases remain deferred. A rejected unrelated global name does not override an approved scoped mapping; the mapping's actual target must remain Active.

The write phase takes the normalization gate and locks targets and items, then rechecks source, parent and item revisions, mapping ownership and target status. Edit-and-revert and competing writes invalidate stale observations. The receipt verification adapter records current proof, audit and review-work changes in the same transaction. Accepted links produce one broad, toast-suppressed receipt-item projection repair after commit; skipped or failed batches produce no link notification. Failure before commit rolls back links and audits together.

Ordinary item updates take the same gate before row locks and the tracked read. This prevents an edit based on an earlier null link from preserving a worker's later assignment under changed source text. Canonical display-label changes and financial-only edits preserve valid identity evidence.

## Short write coordination

`NormalizationWriteGuard` centralizes schema-qualified PostgreSQL snapshot reads and lock acquisition. The transaction-scoped advisory key `RCPT / NORM` serializes guarded write phases, followed by canonical locks in ID order, parent receipt locks where source scope is involved, and item locks in ID order. Canonical `FOR NO KEY UPDATE` locks allow normal foreign-key checks while excluding conflicting status changes. Catalog target validity is checked after acquiring the target lock.

Ordinary receipt-location edits load affected mapped children before audit collection, so assignment invalidation and child audit entries share the application transaction. Database triggers also invalidate scoped assignments for direct SQL source changes and issue new revisions. Canonical merges redirect mapping targets and cancel uncompleted historical approvals for the discarded target with `target_changed`. Their approved identity is not silently rewritten. Recovery requires revoking the redirected mapping, then preparing and approving a fresh preview; independent operations never share mapping ownership. Making a canonical non-Active revokes its mappings.

The resolver and status/rejection operation acquire this gate before locking or scanning children. Rejection locks its matching child rows as it reads them, including trashed items and templates, before clearing links. Merge acquires the same gate only after similarity work and before EF writes children and deletes the discarded parent. Requeue acquires it before reading its confirmed pending set and writing children/deletions. This ordering prevents a merge holding a child from waiting on a canonical row held by rejection while rejection waits on that child.

The gate is deliberately narrow in duration and specific to canonical write coordination. It serializes these write phases even when they concern different canonical rows. Model work remains concurrent; finer locking should be justified by measured contention rather than added speculatively. This protocol does not claim serializable execution of every manual curation workflow.

## Revision and test boundaries

Resolver, durable AI and catalog snapshots use database-generated GUID revisions for items, canonical targets and parent receipts. These detect edit/revert cycles without making every entity update an optimistic-concurrency API. Revisions are internal observations, not portable business identity.

A canonical target revision observes business state. An update that changes only `Embedding` and/or `EmbeddingModelVersion` preserves that revision: rebuilding a semantic projection must not expire receipt choices, label previews, catalog approvals or AI proposals. Every other canonical field remains protected, including canonical text, display labels, status, creation metadata and review hints. Mixed business/projection updates rotate the revision, as do business edit/revert cycles. The database compares the full remaining row, so newly added fields are protected by default. Nonrelational fixtures follow the same rule. Item and parent receipt revisions retain their existing behavior; financial edits still expire stale item choices even while valid identity proof survives.

Portable backup version 7 preserves scoped mappings, assignment origins/tokens and catalog operation/member observations. Restored operations are inspectable historical evidence, explicitly non-resumable and non-undoable against regenerated source revisions. Older backups restore linked assignments as `legacy_unknown`. Restore holds the normalization gate and fences outstanding operations before business upserts; credentials and provider execution are not part of this archive.

Nonrelational unit fixtures exercise source-value and target guards. Real PostgreSQL tests establish revision, lock ordering, default-search-path, competing-writer and audit behavior. They coordinate matching and database lock waits rather than relying on timing sleeps.

## Template declarations and updates

`ItemTemplateService` resolves the requested template name through `GetOrCreateForTemplateAsync` before committing template fields. It captures an opaque per-template revision first. The repository then takes the short canonical write gate, locks proposed targets and templates, and validates every requested source revision before assigning any tracked values. A changed, deleted or missing source conflicts the whole update; no requested template fields or update audits are partially saved.

The repository applies an explicit editable-field whitelist and only a valid server-resolved canonical result. A target that was removed, renamed or rejected during resolution is not attached. The existing classifier-failure fallback remains an unlinked template, and cancellation during canonical work still happens before template fields are saved. Template fields, their accepted canonical link and audits share one final commit.

`PUT /api/item-templates/{id}` returns a typed 409 RFC 9457 problem with the reason in `detail` when this source revision changed. The client keeps the dialog and draft open and displays that reason; retry is deliberate. Initial missing templates still return 404. This protects the server's asynchronous resolution window, not every stale browser draft: requests do not carry a public version or ETag.

Creating or updating a template deliberately declares its name and may reinstate a previously rejected canonical entry through the existing resolver policy. A later rejection is respected at the final template write. Canonical registry creation or reinstatement uses its pre-existing separate context and can commit before a later template conflict or cancellation; it is not included in the template-field atomicity claim.

## Receipt items entered from templates

Single and batch receipt-item creation carry optional, positionally aligned template hints through an explicit service/repository overload. The generic item mapper continues to ignore caller canonical IDs and scores. The create handler keeps the parent-existence check and passes provenance onward; it no longer mutates a domain item with metadata that the mapper discards.

The repository resolves current active templates and valid canonical targets inside the short write transaction. It locks canonical targets before hinted templates and rereads the actual template FK after a lock wait. If the target set changed, it releases the transaction and retries, up to three attempts, without reversing that lock order. Repeated churn falls back to unlinked only for hints whose current target cannot be validated; it does not prevent saving the receipt. Unknown, deleted and unlinked templates retain their existing fallback. Hint alignment is validated at direct service and repository boundaries too.

Accepted hints stamp the canonical ID, template provenance and a fresh assignment token, with a null score: a template declaration is not a measured similarity. Canonical display/name equality with the template is not required on this entry path, because a deliberate canonical merge can leave the template linked to a differently named survivor. Current locked template identity wins. Ordinary no-hint creation is unchanged, and complete-receipt creation does not gain template hints.

Real PostgreSQL creation-chain and controlled matching/lock-wait tests cover these boundaries. The frontend conflict test uses the actual page, mutation hook, app query client, error presentation and a controlled HTTP response, including same-draft retry after 409.

## Retired threshold matcher

The settings-page matcher (auto-accept and pending-review thresholds, impact preview and match test) was removed in RECEIPTS-1099; see [the decision](decisions/2026-10-31-retire-threshold-matcher.md). It only simulated a semantic resolver that no longer runs. Live resolution uses reviewed exact scoped mappings. Catalog source review shows the real candidate shortlist. RECEIPTS-1105 then dropped `NormalizedDescriptionMatchScore`, together with the catalog-undo snapshot of it and the ChatGPT proposal's `SourceMatchScore`, because nothing read them.

## Product identity and readable display labels

A matching decision and a readable name are separate decisions. Catalog suggestions may retain `SuggestedDisplayLabel` for an existing identity without changing that identity, its canonical text, embeddings, mappings, or purchase facts. Identity mapping approval never applies a global display label.

`LabelOnly` requests support sources that are already mapped or protected by manual/template review. All assigned live observations in that exact source must agree on one Active identity; unassigned siblings are allowed. The model sees that identity as its sole candidate and may suggest a label or abstain, never create or relink products. Null or unchanged label suggestions finish as `no_change`, releasing reservations. Ambiguous sources require identity review first.

Global display-label preview reports the target revision and number of live purchases affected. Separate approval rechecks administrator access, Active status, revision, and impact count under the normalization gate and target lock. It uses the same normalization and effective-name collision rules as ordinary rename. Optional label-only suggestion references are consumed atomically with the label write; unchanged sibling proposals retain their review revision. Human overrides retain the original suggestion and record `overridden`. Ordinary identity suggestions keep their separate mapping review pending.

The label protocol uses `receipts-catalog-v2-retrieval-v2`; queued older catalog protocols fail explicitly before a provider request, and retries require a fresh selection. Existing per-item protocol execution remains separate. Pending provider suggestions are not portable backup data; approved registry display labels already are. Cost-history lookup recognizes the effective display name used by spending reports, with canonical-name fallback when no displayed-name match exists.

## Recovering pending identities from the catalog

Catalog source pages expose the total number of distinct linked identities and at most five current names/statuses. This is a bounded read projection, not evidence that legacy assignments were reviewed. Multiple targets remain explicitly ambiguous; model retrieval still admits only Active targets.

A source linked to a PendingReview identity provides an exact Review Queue link. The queue searches canonical text and display labels on the server and can load an exact target by ID without paging through unrelated identities. Navigation, search, and model requests do not change status. Existing explicit Approve/Merge controls remain the curation owners. Status mutation invalidation refreshes both exact-target details and catalog eligibility; the return link retains the source scope.

Pending-name collisions and label-only admission failures use RFC 9457 extensions `normalizedDescriptionId` and `normalizedDescriptionStatus` with the existing machine `code`. The browser offers a pending-review route; it never treats a pending or rejected identity as an Active candidate merely to make a request succeed. Search changes reset queue pagination and operation dialogs, and loading/error views retain the search and return controls.
