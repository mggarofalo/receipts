# Authored synthetic safety challenge

These 24 challenge cases and their taxonomy are fully fictional, authored for
this repository. They contain no receipts, exports, or taxonomy from a real user.
They test recognizable failure modes; **they are not a sample of real prevalence**.
They are public development examples, not an untouched validation set. Do not tune
thresholds on them and then describe their results as held-out evidence.

The taxonomy classifies the kind purchased. Gift, office, and personal use do not
change a clear item kind. The `unclear_purpose` cases instead lack information
about what a fee, membership, or session bought. That uncertainty can affect
taxonomy fit. Merely missing a buyer's intended use must not force abstention.
The narrow Services root includes only passenger travel and repair labor; it
cannot absorb other services. A line spanning separate roots has no listed
common ancestor. An established parent can be selected when its child is unclear.

| Expected outcome | Cases | Denominator |
| --- | --- | ---: |
| Abstain | s01-s16 | 16 |
| Broad parent | s17-s19 | 3 |
| Assignable leaf | s20-s24 | 5 |

Keep these denominators separate from real-data evaluation and from each other.
Report safe abstentions / 16, wrong assignments on expected-abstain cases / 16,
correct parent assignments / 3, and correct leaf assignments / 5. Include
failures and unattempted cases in each cohort denominator; report their counts
separately. A failed provider call is not a safe abstention. Report per-case
outcomes as well as counts. Do not pool this challenge into a real-cohort
accuracy, coverage, safety rate, or prevalence estimate. Related cases share
`group` values; 24 cases do not imply 24 independent observations.

`inputs.json` contains the authored taxonomy and item descriptions. `labels.json`
is evaluator-only: expected outcomes, reasons, and rationales **must never be sent
to Jev**. The strict executable schema is `../synthetic_safety.py`:

- Inputs have exactly `version` (integer 1), `taxonomy`, and `cases`.
- Taxonomy rows have exactly `id`, `name`, `description`, and nullable `parent`.
  IDs are unique, parents exist, and ancestry must be acyclic.
- Cases have exactly `id`, `group`, and `description`, all nonempty strings.
- Labels have exactly `id`, `outcome`, `expected`, `reason`, and `rationale`.
  IDs match cases one to one. Abstentions have a null expectation; parent and
  leaf controls reference existing nodes of the corresponding kind. The fixed
  16/3/5 outcome mix and five abstention-reason counts are validated.

Use `model_state(inputs, case_id)` to build model-visible state: it validates
input fields and copies only that case's description and the taxonomy. IDs and
groups remain local metadata. This helper has no network path, provider SDK,
credential access, or application activation. It does not run Jev. Any future
adapter must preserve this boundary and separately obtain authorization for
live calls. Do not serialize an evaluation record as request state.

Validate entirely offline from the repository root:

```powershell
python -m unittest discover -s scripts/offline-evaluation -v
```

The general `evaluation.py score` command scores exact classification labels;
it does not treat a correct safety abstention as a correct class assignment.
Do not feed these nullable safety labels to that command or quote its generic
accuracy as this challenge's safety score. No model-quality claim follows from
the fixture's consistency tests passing.
