# Migration: WidenReceiptItemQuantityAndUnitPricePrecision (RECEIPTS-770)

**Migration id:** `20260711221607_WidenReceiptItemQuantityAndUnitPricePrecision`
**Date:** 2026-07-11

## What it does

Widens two `receipts.ReceiptItems` columns from the money type `numeric(18,2)` to
`numeric(18,4)`:

| Column | Old type | New type | Why |
|---|---|---|---|
| `Quantity` | `numeric(18,2)` | `numeric(18,4)` | Quantity is a count/weight, not money. Under scale 2 Postgres silently rounded fractional quantities (e.g. `1.125` kg, `2.5` dozen) on insert. |
| `UnitPrice` | `numeric(18,2)` | `numeric(18,4)` | Unit prices legitimately need sub-cent precision (e.g. fuel at `3.459`/gal, per-gram produce). Scale 2 corrupted them on insert. |

`ApplicationDbContext.PrepareEntityTypesInModelBuilder` maps every `decimal` property to the
money type `decimal(18,2)`. That default is wrong for these two non-money / sub-cent columns. The
override lives in `ReceiptItemEntityConfiguration` (applied *after* `PrepareEntityTypesInModelBuilder`,
so it wins). `TotalAmount` and all other money columns are intentionally left at `decimal(18,2)`.

## Why it is safe

Widening scale `2 -> 4` is non-lossy: existing values have at most 2 fractional digits, and
`numeric(18,4)` preserves them exactly (precision 18 is unchanged, so no integer-digit loss). No
backfill or data migration is required.

## Rollback

`Down` narrows both columns back to `numeric(18,2)` — the exact inverse of `Up`. Note this is
lossy *by nature* of narrowing scale: any `Quantity`/`UnitPrice` value that was written with 3-4
fractional digits after this migration applied would be rounded to 2 places on rollback. That is
the correct symmetric restore of the prior type; pair a rollback with reverting the RECEIPTS-770
code change so the model and schema stay in sync.

## Related code

- Column-type override: `Infrastructure/Configurations/ReceiptItemEntityConfiguration.cs`.
- Root cause (money-type default for all decimals): `ApplicationDbContext.PrepareEntityTypesInModelBuilder`.

## Validation

Applied by the Testcontainers integration suite (`tests/Infrastructure.IntegrationTests`,
`Category=Integration`), which migrates a real PostgreSQL instance to HEAD before every test.
`ColumnTypeMappingTests.ReceiptItemEntity_FractionalQuantityAndSubCentUnitPrice_RoundTripWithoutTruncation`
inserts `Quantity = 1.125`, `UnitPrice = 3.4599` and asserts they round-trip exactly. CI does not
run integration tests, so this is validated locally against Docker Postgres.
