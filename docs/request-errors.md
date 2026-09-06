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

RECEIPTS-950 now covers receipt/status reads, receipt mutations and the optional suggestion families described below. Taxonomy and template catalogs, receipt pickers, account/card choices, remaining YNAB lists and diagnostics, and YNAB settings/actions still require complete shared-consumer ownership. Other report reads need explicit classification before claiming that every optional request is local. Suppressing a shared hook requires checking every consumer, so failed requests cannot become silent.

Backup import/export also use this local policy on the shared refresh/replay transport. Their hooks own transfer feedback while the page retains confirmation and file state. Both retain a five-minute transport deadline; see [Backup & restore](backup-restore.md). Error routes and render boundaries remain available throughout the remaining adoption.

## Optional receipt suggestions

Location history, item-code suggestions, similar descriptions, category recommendations and template-history candidates use the paired local read policy. All consumers own a visible error/retry state. Location history retains local MRU entries and cached API choices; failure never clears a typed location. A failed suggestion lookup leaves manual valid entry and cached choices usable. Successful empty results remain distinct from unavailable results.

Debounced suggestion hooks expose `isDebouncing` alongside their stable query fields. Error owners hide obsolete retry controls while the input is changing and enforce current length thresholds; category recommendations also require that no category has been chosen. This matters because imperative `refetch` bypasses `enabled`. A user may explicitly retry a valid item/description lookup while its popover is closed; visibility controls automatic lookup, not the validity of that manual read. The retry control lives beside the field and does not reopen the popover or change the typed value.

The query identities, debounce delays, location scoping and suggestion freshness remain unchanged. Query cancellation is passed through to the shared transport, including location lookup. Template history keeps its existing failure/retry and widening/focus behavior. Promotion's known template-created similarity exception remains intact.

Taxonomy and template catalogs, receipt pickers, account/card choices and remaining YNAB settings/actions still require their complete shared-consumer owners. This suggestion phase does not classify those failures or authorize automatic taxonomy creation after a failed lookup.
