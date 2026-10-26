# Offline portable backup normalization

This workflow cleans up historical product names without changing what was bought,
where it was bought, or what it cost. An agent proposes which receipt descriptions
represent the same product; a separate reviewer checks those decisions. The tool
then applies only the reviewed decisions to a copy of an exported backup. Distinct
brands or pack sizes stay separate, and descriptions that cannot identify a product
remain explicitly unresolved. Excluding a purchase is a separate, justified choice,
not a way to count it as normalized.

After restore, verified product identities support search, price history and report
grouping. Reviewed rules can also recognize the same description, store and item
code on later purchases. Receipt entry remains receipt-first: known sources reuse
their product, while unfamiliar or ambiguous sources are saved with a visible review
requirement. A one-off product choice does not silently become a reusable rule.

Identity evidence and permission for future reuse are separate inputs. For real
data, obtain an explicit owner/operator reuse policy bound to the exact exported
source before proposing reusable rules. Record who supplied it and retain its
hash with the reviewed manifest. A clear product name alone does not authorize
remembering its source. Review each eligible exact scope for supported identity
and whole-scope consistency; leave other choices one-off. Eligibility is not
approval, and the independent reviewer must still justify every remembered rule.

The synthetic acceptance corpus uses a test-author-selected reuse preference,
provided in a separate policy packet before a fresh proposal/review. This is a
policy-conditioned identity review. Keep semantic gold and expected assignments
hidden, and preserve earlier fact-only attempts with their original chronology;
they cannot be retroactively described as policy-conditioned passes. Synthetic
preferences confer no authority to reuse sources in an owner's real backup.

The owner freezes the source installation between export A and restore B. Retain A
byte-for-byte. Work only on a separately named copy B; the exact B must later be
restored into both the frozen source installation and a fresh isolated database.

The tool inspects closed portable files and applies explicitly reviewed decisions directly to a separate output artifact. Run from the repository root:

```powershell
dotnet run --project tools/Receipts.BackupNormalizer -- inspect --source A.db --output C:\private\A-inspection.json
dotnet run --project tools/Receipts.BackupNormalizer -- propose --source A.db --output C:\private\proposal.json --proposer agent-identity --tool-revision exact-git-sha
dotnet run --project tools/Receipts.BackupNormalizer -- verify-baseline --baseline C:\private\A-inspection.json --candidate B.db
dotnet run --project tools/Receipts.BackupNormalizer -- validate-manifest --source A.db --manifest C:\private\reviewed.json
dotnet run --project tools/Receipts.BackupNormalizer -- apply --source A.db --manifest C:\private\reviewed.json --output C:\private\B.db
dotnet run --project tools/Receipts.BackupNormalizer -- verify --source A.db --manifest C:\private\reviewed.json --output C:\private\B.db
```

Outputs must be explicit private paths and must not exist. Reports include raw
scoped source text and IDs: never commit them or put them in public logs. The tool
prints status only. Input must be a closed standalone SQLite backup without WAL,
SHM or journal sidecars. Inspection uses read-only SQLite, integrity/foreign-key
checks, a writer-denying source handle and before/after source SHA-256.

App-exported source formats 6, 8, 9, 10 and 11 are supported. Apply preserves formats 10 and 11 for current exports and publishes format 9 for older sources. Format 10 taxonomy, classification history, migration evidence and deletion rows are validated before and after transformation and preserved unchanged. Product normalization does not change classification. Unsupported future formats and schemas are rejected rather than rewritten.
A previous offline output B cannot become a new source A: embedded offline lineage
metadata or consolidation authority causes rejection. Preserve the original A,
revise and separately review its manifest, and apply to a new output path instead.
For example, to change X-to-Y into X-to-Z, revise the decisions against original A;
do not apply Y-to-Z to B. Reapplying the same original A and identical reviewed
manifest to its existing B remains supported and verifies B without rewriting it.
Adjacent inspection or provenance sidecars alone do not disqualify a source.

 Restore retains compatibility with formats 6, 7 and 8. `OfflineBackupInspection` version 1 contains source
hash, counts, item review-state counts, scoped item/template observations, existing
canonical names, exact decimal totals by table/currency, and canonical factual
table hashes. Facts include every column of every unrelated table and all item /
template columns except normalized target and match score. Normalized catalog,
catalog evidence and export metadata are excluded. Hashes serialize ordinally
sorted columns and JSON rows (including null/type distinctions) with .NET JSON;
the overall baseline hashes the ordinally sorted table-to-hash dictionary.
Baseline equality permits normalization-only changes and format 6 to 9 upgrade;
it does not establish semantic correctness or validate portable review evidence.

