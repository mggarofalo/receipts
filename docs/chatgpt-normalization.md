# Build a useful catalog of purchased items

Receipt descriptions are written for a store's till, not for finding purchases
later. The same product may appear as `BF FRANKS 8CT` on one receipt and
`BEEF FRANKS 8 COUNT` on another. Searching for the words you remember can miss
those purchases, and grouping by the original text can split one product's
history into several unrelated entries.

Normalization connects those receipt descriptions to a shared, readable item
identity. The original receipt text, quantities and prices remain unchanged.
When two descriptions mean the same product, their purchases should be findable
together; genuinely different products must remain distinguishable. A shared
name alone does not make different package sizes or units directly comparable.

## The intended result

The product goal is a complete, maintained catalog, with two connected workflows:

1. **Organize the existing history.** Work through distinct descriptions, reuse
   or create the appropriate normalized identities, and apply reviewed decisions
   to the relevant historical purchases. Show what remains unresolved or needs
   review, including existing automatic assignments that have never been reviewed.
2. **Keep new receipts organized.** Reuse established description mappings when
   new purchases arrive. Surface genuinely new or ambiguous descriptions for
   normalization, and remember accepted decisions so the same work is not
   repeatedly requested.

Search and reports should consume those same identities. For example, searching
for a normalized product name should find its differently abbreviated purchases,
and its price history should retain the date, store, quantity, price and receipt
link for each purchase. Items whose meaning cannot be determined need a visible
unresolved state rather than an invented match.

ChatGPT's role is to help interpret unfamiliar abbreviations and propose suitable
identities. Its value is reducing the work of building and maintaining that
catalog. Connecting a model and producing suggestions are supporting capabilities,
not the definition of a complete normalization feature.

## Catalog workflow in this PR

The catalog extension in this PR is tracked under RECEIPTS-1020.
The workflow below describes that extension; it is not a claim
that your database has already been normalized or that live model quality has
been demonstrated.

**Normalized Descriptions → Catalog** separates five decisions:

1. **Find work.** Browse distinct descriptions, scoped to a store and item code.
   Counts distinguish unassigned purchases, automatic assignments, unknown legacy
   assignments, protected decisions, approved mappings, and exclusions.
2. **Find possible identities.** Retrieve a bounded shortlist from active catalog
   names using lexical and semantic evidence and previously human-approved source
   examples. Retrieval supplies possibilities; it does not establish equivalence.
3. **Interpret the description.** Optionally ask ChatGPT to choose a supplied
   identity, propose a new name, or abstain. You can also review a source manually
   without either ChatGPT or the embedding model.
4. **Approve a precise scope.** Preview the current historical purchases for the
   exact source key, then approve. Including unknown legacy assignments is an
   explicit choice. Manual and template decisions remain protected.
5. **Remember and maintain.** Apply history in durable, bounded batches and reuse
   the approved exact mapping for new purchases. Review unfamiliar descriptions
   through the same process. Revoke a bad mapping to stop reuse; undo restores
   only historical assignments still owned by that operation.

For example, you can review `BF FRANKS 8CT` at one store and approve an
appropriate existing product identity. The preview shows which historical
purchases will change. Subsequent purchases with that exact store, description,
and item-code key reuse the approved mapping without another model request.
An identical abbreviation at another store, or a different item code, does not
inherit that approval. `TURKEY FRANKS 8CT` is a separate decision.

Source keys remove outer ASCII spaces only; tabs, nonbreaking spaces, letter
case, and other characters remain significant. This conservative rule matches
database grouping and lookup, including legacy data. If the suggested shortlist
misses the correct existing name, search for it within source review before
proposing a duplicate. That lookup does not change the source key being approved.

Approval changes the normalized identity used by search and reports. It does
not replace the receipt description, recalculate financial facts, infer a
missing package size, or make unit prices comparable across different units.
Search for a familiar product name to see the linked purchases and their original
price details.

Purchase search matches text in the original description and active canonical
name/display label. Choose readable names containing the terms you expect to
search for. Semantic retrieval helps construct normalization suggestions; the
purchase-search box does not itself ask the model to interpret synonyms.

### Make existing names readable without changing product identity

A product can already be grouped correctly but still be named `BF FRANKS 8CT`.
Reusing that identity is correct; creating a duplicate product merely to call it
`Beef franks, 8 count` is not. The catalog therefore reviews two independent
choices: which product a source means, and what readable display name that
product should have.

A reuse suggestion can include a separate readable name. **Review global display
name** shows the current name, proposed name, and count of purchases using the
product. Approval changes its display label across search and reports, preserving
its ID, canonical matching name, receipt text and financial facts. It does not
approve a source mapping. If the target or affected count changes, preview again.
Preserve known brand, size, flavor and dietary distinctions; do not approve
attributes inferred from an ambiguous abbreviation.

