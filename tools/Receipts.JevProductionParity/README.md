# Offline Jev production question and decision parity

This tool references the shipped Infrastructure/Application assemblies and calls
`JevSuggestionQuestions.Serialize` and the hierarchical `CategorySuggestionPolicy.Decide`
directly. There is no copied question wording or decision policy. It instantiates
no provider, HTTP client, application host, database, or model. It reads only the
explicit fixture file and writes one JSON result to stdout; it reads no credentials
or application configuration.

From the repository root:

```powershell
dotnet run --project tools/Receipts.JevProductionParity -- tools/Receipts.JevProductionParity/fixtures/synthetic-fruit.json
dotnet test tests/Infrastructure.Tests/Infrastructure.Tests.csproj --filter FullyQualifiedName~JevProductionParityTests
```

Build/restore may access package feeds. After building, run the tool DLL directly
to avoid build/restore and to obtain clean machine-readable stdout:

```powershell
dotnet tools/Receipts.JevProductionParity/bin/Debug/net10.0/Receipts.JevProductionParity.dll tools/Receipts.JevProductionParity/fixtures/synthetic-fruit.json
```

Supply synthetic or independently sanitized descriptions, taxonomy labels, and
judgments. The tool does not sanitize or certify input. Its output includes the
exact serialized request, so handle it with the same care as the fixture.

The fixture contains `id`, production `request`, and a typed production hierarchy
`judgment`. The checked-in example is entirely synthetic. Preserve taxonomy order:
production branch question names depend on it. A judgment must cover every root
and branch Choice, including branches the final path does not visit. Invalid
judgments remain production `InvalidResponse` results; invalid requests and
oversized payloads exit with code 2. Valid execution exits 0 even when the policy
rejects a judgment or abstains, since those are recorded evaluation outcomes.

`questionSha256` hashes the exact production UTF-8 request bytes, including state,
question order, and options. `decisionSha256` hashes the compact camelCase JSON of
the entire production decision, including provenance and usage. Both are lowercase
SHA-256 hex and exclude fixture id, filenames, timestamps, and environment values.
The fingerprints are stable for identical inputs and the same code/runtime; they
are not a canonical cross-language JSON standard. Compare records at a recorded
git revision and .NET SDK/runtime version. Model and policy version come directly
from production constants. No evaluation accuracy is implied by matching hashes.

This adapter covers question serialization and typed judgment policy only. It
does not validate raw provider-response parsing, authorization, concurrency,
credential lifecycle, transport errors, end-to-end orchestration, or model quality.
Those remain separate provider/integration tests. It cannot send inference calls.