`OfflineNormalizationManifest` version 1 is the shared apply contract. It binds
the exact uppercase source SHA-256, source format, factual baseline SHA-256, tool
revision, proposer and actual proposal timestamp. Every canonical/name approval,
assignment and explicit old-to-survivor consolidation has its own decision GUID.
Assignments name individual item/template IDs, an exact catalog-normalized scoped
source and fingerprint, an approved target or justified exclusion, and an explicit
one-off/remember choice. Templates use an empty store scope and cannot remember
purchase rules. Unresolved observations remain separate with reasons.

A proposal has `Review: null`. The `propose` command creates an empty typed scaffold
bound to the source; populate explicit decisions using the private inspection.
Proposal generation must never fill review fields.
A separate reviewer records identity, actual review timestamp, explicitly reviewed
decision IDs and individual rationale. Apply requires complete review; no implicit
batch approval. The source service validates IDs, source keys and whole-scope
consistency for remembered rules. Canonical/name approvals are required even for
existing targets. Consolidations cannot chain or cycle. Source fingerprints reuse
`NormalizationCatalogPolicy.Normalize/Hash`: U+0020 trim, ordinal text, .NET JSON,
uppercase SHA-256. Do not reimplement these rules in another language.

Apply retains the full reviewed manifest sidecar and its byte SHA-256, and uses `offline-agent:<reviewer>;manifest-sha256:<digest>` (at most 450 characters) for
truthful portable actor provenance, and use existing format 8 purchase review,
purchase exclusion and approved-mapping evidence. Offline provenance must not
invent application operations or require the reviewer to be a local app user.
Format 9 adds explicit typed consolidation authority so older applications reject an artifact whose catalog retirement they cannot apply.
Apply validates the source schema and reviewed manifest before copying, then changes a
private temporary copy in one SQLite transaction. Format 6 purchases all begin as
NeedsReview with legacy_unknown/unassigned provenance. Format 8/9 evidence is validated
before mutation; unaffected evidence and historical operations retain their meaning.
Consolidation removes the losing catalog row, redirects item/template links and scoped
mappings, and rebinds existing verified evidence to the reviewed survivor. It does not
turn previously unreviewed purchases into reviewed purchases.

Publication requires the factual/financial baseline, SQLite integrity/relationships and
the same pure review, mapping and assignment authority checks used by restore. The
current application supports USD only; other currency values and unsupported schemas
are rejected. Instance source/receipt/identity revisions are generated by restore,
not fabricated offline. Output names must be separate and unused.

The final atomic file move publishes B only after private `B.db.manifest.json` (exact
input bytes) and `B.db.lineage.json` (source, manifest, tool revision and B checksums)
have been written. Failures and cancellation remove temporary artifacts. Retain all
three files together. `verify` validates their checksums, metadata, evidence and A/B
baseline, then replays the same apply transformation from original A and compares the
complete catalog, evidence, assignments and template links, including untouched rows. Reapplying the exact A and manifest to an existing valid B verifies it and
returns unchanged; partial, tampered or mismatched artifacts are rejected. This inspection baseline is one independent gate; exact
identity expectations, corrupted-evidence rejection, repeated import, app/browser
verification and survival of new entry remain required acceptance work.

Format 9 `normalization_catalog` records of kind `consolidation` use the retired
canonical ID as their key. Their strict typed payload contains `RetiredId`,
`SurvivorId`, `DecisionId`, `ReviewedBy`, `ReviewedAt`, `Rationale`,
`ManifestSha256`, `SourceSha256` and `ToolRevision`. These fields preserve the
actual reviewed decision and source provenance; they do not claim an application
operation occurred. The survivor must be active and present, the loser absent.
Duplicate losers/decisions, self-merges, chains and cycles are rejected.

Restore consumes this explicit artifact authority transactionally before survivor
upsert, frees the retired identity's names, and uses the application's entity
removal and semantic Merge audit mechanisms. All live and trashed purchase and
template links follow the survivor; artifact upserts install the reviewed mapping
and purchase evidence for rows actually present in B. Purchases absent from B,
including trash, retain the survivor link as `legacy_unknown` with a fresh
assignment ID, no mapping/review authority, and `NeedsReview`; this invalidation
is recorded by the ordinary field-change audit alongside the consolidation audit.
Restoring such trash cannot resurrect its previous verified decision.
Fresh and frozen-source restores converge on the same
intended catalog. Repeated import skips already absent losers. No deletion is
inferred from omitted catalog rows. Source freeze remains the cutover precondition.

Consolidation authority exists only in this artifact and its retained manifest;
it is not persisted as operation history or re-exported by an already cleaned
installation. A subsequent export describes that installation's current catalog
and does not promise retirement in an arbitrary older database. Existing portable
operations remain inspect-only. Canonical and effective display names are checked
using the application's lowercase uniqueness policy before publication; failures
leave no published database or sidecars. PostgreSQL constraints remain authoritative
on import.

Format 11 additionally preserves and validates category mapping history and heads,
including revocations, fingerprints, operation hashes and referenced taxonomy.
