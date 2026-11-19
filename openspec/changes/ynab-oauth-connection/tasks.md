# Tasks

Tests are written by subagents, per AGENTS.md. Each group lands its own tests and docs.

## 1. Refactor: credential source seam (behaviour unchanged)

- [x] 1.1 Add `IYnabCredentialSource` and the credential state type in Application, with a PAT-only Infrastructure implementation reading `YNAB_PAT`; verify with unit tests for set, blank and missing values
- [x] 1.2 Make `YnabApiClient` take its bearer token and cache-key identity from the credential source instead of reading configuration; verify the existing `YnabApiClient` tests pass unchanged apart from construction
- [x] 1.3 Replace `IYnabApiClient.IsConfigured` with the credential state in `RequireYnabConfiguredAttribute`, `GetYnabConnectionStatusQueryHandler` and `GetYnabStatusQueryHandler`; verify existing handler and filter tests pass and `dotnet test Receipts.slnx --filter "Category!=Integration"` is green
- [x] 1.4 Confirm no behaviour changed: `openapi/spec.yaml` and `src/client` are untouched in this group's diff

## 2. API contract

- [x] 2.1 Add the three `/api/ynab/oauth/` operations and their request/response schemas to `openapi/spec.yaml`, with 400 and 409 responses declared as `application/problem+json`; verify the spec lints clean
- [x] 2.2 Add `credentialSource`, `oauthAvailable`, `reconnectRequired` and `oauthConnectedAtUtc` to `YnabConnectionStatusResponse` and update the descriptions that say "personal access token"; verify `scripts/check-breaking.mjs` reports no breaking change
- [x] 2.3 Regenerate server DTOs and `src/client/src/generated/api.d.ts`; verify `dotnet build Receipts.slnx` and `npm run prebuild` in `src/client` both pass

## 3. Stored connection and migration

- [x] 3.1 Add the `YnabOAuthConnection` entity, its configuration in the `ynab` schema (single well-known row, row version) and a repository; verify with repository tests for create, replace, status change and delete
- [x] 3.2 Add the EF migration and `docs/migrations/<migration-id>.md` (new table, no backfill, `Down` drops it, previous release compatible); verify `HasPendingModelChanges` is false and `dotnet run scripts/run-prerequisite-tests.cs -- postgres` passes
- [x] 3.3 Add the token protector with a dedicated Data Protection purpose; verify with tests that a round trip works, that the stored payload contains neither token in clear text, and that an unreadable payload reports failure without throwing
- [x] 3.4 Keep the table out of the portable backup and list it as deliberately excluded in the `BackupService` comment and `docs/backup-restore.md`; verify with a test that an export taken with a connection has no such table and a restore leaves the connection row in place

## 4. Talking to YNAB's OAuth endpoints

- [x] 4.1 Add the three OAuth settings to `ConfigurationVariables` and an options type that reports OAuth as available only when all three are set; verify with unit tests for each missing combination
- [x] 4.2 Add the typed `YnabOAuthClient` for code exchange, refresh and revoke against `app.ynab.com`, separate from the `ynab` resilience handler and rate-limit tracker; verify with handler-stub tests for success, `invalid_grant`, `invalid_client`, 5xx and transport failure, and that no test log output contains a token or the secret
- [x] 4.3 Build the authorization address (client id, redirect URI, state, S256 challenge, no scope) and the in-memory pending-attempt store with ten-minute expiry bound to the initiating user; verify with tests for unknown, expired, reused and wrong-user state, and that a transport failure leaves the attempt usable

## 5. OAuth credential source and refresh

- [x] 5.1 Extend the credential source to prefer a stored OAuth connection, then `YNAB_PAT`, then none, with no PAT fallback while reconnect is required; verify with unit tests for each row of that order
- [x] 5.2 Implement the single-flight refresh coordinator that re-reads the row under the lock and commits the new pair before returning it; verify with tests that concurrent callers cause exactly one refresh call and that a commit failure never returns an unsaved token
- [x] 5.3 Handle refresh outcomes: `invalid_grant` sets reconnect required, clears tokens and logs the security event without token material; no answer or 5xx leaves the row unchanged; verify each with unit tests
- [x] 5.4 On a 401 from the YNAB API with an OAuth token, force one refresh and retry the request once; verify with tests for refresh-succeeds-then-200, refresh-refused, and that a PAT 401 is not retried
- [x] 5.5 Treat an unreadable stored payload as reconnect required and clear it; verify with a test using a protector keyed differently from the one that wrote the row
- [x] 5.6 Hold the current access token in memory, tagged with the connection id and expiring five minutes before the token does, dropped on connect, disconnect, reconnect required and a 401; verify with tests that repeated YNAB requests read the row once, and that each of the four events forces the next request back to the row
- [x] 5.7 Namespace the in-memory YNAB caches by connection id; verify with a test that budgets cached under one connection are not returned after reconnecting

