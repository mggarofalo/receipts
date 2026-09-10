# Domain

Core domain models with zero dependencies on other layers.

## Structure

- **`Core/`** — Entity classes: `Account`, `Receipt`, `ReceiptItem`, `Transaction`, `Category`, `Subcategory`, `ItemTemplate`, `Adjustment`
- **`Aggregates/`** — Composite domain objects: `ReceiptWithItems`, `TransactionAccount`, `Trip`
- **`Money.cs`** — Value object for monetary amounts (`decimal Amount` + `Currency Currency`)
- **`ValidationWarning.cs`** — Soft validation warnings returned alongside successful responses

## Conventions

- **ID convention:** All entities use `Guid` as their primary key type. `Guid.Empty` is the sentinel value for new (unsaved) entities.
- **Construction checks:** Constructors reject invalid admission values, but entity properties remain mutable for mapping and transfer. Application/API validators and named persistence operations own write policy; do not treat these classes as immutable aggregates.
- **No framework dependencies:** Domain has no references to EF Core, ASP.NET, or any infrastructure concern.
- **Money representation:** `Money` pairs `decimal` with a currency and provides arithmetic operators. Supported inputs are currently USD; the operators carry the left operand's currency and do not enforce mixed-currency safety.

Receipt-item category and subcategory values are historical string snapshots. Their tables
provide suggestions rather than canonical foreign keys; see
[Category snapshots](../../docs/category-snapshots.md). Receipt balance is checked for
transaction create/update and for complete-receipt creation when transactions are present,
not after delete, restore, or every unrelated child edit; see
[Receipt arithmetic](../../docs/receipt-arithmetic.md).
