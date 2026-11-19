# Proposal

## Why

The only way to connect the app to YNAB today is a personal access token in the `YNAB_PAT` environment variable: a non-expiring, full-access credential that the owner must copy out of YNAB and that then sits in a deployment file. YNAB's OAuth authorization code flow lets an Admin connect from the app with a click, so nothing redeemable on its own ever crosses the owner's workstation or clipboard, and the long-lived credential becomes a rotating refresh token that never leaves the server.

A spike against the real YNAB service on 2026-11-19 settled the behaviours the design depends on (secret required, refresh rotation with a grace window, PKCE enforced, working revocation). They are recorded in `design.md`.

## What Changes

- An Admin can **connect** the deployment to YNAB from the YNAB settings page using OAuth (authorization code with PKCE), and **disconnect** it again.
- The app obtains, stores and refreshes YNAB tokens itself. Tokens are encrypted at rest, never returned by the API, and never written to logs.
- Disconnect asks YNAB to revoke the grant and always removes the stored tokens, even when YNAB cannot be reached.
- A refresh that YNAB rejects leaves the app in a visible **reconnect required** state instead of failing silently.
- The connection status reported by the API says which credential is in use (none, OAuth, personal access token) and whether OAuth is available on this deployment.
- `YNAB_PAT` keeps working unchanged as an environment-only fallback when no OAuth connection exists.
- OAuth tokens are never included in the portable backup.
- A **privacy policy** page is added to the app, as YNAB's OAuth Application Requirements demand, reachable without signing in and linked from the YNAB settings page.
- Deployment gains three optional settings: the YNAB OAuth client id, client secret and redirect URI.

**Out of scope**

- Multi-tenancy: there is one deployment-wide YNAB connection, not one per user.
- Any page for pasting a personal access token into the app.
- YNAB's dynamic client registration (reserved by YNAB for its MCP clients).
- The `/budgets` to `/plans` API path rename, tracked separately.
- Removing `YNAB_PAT`.
- Restricting the OAuth grant to the `read-only` scope: the app writes transactions and memos.

**OpenAPI**: `openapi/spec.yaml` changes. The change is compatible: three new endpoints, and new properties on the existing connection status response. No existing endpoint, property or status code is removed or retyped.

**Database**: adds one EF migration (a new table for the stored connection). No data backfill; no existing row is touched.

## Capabilities

### New Capabilities

- `ynab-connection`: how the deployment is authorized to act on a YNAB account — connecting, the credential in use, keeping it valid, losing it, disconnecting, and what is reported about it.
- `privacy-policy`: the privacy policy the app publishes about the data it holds and the external services it exchanges data with.

### Modified Capabilities

None. No existing capability specifies how the app authenticates to YNAB; `envelope-attribution` and `category-restructure` mention YNAB only for mappings, which do not change.

## Impact

- **API**: new Admin-only endpoints under `/api/ynab/oauth/`; additive properties on `YnabConnectionStatusResponse`; regenerated `src/client/src/generated/api.d.ts`.
- **Backend**: `YnabApiClient` stops reading the PAT directly and asks a credential source for the bearer token; a new HTTP client talks to `app.ynab.com` for token exchange, refresh and revocation; `RequireYnabConfigured` and the two status handlers read credential state from the same source.
- **Database**: new table in the `ynab` schema; EF migration; upgrade rehearsal required.
- **Client**: connect/disconnect controls and new states on `/settings/ynab` and `/ynab`; a callback route; a public `/privacy` route.
- **Deployment**: `docker-entrypoint.sh` secrets bridge, `docker-compose.yml`, `.env.example`, and a persisted Data Protection key ring that now protects something an operator would notice losing.
- **Backup**: the new table is excluded from export and ignored on restore.
- **Docs**: a new YNAB connection page, an ADR, a migration note, and updates to deployment, backup and README text that names `YNAB_PAT` as the only option.
- **External**: the owner registers one OAuth application in YNAB's Developer Settings. No YNAB review is needed while only the application's owner authorizes it.
