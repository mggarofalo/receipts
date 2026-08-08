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

## Renaming is cosmetic, by construction (RECEIPTS-876)

**Decision: display label is a separate column from match text.**

`CanonicalName` was set once, verbatim, from whatever raw text the receipt carried, and there
was no update path. "Approve" therefore meant "accept this raw string as the canonical name
forever."

`DisplayLabel` is now a nullable column beside it. `DisplayName` is `DisplayLabel ??
CanonicalName`, and that is what every user-facing surface shows: the review queue, the
registry, merge and split dialogs, the nearest-match evidence line, and the spending report's
bucket labels. The rename endpoint writes the label and nothing else — never `CanonicalName`,
never the embedding.

That separation is the whole point. A clean human label like "Milk" may match receipt text
markedly *worse* than the messy original "MILK 2% GAL", so re-embedding on rename would quietly
degrade resolution for every future receipt while looking like a cosmetic edit. Rejected
alternatives: re-embedding on rename (silently misroutes future matches), and freezing the
vector while letting the name drift with no record of the original (name and behaviour diverge
invisibly — we keep showing the matched text next to a diverged label instead).

### Uniqueness is on the effective display name

A unique functional index on `lower(COALESCE("DisplayLabel", "CanonicalName"))`, not on the
label alone. The collision that actually bites is renaming row B onto row A's *un-renamed* name;
an index over the label column would allow it, and the two rows would then be indistinguishable
everywhere a user looks — including as two identically-named buckets in the spending report.
The existing unique index on `lower("CanonicalName")` stays, since match text still has to be
unique in its own right.

Renaming a row to its own matched text stores `null` rather than a duplicate copy, so the row
reads as "not renamed" rather than permanently pinned to what it already displayed.

## Rejection is a remembered "no" (RECEIPTS-876)

**Decision: a third status, `Rejected`, whose row survives as a tombstone.**

The status enum had two members and no way to say "this is garbage text". The only way to
dispose of a bad pending entry was to merge it into an unrelated active row, which silently
re-pointed its receipt items there. Merge means "this is the same as X"; rejection means "this
does not deserve an entry at all". Those are different judgements and now have different
actions.

Rejecting **unlinks every receipt item and clears its match score**, so the items report under
"(Not Normalized)" — they genuinely are unnormalized. The score is cleared alongside the FK for
the same reason as in the requeue: a live item carrying a score with no description to explain
it is the inconsistent state RECEIPTS-883 exists to prevent. Soft-deleted items are detached
too, or one would return from the recycle bin linked to a row the reviewer rejected.

The row itself is kept. That is what stops the resolver recreating the entry the next time the
same text appears.

### Why the resolver filters tombstones at the query, not at the decision

Rejecting unlinks the items, so they become unresolved again and match the resolver's candidate
predicate forever. If the resolver only declined them *after* fetching a batch, they would
occupy the `Take(BatchSize)` window on every cycle and starve genuinely-new items behind them —
enough rejected rows would halt resolution entirely.

So the candidate query excludes any item whose description matches a `Rejected` row's
`CanonicalName`, case-insensitively, riding the existing unique index. The post-fetch check
survives as a second line of defence for the narrow race where text is rejected between
building the batch and resolving the group.

Tombstones are also excluded from both ANN searches. A rejected row keeps its embedding so the
exact-match lookup still finds it, but it must never win a similarity search: auto-accepting
onto one would re-link the very items the reviewer detached, and citing one as a near-miss would
offer "nearly matched \<the thing you rejected\>" as evidence.

## Splitting moves a group, under a name you choose (RECEIPTS-877)

**Decision: a server-side filter, a multi-select, and a caller-supplied name.**

### The dialog had never worked

The stated problem was a 200-item ceiling. The real one was worse:
`ReceiptItemRepository` projected list rows into a fresh `ReceiptItemEntity` that omitted
`NormalizedDescriptionId`, so `GET /api/receipt-items` returned `normalizedDescriptionId: null`
on every row despite the spec documenting the field. The dialog filtered on exactly that field,
so it could never match — it always reported "no linked receipt items found", regardless of how
recent the items were. Nothing asserted the field, which is why it survived; the same blind spot
hid RECEIPTS-884.

The projection now carries the FK, the match score, and a trimmed stand-in for the neighbour
(id, matched text, label, status). A stand-in rather than an `Include`, because an include drags
the 1024-float embedding across the wire for every row of every page. It is built from a
correlated subquery rather than a navigation access: these queries run under
`IgnoreAutoIncludes`, which leaves the navigation unpopulated, so reading it yields null on some
providers regardless of the underlying row.

