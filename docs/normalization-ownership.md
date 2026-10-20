# Normalization write ownership

`NormalizedDescriptionResolutionService` runs automatic resolution for unresolved receipt items. Application policies own eligibility and matching decisions; infrastructure owns the factual reads and guarded writes. Resolution does not own editable purchase facts or a user's newer curation. Changing raw description clears canonical identity and score. For an approved scoped mapping, changing the item code or parent receipt location also invalidates the assignment because those fields are part of its source key. Financial-only edits preserve classification.

## Reviewed catalog decisions

`Application.Normalization.NormalizationCatalogPolicy.Classify` is the authoritative decision over `CatalogAssignmentFacts`: item/parent liveness, assignment origin, rejection, template protection and ownership by a revoked mapping. Preview, historical application, group suggestion admission and retry consume that decision. Infrastructure projects eligibility facts and parent revisions in bounded batches, and retains SQL grouping/filtering for efficient source pages and coverage; parity tests must keep these read projections consistent with the policy.

`RequireAction` owns the operation-state rules for approval, continuation, cancellation and undo. Restored observations cannot resume or undo. These are concrete catalog policies, not a generic workflow framework.

A source key is exact store-location text, raw description and optional item code, with outer ASCII spaces (U+0020) trimmed consistently by Application and PostgreSQL. Null and space-only codes share the missing-code key; tabs, nonbreaking spaces and other Unicode characters remain significant. Case, punctuation, brand and size text are not silently collapsed. Approved source mappings are separate from canonical product identities and from mutable historical-operation progress.

Existing links migrate as `legacy_unknown`; an Active canonical or null similarity score is not evidence of review. New writes record their assignment origin and a fresh assignment token. Manual, template and reviewed assignments remain protected. Historical unknown links require explicit inclusion. A still-owned assignment from a revoked mapping becomes eligible for a new explicit preview; revocation does not silently rewrite purchases outside an earlier preview.

Preview captures historical membership, prior assignments, item revisions and parent receipt revisions. Approval creates the mapping and processes a bounded chunk. The catalog worker continues approved membership in chunks of 100 without provider calls, checking the approving administrator and mapping before each chunk. Changed members become visible conflicts; they are not overwritten. A failed chunk rolls back and pauses with a safe error; continuation is deliberate. Cancellation revokes the mapping and stops future chunks while retaining committed changes.

Undo revokes the mapping and restores only snapshotted assignments whose item revision, assignment token and parent revision still match. Later edits and manual decisions survive. Future purchases resolved from the mapping are not silently added to the original operation's undo scope; a new preview is required to correct those purchases.

## Candidate evidence and model suggestions

Catalog retrieval is separate from threshold simulation. It queries Active targets before limiting results, combines exact names, lexical matches, human-approved examples and optional semantic neighbors, and reserves candidate capacity for the closest semantic results. Automatic links are not training examples. Retrieval is bounded and is not proof that a missing candidate represents a new product.

`NormalizationCatalogAiPolicy` owns the versioned group prompt, payload allowlist and strict output validation. Identity decisions are `same`, `different` or `insufficient`; proposing a new display name is separate from asserting identity. Approved examples disclose their description and whether their store/code scope matches the source, without sending separate store or code fields. Other-scope examples are weak evidence, and all source/example text is untrusted data. Structural validation cannot establish semantic correctness; human approval remains required.

Group suggestions feed the same fresh catalog preview used for manual decisions. Optional run/proposal/revision references retain suggestion provenance and complete reviewed proposals without invalidating unchanged sibling previews. Fresh preview may change historical inclusion independently of the original suggestion. A human identity override is recorded as `overridden`, retaining the original suggestion alongside the actual catalog operation; accepting the same identity is `applied`. Both retain provenance. The older per-item apply endpoint rejects group suggestions. Saved legacy requests retain their original protocol; unknown protocol versions fail explicitly.

## Read, resolve, conditionally apply

