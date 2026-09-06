# Receipt arithmetic

The client uses `src/client/src/lib/receipt-arithmetic.ts` for receipt line previews, section sums and balance calculations. Each line multiplies its quantity and unit price as decimal operands, then rounds to cents with midpoint ties away from zero. The subtotal sums those rounded lines. Two lines of `0.5 × 2.01` therefore produce `1.01 + 1.01 = 2.02`.

Tax, adjustments and payments are added as decimal amounts without rounding individual terms again. Convert operands before arithmetic: wrapping a binary product or an already accumulated JavaScript sum in a decimal object cannot undo its rounding error.

## Ownership

The API mapper remains authoritative for new line totals. Stored receipt subtotals and expected totals remain authoritative when reading a receipt; the client does not replace them with a reconstructed sum of displayed rows. Historical or imported line totals can differ from a fresh calculation within the domain's accepted tolerance. This change does not rewrite that history.

The helper keeps a private decimal.js constructor with 40 significant digits and explicit midpoint rounding. It does not change global library configuration or expose Decimal objects through component props. Forty significant digits cover multiplication of the supported stored operands; this setting is separate from the explicit two-decimal line rounding. See the [decimal.js API](https://mikemcl.github.io/decimal.js/) for independent constructors and rounding operations.

Inputs and API responses remain JavaScript/JSON numbers. Decimal arithmetic prevents additional binary error during calculation; it cannot recover digits already lost in transport. The existing .NET double-to-decimal mapping can also round near 15 significant digits. This is not an arbitrary-precision JSON contract. See [Unit-price precision](unit-price-precision.md) for supported input/storage precision and migration limits.

## Balance decisions

| Decision | Rule |
| --- | --- |
| New-receipt submission | Finite totals with an absolute difference of at most `0.01`, matching the backend. |
| Persisted receipt discrepancy indicator | An absolute difference of at least `0.005` remains visible. A tolerated one-cent discrepancy can still be reconciled. |
| Reconciliation adjustment | Payment total minus expected total, rounded to cents with midpoint ties away from zero. A zero adjustment means no reconciliation is needed. |

Both new-receipt submit buttons use the same comparison helper. Invalid or nonfinite values cannot enable submission or reconciliation. Existing entry labels retain their direction: item totals above entered payments are “Over by”; entered payments above the item total leave a “Remaining” balance.

This calculation change preserves the existing no-transaction form behavior and the backend's transaction-write-time balance scope. It does not add aggregate-wide enforcement to unrelated edits.

## Verification

`test-data/receipt-arithmetic.json` contains literal expected values consumed by frontend and backend tests. The cases cover per-line midpoints, four-decimal operands, repeated fractional amounts, tax, signed adjustments and tolerance boundaries. The actual new-receipt page regression includes both submit buttons and the actual balance sidebar; its child input injection is a page-level fixture, not an end-to-end backend submission.

Configured API/PostgreSQL acceptance and the Aspire creation-form journey are verified separately. Test evidence must distinguish those workflows rather than treating a mocked page fixture as a full application journey.