### The name is the caller's

A multi-item split routinely spans heterogeneous raw text — "MILK 2% GAL", "milk gallon",
"WHOLE MILK". No automatic rule produces a name anyone would want from that set. The dialog
pre-fills from the first selection and then gets out of the way; once the reviewer edits the
field, further selections leave it alone. Rejected alternatives: modal or most-common raw text,
first-selected text, and one-entry-per-distinct-description (a single click causing surprising
fan-out).

If an entry with the chosen name already exists, the items are re-linked to it rather than a
duplicate row being created, and the audit records `targetWasExistingRow` so the trail does not
imply a row was created when none was.

### All-or-nothing

An unknown id throws before anything is written. A partial split would leave a half-corrected
group with no indication of which half moved — worse than an outright failure, because the
reviewer would have to reconstruct what happened by hand.

### Moved items are rescored

Each item's score was its similarity to the row it is leaving, so after the repoint it describes
a comparison that no longer applies. `PreviewThresholdImpactAsync` buckets items by exactly that
column, so a stale score would skew every later threshold preview. Same reasoning as
RECEIPTS-892 applied to merges; the split path reuses the same helper.

### Empty state says what is true

"No receipt items are linked to this entry" — a statement about the data. The old copy, "not
found in the most recent 200 items", was a statement about the query, and was shown even when
the entry did have linked items.

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

## The registry is paged, searched server-side, and no longer read-only (RECEIPTS-879)

`GET /api/normalized-descriptions` was explicitly unpaginated — the schema said `totalCount`
"matches the length of `items` since the endpoint is not paginated". The registry tab loaded
every Active row and filtered the array in the browser, and every merge-dialog open paid for the
same full list. The row count is bounded by the number of distinct receipt-item descriptions ever
seen, and grocery receipts generate thousands of them.

The endpoint now takes `q`, `offset` and `limit` (default 50, ceiling 200) alongside the existing
`status`, and `totalCount` means the number of rows matching the filter. The additions are all
optional, so `check:breaking` passes; the behavioural change is real though — a caller that omits
`limit` now gets 50 rows instead of all of them, which is the point.

### Search matches both names

A renamed entry has two names: the label an admin gave it and the receipt text its embedding is
anchored to. `q` matches either, case-insensitively. Matching only the label would hide a row
from the admin looking at a receipt; matching only the matched text would hide it from the admin
who renamed it.

### Ordered by display name, with an id tiebreak

Offset paging over an unordered set drops and duplicates rows between pages. The order is
`COALESCE(DisplayLabel, CanonicalName)` then `Id` — by what is on screen, not by the underlying
matched text, so a renamed row does not sort somewhere alphabetically unrelated to its own label.
It also puts near-duplicates adjacent, which is what a reviewer hunting merge candidates wants.

The review queue's client-side "newest first" sort was removed rather than kept. Once the list is
paged that sort only reorders the rows in hand, so it reads as a global ordering while being a
per-page one.

### The total is counted before paging

`totalCount` is the size of the filtered set, not the length of the page. A client cannot build a
pager from a total that tracks the page length — it would report one page forever — and a total
that ignored the filter would offer pages that turn out to be empty.

### Four row actions, because approval was otherwise permanent

Rename, merge, split, and send-back-to-review. The registry is the only place an already-approved
entry can be corrected; read-only meant every approval was irreversible.

**Send back to review is not Reject.** It flips `Active` → `PendingReview` and touches nothing
else: no item is unlinked, no receipt data changes, and approving it again from the queue undoes
it. Reject (RECEIPTS-876) unlinks every item and tombstones the text so the resolver stops
proposing it. One is "I want to think about this again", the other is "this is not a thing". They
are confirmed separately and the copy on each says which consequence applies.

**Merge shows the count before it acts.** Merging is irreversible and direction-sensitive: the
entry being merged away is deleted and its items are re-pointed, so merging the wrong way round
moves the larger set under the smaller name. The dialog states how many items move and that the
source is deleted. Its old copy said "this pending-review entry will be deleted", which stopped
being true the moment the registry could merge two Active entries.