For products already mapped or protected, select **Improve display names only**.
This opens All sources so existing assignments remain accessible. Select source
descriptions and request suggestions. Each source must resolve to exactly one
existing active product; ambiguous groups require manual identity review first.
The model can suggest a label for that identity or abstain, but cannot reassign
purchases or create products in this mode. The registry also supports manual
name editing. Reviewed coverage and readable naming are separate measures:
a mapped purchase does not prove its display name is useful, and an existing
display label does not prove a human has verified its meaning.

An existing assignment may point to a **Pending review** identity. Such an identity
is not eligible for model matching or display-name suggestions yet. Catalog source
rows show their linked identities and statuses; use the pending identity's review
link to inspect it, then explicitly approve or merge it using the existing review
controls. The review queue also supports search. Return to the source after review
to refresh its eligibility. If a proposed new name already belongs to a pending
identity, the rejection provides the same review route. Navigation and model
suggestions never approve identities automatically.

To approve many pending names at once, use **Promote Consistent Pending Names** on
the Maintenance tab. It previews every pending name that has at least two purchases,
none of whose source keys is linked to another name, and that no other name spells
the same way. One confirmation makes them active. Purchases keep their links and
review state, and no mapping is created. Undo returns each name to review unless it
changed afterwards. See
[the decision](decisions/2026-10-31-promote-consistent-pending-names.md).

### What the model can and cannot see

The model does not browse the database or search repeatedly on its own. The
server retrieves and snapshots a bounded candidate set first. Candidates include
stable IDs, names, and a small number of approved description examples. Example
scope is distinguished so evidence from another store or item code is not
presented as an exact source match. Automatically assigned examples do not
quietly become trusted demonstrations.

The model can choose only a supplied candidate ID, propose a new name for review,
or abstain. Server validation rejects unknown IDs and malformed responses.
Neither a similarity score, a confident explanation, nor valid JSON establishes
that two products are the same. The human review step is the semantic safeguard.
Approving a suggestion creates a fresh historical preview rather than treating
an old inference snapshot as permission to change whatever now matches.

There are two independent ways to fail: retrieval can omit the right identity,
and the model can choose incorrectly even when it is present. Evaluation must
measure these separately, including missing candidates, conflicting variants,
ambiguous abbreviations, candidate-order changes, and misleading examples.
Adding more examples is not automatically better.

The [October 20 diagnostic evaluation](normalization-evaluation/2026-10-20-agent-harness.md)
records actual blind model calls using synthetic cases and a small, independently
labeled sample from a real backup. It separates identity agreement, readable-name
review, and consolidation. These agent-harness results do not validate production
Pi authentication, retrieval recall, or the correctness of every historical
assignment. Two equivalent existing identities can each receive a valid match
and a readable name while their histories remain separate; use explicit reviewed
merge controls to consolidate them.

### Why keep review and provenance?

The expensive mistake is a wrong equivalence spreading to future receipts.
Scoped mappings limit that spread. Explicit provenance makes it possible to
distinguish a machine assignment from a deliberate human or template decision.
Revision checks protect changes made while a preview is open. Revocation and
conditional undo provide a correction path without overwriting later edits.
Paused operations expose failures and committed progress rather than silently
restarting an entire historical update.

Catalog mappings and assignment provenance are persisted product data and are
included in backup format 7. Restoring a backup preserves that evidence and the
operation history, but does not resume or undo old operations against newly
restored purchase revisions. Make a fresh preview for further corrections.
Older backups have no provenance evidence, so imported links are labeled unknown
legacy rather than assumed to be reviewed.

Existing links migrate as **unknown legacy**, with their targets preserved.
That label means their origin is unknown, not that they are wrong. A complete
catalog therefore requires reviewing the remaining unknown and automatic work;
an empty model queue is not evidence of completeness.

The [design and research decision](decisions/2026-10-20-normalization-catalog.md)
compares Jev, Clef, and Luna Decisions. No additional provider is enabled by this
change. A specialist model should be selected only after a receipt-specific
comparison, with its account, privacy, and deployment requirements understood.

## Retired per-purchase workflow

Before RECEIPTS-1104, **Normalized Descriptions → ChatGPT** also let you select up
to 50 individual receipt items and ask for suggestions for them. That workflow is
retired. Catalog is the one supported way to ask ChatGPT for suggestions: it works
on source groups, retrieves lexical, semantic and approved-example candidates, and
saves reusable source mappings that apply across history.

What remains for runs saved by the old workflow:

- **Readable.** The ChatGPT tab still lists recent runs and opens any of them,
  so past suggestions and outcomes stay visible.
- **Cancel and discard.** A queued, failed or uncertain run can be cancelled. A
  ready run's suggestions can be discarded, which frees its items for Catalog
  requests and releases its share of the pending-run limit.
- **Not applied, retried or dispatched.** The apply and retry endpoints answer
  `409` with the problem `code` `workflow_retired`. The worker fails a still-queued
  per-purchase run with the same code instead of sending it to the provider.

