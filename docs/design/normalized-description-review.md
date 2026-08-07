# Normalized Description Review — Design Decisions

How the review queue is meant to behave, and why. Each section records a decision, the
alternatives that were rejected, and the reason — so a later reader can tell a deliberate
choice from an accident.

Background: a background resolver (`NormalizedDescriptionResolutionService`) reads raw receipt
item text, embeds it, and searches for a near neighbour among existing canonical entries. Above
the auto-accept threshold it reuses the neighbour. Below the pending-review threshold it creates
a new entry outright. In between it creates the entry but marks it `PendingReview` and records
the near-miss as evidence. The Review Queue is where a human resolves that middle band.

## Approval has to change something (RECEIPTS-875)

**Decision: pending entries are flagged in the spending report, and approval clears the flag.**

The queue was presented as a gate, but nothing was gated. The resolver links receipt items to a
`PendingReview` entry the moment it creates one, and
`ReportService.GetSpendingByNormalizedDescriptionAsync` joined `NormalizedDescriptions` with no
status filter — so pending entries already appeared in the spending report as first-class
buckets, indistinguishable from approved ones. `UpdateStatusAsync` only wrote `entity.Status`.
Approving removed a row from the queue and changed nothing anyone could see.

`SpendingByNormalizedDescriptionItem` now carries `Status`, and the report renders pending
buckets as provisional: an "Unreviewed" badge, muted row treatment, a note above the table, and
a `Review Status` column in the CSV export. **The spend stays in the totals.** That is the
load-bearing part — a report that reconciles against receipt totals is worth more than one that
looks tidy, and a reviewer needs to see the money to judge whether the grouping matters.

Approval invalidates the `["reports"]` query cache, so the badge actually disappears when you
approve. Merge and split invalidate it too, since both move spend between buckets.

Rejected alternatives:

- **Fold pending spend into "(Not Normalized)".** Buckets would appear and vanish as you
  reviewed, so the report would change shape under a reader who had not changed any filter.
- **Reframe the queue as purely advisory.** Honest about the old behaviour, but it concedes that
  review is optional housekeeping rather than a step that means something.
- **Filter pending entries out of the report entirely.** Breaks reconciliation: the report would
  no longer sum to what the receipts say, and the gap would be invisible.

### The status field is null for one bucket, deliberately

The synthetic `(Not Normalized)` bucket has no backing row, so its status is `null` rather than
some third enum value. Clients must not render it as either reviewed or unreviewed — there is
nothing to review. The CSV writes an empty cell for it.

### Wire-format caveat

The spec documents this enum lowercase (`active`, `pendingReview`) and the generated TypeScript
union agrees, but the API currently serializes it PascalCase. NSwag decorates every generated
enum property with a property-level
`[JsonConverter(typeof(JsonStringEnumConverter<T>))]` built from its parameterless constructor —
no naming policy — and a property-level converter outranks the
`JsonStringEnumConverter(JsonNamingPolicy.CamelCase)` registered globally in
`ApplicationConfiguration`. So the global policy never gets a say.

That is **RECEIPTS-884**, tracked separately. Until it is fixed, client-side status comparisons
go through `src/client/src/lib/normalized-description-status.ts`, which compares
case-insensitively. Those predicates keep working under either casing, so fixing RECEIPTS-884
will not silently invert them.
