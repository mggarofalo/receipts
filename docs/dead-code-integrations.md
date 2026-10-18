# Integration dead-code evidence (RECEIPTS-989)

The audit evaluated the production graph at `d71e96e85030032dd52693bfa3ea32792a942a79`
and checked each deletion against the `main` baseline
`3cef6d257fc424f51205a3060066dc2abc8c0142`. Tests and DI registration are not, by
themselves, production consumers. Public application interfaces are internal
assembly boundaries; this repository does not publish these assemblies as an SDK.

## Removed clusters

| Cluster | Incoming edges before deletion | Runtime-root analysis |
| --- | --- | --- |
| `IYnabServerKnowledgeRepository`, `YnabServerKnowledgeRepository`, DI registration | Implementation, one `AddScoped` registration, an unconnected test mock | No constructor, factory, `GetRequiredService`, or application use case consumes the interface. `YnabMemoSyncService` deliberately fetches complete date windows and never reads or writes a cursor. |
| `IImageStorageService.DeleteReceiptImagesAsync` and implementation | Two test `Times.Never` assertions | No production invocation or delegate capture. Upload publishes a version then swaps paths; `ReceiptImageCleanupService` owns deletion through `CleanupUnreferencedAsync`, protected by reconciliation locks, reference snapshots and a grace period. |
| `scripts/run.sh`, `scripts/watch.sh`, `scripts/postgres.sh` | Run/watch invoke postgres; each checks its own pathname | These are obsolete **entrypoints**, not merely files with no callers. Both launch a nonexistent `src/Presentation/Client` Blazor project. The supported startup is Aspire (`docs/development.md`); CI, deployment, editor tasks and hooks do not invoke this cluster. |
| Central `Microsoft.AspNetCore.SignalR.Client` package version | Central version declaration only | No project references or resolved `libraries` entry in restored assets. This is unrelated to the live browser `@microsoft/signalr` dependency. |

The repository's relevant dynamic mechanisms were inspected: Application validator
assembly scanning is limited to validators; Mediator and Mapperly use generated
code; `OwnedChildrenMapProvider` scans soft-deletable owned entity types, not
services. EF entity configuration and migration discovery remain rooted. There
is no string-based plugin discovery for either deleted integration API.

## Reproduce the discovery evidence

Run against the baseline revision, using tracked files so build outputs do not
masquerade as source consumers:

```powershell
git grep -n -E 'IYnabServerKnowledgeRepository|YnabServerKnowledgeRepository|DeleteReceiptImagesAsync' -- src tests
git grep -n -E 'Assembly.Load|GetTypes\(|GetType\(|Activator\.|GetRequiredService|AddValidatorsFromAssembly' -- src
git grep -n -E 'scripts/(run.sh|watch.sh|postgres.sh)|src/Presentation/Client' -- .
git grep -n 'Microsoft.AspNetCore.SignalR.Client' -- '*.csproj' '*.props' '*.targets'
Test-Path src/Presentation/Client
```

For package evidence, parse each restored `project.assets.json` and inspect its
`libraries` keys; do not count the central-version metadata copied into the
`project` section as an installed package. Search findings identify candidates;
semantic reference analysis, runtime-root inspection and deletion followed by
compilation establish the removal boundary. Passing tests alone cannot prove
arbitrary code unreachable.

## Preserved obligations and verification

- Preserve `YnabServerKnowledgeEntity`, its configuration, DbSet and all migrations:
  persisted schema is a compatibility obligation, even though the current application
  does not use the cursor. Backup exclusions remain unchanged.
- Preserve the live YNAB HTTP adapter and wire models. The adapter's optional cursor
  support is an external-integration capability, outside these closed implementation
  islands.
- Replace the YNAB test that verified an uninjected mock was never called with a
  repeated-operation assertion on the actual HTTP-client port: both date-window
  fetches must omit the returned cursor. Preserve the existing single-fetch check.
- Upload success and database-failure tests verify the expected publication call
  and **no other storage calls**, guarding against premature cleanup without retaining
  a dead deletion method solely for an assertion.
- Run focused YNAB memo, upload, storage and cleanup tests; compile the complete
  integrated solution and regenerate/check the API contract. These removals must not
  change HTTP schemas or database model/migrations.

Retained apparent candidates include root TypeScript (commitlint's
`cosmiconfig-typescript-loader` has a required TypeScript peer), NJsonSchema (runtime
OpenAPI response validation), the cryptography security pin, Linux native image
assets, and explicitly registered hosted workers. Manual maintenance scripts and
migration backfill data are not classified dead merely because other code does
not invoke them. The unused PDF pipeline was already removed by RECEIPTS-988;
this change does not remove any further PDF consumer.

## Focused evidence

On the main-based deletion branch, 35 Infrastructure unit tests (34 YNAB memo
cases and one cleanup-worker case) and five upload-handler tests passed with zero
skips. The separate filesystem integration suite is not included in those counts.
A temporary non-null cursor injection caused the new repeated-fetch regression to
fail; after restoring production code all 34 memo cases passed again. These
focused runs allowed the known baseline NuGet advisories as warnings, with audit
enabled. Strict builds and security auditing belong to the final integration with
RECEIPTS-986 and RECEIPTS-988; no project warning policy was changed.
