# Request error presentation

`request-error-policy.ts` makes error presentation explicit while keeping one API client and one session-owned QueryClient. The default is `global`: HTTP 5xx responses use the existing error bridge, which opens the first-error route and toasts later operations. QueryCache and MutationCache present other default failures. For global operations they skip HTTP 5xx because the bridge already owns them. The default query policy also stops automatic HTTP 5xx retries, which would otherwise present the same operation again. Network retries retain their existing default.

A feature with its own inline error or toast selects `localErrorPolicy.request` for its HTTP call and the matching `localErrorPolicy.query` or `.mutation` options for its cache operation. The request middleware suppresses the 5xx bridge; metadata suppresses generic cache feedback. Local query options use manual retry. Direct calls such as login only need the request options because their form owns the error and no query/mutation cache is involved.

```ts
useQuery({
  ...localErrorPolicy.query,
  queryKey: ["trips", receiptId],
  queryFn: async () => {
    const { data, error } = await client.GET("/api/trips", {
      ...localErrorPolicy.request,
      params: { query: { receiptId } },
    });
    if (error) throw error;
    return data;
  },
});
```

An `onError` callback alone does not select local ownership: field-validation callbacks often depend on generic feedback for other errors. `useSessionMutation` carries explicit metadata through its existing session guards.

Request policy is held in a WeakMap keyed by the original Request. It adds no wire header and survives native 401 refresh/replay because response middleware still receives that original request. A future middleware that replaces Request objects must preserve both presentation and session bindings. Local presentation preserves authentication, password-change handling, cancellation, normalization and typed failures. It never turns a failed request into successful empty data. A manually manufactured HTTP 5xx error inside a cache operation also needs an explicit local or toast presenter, because the default cache treats HTTP 5xx as transport-owned.

## Mutation toast ownership

`toastErrorPolicy.request` and `.mutation` keep the current editor mounted and let the session's MutationCache present one actionable error, including HTTP 5xx. Transport still returns the same typed error to field-validation callbacks. The request policy suppresses the global 5xx bridge; it does not suppress authentication, replay, cancellation or password-change handling. A hook must pair both options and apply the request option to every constituent call, including a preliminary GET inside a mutation. An `onError` callback alone does not identify an owner.

Receipt, receipt-item, transaction, adjustment and item-template mutation hooks use this policy, including batch operations, trash restoration and promotion. In-form category/subcategory creation shares the same toast owner with its management page. Promotion keeps its deliberate similarity-cache creation exception. Optimistic rollback, settlement invalidation and success callbacks retain their existing ownership. Mutation retry behavior is unchanged; a failure is not an instruction to replay a possibly committed write.

Grouped report categorization also uses the cache toast owner. Its page owns successful selection cleanup, while the hook awaits every independently committed group and repairs projections on settlement. A partial failure retains selection and shows the server error once; it does not imply all groups rolled back.

The complete-receipt creation page already owns an inline error summary and its toast. Its hook therefore selects the fully local policy, so the same failure does not produce a second cache toast or trigger the unsaved-work navigation blocker.

## Current feature owners

Receipt detail and inline receipt reads own their error UI. Only an actual 404 means not found. Unavailable, network and access failures offer retry; a failed background refresh retains the loaded receipt and editor draft. Login retains generic incorrect-credential wording for 401 and presents 429, server, network and timeout failures with retry guidance. It does not invent a Retry-After duration.

The local YNAB reads are connection status, selected budget, receipt sync statuses, transaction sync status and split comparison. Their shared consumers show unavailable states and retry rather than treating missing/error projections as unconfigured or unsynced. Receipt list/detail chips distinguish loading and unavailable status; retained statuses are labelled last known. An unknown sync status prevents a new push until it can be checked. A cached receipt integration section stays mounted during prerequisite failure, with actions disabled while its selected target cannot be verified. Split-comparison errors stay within the comparison section. Portaled dialog and select actions need their own disabled state and handler guards: neither a disabled fieldset nor a disabled Select trigger reliably disables an already-open portal. Preserve the user’s choices and the close affordance while preventing a new operation against an unavailable target. Deferred mutation callbacks must also check the latest committed availability before launching a follow-up write; a successful earlier resolution is still kept.

