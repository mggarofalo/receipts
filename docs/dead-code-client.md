# Client reachability audit

`npm run audit:dead-code` in `src/client` uses the installed TypeScript compiler
and symbol resolver. Its roots are the module scripts in `index.html`. It follows
resolved static imports, reexports, type imports, side effects, and literal lazy
imports. Tests and test infrastructure are not production roots. This finds
disconnected cycles as well as isolated files; test coverage cannot keep an
otherwise unreachable application module alive.

The check fails for unreachable production files, missing roots, unresolved
imports, or unanalysed computed imports, `require` and glob loaders. Existing CSS
and image imports are assets. Declaration files and generated API contracts are
retained. Dynamic module and namespace imports conservatively preserve all of
their exports, including reexport targets.

Unused exported symbols are **review candidates**, not automatic deletions or a
global proof about JavaScript execution. The report does not prove arbitrary
reflection, property-level reachability, or internal function islands dead. A
deletion also requires checking routes, configuration, external contracts and
test consumers, then typechecking and testing the resulting application.

`npm run test:dead-code` exercises real temporary TypeScript projects, including
live aliases, shorthand references, types, lazy defaults, namespace enumeration,
lazy barrels, disconnected cycles, test-only imports and unknown loaders.

The full baseline report is committed at
`src/client/scripts/evidence/dead-code-main-3cef6d25.json`. Regenerate it against
an installed checkout of the recorded revision using the current audit tool:

```sh
node scripts/audit-dead-code.mjs --root /path/to/baseline/src/client
```

The report has seven unreachable files, 61 export candidates and no unknown
edges. Fifty-nine export candidates were removed; the two testing helpers below
remain intentional. Reexports count separately in that report. No absolute
machine paths are stored in the evidence.

## RECEIPTS-989 findings and removals

The audit of both main and the preceding refactor identified these seven files
without a production path:

- `components/dashboard/RecentReceiptsWidget.tsx`: the dashboard uses its current
  workflows; this widget was only mounted by its own tests.
- `pages/Home.tsx`: placeholder page absent from the route configuration.
- `hooks/usePagination.ts`: superseded by server pagination. Page tests still
  mocked it even though the pages did not import it.
- `hooks/useAggregates.ts` and `hooks/useRoles.ts`: no application consumers.
- `components/ui/navigation-menu.tsx` and `components/ui/scroll-area.tsx`: unused
  UI adapters.

Unconsumed detail/list/batch hooks, UI primitive exports, obsolete type aliases,
format/auth helpers and location-history compatibility aliases were also removed.
Their HTTP endpoints and generated contracts remain: absence of a browser caller
does not establish absence of external API consumers. Every runtime npm dependency
still has a consumer.

The standalone `YnabMemoSyncCard` and `YnabSplitComparisonCard` wrappers were only
used by tests. Their live content now has matching `*Content` filenames. Readiness
belongs to `ReceiptDetail` and `YnabReceiptCard`; the duplicated wrapper gates and
unused standalone styling option were removed. Wire-enum, ownership, failure and
content assertions now exercise those live boundaries. Explicit readiness tests
cover missing configuration/budget and pending lookups without mounting children.

Projection tests now mount current receipt-item list and deleted-transaction
queries, preserving cache invalidation, held-response and stale-write assertions.
Location-history behavior is tested through the live history object. Stale page
pagination mocks were replaced with data for the actual query. Tests exclusively
for removed behavior were removed with that behavior.

After removal the report contains no orphan files or unknown edges. The two
remaining export candidates, `_flushForTesting` and `_resetForTesting` in
`signalr-toast-buffer.ts`, are intentionally retained test infrastructure used by
buffer and authentication/session isolation tests.
