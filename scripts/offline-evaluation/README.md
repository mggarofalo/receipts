# Offline evaluation primitives

The separate [campaign reservation foundation](CAMPAIGN-LEDGER.md) provides
offline durable attempted-call accounting for a future provider adapter. It
contains no dispatch integration and makes no provider billing guarantee.

A separate explicit single-case HTTPS component is described in
[live transport](LIVE-TRANSPORT.md). It has no CLI and has not been dispatched;
the original offline APIs below remain offline.

This Python 3.11+ standard-library tool splits labeled cohorts and scores previously
recorded classification results. It does not call providers, read environment
credentials, locate databases, or search for data. Supply every input and output
path explicitly. Keep evaluation data and generated reports outside the repository.
The tests construct synthetic cases in memory and temporary directories. A
separate [authored safety challenge](synthetic-safety/README.md) is intentionally
checked in; its fictional taxonomy and cases are not real-data evaluation inputs
and must retain separate denominators.

Run from the repository root:

```powershell
python -m unittest discover -s scripts/offline-evaluation -v
python scripts/offline-evaluation/evaluation.py split --cases <cases.json> --holdout-fraction 0.25 --seed <seed> --output <manifest.json>
python scripts/offline-evaluation/evaluation.py score --cases <heldout-cases.json> --predictions <predictions.json> --output <report.json>
```

`python scripts/offline-evaluation/benchmark_frozen_hashing.py` compares complete
frozen verification using the previous whole-file SHA-256 implementation and
bounded-buffer `hashlib.file_digest`. It creates only invented temporary files,
accepts no arguments, launches no executable, and performs no network operations.
The inventory has 240 files totaling 429 MiB: three 64-MiB files and 237 one-MiB
files across source, executable and dependency roots. Six alternating timed runs
per implementation reduce warm-cache ordering bias; profiles identify hash,
filesystem and ancestry costs. Run on the target filesystem to assess local cost.

On Windows with Python 3.13, an initial warm-cache run measured median complete
verification at 0.390 seconds for whole-file reads and 0.325 seconds for streaming
(1.20x). A repeat measured 0.623 and 0.364 seconds (1.71x), showing filesystem
timing variability. A separate 240-MiB profile attributed about 48% of verification time to
path ancestry validation and 30% to SHA-256. These synthetic results establish a
modest local improvement, not a prediction for a real campaign's elapsed time.
Streaming also avoids allocating an entire runtime artifact. Every verification
still reopens each artifact, reads and hashes its full content, checks all path
ancestry, and validates complete root membership. No digest, metadata or ancestry
cache survives or spans checks. Required pre/post stage and execution checks,
size limits, error messages and exchange ownership remain unchanged.

The recorded and live wire guards share `guarded_exchange.validate_envelope`.
For Choice answers they follow the production `CategorySuggestionPolicy`:
total mass may differ from one by at most `0.01 + 1e-12`, inclusively, and the
selected probability may be at most `1e-9` below another option. These are
application validation allowances; the TypeSafe schema describes a sum of one.
Finite probabilities in [0, 1], exact option membership, confidence and envelope
checks still apply. Invalid responses stop the campaign with the reservation
retained and cannot trigger another send. The HTTPS worker only returns raw
bytes; it does not retry or maintain a second probability validator.
`test_choice_validation_parity.py` compiles the actual production policy source
when the .NET 10 SDK is available and checks both production decision modes
against synthetic wire responses, including floating-point boundary neighbours.

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
