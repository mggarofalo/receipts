# Request error presentation

`request-error-policy.ts` makes error presentation explicit while keeping one API client and one session-owned QueryClient. The default is `global`: HTTP 5xx responses use the existing error bridge, which opens the first-error route and toasts later operations. QueryCache and MutationCache present other default failures. They skip HTTP 5xx because the bridge already owns them. The default query policy also stops automatic HTTP 5xx retries, which would otherwise present the same operation again. Network retries retain their existing default.

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

Request policy is held in a WeakMap keyed by the original Request. It adds no wire header and survives native 401 refresh/replay because response middleware still receives that original request. A future middleware that replaces Request objects must preserve both presentation and session bindings. Local presentation preserves authentication, password-change handling, cancellation, normalization and typed failures. It never turns a failed request into successful empty data. A manually manufactured HTTP 5xx error inside a cache operation also needs an explicit local presenter, because the default cache treats HTTP 5xx as transport-owned.

## Current feature owners

Receipt detail and inline receipt reads own their error UI. Only an actual 404 means not found. Unavailable, network and access failures offer retry; a failed background refresh retains the loaded receipt and editor draft. Login retains generic incorrect-credential wording for 401 and presents 429, server, network and timeout failures with retry guidance. It does not invent a Retry-After duration.

The local YNAB reads are connection status, selected budget, receipt sync statuses, transaction sync status and split comparison. Their shared consumers show unavailable states and retry rather than treating missing/error projections as unconfigured or unsynced. Receipt list/detail chips distinguish loading and unavailable status; retained statuses are labelled last known. An unknown sync status prevents a new push until it can be checked. A cached receipt integration section stays mounted during prerequisite failure, with actions disabled while its selected target cannot be verified. Split-comparison errors stay within the comparison section. Portaled dialog and select actions need their own disabled state and handler guards: neither a disabled fieldset nor a disabled Select trigger reliably disables an already-open portal. Preserve the user’s choices and the close affordance while preventing a new operation against an unavailable target. Deferred mutation callbacks must also check the latest committed availability before launching a follow-up write; a successful earlier resolution is still kept.

The transaction sync-status endpoint's documented 404 means no record and may resolve null. Other failures remain errors. HTTP 200 business outcomes such as partial or failed pushes retain their existing result handling; presentation policy does not change destination ownership or retry safety.

## Remaining adoption

This first RECEIPTS-950 step establishes the contract and receipt/status consumers. Budget/account/category lists, mapping and stale-mapping diagnostics, rate-limit/status/event panels, and YNAB select/mapping/memo/resolve/push/bulk actions retain their existing global policy until their visible local owners are completed in the next focused step. The non-YNAB report and editor suggestion reads also need explicit classification before claiming that every optional request is local. Suppressing a shared hook requires checking every consumer, so failed requests cannot become silent.

Backup import/export also use this local policy on the shared refresh/replay transport. Their hooks own transfer feedback while the page retains confirmation and file state. Both retain a five-minute transport deadline; see [Backup & restore](backup-restore.md). Error routes and render boundaries remain available throughout the remaining adoption.
