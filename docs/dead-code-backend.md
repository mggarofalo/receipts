# Backend dead-code evidence

This audit records the v0.14.0 state. Receipt photo infrastructure was subsequently
retired in RECEIPTS-994; see [retirement guidance](receipt-image-storage.md).

The audit starts at main `3cef6d257fc424f51205a3060066dc2abc8c0142` and also checks the integrated refactor at `d71e96e85030032dd52693bfa3ea32792a942a79`. The removed backend declarations are identical at both revisions. Their production consumers were checked against the integrated revision, including generated Mediator dispatch. A separate semantic run against main failed closed on historical advisory diagnostics embedded in its restore metadata; it is not counted as a successful semantic run. Main declarations and production references were compared separately; final integrated proof and builds remain required.

## Reproduce the candidate proof

Restore/build the target checkout first so its generated API DTOs and project references exist. Run the audit tool from the new checkout against the old checkout's solution:

```powershell
dotnet run --project scripts/dead-code/DeadCodeAudit.csproj -- C:/path/to/baseline/Receipts.slnx scripts/dead-code/backend-candidates.json
python scripts/dead-code/test_verifier.py
```

After all backend and integration deletions are integrated:

```powershell
dotnet run --project scripts/dead-code/DeadCodeAudit.csproj -- Receipts.slnx scripts/dead-code/backend-candidates.json --expect-absent
```

The selected .NET SDK supplies Roslyn through its bundled `dotnet-format` tool. The verifier uses MSBuildWorkspace and SymbolFinder, the semantic machinery behind C# editor reference searches. It loads every solution project, compiles it with its real references and generators, and rejects compiler errors, workspace failures, missing inventoried production projects, missing candidates, and unclassified references. This semantic-only analysis leaves NuGet auditing enabled but does not promote warnings to errors, allowing historical advisory-bearing baselines to be inspected. Every compilation error and workspace failure still rejects the proof; strict builds and dependency auditing are separate checks. No textual-name count establishes removal eligibility. Audit-tool fixtures use disposable solutions and do not change application code.

The manifest is a reviewed candidate inventory, not a universal dead-code detector. Type candidates include explicitly declared members, so extension-method calls count even when their containing type is not spelled at the call site. Compiler-synthesized record overrides are not independently searched: Roslyn's override-family search would incorrectly attribute every `object.ToString` implementation to a candidate record. The record type itself is searched, including its generated Mediator references. For generic/inherited/override-rich candidates, extend the proof instead of assuming this inventory is sufficient.

The committed [baseline evidence](../scripts/dead-code/evidence/backend-d71e96e8.txt) records every accepted edge. Test references are identified by their project files under `tests/`. Explicit cluster files are only allowed for this reviewed deletion set. Source-generated Mediator dispatch is admitted only for the two explicitly marked requests; a `GeneratedCode` attribute does not make authored code generated. Dispatch registration is not evidence that anyone constructs or sends a request. Removing these request/handler pairs removes the generated dispatch entries on rebuild.

## Removed responsibility islands

| Candidate | Production evidence and retained owner |
| --- | --- |
| `Common.ApiKeyUtilities` | Only utility tests called it. `ApiKeyService` creates production keys, applies hashing and owns lifecycle. |
| `Common.DateTimeExtensions` / `DecimalExtensions` | All three `Between` overloads had test-only references; no production extension invocation exists. |
| `CreateYnabSyncRecordCommand` / `UpdateYnabSyncRecordStatusCommand` and handlers | Only paired handlers and generated dispatch referenced the requests. No API, tool or worker constructs/sends them. Live YNAB workflows call the existing sync-record service and retain its durable state behavior. |
| `YnabConvert.FromMilliunits` | Test-only; `ToMilliunits` and its five tests remain live. |
| YNAB server-knowledge repository, interface and registration | DI registration and orphan mock assertions were the only edges. Persisted entity, schema, migrations and backup compatibility remain. |
| `DeleteReceiptImagesAsync` interface/implementation member | No production caller. Version publication remains owned by `SaveImageSetAsync`; the cleanup worker uses `CleanupUnreferencedAsync` with reference snapshots and a grace period. |

The 18 Common utility tests and empty Common.Tests project are removed with the code they exclusively exercised. The three inverse-conversion test cases are removed with their unused method. This does not weaken tests for retained behavior. Integration cleanup replaces orphan `Never` assertions with assertions against the real live port; see the integration audit evidence.

## Runtime roots and exclusions

The API controllers/routes, hosted services, Aspire host, DbMigrator/DbSeeder/DbExporter entry points, explicit DI registrations and their constructor dependencies remain roots. Repository registrations are not enough on their own: the server-knowledge registration's interface has no production consumer or dynamic resolver. The application does not publish these internal assemblies as a third-party library or deserialize Mediator request types from external names. Reflection inspection found entity ownership scanning, EF converters/configurations, validation and API DTO serialization; none activates these candidates.

Do not remove EF entities, configurations, migration/snapshot classes, JSON DTO members, API endpoints or configuration keys merely because ordinary reference search misses their framework/external consumers. `DesignTimeDbContextFactory`, Mapperly partial methods, assembly-scanned validators, all reachable Mediator requests, Money's ratio operator path, and receipt list projections were retained. The unused PDF pipeline was already removed by RECEIPTS-988 and is not counted again here.

## Verifier checks and limits

Six fixtures exercise actual compilations: a production caller is rejected; a test-only caller is admitted; an authored caller with a generated-code annotation is rejected; incomplete binding and missing production projects fail closed; removed symbols pass absence mode. The baseline's real source-generated Mediator edges additionally exercise the generated-dispatch classification. Independent review must check runtime roots and the manifest allowlist before deletion; passing this tool alone cannot prove arbitrary reflection or externally invoked code dead. Final compilation, API contract checks and retained behavior suites are separate obligations.
