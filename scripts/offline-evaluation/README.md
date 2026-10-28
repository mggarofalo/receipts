# Offline evaluation primitives

This Python 3.11+ standard-library tool splits labeled cohorts and scores previously
recorded classification results. It does not call providers, read environment
credentials, locate databases, or search for data. Supply every input and output
path explicitly. Keep evaluation data and generated reports outside the repository.
The tests construct synthetic cases in memory and temporary directories.

Run from the repository root:

```powershell
python -m unittest discover -s scripts/offline-evaluation -v
python scripts/offline-evaluation/evaluation.py split --cases <cases.json> --holdout-fraction 0.25 --seed <seed> --output <manifest.json>
python scripts/offline-evaluation/evaluation.py score --cases <heldout-cases.json> --predictions <predictions.json> --output <report.json>
```

Output files must not already exist. Input files are UTF-8 JSON arrays; malformed
JSON, duplicate object keys, unknown prediction IDs and duplicate
IDs are rejected. Probability validation rejects nonfinite values in distributions;
extra case fields are ignored. No output is written when validation fails.

A case has this shape (values below are synthetic):

```json
{"id": "case-a", "group": "family-a", "expected": "alpha"}
```

IDs must be unique. `id`, `group` and `expected` must be nonempty strings.
Extra case fields are ignored and never copied into outputs. Choose group keys
before evaluating: all duplicates, related descriptions, or other leakage units
must share a group. The tool cannot discover those relationships. A split requires
at least two groups. It sorts groups by a SHA-256 hash of seed and group, selects
`ceil(group_count * holdout_fraction)` for holdout (clamped to leave both partitions
nonempty), and emits a manifest containing only ID, group and split. The fraction
is a fraction of groups; unequal group sizes can produce a different case fraction.
Input order does not affect the manifest. The same cohort, seed and fraction give
the same split. Freeze the manifest before tuning and keep holdout labels separate
from the tuning workflow. This tool does not enforce a release or tuning process.

Filter the original cases to the manifest's holdout IDs before scoring; the score
command evaluates exactly the cohort supplied to `--cases`.

Predictions are recorded outcomes, independent of any provider response schema:

```json
[
  {"id": "case-a", "status": "answered", "label": "alpha"},
  {"id": "case-b", "status": "abstained"},
  {"id": "case-c", "status": "failed"},
  {"id": "case-d", "status": "unattempted"}
]
```

Only `answered` permits a label, which must be a nonempty string. An omitted case
is counted as `unattempted`. Record transport or malformed-answer failures as
`failed`; a policy decision to withhold a valid answer is `abstained`. Failed calls
must never become negative answers. A wrong label is an incorrect answer, even if
it is outside the expected label set. Provider adapters and abstention policies
belong in separate code and are intentionally absent here.

Reports contain counts and rates with explicit numerators and denominators:

- `accuracy`: correct answers / all cases, including abstentions, failures and
  unattempted cases in the denominator.
- `coverage`: answered / all cases.
- `selective_accuracy`: correct answers / answered cases.

Zero denominators produce JSON `null` rates and intervals. Intervals are approximate
95% Wilson binomial intervals. They assume independent cases and are **not adjusted
for correlation within groups**, repeated tuning or multiple comparisons. Treat
them as descriptive diagnostics, not evidence of significance for grouped data.
No provider quality, calibration, latency or cost claim follows from this tool.

For use by separately maintained adapters, `validate_probabilities(distribution,
candidates)` checks exact candidate membership, unique candidate names, finite
numeric probabilities in [0, 1], and total mass within 1e-6 of one. It rejects
booleans and does not coerce or renormalize values. It validates an already extracted
normalized distribution, not a complete provider response. Catch its `ValueError`
and record a failure. No threshold or automatic label selection is built in.
