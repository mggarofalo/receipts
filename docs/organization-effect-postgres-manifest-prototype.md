# Inactive PostgreSQL manifest projection experiment

`OrganizationEffectPostgresManifestPrototypeTests` extends the session-local string
encoder experiment with the complete `OrganizationEffectContractV1.ManifestRequest`
projection. It creates only temporary typed header/member tables and `pg_temp`
functions. There is no migration, deployed SQL, capture witness, activation path,
or archive version change.

The fixture inserts primitive typed fields, not JSON or caller-computed hashes.
PostgreSQL reads those rows, rebuilds every nested object, sorts the fixed ASCII
property names using the C collation, preserves the explicit member ordinal and
path array order, emits lowercase UUIDs and six-digit UTC timestamps, and computes
SHA-256 over the v1 domain, one NUL byte, and the complete canonical UTF-8 bytes.
The assertions independently compare both bytes and hash against the actual .NET
v1 writer. The existing exhaustive Unicode scalar test remains the encoder oracle.

The full-manifest test covers an empty vector, multiple receipts/items, reverse
physical insertion, persisted member order, selected-category-to-root paths,
SQL null versus empty text, control characters, escaped ASCII, combining marks,
BMP and supplementary Unicode, microseconds including years 0001 and 9999, and a
non-UTC PostgreSQL session timezone. Valid field/context mutations must retain
byte/hash parity and change the baseline hash. Version and decision kind are
fixed v1 discriminants; invalid-contract validation is not implemented by SQL.
Direct SQL edits, reordering and deletion are compared against independently
updated .NET records without resubmitting a digest to the SQL projection.

This proves only serialization of this bounded typed-row model. The temporary
rows are populated from the test's caller-declared request, not selected from
production receipts, assignments, mappings and taxonomy history. A future witness
must derive the authoritative complete member vector from persisted domain rows
inside its transaction, enforce ownership/eligibility and revision checks, bind
approved mapping evidence and expected assignments, validate the category ancestry,
and define locking/isolation and singleton context rules. This experiment does
not supply those joins, constraints, validations or concurrency guarantees.
Deleting a stored member produces the exact bytes/hash for the smaller vector;
that is intentionally tested. Hash equality alone establishes neither capture
authority nor completeness, and must never authorize apply/undo.

Run against real Docker PostgreSQL with:

```powershell
dotnet test tests/Infrastructure.IntegrationTests/Infrastructure.IntegrationTests.csproj --filter 'FullyQualifiedName~OrganizationEffectPostgres'
```

Verification on 2026-10-28: the command above passed all 16 tests, zero skipped,
on the fixture's real pgvector/pgvector:pg17 Docker server. This includes 28 full
manifest projections and the existing 1,112,063 representable Unicode scalar
comparisons. Logs: `postgres-prototypes-test.log`; TRX:
`tests/Infrastructure.IntegrationTests/TestResults/postgres-prototypes.trx`
(local ignored artifacts). Boundary timestamp insertion uses an explicit finite
ISO text cast into the typed `timestamptz` column to avoid Npgsql's default
`DateTimeOffset.MinValue` to PostgreSQL `-infinity` sentinel conversion. Production
input/validation would need to define its timestamp transport and reject infinity.

## Relational extension (still inactive)

`OrganizationEffectPostgresRelationalPrototypeTests` reads the actual migrated
`OrganizationEffectManifests`, `OrganizationEffectItems`, operation reservations,
immutable mapping revisions and frozen taxonomy nodes. Its only caller inputs are
the manifest ID and ordered item-ID vector. The schema does not persist member
order; the caller must retain that vector for any future witness. SQL rejects
empty, duplicate, missing and extra IDs by exact equality with the stored item
set, then derives all values from database rows. It never accepts request JSON,
paths, projected member fields or caller hashes. The existing session-local
serializer receives its typed rows exclusively from that SQL projection.

The bounded experiment supports one normalized source group with one or more
members. Raw fields remain untouched in the manifest; mapping tuple linkage uses
ordinary U+0020 trimming and null/empty item-code normalization. It checks decision
shape and mapping/source/category/approval context, reserved operation identities,
manifest taxonomy context, nonempty source revisions and the selected-to-root
path, rejecting missing, archived and cyclic ancestry. It assumes ordinary
mapping source hashes, request hashes and semantic fingerprints have already
passed the application's existing mapping validation. It does not recompute those
policies, establish current approval/head ownership, validate live source
eligibility or assignment authority, or define concurrency/locking. Those remain
future witness prerequisites; byte/hash parity cannot authorize any effect.

Differential tests compare complete bytes and domain-separated SHA-256 with the
actual .NET v1 codec for one and two members, reverse physical insertion and
caller order. Rejection cases include malformed decision/context and incomplete
vectors. Missing ancestry, cyclic ancestry, mismatched taxonomy, empty revision
and absent operation tests deliberately bypass immutable/FK guards only inside a
rolled-back transaction in a private test database. No application schema or
production guard changes are part of the experiment.

The relational metadata guard uses the complete `.NET char.IsWhiteSpace` BMP set,
not PostgreSQL collation/regex whitespace. Tests compare that set with the running
.NET implementation and exhaust every PostgreSQL-representable BMP scalar against
the SQL predicate. Empty and all-whitespace Actor/PolicyVersion values (including
tabs, line breaks and NBSP) reject; surrounding whitespace, zero-width space and
supplementary characters retain exact codec byte/hash parity.
