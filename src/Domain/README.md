# Domain

Core domain models with zero dependencies on other layers.

## Structure

- **`Core/`** — Entity classes: `Account`, `Receipt`, `ReceiptItem`, `Transaction`, `Category`, `Subcategory`, `ItemTemplate`, `Adjustment`
- **`Aggregates/`** — Composite domain objects: `ReceiptWithItems`, `TransactionAccount`, `Trip`
- **`Money.cs`** — Value object for monetary amounts (`decimal Amount` + `Currency Currency`)
- **`ValidationWarning.cs`** — Soft validation warnings returned alongside successful responses

## Conventions

- **ID convention:** All entities use `Guid` as their primary key type. `Guid.Empty` is the sentinel value for new (unsaved) entities.
- **Transfer-oriented entities:** Constructors enforce timeless structural checks, but properties remain mutable for mapping and transfer. Application/API validators and named persistence operations own admission and write policy; do not treat these classes as immutable aggregates.
- **No framework dependencies:** Domain has no references to EF Core, ASP.NET, or any infrastructure concern.
- **Money representation:** `Money` pairs `decimal` with a currency. Addition and subtraction require matching currencies. Multiplication and division by `decimal` are scalar operations that return `Money`; dividing one `Money` by another matching-currency value returns a dimensionless ratio. Supported inputs remain USD-only.

Receipt and transaction constructors accept any stored `DateOnly`, so hydrating historical
rows never depends on the machine clock. Create/update command validators and HTTP request
validators apply the shared `AdmissionDatePolicy`, whose `TimeProvider` defines the local
calendar boundary. Future workers must use the same application commands or policy.

Receipt-item category and subcategory values are historical string snapshots. Their tables
provide suggestions rather than canonical foreign keys; see
[Category snapshots](../../docs/category-snapshots.md). Receipt balance is checked for
transaction create/update and for complete-receipt creation when transactions are present,
not after delete, restore, or every unrelated child edit; see
[Receipt arithmetic](../../docs/receipt-arithmetic.md).
