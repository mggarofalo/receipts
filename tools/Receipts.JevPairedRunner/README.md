# Paired offline evaluation runner, version 1

An offline file-exchange executable and a separate recorded-response ledger seam
are now available; see [guarded exchange](GUARDED-EXCHANGE.md). They add no live
transport or credential access. The original library behavior described below is
unchanged; statements about absence of an executable apply to its initial stage.

This library stages the same purchase description, taxonomy revision and ordered taxonomy through the actual production Jev provider and the experimental exhaustive shortlist, then candidate verifier. Production ignores store and product code because its real contract only accepts description. Candidate receives those optional fields through its existing purchase contract. No labels, gold answers, groups, IDs or scoring metadata belong in `PairedInput`.

`PairedReplay.Prepare` creates immutable request snapshots and a SHA-256 binding over version, exact production payload, exhaustive shortlist payloads, model, policy versions, source revisions and limits. `PrepareVerification` reduces a complete ordered set of supplied shortlist replies using the actual candidate reducer. Its dynamic verification payload has its own existing candidate binding. `ReplayAsync` invokes the real production provider using a sealed in-memory `HttpMessageHandler`, and uses the actual shortlist and verification parsers and reducers. The dummy credential is a constant; nothing reads environment variables, secrets, configuration files or remote endpoints. There is no live execution option, executable or dependency injection hook for a network handler. All replies must be supplied by the caller.

Frozen source baseline: production e0916a54 (production Jev files unchanged from candidate base); candidate 16a91add. Pin: `jev-1.14.0`. One case allows exactly one production replay, every planned shortlist batch, and zero or one verification replay. Maximum requests = planned shortlist batches + 2, retries = 0, concurrency = sequential. The exact shortlist count includes every category node and is available in the plan. Each shortlist batch has at most 16 questions and 64 KiB, as enforced by the candidate implementation; verification has at most 258 questions and 8 MiB; production has at most 64 KiB. Actual verification count is available from the final payload. Input/output token limits per request and token limits per question are **unknown (null)**. Byte bounds do not establish token or monetary bounds. Maximum real network calls is **zero**.

Synthetic demonstration lives in `PairedReplayTests`; test responses are scripted, not model-quality evidence. Prepare both arms once for each case, retain the plan binding and dynamic verification binding, and score only after receiving both outcomes. Reordering or altering a case requires a new binding. Separate held-out labels may be joined afterward by the caller, never serialized into requests. Do not treat replay success or literal response usage as observed provider billing.

This first stage has no campaign ledger integration, persisted output, private-data reader, HTTP transport or live-call authorization. Before any live runner can exist, independent review must validate the plan, complete fail-closed campaign ledger reservations before every possible send, reconcile request/question/token accounting (including unknown outcomes), and enforce a campaign budget without retries. That is an agent review/integration step, not a user confirmation gate. This API cannot be used to send live requests.

The source revision strings are declarations, not executable attestation. Project references compile the code present in this checkout; a later edit to reducer logic could preserve a version string and payload binding. Before frozen or live execution, verify exact source and executable hashes against the independently reviewed artifacts. Do not trust the revision strings or request bindings as proof of which decision implementation executed.

Payloads, response bytes, hashes and verification outputs can reveal user evidence. Keep any future private artifacts outside the checkout in private storage; never commit real purchase evidence, response bodies or credentials. The checked-in demonstration contains invented inputs only.

## Purchase-evidence diagnostic exchange

`prepare-purchase-experiment` and `validate-purchase-experiment` are offline file
commands with the same absolute paths, private output and CreateNew rules as the
other commands. The input object has `purchase`, `taxonomyRevision`, `taxonomy`,
and `terminalIds`. Supply a nonempty, sorted, unique array of category IDs before
seeing any responses. The existing candidate planner validates the taxonomy and
expands each terminal to include its ancestors. The resulting complete ordered
paths are exported explicitly. Prepare takes a replies file containing JSON
`null`; its output includes both exact payload strings, their plan hashes and
question-to-path mappings. Keep inputs and outputs private.

Validation takes a replies object containing `variant` (`purchase-only` or
`unresolved-purpose`) and `response` (the exact raw JSON response as a string).
It reconstructs the request from the input and invokes the corresponding real
`PurchaseExperimentRequest.ParseResponse`; invalid, mismatched or stale responses
fail without creating an output. Successful output includes the plan hash and
parsed answers. These commands have no network or credential access, no retries,
and do not make a category decision. They are not a campaign controller: a future
controller must bind exact input, payload and executable/dependency hashes, enforce
shared reservations, ordering and terminal failure, and revalidate on restart.
No live experiment is authorized by these commands.