The transaction sync-status endpoint's documented 404 means no record and may resolve null. Other failures remain errors. HTTP 200 business outcomes such as partial or failed pushes retain their existing result handling; presentation policy does not change destination ownership or retry safety.

## Remaining adoption

RECEIPTS-950 now covers receipt/status reads, receipt mutations, optional suggestion families, complete taxonomy and template catalog lookups, account/card choices, and receipt pickers described below. Remaining YNAB lists, diagnostics, settings and actions still require complete shared-consumer ownership. Other report reads need explicit classification before claiming that every optional request is local. Suppressing a shared hook requires checking every consumer, so failed requests cannot become silent.

Backup import/export also use this local policy on the shared refresh/replay transport. Their hooks own transfer feedback while the page retains confirmation and file state. Both retain a five-minute transport deadline; see [Backup & restore](backup-restore.md). Error routes and render boundaries remain available throughout the remaining adoption.

## Optional receipt suggestions

Location history, item-code suggestions, similar descriptions, category recommendations and template-history candidates use the paired local read policy. All consumers own a visible error/retry state. Location history retains local MRU entries and cached API choices; failure never clears a typed location. A failed suggestion lookup leaves manual valid entry and cached choices usable. Successful empty results remain distinct from unavailable results.

Debounced suggestion hooks expose `isDebouncing` alongside their stable query fields. Error owners hide obsolete retry controls while the input is changing and enforce current length thresholds; category recommendations also require that no category has been chosen. This matters because imperative `refetch` bypasses `enabled`. A user may explicitly retry a valid item/description lookup while its popover is closed; visibility controls automatic lookup, not the validity of that manual read. The retry control lives beside the field and does not reopen the popover or change the typed value.

The query identities, debounce delays, location scoping and suggestion freshness remain unchanged. Query cancellation is passed through to the shared transport, including location lookup. Template history keeps its existing failure/retry and widening/focus behavior. Promotion's known template-created similarity exception remains intact.

Remaining YNAB settings/actions still require their complete shared-consumer owners. Automatic taxonomy creation has the separate ownership rules below.

## Taxonomy lookup ownership

The complete category and parent-scoped subcategory lookup hooks use the paired local policy. Receipt item/template forms, the subcategory form/page, new-receipt line items and the uncategorized-items report own unavailable/retry feedback. Cached choices and selected historical labels remain visible during failure. Required category IDs do not become free-text IDs, and existing permissions for custom receipt labels are unchanged. Retrying a category-name lookup keeps the subcategory page and its dialogs mounted.

Automatic subcategory creation is owned by ReceiptItemForm submission and LineItemsSection add/edit selection. These consumers read all subcategories for the current parent, including inactive rows, while displaying only active choices. The complete result is necessary because inactive names still participate in the database uniqueness rule. A category lookup and its current parent-scoped subcategory lookup must both succeed and finish fetching before the UI can infer that a name is new. A pending or failed lookup permits valid manual receipt text but does not start a taxonomy write. A later retry never schedules deferred creation. A rejected automatic create reports its error without clearing the manual label; a delayed failure cannot erase a newer category or row draft. ReceiptItemForm catches only the awaited automatic-create rejection and stops that submission, leaving feedback to the mutation owner. Local presentation does not consume `mutateAsync` rejections, and React Hook Form rethrows rejected submit callbacks.

Other consumers retain their active-only lookup contract. Retry controls for scoped lookups require the current parent identity; switching parent cannot retry the old disabled query. This policy prevents automatic creation based on unavailable or incomplete lookup evidence; it does not make the client lookup and the server create transaction atomic against another user's simultaneous creation.

## Template catalog ownership

The shared item-template list uses local request/query presentation in the receipt item form, catalog page, canonical-template link dialog and command palette. Receipt description hints retain manual entry and template provenance rules. Catalog failure and no-data retry keep the page frame and create/edit dialogs mounted; unsuccessful reads do not claim an empty catalog. Queries pass their cancellation signal to the shared transport.

The link dialog keeps its search and selected ID but requires a successful, settled current search result containing the target before selection or confirmation. Raw/debounced search differences, failed reads and targets absent from the current page prevent a new link. Both the control and the handler enforce this rule; stale consequence text is hidden. Link mutation failures use the existing toast owner and keep the dialog open. Successful linking/consolidation behavior is unchanged; broader curation invalidation remains RECEIPTS-948.