## 6. Connect, disconnect and status

- [x] 6.1 Add the start-authorization, complete-authorization and disconnect commands with handlers in Application behind `IYnabConnectionService`; verify with handler tests covering every scenario in `specs/ynab-connection/spec.md` for those three requirements
- [x] 6.2 Implement disconnect to revoke at YNAB then delete the row whatever the outcome, reporting `succeeded`, `failed` or `notAttempted`; verify with tests for each outcome and that the row is gone in all three
- [x] 6.3 Add `YnabOAuthController` under the `RequireAdmin` policy using `ApiProblem` for 400 and 409; verify with API tests for 200, 400, 409, 403 for a non-admin, and 503 when YNAB is unreachable
- [x] 6.4 Populate the new connection status properties and update the mapper; verify with tests for the four status scenarios in the spec and that the serialized response contains no token
- [x] 6.5 Run `dotnet run scripts/generate-api-contract.cs` and confirm no drift between the server output and `openapi/spec.yaml`

## 7. Client: connection controls

- [x] 7.1 Add hooks for starting an authorization, completing it and disconnecting, each with a `use*.test.ts` and cache invalidation per `docs/query-invalidation.md`; verify the hook tests pass
- [x] 7.2 Add the `/settings/ynab/callback` route that strips `code`, `state` and `error` from the address before posting, and handles success, declined and invalid-attempt results; verify with a page test for each result and that the address no longer holds the query
- [x] 7.3 Update `YnabSettings.tsx` to show connect, reconnect and disconnect to Admins by state, with a confirmation before disconnect and the remote-revocation outcome afterwards; verify with page tests for each state and for a non-admin seeing no actions
- [x] 7.4 Replace the "set `YNAB_PAT`" wording on `YnabSettings.tsx` and `Ynab.tsx` with text that covers both ways to enable the integration; verify with updated page tests and `tests/visual/field-overlap.spec.ts` still passing

## 8. Privacy policy

- [ ] 8.1 Read what the Jev and ChatGPT integrations send and record, in the PR description, which data kinds leave the deployment and whether any are YNAB-sourced; verify by citing the request-building code for each
- [x] 8.2 Add the public `/privacy` page with the statements required by `specs/privacy-policy/spec.md` and a last-updated date, written from the findings of 8.1 (a TermsFeed draft, which YNAB's requirements page suggests, may seed the general sections only); verify with a page test that it renders while signed out and contains each required section
- [x] 8.3 Link the policy from the YNAB settings page beside the connection controls and from the sign-in page; verify with page tests

## 9. Deployment and documentation

- [ ] 9.1 Bridge the optional `/secrets/ynab_oauth_client_secret` file in `docker-entrypoint.sh` without failing when it is absent, and add the three settings as commented entries in `docker-compose.yml` and `.env.example`; verify by starting the container with and without the file
- [x] 9.2 Write `docs/ynab-connection.md`: registering the OAuth application (name must not contain "YNAB" unless preceded by "for"), the redirect URI (one application can list several, so development and production can share it), the three settings, connect and disconnect, reconnect required, the `YNAB_PAT` fallback, development setup, and the observed YNAB behaviour table from `design.md`; verify every setting name and path in it matches the code
- [x] 9.3 Update `docs/deployment.md`, `README.md` and `docs/architecture.md` where they describe YNAB setup or the secrets table; verify no remaining text presents `YNAB_PAT` as the only option
- [x] 9.4 Add `docs/decisions/2026-11-19-ynab-oauth-connection.md` as described in design decision 15; verify it exists and is linked from `docs/ynab-connection.md`

## 10. Integration and rehearsal

- [x] 10.1 Run the full unit suite, the Postgres gate, the client test suite and the client build; verify all are green locally
- [ ] 10.2 Open the PR with a compatible Conventional Commit title (no `!`, no `breaking-changes-allowed` label) and confirm the API Breaking Changes gate passes
- [ ] 10.3 Checkpoint: CI green; report ready for rehearsal and wait for the go
- [ ] 10.4 Upgrade rehearsal: latest released container with the restored backup upgraded in place to the new image; verify the migration applied once, the new table is empty, YNAB works on `YNAB_PAT`, status reports `personalAccessToken`, and a fresh export has no connection table
- [ ] 10.5 Owner check against real YNAB on the rehearsal or deployed instance: connect, push one transaction, disconnect, and confirm the application's access is gone in YNAB; verify each step in the UI

## Workflow follow-up

- Archive the change on its branch as the last commit before squash-merge, after confirming the ADR and the migration note exist.
- After merge and release, link the PR from the tracking work item and set it to Done once every acceptance item is verified.
