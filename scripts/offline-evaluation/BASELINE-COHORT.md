# Baseline-only evaluation seam

`BaselineCohort` prepares the existing paired plan offline but sends only its
production payload through the existing coordinator and explicit transport.
Each response is envelope-checked and parsed by the real C# production provider
before success is recorded. Results and all case inputs are private. The C#
`baseline-result` command has no network access and accepts only one supplied
production reply with an empty shortlist and no verifier.

The caller supplies one existing `CampaignLedger`, private directory, ordered
case list, and an explicit freeze file and SHA-256. The manifest binds exact
inputs, plans, build change detectors, transport identity and freeze file identity
before dispatch. Labels are forbidden from the request input. The caller must
independently verify the freeze's referenced cohort and label hashes, tuning-only
membership, pilot exclusion, privacy and chronology; the freeze hash alone does
not prove any of them. The controller enforces at most 12 cases and calls, at
least $0.65 carry, and a budget below $5. It uses one journal, retains all
reservations, and stops the whole run on any error or unresolved attempt. Reopen
can read a completed response but never resends an existing stage.

This is a review packet, not authorization to dispatch: independently review the
controller, frozen execution dependencies, launch/key isolation, data and budget
history before any real call. A recorded response transport supports synthetic
tests. Live mode is explicit; no credential is accessed by this module. Provider
context fit cannot be inferred from byte length. Accounting uses the existing
ledger assumptions and is not a provider invoice or guaranteed billing cap.
