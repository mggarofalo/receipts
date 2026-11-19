# YNAB connection

How a deployment is authorized to act on a YNAB account. The behaviour contract is the
[ynab-connection](../openspec/specs/ynab-connection/spec.md) spec; the reasons behind it are in
[the decision record](decisions/2026-11-19-ynab-oauth-connection.md).

There are two ways to connect. OAuth is the recommended one.

| | OAuth | Personal access token |
|---|---|---|
| Set up by | An Admin clicking **Connect** on `/settings/ynab` | `YNAB_PAT` in the environment |
| Credential lifetime | Access token: 2 hours, renewed automatically | Never expires |
| Stored | Encrypted in the database | In the deployment's environment |
| Revoked by | **Disconnect** in the app, or in YNAB | Deleting the token in YNAB |

When both exist, the OAuth connection is used. The connection status names the credential in use.

There is one connection for the whole deployment. Every signed-in user's YNAB features use it; only an Admin can
connect or disconnect.

## Setting up OAuth

### 1. Register an OAuth application with YNAB

In YNAB, open **Account Settings → Developer Settings** and create a new OAuth application.

- **Name.** YNAB does not allow "YNAB" or "You Need A Budget" in the name unless preceded by "for". "Receipts for
  YNAB" is fine.
- **Redirect URI.** The public address of this deployment followed by `/settings/ynab/callback`, for example
  `https://receipts.example.com/settings/ynab/callback`. It must match the `YNAB_OAUTH_REDIRECT_URI` setting exactly.
  One application can list several redirect URIs, so a development instance can share it.
- **Privacy policy URL.** The deployment's `/privacy` page.
- Leave **default plan selection** off. The app has its own budget selection.

YNAB shows the application's **Client ID** and **Client Secret** once it is saved.

No YNAB review is needed. A new application is in "Restricted Mode", which caps how many *other* YNAB users may
authorize it; the application's owner is not counted.

### 2. Give the deployment the client settings

| Setting | Value |
|---|---|
| `YNAB_OAUTH_CLIENT_ID` | The Client ID. Not a secret. |
| `YNAB_OAUTH_CLIENT_SECRET` | The Client Secret. |
| `YNAB_OAUTH_REDIRECT_URI` | The redirect URI registered above. |

All three are needed; with any missing, the app does not offer **Connect**.

The secret can also be supplied as a file, `/secrets/ynab_oauth_client_secret`, in the secrets volume. The
entrypoint exports it as `YNAB_OAUTH_CLIENT_SECRET` when the variable is not already set. The file is optional.

The client secret alone grants nothing: it only works together with an authorization code or a refresh token, and
the refresh token never leaves the server.

### 3. Connect

Restart the app, sign in as an Admin, open `/settings/ynab` and choose **Connect**. YNAB asks you to approve
access and returns you to the app. Then pick the budget as before.

`YNAB_PAT` can be removed afterwards. Leaving it set is harmless: it is ignored while an OAuth connection exists.

## What the app does with the connection

- **Renewal.** A YNAB access token lasts two hours. The app renews it just before a request needs it, saves the
  new tokens, and only then uses them. Nothing renews while the app is idle.
- **Storage.** Tokens are encrypted with ASP.NET Data Protection before they reach the database. The keys live in
  `/data/DataProtection-Keys` on the `app-data` volume, so a database dump alone does not contain usable tokens.
- **Secrecy.** No API response or log entry contains a token, an authorization code or the client secret.
- **The authorization code.** YNAB returns the browser to `/settings/ynab/callback` with a one-time code in the
  address. The app removes it from the address before anything else runs, keeps it out of saved routes and error
  reporting, and exchanges it once. A reverse proxy in front of the app still sees that one request and may log
  it. The code cannot be redeemed without the client secret and a verifier that never leaves the server.
- **Backup.** The connection is never part of the [portable backup](backup-restore.md), and restoring a backup
  leaves the current connection in place.

## Reconnect required

The app shows **Reconnect required** when it can no longer use the stored connection:

- YNAB refused the refresh token. This happens when access was removed in YNAB, when the token expired after a
  long idle period, or when someone else used the refresh token first.
- The stored tokens cannot be decrypted, for example after moving the database to a host with a different
  `/data/DataProtection-Keys`.

Until an Admin reconnects, YNAB features answer as unavailable. The app deliberately does **not** fall back to
`YNAB_PAT` in this state: a refused refresh token is the only sign YNAB gives that a token may have been used
elsewhere, and a silent fallback would hide it. Choosing **Disconnect** clears the state and restores the
fallback.

A refused refresh token is logged as a warning with event id `YnabOAuthConnectionRejected`.

## Disconnect

**Disconnect** asks YNAB to revoke the grant, then removes the stored tokens. The tokens are removed even if YNAB
cannot be reached; the app then says the remote step failed, and the application's access should be removed by hand in YNAB's account
settings.

## Development

`YNAB_PAT` is the simplest way to run YNAB features locally: set it in the API's user secrets or environment.

OAuth works locally too. YNAB accepts an `http://localhost` redirect URI, but it must be registered with a fixed
port, and Aspire assigns Vite a dynamic one. Pin the port, add `http://localhost:<port>/settings/ynab/callback` to
the OAuth application, and set the three settings for the API. Connecting or disconnecting a development
instance does not affect another instance using the same application.

## Observed YNAB behaviour

YNAB's documentation leaves several things unstated. These were measured against the real service on
2026-11-19.

| Behaviour | Result |
|---|---|
| Code exchange, refresh or revoke without the client secret | Refused, even with PKCE |
| Access token lifetime | 7200 seconds |
| Refresh token | Replaced on every refresh |
| Previous tokens after a refresh | Valid until the new access token is first used, then both stop working |
| Re-using a retired refresh token | Refused; the current tokens keep working |
| PKCE | `S256` only; a wrong or missing verifier is refused |
| A failed code exchange | Does not consume the code |
| Re-using an authorization code | Refused; tokens from the first exchange keep working |
| `POST /oauth/revoke` (not in YNAB's documentation) | Needs the client secret; revoking either token ends the grant; answers 200 for an unknown token too |
| Two authorizations of one application | Independent of each other |
| `http://localhost` redirect URI | Accepted |
| Rate-limit response headers | None; the app counts requests itself |
| Dynamic client registration | Reserved for YNAB's MCP clients |

Not measured: how long an authorization code or a refresh token lasts.
