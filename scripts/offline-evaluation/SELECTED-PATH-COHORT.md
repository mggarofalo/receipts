# Selected-path cohort

This operator consumes exactly ten ordered, label-free cases. Each case contains
`id`, the original `PairedInput` as `input`, and the exact retained production
response as the `production` JSON string. The offline C# exchange must parse each
response into an actual production selection; a baseline abstention is not a
follow-up request. The operator freezes all ten inputs, derived requests, build
identity, transport identity and prior monetary carry in a private manifest
before reserving any request. It rejects additional label or scoring fields.

The preregistered comparison uses description only: the C# exchange sets store
and product code to null for the follow-up and excludes global fit from model
state. Labels, membership provenance, and whether prior spend is accurate must
be audited separately; this tool cannot establish them.

Use a stable private directory and input outside the checkout, an explicitly
named credential environment variable, and a locally built runner DLL. Invoke
`selected_path_operator.py` with `--private-root`, `--input`, `--host`, `--dll`,
`--key-environment`, `--budget-nanodollars`, and
`--prior-spend-nanodollars`; use `--create` only for the first run. The ledger
allows exactly ten attempts, and the stated budget must cover the explicit carry
plus all ten conservative reservations. The caller must preserve the ledger,
manifest, response files and stop marker together. Do not delete or roll them
back. A failure or uncertain attempt stops the whole campaign without retry.
Successful reruns read retained evidence and consume no further attempts.
Results stay in `selected-path-results.json` in the private root; the launcher
prints no case data. This local change detector is not runtime attestation.
