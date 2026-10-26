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

This is an internal foundation, not a completed entry workflow. Resolution is advisory.
The purchase write integration must acquire `taxonomy.Head` first, recheck source,
current mapping revision, semantic validity and explicit choice, then atomically append
classification evidence. Never use a preview alone to authorize a later write.

## Required before enabling or releasing

- Extend the portable archive version and manifest with `CategoryMappingRevisions`
  and `CategoryMappingHeads`. Preserve exact IDs, actor/operation request hashes,
  source values/fingerprints, previous links, timestamps, revocations, category IDs,
  semantic fingerprints and referenced taxonomy revisions. Export revoked evidence,
  not just active heads. Add offline/online inspection, round-trip, rejection, and
  restore-order tests; validate source and semantic fingerprints, acyclic same-source
  revision chains, unique actor/operation IDs and head consistency. Old archive versions
  explicitly import no approvals. Restore taxonomy before mapping evidence and heads.
- Until that extension ships, backup export/restore must refuse silent loss of mapping
  evidence, or mapping writes must remain unreachable. Existing API and UI expose no
  mapping writes in this packet. DI registration alone is not release authorization.
- Add spec-first HTTP contracts and review UI for explicit approval and revocation,
  conflict handling and current mapping provenance. Use a single nested category field.
  Provide a read surface for current mapping/revision and category paths.
- Integrate aggregate purchase entry atomically, with explicit choice winning,
  source-edit invalidation, idempotent retries and current classification provenance.
  Add backend and frontend tests covering this actual user flow. Keep model/provider
  adapters outside these application-owned rules.
