# ChatGPT normalization: eligibility and deployment topology

Date: 2026-10-19. Status: proposed deployment decision; provider activation blocked.
Owner: Receipts product owner and integration maintainer.
Tracking: [RECEIPTS-1000](https://plane.wallingford.me/dev/projects/aaac8dc9-bc4c-42db-ac99-eee7864c78e9/issues/65628ef1-9102-47d0-acb9-f0e0bbd601f1).

## Decision

Select **personal, same-machine operation** as the first documented candidate:
the administrator's browser and a native Receipts API process run on one computer.
Connect an AI account to an existing Receipts administrator; retain Receipts login
and authorization. This is a design selection, not a claim that this installation
or account has successfully enrolled. No integration is implemented or enabled by
this ADR.

OpenAI explicitly includes personal projects running locally in its current
availability statement. Therefore the repository's all-rights-reserved notice is
not, by itself, a reason to reject this narrow topology. Eligible Plus and Pro
users may grant plan usage separately from identity. Neither grant provides
ChatGPT conversation access. [Official availability explanation](https://developers.openai.com/cookbook/articles/sign-in-with-chatgpt).

The actual deployment target remains an owner decision in RECEIPTS-1001. If the
required target is the remote Docker installation described in
[deployment.md](../deployment.md), this local candidate does not satisfy that
requirement. Keep provider implementation gated until the remote eligibility
evidence below exists. Do not quietly substitute a desktop product for a remote
deployment requirement.

## Capability and eligibility matrix

All source observations below were checked on 2026-10-19. Recheck before enrollment
and again before rollout: this is a preview, not a permanent entitlement guarantee.

| Deployment | Documented route | Receipts disposition and missing evidence |
| --- | --- | --- |
| Personal project; browser and native API on the same computer | Local plan-usage flow, with dynamic registration and separate usage consent | Candidate selected. Owner must accept local-only scope; eligible account, workspace and granted usage still require an authorized verification run. |
| Personal remote Docker/Pi/VM; browser elsewhere; current all-rights-reserved repository | The VM transfer guide explicitly covers an open-source app; the overview directs remotely hosted apps to express interest | Unresolved for this private remote case. Need current official documentation explicitly covering it or written OpenAI approval identifying this application, topology and plan-usage scope. “Self-hosted” alone is insufficient evidence. |
| Open-source remote app | Local authorization followed by protected credential transfer is documented | Conditional comparison only. Receipts has no recorded open-source license decision; do not relicense it to make this row apply. |
| Private/commercial hosted website | Selected partner website OAuth integration, with provisioned client and registered callbacks | Not enrolled. Need provider approval for the exact deployment and separately for plan inference; identity-only approval is insufficient. |
| OpenAI API key | Separate API integration and payer decision | Outside this selection; RECEIPTS-1018 is optional. Never silently fall back to API billing. |

The [plan-usage overview](https://developers.openai.com/siwc/token-sharing-open-source)
describes local/OSS availability and the remote interest route. The
[VM guide](https://developers.openai.com/siwc/token-sharing-open-source/self-hosted-vms)
does not establish private remote eligibility. The
[website guide](https://developers.openai.com/siwc/website) requires a supplied
client ID, exact callbacks and registered token authentication method, and covers
identity separately from plan usage. No form submission, provider approval,
registration or live account verification occurred for this ADR.

## Callback and runtime ownership

For the selected candidate, a narrowly scoped local authorization component in
the native backend owns the temporary loopback listener, OAuth transaction,
credential protection and refresh. A separate helper executable is unnecessary
for this topology. Its packaging and supported OS are an implementation decision
for RECEIPTS-1004/1005, subject to the owner choosing this topology first.

The SPA only starts a connection through an authenticated administrator action
and reads safe connection status. The local backend binds each attempt to that
administrator and installation, expires it, and consumes it once. No arbitrary
URL, caller-selected credential path or general-purpose token import endpoint is
introduced. Bind the callback listener only to loopback; validate the intended
host/path and transaction, and close it after completion or cancellation. An
unrelated website or local process must not be able to replace the active account.

The documented direct flow starts registration with `dynamic_agent_client` and
then exchanges the code with the issued client ID. It uses fresh state, nonce
and PKCE, plus a stable host ID. The implementation must validate identity and
permissions before replacing a connection. These are requirements for a future
implementation, not steps executed here.
[Registration contract](https://developers.openai.com/siwc/token-sharing-open-source/sign-in).

A remote topology using the dynamic-registration/VM route would require a local
helper because browser loopback cannot reach the remote host. After eligibility is established, the documented VM
option transfers only the chosen registration over a protected channel, retains
the VM's distinct host ID, and gives the VM subsequent refresh ownership.
Transferred sessions currently lack host-specific usage attribution/revocation.
That operational limitation must be accepted before choosing transfer. A website
client would avoid the local helper but requires its own approved callback and
client contract; it is not an interchangeable fallback for dynamic registration.
[VM transfer limitations](https://developers.openai.com/siwc/token-sharing-open-source/self-hosted-vms).

## Security and normalization boundaries

These are Receipts design constraints, not assertions of existing implementation:

- Persist provider secrets only in backend-owned protected storage. Exclude them
  from browser storage, API responses, logs, telemetry, ordinary exports and
  backups. Scope each connection to its Receipts owner and verified provider
  identity/workspace. A host identifier is not an authentication secret.
- Serialize refresh and atomically replace credentials. Disconnect first stops
  new work, then attempts provider revocation and clears local secrets. Report an
  unconfirmed remote revocation honestly. Account switches cannot redirect work
  already consented for another connection.
- Show separate states for disconnected, identity verified, plan permission
  granted, usable model available, and paused/unavailable. Identity success or a
  paid subscription label alone cannot enable inference.
- Begin with explicit review of suggestions. The owner must approve connection
  scope, payload, retention, usage limits and review policy in RECEIPTS-1001.
  Send only required item description context and opaque correlation IDs, not
  receipt documents, payment/account fields or a full purchase history. Treat
  item text and model output as untrusted data; request no tools.
- Preserve [normalization ownership](../normalization-ownership.md): Application
  owns deterministic policy; Infrastructure supplies provider facts. A model
  suggestion cannot bypass rejection tombstones, templates, human decisions or
  final stale-write checks. Inference runs outside database write locks. Durable
  operations and apply/audit behavior belong to RECEIPTS-1007 through 1009.

The supported inference route uses the selected account's current model catalog
and the public Responses endpoint with streaming enabled and storage disabled.
Only a completed terminal result can become a suggestion; incomplete streams
cannot be treated as success.
[Models and inference](https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference).
The preview excludes fields including `max_output_tokens` and `background`.
Consequently this design promises neither a provider-enforced token ceiling nor
background/Batch execution. Local cancellation and bounded scheduling reduce
exposure but cannot reverse incurred usage or guarantee exactly-once charging.
[Preview constraints](https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations).

## Gates and evidence owners

| Gate | Owner | Required recorded evidence | Current state |
| --- | --- | --- | --- |
| Intended deployment and account ownership | Product owner, RECEIPTS-1001 | Local-only acceptance, or a specific remote target and who may spend whose allowance | Open |
| Remote/private eligibility, if needed | Product owner with OpenAI | Dated official coverage or application-specific approval for hosting and plan usage | Missing; remote path blocked |
| Local registration implementation | Integration maintainer, RECEIPTS-1004/1005 | Reviewed callback, identity/workspace validation, protected credentials and recovery tests | Not implemented |
| Actual account/model capability | Account owner and maintainer, RECEIPTS-1006/1014 | Authorized synthetic run; validated usage permission, current model and completed response | Not attempted |
| Schema, policy and quality | Normalization owner, RECEIPTS-1002/1013/1014 | Strict-output route/model compatibility, output validation and independently reviewed evaluation results | Not verified |
| Activation and rollout | Product owner, RECEIPTS-1016 | Earlier gates passed, operational controls and explicit rollout decision | Disabled |

Completing a documentation PR must not mark these runtime gates satisfied or
automatically unblock provider implementation. Independent synthetic corpus work
(RECEIPTS-1013) can proceed without a provider connection.

## Future synthetic verification script

This is a procedural test script for the implementation tickets, **not an
executable tool or a completed test run**. First implement offline provider fakes
and a disposable Receipts instance with no real receipts. Run the negative cases
without contacting OpenAI. Only after the owner authorizes registration and a
small, explicit plan-usage run may the positive provider steps execute. Never use
private Codex tokens or ChatGPT `backend-api` endpoints.

| Step | Action | Expected observable result |
| --- | --- | --- |
| 1. Eligibility preflight | Record ADR date, approved topology, owner, account/workspace selection and authorized usage budget | Missing evidence disables connect/run; remote target cannot pass as local merely because its URL is localhost through a tunnel |
| 2. Callback negatives | Simulate wrong/expired/replayed state, nonce, issuer, audience, callback host/path; non-admin and another browser session | Connection unchanged; no credential leak or inference request; one safe failure event |
| 3. Local positive enrollment | On the approved machine, start a fresh Receipts-owned authorization; grant or decline optional usage | Callback reaches that machine's backend; issued registration is validated; decline leaves inference unavailable |
| 4. Persistence and refresh | Restart, race two refreshes, fail persistence, switch account/workspace | Stable installation identity; one refresh owner; atomic credentials; old jobs cannot consume the new account's allowance |
| 5. Capability check | Request current model catalog; select an offered model; submit one synthetic item such as `DEMO OAT DRINK 1 L` using the eventual reviewed schema | Record catalog/schema version and safe response metadata; require completed, schema-valid output; no item mutation |
| 6. Output failures | Fake refusal, unknown IDs, extra fields, prompt injection, malformed JSON, interrupted/failed terminal events and unsupported schema | Reject or abstain with a reviewable reason; no canonical writes and no invented fallback model |
| 7. Usage and cancellation | Fake plan denial/exhaustion, 429, transport uncertainty, user cancel, crash after dispatch | Pause or bounded retry according to durable operation policy; uncertain charges stay uncertain; no automatic API-key fallback or unlimited replay |
| 8. Disconnect and isolation | Disconnect during pending work; simulate revocation network failure; inspect browser/HTTP/log/export artifacts | No new dispatch; secrets cleared locally; uncertain remote revocation visible; artifacts contain no tokens or raw synthetic prompts unless deliberately allowed by the fixture harness |
| 9. Review/apply safety | With synthetic database rows, change an item or reject a canonical target after suggestion generation | Final guarded apply skips stale/conflicting proposals; accepted changes and audit commit together; no provider call under write locks |

Record date, commit, topology, fixture revision, model slug, schema version,
case outcomes and redacted provider request IDs where available. Keep credentials
and payloads out of PR/Plane evidence. Failed eligibility or schema verification
blocks rollout; successful mocks establish local behavior only. Live registration,
refresh, quota behavior and inference remain unverified until explicitly tested.
