# Design

## Context

See `proposal.md` for motivation. What shapes the approach:

- **One place authenticates to YNAB.** `YnabApiClient.ApplyAuth` reads `YNAB_PAT` from configuration on every request. `IsConfigured` is a synchronous property on `IYnabApiClient`, read by `RequireYnabConfiguredAttribute` and by the two status query handlers. In-memory cache keys are namespaced by a hash of the PAT.
- **`YnabAuthException` is a definite rejection.** It implements `IYnabDefiniteRejection`, which the push pipeline relies on to know a request was not applied.
- **Data Protection keys are already persisted** to `/data/DataProtection-Keys` in containers (`Program.cs`).
- **Secrets reach the API as environment variables**, bridged from files in the `/secrets` volume by `docker-entrypoint.sh`.
- **The app runs as one API process.** The one-off `--chatgpt-*` operator commands never call YNAB.
- **The client authenticates with a bearer header**, so a browser redirect from YNAB arrives with no credentials.
- **YNAB tables live in the `ynab` schema**; `YnabSelectedBudgets` is the precedent for deployment-wide YNAB state.

### Observed YNAB behaviour (spike, 2026-11-19)

Run against the real service with a throwaway application and `read-only` scope.

| Behaviour | Result |
|---|---|
| Code exchange, refresh or revoke without `client_secret` | Refused (`invalid_client`; 403 on revoke) |
| Access token lifetime | 7200 seconds |
| Refresh token | Rotates on every refresh |
| Previous pair after a refresh | Valid until the new access token is first used; then both halves die |
| Replay of a retired refresh token | Refused; the live pair is not revoked |
| PKCE | S256 only; a wrong or missing verifier is refused |
| Failed exchange (no secret, or wrong redirect URI) | Does not consume the code |
| Code reuse | Refused; tokens from the first exchange stay valid |
| `POST /oauth/revoke` (undocumented) | Needs the secret; revoking either token ends the whole grant; answers 200 for an unknown token too |
| Two grants on one application | Independent; revoking one leaves the other working |
| `http://localhost` redirect URI | Accepted |
| `state` | Returned unchanged |
| Client authentication by HTTP Basic | Works, as do body parameters |
| Rate-limit response headers | None |
| Application owner authorizing an unreviewed application | Works |
| Dynamic client registration | `mcp` scope and allow-listed MCP redirect URIs only |

YNAB's registration form accepts several redirect URIs for one application (confirmed by the owner), so development and production can share one application; with independent grants, connecting or disconnecting one does not affect the other.

Not observed: lifetimes of authorization codes and refresh tokens; whether revoking a fresh pair also ends its not-yet-retired predecessor.

The spike scripts were throwaway and are not kept; this table is the record.

## Goals / Non-Goals

**Goals:**

- Put every credential decision behind one seam so `YnabApiClient` no longer knows where its bearer token comes from.
- Make refresh safe against crashes and concurrent requests using the grace window YNAB provides.
- Keep the PAT path byte-for-byte equivalent in behaviour.

**Non-Goals:**

- A background job that refreshes tokens while the app is idle.
- Detecting token theft beyond logging the one signal YNAB gives (`invalid_grant`).
- Supporting more than one API process refreshing the same connection.
- Using `/oauth/token/info` or `/oauth/introspect` for status.

## Decisions

### 1. A credential source behind `ApplyAuth`

Application defines `IYnabCredentialSource`: it returns the current credential state (none, personal access token, OAuth connected, OAuth reconnect required) and, on request, a bearer token. Infrastructure implements it. `YnabApiClient` asks it for the token; `IYnabApiClient.IsConfigured` is replaced by the state, and `RequireYnabConfiguredAttribute` and the two status handlers read the state from the same source.

The first step lands this seam with a PAT-only implementation and no behaviour change, so the refactor is proven before OAuth exists.

*Alternative considered:* teach `YnabApiClient` about OAuth directly. Rejected: it would mix token lifecycle into a class that already carries caching, rate limiting and response bookkeeping.

### 2. Confidential client; three settings

`YNAB_OAUTH_CLIENT_ID`, `YNAB_OAUTH_CLIENT_SECRET` and `YNAB_OAUTH_REDIRECT_URI`, added to `ConfigurationVariables`. OAuth is "available" only when all three are set. The entrypoint's secrets bridge exports `YNAB_OAUTH_CLIENT_SECRET` from `/secrets/ynab_oauth_client_secret` when that file exists and the variable is not already set; the file is optional, unlike the three required secrets.

The redirect URI is configured, not derived from the request, because YNAB requires an exact match with the registered value and a reverse proxy makes derivation unreliable.

The secret is sent in the token request body (`client_secret_post`), which is the documented method.

*Alternative considered:* a public client with PKCE only. The spike showed YNAB refuses it.

### 3. Full-access scope

No `scope` parameter is sent, which yields YNAB's default full-access grant. The app pushes transactions and memos, and `read-only` is the only narrower scope offered.

### 4. The redirect lands on a client route

