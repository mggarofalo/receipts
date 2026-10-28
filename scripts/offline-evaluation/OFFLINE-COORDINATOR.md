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
