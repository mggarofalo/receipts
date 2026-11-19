# The YNAB connection is an OAuth grant held by the server

Date: 2026-11-19. Status: accepted.

## Context

The only way to connect a deployment to YNAB was a personal access token in `YNAB_PAT`. A PAT never expires,
grants full access, and has to be copied out of YNAB by hand into a deployment file, so it crosses the owner's
clipboard and then sits in plain text.

YNAB also offers OAuth. Its documentation leaves the important behaviours unstated, so they were measured against
the real service before deciding; the results are in [YNAB connection](../ynab-connection.md#observed-ynab-behaviour).
Two of them shaped the decision: YNAB requires the client secret even with PKCE, and it keeps the previous tokens
valid after a refresh until the new access token is first used.

## Decision

- **OAuth is the primary way to connect.** An Admin connects and disconnects from the app, using the authorization
  code grant with PKCE. What reaches the owner's workstation is a client secret, which grants nothing without an
  authorization code or a refresh token.
- **The server holds the tokens, encrypted.** They are stored in the database under ASP.NET Data Protection. Stored
  tokens that cannot be decrypted mean "reconnect", never an error.
- **Client credentials come from the deployment's environment**, not from a page in the app.
- **One connection per deployment.** The app is single-owner; there is no connection per user.
- **Refresh saves before it uses.** The new tokens are committed before any request is sent with them, so a crash
  between the two leaves a stored pair YNAB still accepts.
- **`YNAB_PAT` stays as a fallback** when no OAuth connection exists. It keeps local development simple.
- **A rejected connection does not fall back to the PAT.** A refused refresh token is the only signal YNAB gives
  that a token may have been used elsewhere, so the app stays disconnected until an Admin reconnects or
  disconnects.
- **Tokens are never in the portable backup**, and a restore does not touch the connection.
- **Disconnect always removes the local tokens**, whatever YNAB answers. YNAB's revocation endpoint is undocumented
  and may change.

## Consequences

- The Data Protection key ring under `/data/DataProtection-Keys` now protects something an operator would notice
  losing: without it the deployment must be reconnected.
- The owner registers one OAuth application with YNAB. No YNAB review is needed while only the application's owner
  authorizes it; a hosted, multi-user service would need one.
- YNAB requires OAuth applications to publish a privacy policy, so the app has a public `/privacy` page.
- A deployment left idle long enough for its refresh token to lapse comes back needing one click to reconnect. If
  that proves common, a periodic refresh can be added without changing what is stored.

## Not chosen

- **A page for pasting a PAT into the app.** It removes the environment variable but keeps the full-access token
  on the clipboard, which is the exposure this decision avoids.
- **A public client with PKCE and no secret.** YNAB refuses it.
- **YNAB's dynamic client registration.** YNAB reserves it for its MCP clients.