The merge dialog's candidate list also moved to server-side search here, out of necessity: it
filtered whatever the Active list had already loaded, and once that list is one page the dialog
could only find a target that happened to be on it. It now shows "showing N of M" when there are
more matches than fit. Including *pending* entries as merge targets, linked-item counts beside
each candidate, and the tests for those remain RECEIPTS-878.

## Every legitimate merge target, in one list (RECEIPTS-878)

The merge dialog took candidates from `useNormalizedDescriptions("Active")` and cut them to 50
with `.slice(0, 50)` in both branches, with nothing on screen saying so. With a real registry the
target you wanted might simply not be shown, and the search box only re-filtered the same
client-side array. RECEIPTS-879 moved that search to the server and added the "showing N of M"
notice; what was left is *which* rows count as candidates.

### Pending entries are targets too

Two near-duplicate pending entries out of the same resolver batch are exactly the pair a reviewer
wants to merge. Requiring one to be approved first forced a judgement — "this is a real item" —
that they had not made yet, and often could not make until after the merge.

The survivor stays pending. That is correct rather than an omission: merging two near-duplicates
answers "are these the same thing?", which is a different question from "should this be in the
registry?". The candidate is badged **Pending review** so the reviewer knows the merge does not
also approve it.

Rejected rows are never candidates. A tombstone exists to stop the resolver proposing that text
again; merging items into one would resurrect it. They are excluded in the query rather than
filtered out of the response — a client-side filter would still let tombstones consume the page
and push real candidates off it.

### The status filter takes a set

`?status=Active&status=PendingReview` (comma-separated in one value works too). A single-valued
filter could not express "any legitimate target", and the alternatives were worse: two round
trips whose totals have to be reconciled for one pager, or redefining "no filter" to silently
exclude tombstones, which changes what an unfiltered request means for every other caller.

An empty set means *no filter*, not *match nothing*. A caller that builds the list from an empty
selection gets everything rather than a silent zero. One unparseable value fails the whole
request — dropping it would answer a narrower question than was asked while looking successful.

`?status=Active` behaves exactly as before, and `check:breaking` does not flag the change: it
compares schemas and endpoint presence, not query-parameter shapes.

### Counts beside each candidate

Merging is direction-sensitive and irreversible — the source is deleted and its items re-pointed —
so merging the wrong way round moves the larger set under the smaller name. Both counts are now on
screen: the source's in the dialog description, each candidate's in its row.

## Saying what an action does before it is taken (RECEIPTS-874)

Four bare outline buttons sat at the end of every review-queue row with no tooltip, no help text,
and no explanation of consequences. The only prose describing each action lived inside the dialog
you got *after* clicking — and Approve has no dialog at all, so its effects were never stated
anywhere. Merge, the sharpest edge, deletes a row.

### An explainer above the table, not behind an icon

It says what a pending description is (a near-match the resolver would not guess at), why the row
is here, that its items are already linked and its spending already in the reports, and what each
of the four actions does. Above the table rather than behind a help icon because the actions are
destructive and mostly irreversible: a reviewer meeting the queue for the first time has no way to
know that Merge deletes a row, and an affordance you have to discover is one most people will not.

It stays up when the queue is empty. An empty queue is exactly when somebody is reading to work
out what the page is for.

Rejected: remembering a dismissal. That is persisted per-user state we would have to store and
would get wrong on a shared or fresh login; a short block that costs a glance to skip is cheaper
than the bug.

### Hints are descriptions, not labels

Every action carries a hint that is both a tooltip and the button's `aria-describedby` target, so
it reaches keyboard and screen-reader users rather than only whoever hovers. It is not folded into
`aria-label`: the label is the action and the hint is what the action does, and collapsing them
would make the button announce a paragraph where a verb belongs.

Each hint carries the row's item count, because the difference between "Merge re-points the items"
and "Merge re-points 47 items" is the difference between a rule and a decision.

### "Merge into…"

Renamed from "Merge". The ellipsis says a picker follows; "into" says the direction, which is the
part that decides which of the two rows survives. The issue also floated "Not a new item" —
rejected, because that describes the *judgement* rather than the action, and it stopped being
accurate once Reject existed to carry the "this is not a thing" case.

The dialog's confirm button stays "Merge", which is also what makes the two unambiguous to query
in tests.

### Approve's toast states the effect

