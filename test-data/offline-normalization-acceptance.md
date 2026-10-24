# Synthetic offline round trip oracle

`offline-normalization-acceptance.json` defines independent gold for
`named1026offlinebackuproundtrip`. It contains only invented public facts. Keep
`gold`, `followups` and this document out of the blinded offline review packet.
Freeze this file's hash before running the normalizer. Reviewers receive A and
the raw synthetic source facts, not expected assignment lists. Compare their
reviewed decisions with this oracle only after decisions are frozen.

## Arrange through supported routes

Use the existing task-owned Aspire harness only after the final candidate is
integrated. Record candidate SHA, dirty-state evidence, actual app endpoints and
portable format. Never launch this test against the owner's installation.

1. Authenticate as the synthetic admin. POST `/api/accounts` using `account.request`;
   POST `/api/cards` using `card.request` plus returned `accountId`. POST
   `/api/item-templates` using `template.request`. Capture response IDs privately.
2. At least r1 must be created through `/receipts/new` in the real browser. Fill
   receipt, payment and item facts exactly; use the source preview/product controls
   for the explicit fragment decisions below. Retain UI evidence and network
   response bindings. Verify whitespace round trips; if browser controls trim r5,
   enter r5 via the actual API and compare literal response text.
3. For every item having `fragmentName`, preview its actual source through POST
   `/api/receipt-items/normalization-preview` with
   `{source:{location,description,itemCode:receiptItemCode},query:fragmentName,offset:0,limit:50}`.
   Submit `normalizationIntent:{kind:"confirmNew",clientRowId:<new uuid>,sourceFingerprint:<returned>,newName:fragmentName,rememberSource:false}`.
   This deliberately creates reviewed but fragmented catalog entries through the
   supported entry workflow; it does not bypass persistence. All other initial
   rows omit intent, including the template provenance row, and remain NeedsReview.
   Assert the fragments actually exist separately before export. Do not weaken
   this precondition if the app rejects a fragment: report the case as blocked.
4. API complete entry is POST `/api/receipts/complete` with
   `{clientRequestId:<new stable uuid>,receipt:{location,date,taxAmount},transactions:[{amount:transactionAmount,date,cardId:<bound card>}],items:[<actual CreateReceiptItemRequest fields>],adjustments:[<actual adjustment fields>]}`.
   Strip fixture-only `key`, `entry`, `fragmentName`, and `itemTemplateKey` fields;
   resolve the last to actual `itemTemplateId`. No currency property exists in
   these entry DTOs; the current domain currency is USD.
5. r4 additionally exercises POST `/api/receipts` with `{location,date,taxAmount}`;
   POST `/api/receipts/{receiptId}/receipt-items` once per item with
   `{description,receiptItemCode,quantity,unitPrice,category}`; create its payment
   through POST `/api/receipts/{receiptId}/transactions` using the transaction DTO.
   Check that parent linkage is correct in API and real PostgreSQL. If this route
   changes in the integrated candidate, check its controller before execution.
6. Record response IDs and every factual field independently from normalization
   decisions. r6's template link must confer no initial trust. r8 has zero items,
   one negative `other` adjustment and one negative payment; if aggregate balance
   policy rejects it, report the precise supported-entry gap rather than inserting
   database rows. The gold deliberately does not prescribe generated UUIDs.

## Export, offline review and exact artifact restore

POST `/api/backup/export`; retain byte-identical A and SHA-256, and freeze source
data. Capture independent baseline bindings and raw monetary facts. Copy A to B;
review and mechanically edit B directly using the production offline pipeline.
Never restore A into another app for normalization. The independent verifier must
compare B with A and this oracle, including every parent ID and all raw facts,
before importing. The desired business states use API enum spellings
`needsReview`, `verified`, `excluded`; archive/PG storage may use other encodings.

Restore the exact B via POST `/api/backup/import` multipart field `file` into both
the still-frozen source and a fresh isolated real PostgreSQL installation. Hash B
before each import and retain success/error bodies. Import twice on each target;
compare semantic rows and counts, not importer activity counters. Check SQLite
integrity and foreign keys before import, real PostgreSQL relationships afterward,
and the actual portable review/source/revision evidence contract. Keep external
operations inspect-only. Restart only after the frozen source restore checks;
the existing harness uses ephemeral container filesystems, so do not destroy its
database while trying to test application-process restart.

API and browser checks must show original raw text and reviewed names, receipt
links in date-ordered beef price history, exact five trusted report groups, three
scoped reusable rules, four unresolved lines and the non-product fee separately.
Search raw `FRNK`, reviewed `Acme Beef Franks 12 oz`, and ambiguous `HOT DOG`;
record exact result item/receipt IDs, not just nonempty results. Each r3 HOT DOG
line remains unresolved even though its source is identical; price alone cannot
identify protein, brand or pack. The r4 HOT DOG text must not acquire cross-store
trust. Excluded spend remains part of financial reconciliation and is never
counted as normalized product coverage.

Only after both restores pass, create each `followups` row as a new receipt with
zero tax, one payment equal to quantity*unitPrice and Groceries category. Omit
intent to exercise mapping reuse; resolve the template provenance on oneoff-repeat.
Use current API preview `knownSource` intent if the UI requires explicit acceptance
of its current mapping; echo actual mapping/target revision and source fingerprint.
For NeedsReview cases, a provisional candidate link is allowed; no verified
assignment or reusable rule is allowed. Compare the exact after-followup totals.

## Independent sensitivity and complementary coverage

On private corrupt copies only, force one turkey assignment into beef12: semantic
gold must fail even with unchanged money. Change a raw price by 0.01: factual and
financial baseline checks must fail. These sensitivity probes do not establish
import validation; separately assert rejection/rollback for dangling mappings,
bad fingerprints/revisions, unsupported evidence/currency and truncated files.
Exercise edit, revoke/reject, merge, delete/restore and unavailable-provider flows
in separate mutable-phase receipts, snapshotting totals before and after each.
Do not mix those receipts into this frozen eight-receipt oracle or its seven
followups. Real legacy v6 signed-line/pre-policy trust preservation belongs to the
genuine v6 rehearsal and cannot be claimed from this current-entry corpus.

No runtime, normalization, import, browser action or acceptance gate has been
executed by authoring these files.
