# YNAB memo matching

`YnabMemoSyncService` owns automatic receipt-link matching. A single receipt and a bulk request use the same operation: capture the selected budget and account mappings, load local payments and existing bindings, fetch remote candidates once per date, plan assignments, then update permitted memos. Date-filtered responses do not advance the complete-budget server-knowledge cursor.

## Automatic identity

A local payment's account comes from `Transaction.Card.AccountId`. Exactly one mapping must connect that account to a nonempty remote account ID in the captured budget. Cards sharing a local account share its mapping. Missing card data, absent mappings and contradictory mappings fail closed.

Candidates must match that remote account, the payment date and the negated milliunit amount. Reconciled candidates are excluded. Automatic assignment also requires exactly one candidate passing the existing payee comparison (normalized equality, containment or trigram similarity at the existing 0.3 threshold). Blank payees and a sole date/amount match without sufficient payee identity require explicit confirmation. Fuzzy payee comparison remains a heuristic, so a successful match is not proof of merchant identity.

The existing USD-only and already-synced guards remain, but they apply only to the captured budget. A record for another budget is preserved as history and is not evidence that the payment is synced to the selected budget. The selected destination can create its own record for the same payment and operation type.

## Ownership within an operation

Before choosing any target, the service reserves all nonempty same-budget target IDs in participating payments' memo-update and transaction-push records, including pending and failed records. A later already-synced input therefore protects its target from an earlier unsynced input. Repeated receipt and payment IDs are processed once.

All confident assignments are planned before the first write. If two unbound local payments select the same remote transaction, both require explicit confirmation; input order does not choose a winner. A payment cannot automatically replace its existing target binding. Removing a reserved candidate does not promote a previously ambiguous alternative into an automatic match.

Reservations are established before both the remote PATCH and the existing-receipt-link shortcut, which can create a local binding without a PATCH. They remain reserved through failures and uncertain acknowledgements for the rest of that operation.

## Explicit confirmation and recovery boundary

The existing resolution flow lets the user choose a remote transaction explicitly and retains the reconciled-transaction guard. The dialog supports one candidate as well as several. Automatic account/payee matching does not silently override that deliberate selection. Neither path overwrites another budget's historical record.

These reservations are in memory and cover participating payments in one request. They do not establish exclusive ownership across concurrent requests or all historical payments. Destination-scoped records are enforced by the database and service lookups; durable claims, retries and uncertain remote-write recovery remain tracked in RECEIPTS-962. This matching flow does not claim exactly-once delivery.

Tests use controlled services and fake HTTP responses, with no real YNAB writes. They assert outcomes and outbound target IDs for account mismatches, insufficient payee identity, competing payments, retained bindings and legitimate matches.