Approve remains the one action with no confirmation step — it is the only non-destructive one, and
a dialog on the common case would train people to click through dialogs. Its toast is therefore
the only place its effect is ever stated, so it says what changed ("nothing was moved, and its
spending is no longer reported as unreviewed") rather than restating the status the row was just
given ("Approved as active").

## The page is empty, not too wide (RECEIPTS-880)

**Decision: no max-width. Give the tables something to hold instead.**

The complaint was that the page stretches uncomfortably across a desktop monitor: the Review
Queue had four short columns, so the name sat pinned far-left and the buttons far-right with a
void between, and the Registry was two columns at full width, which is worse.

Capping the width would have treated the symptom. Rejected explicitly: capping *this* page only
leaves the same problem everywhere and makes pages inconsistent, and a global readable width on
`.page` is wrong for the dense tables and charts elsewhere that genuinely want the room.

What actually fills the space is evidence. The Review Queue's dead Status column — every row
rendered the identical badge, because the query is filtered to `PendingReview` — went in
RECEIPTS-873 (`54c50682`), replaced by the near-miss, the linked-item count, and the sample raw
text. RECEIPTS-879 gave the Registry the same treatment plus row actions. This issue finishes it:

- **Last seen**, a new column and API field. `createdAt` alone cannot distinguish a two-year-old
  entry still matching this week's receipts from one nothing has matched since, and that is
  exactly the difference between a live registry entry and dead weight. It is the latest receipt
  date among live linked items, computed as one more correlated subquery in the existing
  projection — no extra round trip.
- **Null means never**, rendered as "Never". An entry that has never matched anything and one
  that last matched in 2023 call for opposite decisions, so a default date would merge two
  different facts.
- The pending-count element is sized to its contents. One number in a full-width bordered bar
  reads as a section header with nothing in it.

### The width sweep found a real bug

`tests/visual/page-overflow.spec.ts` asserts the document never exceeds the viewport across
375–2560px, on both tabs. A baseline screenshot cannot catch this: it is taken at one width, and
the widths where layout breaks are the ones nobody screenshots. So the test carries no baselines
to regenerate — it reads geometry and reports which element is doing the widening.

It immediately failed at 375px, and not on the table: `TabsList` is `inline-flex w-fit`, so four
tabs pushed the document ~10px past the viewport. A horizontally-scrolling page detaches every
fixed element from the content as you scroll and competes with vertical scrolling on touch. Fixed
globally with `max-w-full overflow-x-auto` on the tabs-list variant, so an oversized tab strip
scrolls inside its own box. Tables were already fine — `Table` wraps itself in an
`overflow-x-auto` container.

The probe skips any element inside a horizontally-scrolling ancestor. Without that, every cell of
a scrollable table reports as an offender and buries the element actually widening the page.

## A template is the strongest evidence on a row (RECEIPTS-930)

Everything else the queue shows a reviewer is a machine's opinion: the near-miss score, the sample
raw text, the linked-item count. RECEIPTS-881 gave `ItemTemplate` an FK to the registry, so there is
now one fact on the row that a human put there — somebody created a template saying this item exists
and is called that.

So each row shows **"Declared by template X"** when a template points at it, and the queue's
explainer says what to do with that: a row somebody already named by hand is the clearest Approve
there is. The same badge appears in the Registry, where it means the opposite — an entry a user
curated should not be merged away on the same impulse as resolver output.

Read off the FK, never matched by name, so it can only say "these are linked" and never "these look
alike". Projected as two more correlated subqueries in the same query as `lastSeen`, so no extra
round trip. It reports a count as well as a name, because two templates on one row is the ordinary
result of merging two template-backed entries, and showing one while implying it is the only one
would hide the case worth seeing.

### "Link to template…" is a fifth action, and it deletes the row

For the reverse recognition: a reviewer looking at a resolver-derived row who knows it is an item
they already have a template for. It resolves that template's canonical entry and **consolidates
this row into it**, rather than pointing the template's FK at this row — the FK alone would be
re-derived away by the template's next edit and would leave this row's existing receipt items in
their own bucket anyway. Full reasoning in
[item-vocabularies.md](item-vocabularies.md#link-to-template-consolidates-it-does-not-just-set-the-fk).

That makes it as destructive as Merge, so it is labelled and explained like Merge: an ellipsis
because a picker follows, a tooltip naming the item count that moves, and a per-selection line in
the dialog that says whether this row survives — predicted with the same exact-match rule the server
uses, then confirmed by what the server reports back.

Five actions do not fit one line at 375px, so the actions cell wraps. The width sweep from
RECEIPTS-880 was re-run and still passes with the fifth button and the badge in place, including a
deliberately long template name in the fixture.