Palette template errors and scope notices render outside cmdk's filtered result list so an unmatched term cannot hide recovery controls. Cached matches remain usable; an unavailable or loading template group cannot claim successful empty results. Retry requires an open palette with settled nonempty input. Other palette query families still require their own classification.

Palette template matching intentionally retains its existing name, description, category and entity-prefix tokens within the 500-row ceiling. The server's available `q` filter searches names only, so switching to it would remove existing matches. Extending server search is deferred until its contract preserves these tokens. When more rows exist than were loaded, the palette states the search scope. Remaining YNAB owners require separate adoption.

## Account and card lookups

The complete account/card lists and account-scoped card lists use paired local request and query policies. Single-account and multi-account observers share the same key and policy. Their owners retain drafts, selected rows, cached labels and IDs: account/card selection, card forms and lists, expandable account rows, transaction summaries, merge planning, and receipts-account choices in YNAB settings. Each owner exposes lookup failure and deliberate retry; a failed request is not an empty list. The multi-account map includes a successfully loaded empty list and omits an unknown list. Its memo signature preserves that distinction, and retry uses the current account IDs.

Ordinary transaction editing retains the RECEIPTS-853 membership contract. The latest successful scoped card data can disprove a selected pair, including after a later refresh failure. A historical pair whose list never loaded remains unknown and can still be submitted for server validation. Inactive current choices and manual values remain available.

A destructive merge requires successful, settled account/source-card reads and a successful preview before submission, in both existing-account and new-account modes. Both the control and actual submit handler enforce this. Quiet preview failure is an inline error with retry, not permission to continue. A returned mapping conflict remains a business result with its existing resolution flow. Switching to a new target includes every selected source account in the completeness check.

Account preparation and merge dispatch are separate operations. The dialog checks committed input ownership across each awaited create or rename, settles account/source reads, verifies complete source selection, and fetches the preview for the captured input before dispatching the merge. Closing, unmounting, changed inputs or failed prerequisites stop dependent work. A still-open dialog retains a prepared account for deliberate retry; a closed dialog cleans up its owned empty target, including late creation and failed merges after navigation. Cleanup errors retain their specific warning. A dispatched merge is allowed to finish without close cleanup deleting its target. These client checks do not make the requests atomic; the server still validates live data, and a browser crash can prevent cleanup.

Account/card create and update mutations use one cache-owned toast. Merge keeps caller-owned conflict handling and a single explicit non-conflict presenter. Raw account cleanup and rename suppress global transport presentation and retain their own feedback. Account/card delete hooks remain outside this adoption because their conflict presenters need separate classification.

YNAB account mapping also requires a successful, settled receipts-account lookup before a target change or removal. Both already-open menu items and their handlers enforce the rule. This does not establish completeness of the remaining remote-account or mapping queries; their adoption remains required.
## Receipt picker

Receipt-item forms use a dedicated bounded receipt picker rather than loading every receipt page before rendering. Opening the picker fetches 50 recent receipts. Scrolling near the list end or activating the keyboard-accessible load-more control fetches one additional page. A failed later page retains the loaded choices and exposes an explicit retry; no observer or empty viewport automatically drains the remaining history.

Search combines two scopes. Already loaded labels and raw ISO dates retain the existing local fuzzy matching. While unfiltered history remains incomplete, a debounced server query searches all receipt locations. The picker discloses this scope because the current API does not search dates in unloaded history. A successful remote empty result therefore does not claim that no receipt date matches outside the loaded pages. Once unfiltered history is complete, local matching is sufficient and remote search stops.

The current receipt ID and its detail query are independent of picker pages and search text. Selecting a loaded row may seed the explicit receipt-detail shape only when no detail is cached; the seed is stale so the locally owned detail query verifies it without enumerating history. A list row never overwrites newer detail data. Detail failure keeps the raw ID and every item field, exposes retry, and never invents an empty location. An explicit location prop retains precedence. A hidden receipt field issues no picker or detail request.

Browse, search and detail requests pair local transport and cache policies and consume cancellation signals. Query keys separate finite lists, picker browse pages, each normalized search and receipt detail shapes. Old search results may remain cached but cannot replace current options. These client pages are offset-based and deduplicate rows by ID; they do not establish a database snapshot against concurrent receipt insertion or deletion.
