# ChatGPT normalization in a personal Docker deployment

Receipts uses a dedicated Pi provider runtime with ChatGPT device-code sign-in,
following the same headless-client pattern as Paperless LLM. Receipts login stays
unchanged. This connection supplies model suggestions; it does not give the model
receipt editing tools or make it an autonomous agent.

This is different from registering Receipts as a public Sign in with ChatGPT
website. OpenAI documents headless device authentication and Docker credential
storage for Codex clients. Those documents establish the deployment mechanism;
they do not certify Pi or guarantee an account's model access. This implementation
supports a personal client in a self-hosted container. Public website registration
and commercial hosted-service eligibility are separate integration questions.
[Official headless authentication](https://learn.chatgpt.com/docs/auth#login-on-headless-devices).

## Configure the owner and private storage

Set these environment values on the app service, then recreate the container:

```yaml
environment:
  - ChatGpt__Enabled=true
  - ChatGpt__OwnerUserId=YOUR-EXISTING-RECEIPTS-ADMIN-USER-ID
volumes:
  - chatgpt-auth:/data/chatgpt
```

The owner is an existing Receipts administrator's user ID, not their email or
ChatGPT account ID. Only that owner may use the connection through Receipts.
The supplied Compose file includes the separate named credential volume; keep it
private and exclude it from ordinary database/export backups. Do not mount your
Paperless or desktop Codex credential directory here. Each installation owns its
own rotating session. `docker compose down -v` deletes credentials and application
data; it is not an upgrade command.

Optional settings are `ChatGptRunner__HomeDirectory` (default `/data/chatgpt`),
`ChatGptRunner__BridgePath` (default `/app/runner/bridge.mjs`),
`ChatGptRunner__NodeExecutable` (default `node`) and
`ChatGptRunner__TimeoutSeconds` (default 180). These are deployment settings, never
caller-supplied paths or executable names.

`ChatGpt__PendingRunLimit` defaults to 10 (bounded to 1–100 outstanding runs per
owner). Queued, running, ready and unknown runs count toward this limit. A limit
conflict means finish or discard earlier runs before starting another request.
`ChatGpt__UnacceptedRetentionDays` defaults to 30 (bounded to 1–365 days). Expiry
removes unaccepted proposal payloads and releases their reservations, retaining a
minimal request/idempotency record and applied provenance. An expired request needs
a fresh selection and request ID; it is never automatically retried.

Expiry also removes discarded proposals from partially accepted runs; accepted
proposal provenance remains. These are local admission/retention controls, not a
guarantee about provider billing or usage.

## Sign in on the Docker host

With the deployment database running, start the one-off setup command:

```sh
docker compose run --rm --no-deps app --chatgpt-login
```

Open the printed OpenAI device-approval URL on any computer or phone and approve
the code. Keep the command running until it completes. Enable device-code login
in ChatGPT security settings if the account requires it. No published callback
port, reverse-proxy route, localhost helper or SSH tunnel is needed.

The one-off container uses the normal entrypoint to load the deployment secrets,
then runs the command without starting migrations or the web server. It mounts the
same private volume as the application. Do not substitute `docker compose exec`
with a bare `dotnet API.dll`: that process does not inherit secrets exported only
to the running API process.

The provider stores and refreshes its OAuth credentials in the private volume.
Receipts serializes access with a cross-process file lock; an overlapping command
reports busy rather than racing rotating tokens. Do not run a second copy with a
cloned credential volume. Login/logout should be performed while normalization
work is idle. Account changes invalidate old pending work through the application's
connection-generation checks.

```sh
docker compose run --rm --no-deps app --chatgpt-status
docker compose run --rm --no-deps app --chatgpt-probe YOUR-SELECTED-MODEL
```

Status indicates saved credentials and lists cached model IDs, not model entitlement.
Pass the selected model ID to the probe. The probe is an
explicit synthetic inference request and consumes account usage; it must complete
with valid output before treating the connection as usable. A cached model catalog
also does not prove access. There is no silent fallback to another model or a
separately billed API key.

## Review, failure and recovery

Select the intended unresolved item descriptions and review the payload disclosure
before requesting suggestions. The request sends only the disclosed text and
candidate context. Model output remains untrusted: unknown IDs, invalid fields,
incomplete responses and tool calls cannot become accepted suggestions. Applying
a suggestion rechecks current item/canonical state and commits changes with audit.

In **Normalized Descriptions → ChatGPT**, choose up to 50 unresolved or pending-review
items and a model from the provider's catalog. Receipt date and store help you select
items locally; the provider receives only item identifiers, original descriptions,
and candidate names/identifiers. Separate price, quantity, date and store fields are
excluded; the selected descriptions themselves are sent as displayed.
The run URL and recent-run list let you leave and return while a request is running.

Select the suggestions you accept, confirm that you reviewed them, and apply once.
All selected changes commit together or none do. Unselected suggestions are discarded;
their receipt items remain unchanged and can be selected in another run. Creating a
reviewed name creates a canonical name, like the existing manual split workflow.
Reusing a name only links to a supplied active candidate. Neither action renames an
existing canonical or activates a rejected/pending name as a side effect.

Changing an item or target after inference invalidates the affected proposal, even
if its visible values were subsequently changed back. Cancel the stale run and select
the current items. An uncertain run can also be cancelled without requesting another
inference. Changing the connection or importing a backup invalidates outstanding work.

### Responsibility boundaries

Application owns payload minimization, strict model-output validation, source/target
eligibility and review-state decisions. Infrastructure loads efficient projections,
captures immutable observations, leases durable runs, calls the provider and applies
approved changes under database locks. Database triggers issue fresh revision IDs for
item and canonical updates, including SQL updates outside EF tracking.

The API adapts authenticated transport and returns problem documents for reasoned
rejections. React owns selection, disclosure, confirmation and resumable presentation;
the server rechecks ownership and validity. The isolated provider process owns OAuth
storage/refresh and text inference; it has no receipt tools or application secrets.

The credential lock serializes SDK operations. A separate operator lock spans the
database fence, login/logout and completion fence. If that process crashes, the
database remains in a changing state and refuses dispatch until another explicit
login/logout completes recovery. Do not bypass these commands to replace credentials.

Cancellation stops the local request; it cannot undo provider usage already
incurred. Interrupted dispatches must remain visible as uncertain failures rather
than being retried without limit. Missing/revoked authorization requires a new
login. Retry transient transport failures through the visible operation controls.

To remove this deployment's saved connection:

```sh
docker compose run --rm --no-deps app --chatgpt-logout
```

Local logout is not a promise that remote access has been revoked; use ChatGPT
account controls for remote revocation. Accepted receipt changes remain in Receipts.
Never paste credential files, raw provider error bodies or receipt payloads into
issues. Fixed error categories can be reported safely.

Before hosted acceptance, verify a fresh device grant, the synthetic probe,
container restart, credential refresh, cancellation and revoked authorization.
Offline tests establish application and process-boundary behavior; they do not
establish live grant/refresh or account entitlement.

For optional model-quality acceptance, the
[synthetic evaluation guide](normalization-evaluation/README.md)
describes `--chatgpt-evaluate MODEL example|holdout REPORT_PATH`. It sends only the
embedded synthetic corpus, with at most eight example or 24 holdout requests, and
consumes plan allowance. Run examples first; preserve the untouched holdout for
independent assessment. A schema-valid probe alone does not establish label quality.

For local development, install the pinned runner dependencies as described in
[runner/README.md](../runner/README.md), configure absolute bridge and private home
paths, and invoke the same CLI switches through the API project. Keep credential
storage outside the repository.