Approved operations reserve their still-pending snapshotted members from both deterministic and embedding resolvers, including remaining members after cancellation. Reservation holds only while item and parent revisions still match; a changed observation requires fresh resolution or review. Unapproved previews and newly entered purchases are not reserved. This prevents automatic resolution from bypassing historical progress and undo ownership.

A cycle first tries a bounded deterministic batch of approved scoped mappings and exact Active canonical names, even when embeddings or ChatGPT are unavailable. This phase never invents a target for unknown text. It rechecks observed revisions after locking; changed observations wait for a new cycle. If there is no deterministic work, the embedding resolver selects up to 50 candidates and groups identical descriptions to avoid duplicate matching work. Model and similarity calls run without database write locks.

The final phase uses a fresh context and a short transaction. It locks canonical targets before items, reads current state, and accepts only unchanged, live, unresolved snapshots. The revision comparison detects edit-and-revert and manual link/unlink cycles even when the visible values return to their earlier state. A competing resolver's first committed result changes that revision and protects its assignment.

Ordinary item updates also lock their item rows before the tracked read and hold those locks through the short audited commit. This protects the reverse order: an edit cannot read null metadata, let the worker fill it, and then omit its null clears because EF still sees the earlier null originals. A text edit reads the actual current link before clearing it, so both stored state and audit old values are accurate. Item edits need no canonical gate because they only preserve or clear links.

Targets must still exist, retain the resolved canonical name and remain non-Rejected. An exact-name tombstone for the trimmed source text also blocks attachment to a different fuzzy target. Display-label changes remain cosmetic and do not invalidate matching.

Only accepted items receive canonical ID and score changes. Their tracked `SaveChangesAsync` call writes the changes and automatic audit records atomically under RECEIPTS-956. After the guarded transaction commits at least one link, the worker publishes one broad, toast-suppressed receipt-item projection repair; it publishes nothing for an empty, skipped or failed batch. Skipped snapshots add no link audit; the summary reports them as skipped. Changed unresolved items remain available for a fresh cycle, while rejected text follows the existing tombstone filter. A failure before commit rolls back accepted links and their audits together. Canonical entries created by earlier matching calls have their own existing persistence lifecycle.

## Short write coordination

`NormalizationWriteGuard` centralizes schema-qualified PostgreSQL snapshot reads and lock acquisition. The transaction-scoped advisory key `RCPT / NORM` serializes guarded write phases, followed by canonical locks in ID order, parent receipt locks where source scope is involved, and item locks in ID order. Canonical `FOR NO KEY UPDATE` locks allow normal foreign-key checks while excluding conflicting status changes. Catalog target validity is checked after acquiring the target lock.

Ordinary receipt-location edits load affected mapped children before audit collection, so assignment invalidation and child audit entries share the application transaction. Database triggers also invalidate scoped assignments for direct SQL source changes and issue new revisions. Canonical merges redirect mapping targets and cancel uncompleted historical approvals for the discarded target with `target_changed`. Their approved identity is not silently rewritten. Recovery requires revoking the redirected mapping, then preparing and approving a fresh preview; independent operations never share mapping ownership. Making a canonical non-Active revokes its mappings.

The resolver and status/rejection operation acquire this gate before locking or scanning children. Rejection locks its matching child rows as it reads them, including trashed items and templates, before clearing links. Merge acquires the same gate only after similarity work and before EF writes children and deletes the discarded parent. Requeue acquires it before reading its confirmed pending set and writing children/deletions. This ordering prevents a merge holding a child from waiting on a canonical row held by rejection while rejection waits on that child.

The gate is deliberately narrow in duration and specific to canonical write coordination. It serializes these write phases even when they concern different canonical rows. Model work remains concurrent; finer locking should be justified by measured contention rather than added speculatively. This protocol does not claim serializable execution of every manual curation workflow.

## Revision and test boundaries

The original fuzzy resolver uses `xmin` as a short-lived row observation. Durable AI and catalog snapshots use database-generated GUID revisions for items, canonical targets and parent receipts. These detect edit/revert cycles without making every entity update an optimistic-concurrency API. Revisions are internal observations, not portable business identity.

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