Redirect URI path: `/settings/ynab/callback`. That route reads `code`, `state` and `error` from the query, immediately replaces the browser address to drop them (they would otherwise sit in history and in Sentry's route breadcrumbs), and posts `code` and `state` to the API with the user's bearer token.

*Alternative considered:* an unauthenticated API callback. Rejected: it would need its own way to prove the caller is the Admin who started the flow.

### 5. Endpoints (Presentation)

A new `YnabOAuthController` at `api/ynab/oauth`, every action under the `RequireAdmin` policy, each sending a Mediator command:

| Endpoint | Success | Failures |
|---|---|---|
| `POST /api/ynab/oauth/authorization` | 200 `{ authorizationUrl }` | 409 problem: OAuth not configured |
| `POST /api/ynab/oauth/callback` `{ code, state }` | 200 connection status | 400 problem: attempt not valid; 400 problem: YNAB rejected the code; 503: YNAB unreachable |
| `DELETE /api/ynab/oauth/connection` | 200 `{ remoteRevocation }` (`succeeded`, `failed`, `notAttempted`) | — |

`YnabConnectionStatusResponse` gains `credentialSource` (`none`, `oauth`, `personalAccessToken`), `oauthAvailable`, `reconnectRequired` and `oauthConnectedAtUtc`. `YnabStatusResponse.isConfigured` keeps its name and now means "a usable credential exists". The 503 on unreachable YNAB stays bodiless, matching the existing YNAB endpoints.

### 6. Pending authorization attempts live in memory

State and PKCE verifier are generated server-side and held in `IMemoryCache` for ten minutes, keyed by a hash of the state and tagged with the initiating user id. An attempt is removed on success or on a definite rejection from YNAB, and kept on a transport failure so the same code can be retried (the spike showed a failed exchange does not consume the code).

*Alternative considered:* a database table. Rejected: the app is a single process, a restart mid-flow costs one more click, and the verifier never needs to touch disk.

### 7. Stored connection (Infrastructure)

One table, `ynab.YnabOAuthConnections`, holding at most one row:

| Column | Notes |
|---|---|
| `Id` | Fixed, well-known id; enforces the single row |
| `Status` | `Connected` or `ReconnectRequired` |
| `ProtectedTokens` | Data Protection payload of access and refresh token; null when reconnect is required |
| `AccessTokenExpiresAtUtc` | Plain, so expiry is checked without decrypting |
| `ConnectionId` | A new random id on every completed authorization; namespaces cache keys. Not a counter: disconnect deletes the row, so a counter would restart and two different connections could share a cache namespace |
| `ConnectedAtUtc`, `ConnectedByUserId`, `RefreshedAtUtc`, `StatusChangedAtUtc` | Bookkeeping |
| `ConcurrencyStamp` | Changed on every write; the optimistic concurrency token |

Tokens are protected with a dedicated purpose string. A payload that fails to unprotect is handled as `ReconnectRequired` and its ciphertext is cleared. No domain entity is added: this is integration state, like the other `Ynab*` entities.

Disconnect deletes the row. The cache key prefix in `YnabApiClient` becomes credential identity: the existing PAT hash for a PAT, `oauth-{ConnectionId}` for OAuth.

### 8. Refresh: single-flight, persist before use

A singleton coordinator serialises refresh with a `SemaphoreSlim`. On a token request, if the access token expires within five minutes: take the lock, re-read the row (another caller may have refreshed), call YNAB, **commit the new pair, then return it**. Because YNAB keeps the old pair alive until the new access token is first used, a crash between the refresh call and the commit leaves a stored pair that still works. If the commit hits a concurrency conflict, the freshly received pair is dropped and the row is re-read.

Outcomes of the refresh call:

- **`invalid_grant`** — set `ReconnectRequired`, clear tokens, log a warning with a dedicated event id and no token material, throw `YnabAuthException`.
- **Any other refusal** (for example `invalid_client`, a wrong client secret) — leave the row alone, log an error and throw `YnabAuthException`. The connection itself was not refused, so its tokens are kept.
- **No answer, or 5xx** — leave the row alone and throw a temporary failure that is both an `HttpRequestException`, like the YNAB client's other temporary failures, and an `IYnabDefiniteRejection`, because the YNAB request was never sent.

A 401 from the YNAB API on an unexpired OAuth token triggers one forced refresh and one retry of the request. A 401 means YNAB did not apply the request, so retrying is safe for pushes. If the forced refresh is refused, the `invalid_grant` path applies.

There is no background refresh. Tokens are acquired just in time, when a YNAB request needs one.

The current access token is also held in memory, so a YNAB request does not read and decrypt the row every time. The cached entry carries the connection id and expires five minutes before the token does, which is the same threshold that triggers a refresh. It is dropped on connect, disconnect, a move to reconnect required, and a 401 from YNAB. The database row stays the source of truth: a cache miss falls through to the row, and only then to a refresh.

*Alternative considered:* a cross-process file lock as used for ChatGPT credentials. Not needed: only the API process calls YNAB, and the row version covers the unexpected second writer.

### 9. Reconnect required does not fall back to the PAT

When the row is `ReconnectRequired`, YNAB requests fail as unauthenticated even if `YNAB_PAT` is set. Falling back would hide the only theft signal available and could silently switch the app to a different YNAB account. An explicit Disconnect clears the row and restores the fallback.

### 10. Disconnect

Read and unprotect the tokens, `POST /oauth/revoke` with the refresh token and client credentials, then delete the row regardless of the outcome. The response reports `succeeded` (YNAB answered 200), `failed` (anything else, or no answer) or `notAttempted` (no row, unreadable tokens, or OAuth client settings missing). YNAB answers 200 for unknown tokens too, so `succeeded` means "request accepted", and the UI wording says so.

### 11. A separate HTTP client for `app.ynab.com`

A typed `YnabOAuthClient` handles exchange, refresh and revoke. It has its own base address and a short timeout, and no retry on refusals. It does not share the `ynab` resilience handler or the rate-limit tracker, which count requests to `api.ynab.com`.

### 12. Backup

`YnabOAuthConnections` is not added to `PortableBackupFormat`, `BackupService` or `BackupImportService`, and is listed among the deliberately excluded tables in `BackupService`'s comment and in `docs/backup-restore.md`. The ciphertext would be useless without the key ring, and restoring it would only produce a confusing reconnect prompt.

### 13. Privacy policy

A public client route `/privacy`, outside the authenticated layout alongside `/login`, rendering static content with a last-updated date. Linked from the YNAB settings page and from the sign-in page. Its third-party section is written from what the code sends to Jev and ChatGPT, checked during implementation, not from memory.

YNAB's OAuth requirements page suggests TermsFeed for drafting. A generated draft is an acceptable starting point for the general sections; the YNAB and third-party sections are written by hand from the code, because a generator cannot know what this app sends where.

The text describes what the software does and names the deployment's operator as the party responsible for the data, since each deployment is run by its own owner.

### 14. Layer ownership

| Piece | Layer |
|---|---|
| `IYnabCredentialSource`, credential state, connection commands and their handlers, `IYnabConnectionService` | Application |
| Credential source, refresh coordinator, `YnabOAuthClient`, token protector, entity, configuration, migration | Infrastructure |
| `YnabOAuthController`, mapper additions, `RequireYnabConfiguredAttribute`, OpenAPI | Presentation |
| Settings controls, callback route, privacy page, hooks | Presentation (client) |
| Nothing | Domain |

### 15. Decision record

`docs/decisions/2026-11-19-ynab-oauth-connection.md` records the choices meant to outlast this change: OAuth tokens encrypted in the database, client credentials from the environment, `YNAB_PAT` kept as a fallback, tokens never in the backup, and no PAT fallback while reconnect is required. It reverses no accepted decision.

## Risks / Trade-offs

- **Refresh token lifetime is unknown** → A deployment left idle may come back needing a reconnect. That is one click, and the state is visible. If it happens in practice, a periodic refresh can be added without changing the stored shape.
- **The revocation endpoint is undocumented** → Disconnect always deletes locally and reports the remote outcome, so a change at YNAB degrades to "remove access in YNAB yourself".
- **No replay detection at YNAB** → A stolen refresh token used first wins; ours then fails with `invalid_grant`. This is logged as a security-relevant warning and surfaces as reconnect required. The policy in decision 9 keeps it from being masked.
- **Losing the Data Protection key ring** → The connection becomes unreadable. Handled as reconnect required; documented alongside the backup guidance.
- **Local development** → The redirect URI must be fixed and registered, and Aspire puts Vite on a dynamic port. `YNAB_PAT` remains the documented development path; OAuth in development needs a pinned port, whose localhost redirect URI can be added to the same OAuth application as production's.
- **Code and state in the browser address** → Replaced immediately on arrival; the code is single-use and useless without the secret and verifier.
- **Privacy policy accuracy** → A static page can drift from behaviour. The spec ties the third-party statement to what is sent, and the task verifies it against the code.
- **Two credentials configured at once** → The status names the one in use, so an operator who sets `YNAB_PAT` and also connects OAuth can see which applies.

## Migration Plan

**Schema.** One migration creates `ynab.YnabOAuthConnections`. No existing table or row changes, and there is no backfill.

**Existing deployments.** Nothing changes until an Admin connects. A deployment with `YNAB_PAT` keeps using it. To move over: register the OAuth application at YNAB, set the three settings, restart, click Connect, then remove `YNAB_PAT` if wanted.

**Upgrade rehearsal.** Latest released container with the restored backup, upgraded in place to the new image. Then check: the migration applied once; the new table exists and is empty; YNAB features still work on `YNAB_PAT`; the status reports `personalAccessToken`; a backup exported afterwards has no connection table. Runs only after an explicit go.

**Rollback.** `Down` drops the table; any stored connection is lost and the deployment falls back to `YNAB_PAT` or to not configured. The previous release runs against the new schema unchanged, since it never references the table, so rolling back the image alone is safe.

**Migration note.** `docs/migrations/<migration-id>.md` covers the above.
