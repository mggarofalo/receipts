# Label-blind holdout controller

`HoldoutCohort` runs twelve ordered, unique, label-free cases through one
`OfflineCoordinator` and `CampaignLedger`. Freeze and audit membership and labels
outside this controller; it cannot establish their provenance. Keep labels out of
its inputs and all model state. Each input has only `purchase` (description,
store, productCode), `taxonomyRevision`, and `taxonomy` nodes (id, name,
definition, parentId).

Create the ledger in a private local directory with a 24-call policy, an exact
prior-spend carry, and enough budget for that carry plus 24 reservations. Give
the coordinator and controller the same directory, the built C# runner DLL and
host, the frozen cases, and either an explicit `CohortLiveTransport` or recorded
response bytes keyed by `(case_id, arm, phase, 0)`. Do not use both modes.
Before any reservation the controller binds the complete ordered inputs, baseline
plans, policy, transport, the journal path and file identity, and the host,
managed output and source identity in a manifest. Reopen the same journal on
restart; a fresh journal with the same policy is rejected.
Selected-path plans are derived from retained successful baseline responses and
frozen privately before their reservations. Both stages use the actual C# offline
prepare/parse code. A baseline abstention stays an abstention; otherwise the
selected-path stage applies the C# fixed global-fit and path-support gates (both
at least 0.7). No alternative category is substituted.

A failure or unknown reservation stops the entire run without retry. Successful
retained responses can be replayed in the same order without spending again.
The result and private `holdout-predictions.json` contain only case IDs and the
baseline and selected status/category predictions. The private directory also
contains the label-free inputs, payload plans, and retained model responses; it
must be preserved with the ledger and protected accordingly. Local files and
lightweight hashes are change detectors, not runtime attestation or protection
against rollback or external tampering. No quality claim is made without a
separate label audit and analysis.
