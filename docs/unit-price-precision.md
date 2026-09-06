# Unit-price precision

Template default prices and receipt-item unit prices use PostgreSQL `numeric(18,4)`. Receipt-item quantities also use four decimal places. Receipt totals, tax, adjustments and payments retain their two-decimal storage policy.

Unit-price inputs accept up to four decimal places. Price displays retain sub-cent digits, while totals display cents. Selecting a template copies its default price unchanged; focusing and leaving that input must not round it to cents. A template price of `3.459` therefore remains `3.459` when used for a receipt item.

The API continues to use JSON numbers. This change preserves supported ordinary four-decimal prices; it does not introduce an arbitrary-precision transport. JavaScript numbers and the existing double API DTOs cannot represent every value available in an 18-digit database column. Exact line-total and balance calculation policy is separate from storage and input precision.

## Migration and rollback

Changing template price storage from `numeric(18,2)` to `numeric(18,4)` preserves existing cent values within the new range. It reduces integer capacity: the absolute value must remain below `100000000000000`. PostgreSQL first rounds to the declared scale, then checks integer capacity; see its [numeric type documentation](https://www.postgresql.org/docs/18/datatype-numeric.html#DATATYPE-NUMERIC-DECIMAL).

The migration locks the template table and counts incompatible values, including soft-deleted rows, before changing the column. Any incompatible value stops the migration. The diagnostic count is emitted through the migration runner. No row is rounded, clamped or removed automatically. Inspect those values and choose a correction before retrying; the deployment does not silently reconcile historical prices.

Rollback also locks and checks the table. It refuses to narrow the column while any active or soft-deleted template has a sub-cent default. Prefer a forward fix when retaining that data. If rollback is necessary, explicitly reconcile those prices first; the migration does not discard their precision on the operator's behalf.

Prices rounded by an earlier application version cannot be reconstructed from the rounded value. Restore them from a trustworthy source if needed.

## Portable backups

Portable backups already store template prices as invariant decimal text. No format-version change is needed. Export and restore preserve four-decimal values for both new and existing templates.

A legacy backup may contain a template price that fit the former column but cannot fit the current one. Import rejects that price with an actionable error and rolls back the entire import. It never clamps the price or leaves earlier imported rows committed.
