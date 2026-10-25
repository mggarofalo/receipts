# Jev connection setup

The canonical API-server credential is `TYPESAFE_API_KEY`. Configure local development with:

The API project declares its own stable `UserSecretsId`; AppHost has a separate
identity. The offline classification harness can explicitly reuse the API store
in live mode with `--api-user-secrets` (see
[prepared evaluation command](classification-evaluation/README.md)). No second
credential configuration is required.

```powershell
dotnet user-secrets set TYPESAFE_API_KEY "<your-key>" --project src/Presentation/API
```

User Secrets are local development storage, not encrypted production storage. Never paste credentials into chat, issues, logs, source control or exports. Deployments inject `TYPESAFE_API_KEY` through the API process environment/secret manager. There is no key-per-file provider: map mounted secrets into the environment with deployment tooling. Never use client/Vite configuration.

Actual precedence follows YNAB's `YNAB_PAT`: `WebApplication.CreateBuilder` defaults, then in Development additional reloadable `appsettings.Development.json` and API User Secrets, then `AddEnvironmentVariables` last. Environment overrides secrets and JSON. Aspire launches the API project, which reads its own secrets; AppHost secrets are not implicitly API settings.

Set `Jev__Enabled=false` to disable. Restart the API after provisioning, removing or rotating credentials or changing enabled state. Environment settings do not reload. Each operation reads current typed options; no validated identity/catalog persists server-side. Replacement options fence an in-flight old-key result.

Administrators see a Jev connection card in Settings. GET `/api/jev/status` is passive: disabled, missing or configuredUnverified, with zero provider requests. Explicit POST `/api/jev/check` makes one cancelable, 10-second GET `https://api.typesafe.ai/v1/models`, without retries, inference or receipt data. It returns connected, invalid (401/403), unavailable (rate limit, timeout, malformed response, provider failure), or disabled/missing. Errors are redacted. Connected means authentication succeeded at that instant, not inference readiness. Refreshing resets to unverified and clears the catalog; check again after restart/rotation.

Protocol sources inspected 2026-10-25: [TypeSafe OpenAPI](https://api.typesafe.ai/openapi.json), [documentation index](https://docs.typesafe.ai/llms.txt). Models use bearer authorization and `{ models: [{ name, description, release_date }] }`. Tests use fake HTTP handlers. No live authentication or paid inference was performed.