Purchase search matches active normalized names and display labels as well as raw
receipt descriptions, so an approved name is useful immediately for the purchases
linked to it. See [Purchase search](purchase-search.md) for the current search and
price-history contract. Neither workflow automatically sends new unfamiliar receipt
text to the provider; requesting model suggestions remains an explicit action.

The suggestion instructions ask the model to preserve product distinctions such as
brand, package size and dietary variant. Review must verify those distinctions;
structural validation cannot prove that two names mean the same product. A search
for "hot dogs" can find several normalized products while their individual price
histories remain distinct.

The remainder of this guide describes connection setup and how Catalog requests
behave. Connection status is not evidence of model quality or catalog completeness.

## Connection and deployment

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

## Sign in through Receipts

As the configured administrator and connection owner, open the admin Catalog's
ChatGPT tab and select **Sign in with ChatGPT**. Confirm account ownership, open
the OpenAI link and enter the displayed code. Receipts polls for completion; the
displayed deadline comes from the runner and does not establish model access.
After connecting, select a model below or follow **Set up post-save ChatGPT
suggestions** to configure optional assistance. Signing in does not enable it.

Cancel stops the attempt; credentials may already have been saved. Use the
separate **Disconnect ChatGPT** action to remove them. Reconnect starts a fresh
sign-in with explicit consent. Codes and links are transient and are not stored
in browser preferences or portable backups.

Credentials now require an owner-consent binding tied to the database connection
generation. Existing unbound credentials, an owner change or a restored database
require fresh sign-in before inference. Deploy the updated API and runner together
and stop old runner processes before restarting; recovery never steals a lock
from an active or unverifiable process. A busy state can require stopping an old
process before retrying. Ordinary status/model discovery does not grant consent.

## Sign in on the Docker host

With the deployment database running, start the one-off setup command:

```sh
docker compose run --rm --no-deps app --chatgpt-login --owner YOUR-EXISTING-RECEIPTS-ADMIN-USER-ID --consent
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

Choose the source groups in **Catalog** and review the payload disclosure before
requesting suggestions. The request sends only the disclosed text and candidate
context. Model output remains untrusted: unknown IDs, invalid fields, incomplete
responses and tool calls cannot become accepted suggestions. Approving a suggestion
goes through catalog preview, which rechecks current source and identity state and
commits changes with audit. The run URL and recent-run list let you leave and return
while a request is running.

Changing a source after inference invalidates the affected suggestion, even if its
visible values were subsequently changed back. Cancel the stale run and select the
current source group. An uncertain run can also be cancelled without requesting
another inference. Changing the connection or importing a backup invalidates
outstanding work.

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

## Maintaining new purchases after saving

The catalog's **Saved purchases needing review** inbox groups outstanding purchases
by the exact trimmed store, description and item code. It shows purchase counts,
receipt links, first-seen dates and assistance status. Saving a valid receipt does
not depend on ChatGPT being connected. Manual source review works independently of
the optional connection. A queued, completed or failed suggestion is never evidence
that a purchase has been verified.

The configured connection owner can explicitly enable post-save suggestions,
choose a model and set a daily limit (1–100 new source requests per UTC day).
This sends saved source descriptions and catalog candidate evidence through the
existing ChatGPT runner. It does not send keystrokes, approve identities, create
products, merge products or apply a mapping. Review suggestions before applying
them. The daily limit bounds source requests, not token consumption or billing.
Repeated purchases of an already attempted exact source do not trigger another
automatic inference request.

Receipt saves register outstanding work in the same database transaction. A
30-second database reconciliation repairs missing work markers and dirty direct-SQL
observations, retaining each purchase's revision. It deliberately does not enroll
untouched historical purchases; historical cleanup remains an explicit catalog
operation. Restarts and missed in-process notifications cannot lose registered work.

Admission persists a request ID before external connection/model checks. The short
guarded write transaction verifies owner consent and source revisions, reserves
the source and binds the attempt to its durable AI run atomically. The AI worker
checks source revisions again before dispatch. Admission failures back off from
30 seconds, with at most five attempts before pausing. Authentication failures
require intervention. Once a run exists, the inbox never automatically retries
inference; unknown outcomes may have consumed allowance. Use the existing explicit
retry/cancel controls. Explicit retries keep the inbox linked to the new run.

Changing assistance settings cancels outstanding automatic admission and queued or
running automatic runs. Completed suggestions remain reviewable. Connection changes,
backup restoration or a deployment owner change disable consent; an owner must
enable it again. These deployment-local preferences and attempt records are not
part of portable receipt backups. The durable run's existing privacy-retention
policy continues to govern inference payloads and candidate evidence.

Ownership: `ReceiptNormalizationPolicy` defines trust; the atomic receipt writer
records purchase evidence and outstanding work; `NormalizationReviewInboxService`
projects/reconciles pending work and manages opt-in admission; `NormalizationAiService`
owns provider runs, reservations, cancellation, retry and explicit application.
React renders those states and collects choices. An exact source with multiple
recorded identities cannot silently reuse an unconditional mapping; confirm an
individual purchase or resolve the source history first.