## Matching decisions and threshold previews

`Application.Normalization.NormalizationMatchPolicy` owns the immutable threshold
configuration and the decision from observed matching facts. Exact Active and
PendingReview names are reused without embeddings; exact Rejected names suppress
attachment. Otherwise the top candidate is reused at the inclusive auto-accept
threshold, creates a pending entry at the inclusive pending-review threshold, or
creates a new active entry below it. Domain validation rejects nonfinite thresholds.

`NormalizedDescriptionService` supplies exact-name and ANN facts, obtains embeddings,
and executes the decision through its existing persistence operations. It retains
the background versus interactive scheduler distinction. Missing embedding capacity
is an explicit decision: execution retains its new-active fallback while simulation
reports `EmbeddingUnavailable`. Cancellation is not missing capacity and propagates.

The match-test response adds the `Rejected` outcome with no simulated target. Its
candidate still identifies the tombstone for explanation; it does not imply a link.
The response remains an unrestricted string with the same shape. Template declaration
is intentionally separate: exact lookup or active creation, no fuzzy match, with
explicit reinstatement of a tombstone and preservation of pending review.

`ThresholdImpactCalculator` uses the same score bands for counts and transitions.
Infrastructure captures linked scores and structurally unresolved items in one narrow
query, then compares current and proposed thresholds over that single population.
This is a reclassification of recorded evidence, not a new model run or a prediction
of template/manual-link decisions. Null scores or missing links remain unresolved;
no writes or embeddings are performed by the calculation. The existing response
exposes four transition directions; it does not claim to enumerate every transition.

## Product identity and readable display labels

A matching decision and a readable name are separate decisions. Catalog suggestions may retain `SuggestedDisplayLabel` for an existing identity without changing that identity, its canonical text, embeddings, mappings, or purchase facts. Identity mapping approval never applies a global display label.

`LabelOnly` requests support sources that are already mapped or protected by manual/template review. All assigned live observations in that exact source must agree on one Active identity; unassigned siblings are allowed. The model sees that identity as its sole candidate and may suggest a label or abstain, never create or relink products. Null or unchanged label suggestions finish as `no_change`, releasing reservations. Ambiguous sources require identity review first.

Global display-label preview reports the target revision and number of live purchases affected. Separate approval rechecks administrator access, Active status, revision, and impact count under the normalization gate and target lock. It uses the same normalization and effective-name collision rules as ordinary rename. Optional label-only suggestion references are consumed atomically with the label write; unchanged sibling proposals retain their review revision. Human overrides retain the original suggestion and record `overridden`. Ordinary identity suggestions keep their separate mapping review pending.

The label protocol uses `receipts-catalog-v2-retrieval-v2`; queued older catalog protocols fail explicitly before a provider request, and retries require a fresh selection. Existing per-item protocol execution remains separate. Pending provider suggestions are not portable backup data; approved registry display labels already are. Cost-history lookup recognizes the effective display name used by spending reports, with canonical-name fallback when no displayed-name match exists.

## Recovering pending identities from the catalog

Catalog source pages expose the total number of distinct linked identities and at most five current names/statuses. This is a bounded read projection, not evidence that legacy assignments were reviewed. Multiple targets remain explicitly ambiguous; model retrieval still admits only Active targets.

A source linked to a PendingReview identity provides an exact Review Queue link. The queue searches canonical text and display labels on the server and can load an exact target by ID without paging through unrelated identities. Navigation, search, and model requests do not change status. Existing explicit Approve/Merge controls remain the curation owners. Status mutation invalidation refreshes both exact-target details and catalog eligibility; the return link retains the source scope.

Pending-name collisions and label-only admission failures use RFC 9457 extensions `normalizedDescriptionId` and `normalizedDescriptionStatus` with the existing machine `code`. The browser offers a pending-review route; it never treats a pending or rejected identity as an Active candidate merely to make a request succeed. Search changes reset queue pagination and operation dialogs, and loading/error views retain the search and return controls.
