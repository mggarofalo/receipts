# ChatGPT normalization: personal container deployment

Date: 2026-10-19. Status: selected implementation topology; live account acceptance remains separate.
Tracking: [RECEIPTS-1000](https://plane.wallingford.me/dev/projects/aaac8dc9-bc4c-42db-ac99-eee7864c78e9/issues/65628ef1-9102-47d0-acb9-f0e0bbd601f1).

## Decision

Target the owner's existing remote Docker deployment. Use an isolated, pinned Pi
`ModelRuntime` with its `openai-codex` provider and device-code authentication,
following the user's working Paperless LLM deployment. The user explicitly chose
this precedent and asked us to complete the usable feature, rather than release
prerequisite documents. Receipts keeps its existing login and authorization.

The initial version of this ADR selected a same-machine native API and gated
remote implementation on application-specific approval. That decision is superseded.
It incorrectly treated the direct SIWC registration route as the only possible
integration and did not inspect the owner's existing headless client. No separate
provider approval, registration or licensing change has occurred.

## Distinguish the two integration routes

OpenAI's [headless authentication documentation](https://learn.chatgpt.com/docs/auth#login-on-headless-devices)
documents device-code authentication, persistent credential refresh and Docker
credential storage for Codex clients. The published procedure solves the browser/
container callback problem: the user approves a short code from any browser while
the headless client completes authentication. These documents establish the
mechanism, not OpenAI certification of Pi or unrestricted subscription use by every
third-party application. Actual account/model availability is verified at deployment.

The separate [direct SIWC plan-usage flow](https://developers.openai.com/siwc/token-sharing-open-source)
uses dynamically issued client registrations and has its own availability,
permissions and endpoint contract. Its [self-hosted VM procedure](https://developers.openai.com/siwc/token-sharing-open-source/self-hosted-vms)
describes protected credential transfer for an open-source app. These sources do
not establish Receipts eligibility for a public/commercial offering; neither do
they establish a universal requirement to enroll every personal Docker client.
Do not reuse direct-SIWC token schemas, consent states or endpoint assumptions in
the selected Pi adapter. A future public hosted service needs its own evaluation.

This implementation neither changes Receipts licensing nor uses a separately
billed API key. No implicit model or billing fallback is allowed.

## Ownership and deployment

- Infrastructure launches the pinned Node provider with a minimal environment,
  dedicated credential home, bounded input/output and process-tree cancellation.
  It never creates a general agent session or supplies shell, file or other tools.
- Pi owns OAuth exchange, token persistence, refresh and model transport. Receipts
  owns cross-process exclusive access to that credential home. CLI commands and
  application requests share the same file lock; overlapping access reports busy.
- The private `chatgpt-auth` volume is separate from ordinary application/database
  exports. Do not share or clone another tool's rotating credential session.
  Temporary HOME/work directories contain no inherited personal configuration.
- CLI login shows only the allowlisted OpenAI device URL and short code. React
  receives safe status, model names and fixed error categories, never OAuth tokens.
  There is no public callback listener or token-paste/import endpoint.
- `ChatGpt__OwnerUserId` designates an existing Receipts administrator who may use
  the connection. Host administrators perform CLI login/logout. Changes to the
  connection invalidate earlier operations rather than spending a new account's
  allowance for an old consented request.
- Saved credentials and a cached model catalog are separate from successful model
  access. Status must not describe either as proven entitlement. The synthetic
  probe verifies one selected model's completed response without receipt data.

See [deployment and commands](../chatgpt-normalization.md) and
[provider protocol](../../runner/README.md).

## Normalization obligations

Application validates model proposals and owns their eligibility and reviewed
application. Infrastructure provides persistence and inference capability. The
model cannot create canonical entries, merge, reinstate rejected names, edit
financial facts or bypass a human approval on its own. Preserve the existing
[normalization policy boundaries](../normalization-ownership.md).

Only explicitly selected item text and disclosed candidate context may leave the
application. Prompts and model replies are untrusted. Schema instructions alone
are not provider-enforced structured output: validate IDs, exact field shape,
allowed actions, sizes and target membership before proposals become reviewable.
Only a completed provider response is success; discard incomplete or tool-bearing
responses. Model confidence must never masquerade as an embedding similarity score.

Inference takes place outside database write locks. Durable operation state records
failures, cancellation and connection generations. Final apply rechecks source and
canonical state and commits changes with audit. Acceptance of an old proposal must
not overwrite a later human decision or resurrect soft-deleted data.

Request, payload and time bounds constrain exposure; they do not promise a hard
provider token ceiling, exactly-once charging or reversal of already incurred
usage. Uncertain dispatch is visible and must not cause unbounded automatic retry.
Disconnect prevents new work and removes local credentials; it does not undo
accepted changes or prove remote revocation.

## Acceptance evidence

Offline verification must cover the real process boundary as well as fake provider
contracts: device URL/code validation, terminal message sequencing, output bounds,
environment isolation, competing credential access, cancellation, cleanup and safe
errors. Database tests establish durable claims, stale apply and atomic audit.
Browser/HTTP tests establish selection, disclosure, failed requests and review/apply.
Tests must not report mocked login as a successful live grant.

Hosted acceptance additionally requires the owner to complete a fresh device login,
run a synthetic probe with the chosen model, restart the container, and verify token
refresh and revoked authorization. No personal workstation or Paperless tokens are
copied for testing. Live account approval belongs to the account holder; implementation
and offline verification can finish before that interactive deployment step.

Record the tested revision, package/model versions, synthetic fixture and passed,
failed or blocked checks. Keep credential files and raw provider diagnostics out of
PRs, Plane and ordinary logs. This ADR alone establishes no runtime success.
