# Synthetic offline coordinator foundation

`OfflineCoordinator` consumes only `RecordedTransport` bytes, never network or
credentials. A named ledger reservation is fsynced before consuming a response.
The exact response and its request and stage binding are written to a new partial
file, flushed, then renamed to a complete file before the supplied parser runs.
On restart a complete response is checked and reparsed without another send; a
missing or partial response remains unknown and cannot be sent again. A failed
parse can be retried from the same response. Other cases may proceed under the
same ledger allowance; an unresolved stage blocks later work in its own case.
The caller must use one stable private directory and ledger, and preserve them.
The journal and directory are local evidence, not protection against rollback,
manual alteration, or hardware failure; a lost rename leaves an unknown stage.

This primitive does not schedule phases, enforce their order, generate requests,
validate the real Jev response, derive a shortlist or abstention, run either C#
arm, record source/build/runtime/policy provenance, or export predictions. A
caller-provided parser is not evidence that a real C# parser ran. It is not an
API client or authorization for live calls. The same-case guard only prevents
progress past an unresolved earlier reservation; it does not prove dependencies.

## Actual C# synthetic integration check

`test_offline_actual_arms.py` drives the coordinator with recorded invented
answers and invokes the checkout's paired C# executable to prepare requests,
validate each retained response, derive the verifier and replay both decisions.
It exercises two named cases against one ledger, asserts the case/arm/phase
identities and resulting categories, and checks an invalid actual-parser response
across reopen while another case continues. It also checks a missing response
after reservation and exhaustion of the shared budget before an additional
response is consumed. The executable is built from the
local package cache. Responses are scripted fixtures, not model observations.
This is test harness code, not a general campaign scheduler or live client; it
does not attest the runtime or establish model accuracy. The cases use distinct
invented purchase descriptions with the same taxonomy and answer pattern.
Existing coordinator limitations
above continue to apply.

## Recorded paired cohorts

`paired_cohort.PairedCohort` schedules an explicit ordered list of
`{'id': 'case-a', 'input': <paired runner input>}` objects. Construct it with
an open `OfflineCoordinator`, the dotnet host and the locally built paired
runner DLL. It exclusively creates a canonical `paired-cohort.json` in the
coordinator's private directory before the first reservation, binding the
ordered inputs, C# initial plan hashes, managed runner build files, dotnet host,
and scheduler/coordinator Python sources. It verifies this local change
detector on resume and before runner commands; it is not build provenance or
runtime attestation, and does not identify the installed .NET framework. On reopening,
pass the same ordered inputs, directory and ledger. IDs and each input are
bound; changed inputs or order are rejected. Preserve the whole directory and
ledger together. Local deletion, rollback and concurrent external alteration
are outside this mechanism.

`run_case(id, responses)` returns prediction-only baseline and candidate
category IDs and answered, abstained, or failed statuses. `run(responses)`
returns each case as complete with those predictions or unresolved; it continues
independent cases after a case failure and aborts on identity changes. The responses are recorded bytes indexed by `(arm, phase, index)` per
case; `run` accepts a dictionary keyed by case ID. The scheduler prepares the
production and shortlist payloads, validates each response with the wire checks
and actual C# parser, derives the verification payload from retained shortlist
answers, and replays both arms. Existing complete responses are reparsed on
restart and need not be supplied again. A missing response for an unattempted
stage is rejected before reservation. An unknown or invalid attempted stage
is never resent; `run_case` can still advance a different case under the same
ledger allowance. All transport is recorded;
this is not a live client, runtime attestation, or evidence of model quality.

The focused synthetic test `python -m unittest test_paired_cohort` builds the C#
runner from the local package cache and uses invented inputs and answers.

## Recorded batched paired cohorts

`BatchedPairedCohort` in `batched_paired_cohort.py` is a separate scheduler with recorded and explicit live modes. It uses the actual C# `prepare-batched`, `validate-batched`, and
`replay-batched` commands. Construct it with an `OfflineCoordinator`, dotnet
host, locally built paired runner DLL, ordered explicit `{'id', 'input'}`
cases, and explicit `aggregate_call_limit` and `prior_calls` integers. The
manifest binds those call limits and the complete ledger policy. It binds the input, initial plans and local build identities in a private
manifest. Preserve that manifest, response evidence, vector files and ledger
together; this detects local changes, not build provenance, runtime attestation,
rollback, concurrent external modification or hardware failure.

Responses are recorded bytes keyed per case by `(arm, phase, index)`; the
candidate phases are `shortlist`, `verification`, and `coverage`. After all
shortlist responses, the actual C# planner derives the complete ordered vector.
The scheduler flushes a case-specific vector before any dynamic reservation,
checks every payload and the full remaining call and monetary allowance, and
validates each retained prefix. It validates complete aggregation before marking
the final dynamic response successful. On restart it regenerates and compares
the complete vector, reparses retained prefixes, and never resends a reserved
unknown attempt. Other cases can be run independently. A process-wide lock
serializes these batched cohort executions; callers must prevent other code
from using the same ledger concurrently. The ledger's `max_calls` must not exceed `aggregate_call_limit - prior_calls`;
its monetary carry must be at least the full fixed reservation for each prior
call, including unknown outcomes. The caller must conservatively include any
other earlier spending in the carry; neither figure is inferred from another
ledger. Every existing attempt remains charged.

The C# planner retains its 30-request and 128-path bounds and may abstain or
reject a vector that does not fit; no alternatives are dropped. Byte limits do
not establish token fit. Synthetic tests do not establish live readiness. Focused tests use invented 309-category inputs
and recorded synthetic replies only.

## Explicit live batched operator

`batched_cohort_operator.py` accepts a JSON array containing exactly one `{"id": ..., "input": ...}` case from an absolute private path outside a checkout. Use an existing private local directory, absolute paths to the dotnet host and locally built runner DLL, and the name of the credential environment variable. The operator accesses that credential only after the ledger reserves a stage. Supply every limit explicitly: `--aggregate-call-limit`, `--prior-calls`, `--max-calls`, `--budget-nanodollars`, and `--prior-spend-nanodollars`. The aggregate call limit must be at most 30. The budget is the total including prior spend, not the remaining budget. Prior calls count earlier calls against this specific pilot allowance from other ledgers only; prior spend covers relevant historical ledgers, including unknown reservations. Prior calls require at least one full fixed reservation each. The current ledger retains and charges its own attempts across resumes: do not add them again to the prior carry or reset the ledger. These carry values are not inferred from other campaigns. The 162 historical calls belong to independent earlier campaigns, so this pilot starts with `--prior-calls 0`. Its proposed `--prior-spend-nanodollars 565527228` is a deliberately conservative historical carry; it may double count and is not a guaranteed bill.

Add `--create` only for a new ledger; omit it when resuming with exactly the same inputs, policy, limits, executable and credential variable name. Preserve the entire private directory and its ledger. Progress contains aggregate counts only. Once every case completes, `predictions.json` holds private per-case predictions; unresolved cases leave no prediction file and return a nonzero exit code. An attempted stage without complete evidence remains charged and is never sent again. The process continues independent cases after an unresolved case but stops on identity changes. A changed output file stops the run. This mechanism does not guard against directory rollback or external concurrent modification. The caller must prevent other code from using the ledger concurrently and must protect the private directory and input with suitable filesystem permissions.

The operator uses the fixed live HTTPS transport when invoked. Tests patch its exchange method with synthetic responses; tests do not read credentials or make network requests. No live readiness or model-quality claim follows from those tests.
