# Architecture

This is a .NET 10 Clean Architecture solution for a receipt management application. It uses central package management via `Directory.Packages.props`.

## Layer Structure

- **Common** - Shared utilities, extension methods, and configuration variable constants
- **Domain** - Core domain models with no dependencies on other layers
  - `Core/` - Entity classes (Account, Receipt, ReceiptItem, Transaction, Category, Subcategory, ItemTemplate)
  - `Aggregates/` - Composite domain objects (ReceiptWithItems, TransactionAccount, Trip)
- **Application** - Business logic using CQRS pattern with [martinothamar/Mediator](https://github.com/martinothamar/Mediator)
  - `Behaviors/` - Mediator pipeline behaviors (e.g., `ValidationBehavior`)
  - `Commands/{Entity}/Create|Update|Delete/` - Command + Handler pairs for write operations
  - `Queries/Core/{Entity}/` - Query + Handler pairs for read operations
  - `Queries/Aggregates/` - Complex queries joining multiple entities
  - `Interfaces/Services/` - Service interfaces implemented by Infrastructure
- **Infrastructure** - Data access with PostgreSQL via EF Core
  - `Entities/` - Database entity classes (separate from Domain)
  - `Repositories/` - Repository pattern implementation
  - `Services/` - Service implementations (audit logging, embeddings, similarity search)
  - `Mapping/` - Mapperly mappers (Domain <-> Entity)
- **Presentation**
  - **API** - ASP.NET Core Web API with SignalR hub for real-time updates
    - `Controllers/Core/` and `Controllers/Aggregates/` - REST endpoints
    - `Mapping/` - Mapperly mappers (Domain <-> generated DTOs)
    - `Generated/` - NSwag-generated Request/Response DTOs from OpenAPI spec
    - `Validators/` - FluentValidation validators (business rules only; spec-expressible constraints use DataAnnotations)
    - `Configuration/` - Service registration extension methods
    - `Hubs/ReceiptsHub.cs` - SignalR hub
  - **Client** (`src/client/`) - React/Vite SPA (TypeScript, React Router 7, TanStack Query, Tailwind CSS, shadcn/ui)
- **AppHost** (`src/Receipts.AppHost/`) - .NET Aspire orchestration (API + PostgreSQL + React dev server)

## Key Patterns

- **CQRS**: Commands and Queries are separate with dedicated handlers
- **Mediator Pattern**: martinothamar/Mediator dispatches commands/queries to handlers via source-generated dispatch (no runtime reflection)
- **Validation Pipeline**: `ValidationBehavior<TMessage, TResponse>` intercepts Mediator requests and runs registered `IValidator<T>` instances before handlers execute. Application owns its validator registration. `FluentValidationActionFilter` validates controller DTOs and every collection element before action dispatch. Generated schema constraints and FluentValidation failures share the 400 problem contract. See [validation ownership](api-guidelines.md#validation-ownership).
- **Repository Pattern**: Infrastructure repositories abstract EF Core
- **Mapping**: Mapperly handles Domain <-> Entity (Infrastructure) and Domain <-> generated DTOs (API)
- **Service Registration**: Each layer has a static extension method (`RegisterApplicationServices`, `RegisterInfrastructureServices`) for DI setup
- **Soft Delete**: Entities support soft delete with restore capabilities and trash management
- **Audit Logging**: All mutations are logged with user/API key attribution

Application validators are registered from the Application assembly; API DTO validators
are registered by Presentation and traverse every batch element. Error middleware is a
fallback for bodiless framework failures, not a replacement for endpoint-owned RFC 9457
responses. See [API validation ownership](api-guidelines.md#validation-ownership) and
[request error ownership](request-errors.md).

### Receipt use-case ownership

Complete receipt creation is the bounded pilot for application-owned write policy and
atomic persistence ports. The receipt list uses an explicit read projection rather
than a generic entity-shaped service path. See [Receipt use-case boundary](receipt-use-case-boundary.md)
for the entry point, policy, commit, adapter, notification, and projection owners.

### Adjustment Entity

The `Adjustment` entity captures receipt-level monetary adjustments (tips, discounts, coupons, rounding):

```csharp
public class Adjustment
{
    public Guid Id { get; set; }
    public Guid ReceiptId { get; set; }
    public AdjustmentType Type { get; set; }  // Tip, Discount, Rounding, Coupon, etc.
    public Money Amount { get; set; }          // Signed: +tip, -coupon
    public string? Description { get; set; }   // Required when Type == Other
}
```

The balance equation enforced across receipts:

```
sum(item.TotalAmount) + Receipt.TaxAmount + sum(adjustment.Amount) == sum(transaction.Amount)
```

### Validation Tiers

- **Hard invariants** (reject if violated): Balance equation, non-negative prices, line-item totals within rounding tolerance
- **Soft invariants** (warn, don't reject): Tax reasonableness (0–25%), adjustment reasonableness (<10% of subtotal), date consistency

See the [Correctness Hardening module](https://plane.wallingford.me/dev/projects/aaac8dc9-bc4c-42db-ac99-eee7864c78e9/modules/1addfa25-4ce8-44f7-b9e7-44b3d3a27d69) in Plane for the full design history.

### Role management and authorization revocation

`IRoleManagementService` owns Add, Remove, and Replace operations for existing users. Both role routes and the user profile PUT use this operation. A profile supplied with replacement roles participates in the same transaction, so a rejected role change cannot leave a partial profile update.

The PostgreSQL implementation locks the shared Admin role row before reading membership and making policy decisions. All three operations use that coordinator; locking only the target user would allow two administrators to demote each other concurrently. Every Identity result is checked, and effective membership changes and the security-stamp update commit together. No-op membership changes preserve existing sessions. Disabling an account still rotates its stamp and revokes its API keys.

Both routes require an unambiguous authenticated administrator subject and prohibit removing that caller's own Admin role. When a JWT and API key both authenticate different users, these operations reject the request; one identity's subject cannot be combined with another's administrator authority. Role changes also preserve at least one Admin membership. This is a membership guarantee: the existing account disable/deactivate policies remain separate, and the role operation does not promise that an administrator is currently unlocked or enabled. Initial role assignment remains part of the separate user-creation flow.

After a role change, an old JWT fails the existing per-request stamp check with 401. A fresh JWT contains current roles. API keys continue reading current roles on every request: demotion leaves the key usable for ordinarily authenticated endpoints but denies Admin endpoints with 403. Role changes do not revoke the key itself. Policy/Identity validation failures return 400 ProblemDetails; concurrency conflicts return 409 ProblemDetails; missing users remain bodiless 404 responses.

## Database

PostgreSQL with EF Core + pgvector extension. Connection configured via environment variables:
- `POSTGRES_HOST`, `POSTGRES_PORT`, `POSTGRES_USER`, `POSTGRES_PASSWORD`, `POSTGRES_DB`

The API does not self-migrate. Aspire and deployment orchestration run `src/Tools/DbMigrator` before starting it. The tool delegates to `IDatabaseMigratorService`, which surfaces PostgreSQL migration notices in the deployment log.

### Unit-price precision

Template defaults and receipt-item unit prices retain four decimal places; reconciled money totals retain cents. See [Unit-price precision](unit-price-precision.md) for input/display behavior, guarded migration and rollback, and portable-backup compatibility.

### Transaction account ownership

`Transaction.CardId` is the stored relationship. All transaction account reads resolve through `Card.AccountId`, including receipt filters, dashboards, YNAB mapping, account deletion guards and trashed history. Reassigning a card deliberately moves that history to its new parent account. Account merges move cards and their integration mappings; they count affected transactions for semantic audit records without rewriting transaction rows.

Create/update requests accept the card, amount and date. Response `accountId` remains available as a derived value. Infrastructure read mapping requires a loaded or projected card, including responses from ordinary, balance-guarded and complete-receipt creation. A domain response's `AccountId` is ignored on persistence writes.

`CARD_CHANGE_QUERY_KEYS` owns the browser dependencies of card edits and merges. Local mutations and remote card notifications use the same list to refresh transaction/trip account data, dashboard account aggregates, YNAB split comparisons and account-filtered receipt lists. Inactive cached transaction and trash queries become stale too. A no-op merge retains its existing no-invalidation behavior.

The `DropTransactionAccountId` migration locks both tables, checks agreement across active and trashed transactions, and rejects any divergence with its count before removing the redundant column. Its downgrade reconstructs the account from each card. Portable backups retain their existing card identities and require no format change for this migration.

### Transaction and audit ownership

All synchronous and asynchronous `ApplicationDbContext.SaveChanges` overloads use the same persistence policy. Automatic audit rows are prepared using the tracked entities' assigned IDs, then saved with business changes in one base EF save. Temporary database-generated keys on audited entities are unsupported and fail before writing. Description reconciliation participates in the same transaction. PostgreSQL is the production guarantee; the InMemory test provider cannot establish transactional atomicity.

Without an existing transaction, the context owns and commits a transaction around the entire save. Within a caller-owned transaction, it uses a savepoint and leaves commit/rollback ownership with the caller. Unsupported ambient/enlisted transactions and caller transactions without savepoints fail before writing. Existing role, merge, import, and balance operations retain their transaction boundaries. Retrying execution strategies must coordinate the whole transaction explicitly; the application does not enable automatic retries for this pipeline.

The tracker accepts changes only after the entire save succeeds. `acceptAllChangesOnSuccess: false` leaves caller-owned changes pending; internal automatic audit entries are detached so they cannot leak into later saves. Failure also detaches only the automatic audit rows from that attempt. Caller-added semantic audits and business entries remain owned by the caller. The return count excludes automatic audit rows, preserving the prior business-save count.

A failure before commit rolls back business changes, mandatory audits, and description reconciliation together. Cancellation during rollback does not reuse the cancelled request token. A lost connection or cancellation during COMMIT can have an unknown outcome: reconcile persisted state before retrying; this is not an exactly-once delivery guarantee. If rollback itself fails, discard the context and transaction.

Description-change notifications are wake-up hints. An owned transaction emits its hint after commit. A caller-owned transaction may emit a hint after a successful savepoint while its outer transaction is still pending; the resolver's periodic scan remains the recovery mechanism after delayed commit or rollback.

The boundary follows [EF Core transaction and savepoint behavior](https://learn.microsoft.com/en-us/ef/core/saving/transactions).

### Vector Similarity Search

The system uses pgvector for semantic similarity search on item names and descriptions. Embeddings are generated locally via ONNX Runtime using the `bge-large-en-v1.5` model (1024-dimensional vectors, CLS pooling). No external API keys are required.

The 1.34 GB model is deliberately not shipped in the container image or copied into build output — doing so made the image 2.54 GB, most of it three copies of the same file (RECEIPTS-929). It is fetched once into a persistent directory and loaded from there.

- **`EmbeddingModelProvisioningService`** — Background service that downloads the model on first start if absent, verifying size and SHA-256 against a pinned upstream revision before the file is moved into place. Failures are logged and retried, never fatal. Configured via `Embeddings__ModelPath` (the container points it at `/data/models`, so budget ~1.4 GB of volume space) and `Embeddings__AutoDownload`
- **`OnnxEmbeddingService`** — Singleton service that loads the ONNX model and tokenizer lazily on first use, generates 1024-dim L2-normalized embeddings. Reports `IsConfigured == false` while the model is still missing, which every consumer already handles by degrading rather than failing
- **`EmbeddingGenerationService`** — Background service that polls every 30s, generates embeddings for new/changed ItemTemplates and ReceiptItems in batches of 50
- **`ItemTemplateSimilarityService`** — Hybrid search combining trigram similarity (0.4 weight) and cosine vector similarity (0.6 weight) with HNSW indexing

## Test Project Structure

Tests mirror src structure. `SampleData` provides reusable domain and persistence
fixtures without depending on the API host. API-generated request fixtures live in
`Presentation.SampleData`, so lower-layer test builds do not pull in presentation
startup or contract generation.

```
tests/
  Common.Tests/
  Domain.Tests/
  Application.Tests/
  Infrastructure.Tests/
  Presentation.API.Tests/
  SampleData/
  Presentation.SampleData/
```

## Object Mapping with Mapperly

> **See also:** [docs/coding-standards.md](coding-standards.md#mapperly-rules) for the concise rule list. Both documents should stay in sync — code examples live here, rules live there.

This project uses [Mapperly](https://github.com/riok/mapperly) for compile-time object mapping. Mapperly was chosen over AutoMapper for:
- Zero licensing costs (Apache 2.0 vs AutoMapper's commercial license)
- 8.61x faster performance (no reflection)
- Compile-time safety (mapping errors caught during build)
- Debuggable generated code

### Mapperly Patterns

**Basic Mapper Structure (Domain <-> generated DTOs):**
```csharp
[Mapper]
public partial class AccountMapper
{
    [MapperIgnoreTarget(nameof(AccountResponse.AdditionalProperties))]
    public partial AccountResponse ToResponse(Account source);

    public Account ToDomain(CreateAccountRequest source)
    {
        return new Account(Guid.Empty, source.AccountCode, source.Name, source.IsActive);
    }

    public Account ToDomain(UpdateAccountRequest source)
    {
        return new Account(source.Id, source.AccountCode, source.Name, source.IsActive);
    }
}
```

**Value Object Decomposition (Money -> decimal + Currency):**
```csharp
[Mapper]
public partial class ReceiptMapper
{
    // Flatten Money value object to separate fields
    [MapProperty(nameof(Receipt.TaxAmount.Amount), nameof(ReceiptEntity.TaxAmount))]
    [MapProperty(nameof(Receipt.TaxAmount.Currency), nameof(ReceiptEntity.TaxAmountCurrency))]
    public partial ReceiptEntity ToEntity(Receipt source);

    // Reconstruct Money value object from separate fields
    private Money MapTaxAmount(decimal amount, Currency currency) => new(amount, currency);

    public partial Receipt ToDomain(ReceiptEntity source);
}
```

**Receipt update ownership:**

`ReceiptRepository.UpdateAsync` and `ReceiptItemRepository.UpdateAsync` assign an explicit set of editable fields to tracked rows. A domain-to-entity mapping is not a complete replacement for a stored row: image paths, normalization metadata, parent identity, and deletion metadata have separate owners. Adding a persistence field must not silently make it writable through an ordinary edit.

Receipt edits own location, date, and tax amount/currency. `UpdateImagePathsAsync` owns the original and processed image paths. Item edits own the item code, raw description, quantity, prices/currencies, category, and subcategory; each item retains its stored `ReceiptId`, including batches spanning multiple receipts. Categories and subcategories remain historical string snapshots.

An item edit with an identical raw description preserves both `NormalizedDescriptionId` and `NormalizedDescriptionMatchScore`, including curated links and null scores. Any ordinal text change, including case or whitespace, clears both fields in the same item update. The existing background resolver can then classify the new description; while unavailable, the item remains unresolved. A null score does not identify manual curation, so it is not used to infer provenance. Explicit normalization operations retain ownership of assigning and changing canonical links.

**Ignoring Navigation Properties:**
```csharp
[MapperIgnoreTarget(nameof(ReceiptItemEntity.Receipt))]
[MapperIgnoreTarget(nameof(ReceiptItemEntity.ReceiptId))]
public partial ReceiptItemEntity ToEntity(ReceiptItem source);
```

**Aggregate Mappers with Nested Objects:**

When mapping aggregates that contain nested objects with value object decomposition, create manual mapping methods that delegate to the appropriate Core mappers:

```csharp
[Mapper]
public partial class ReceiptWithItemsMapper
{
    private readonly ReceiptMapper _receiptMapper = new();
    private readonly ReceiptItemMapper _receiptItemMapper = new();

    public ReceiptWithItemsResponse ToResponse(ReceiptWithItems source)
    {
        return new ReceiptWithItemsResponse
        {
            Receipt = _receiptMapper.ToResponse(source.Receipt),
            Items = source.Items.Select(_receiptItemMapper.ToResponse).ToList()
        };
    }
}
```

**Note:** Don't use `[UseMapper(typeof(...))]` - it doesn't work as expected. Instead, instantiate mapper dependencies as fields and call them explicitly.

### Testing with Mapperly

Use concrete mapper instances in tests instead of mocks:

```csharp
// GOOD: Use actual mapper
private readonly AccountMapper _mapper = new();
private readonly AccountService _service;

public AccountServiceTests()
{
    _service = new AccountService(_mockRepository.Object, _mapper);
}

// BAD: Don't mock mappers
Mock<IMapper> mapperMock = new();
```

Benefits:
- Tests use actual mapping logic (more realistic)
- No need to set up mock behaviors
- Catches mapping errors in tests
- Simpler test setup

## YNAB memo matching ownership

`YnabMemoSyncService` plans automatic matches using the selected budget, the authoritative card account and operation-wide target reservations. See [YNAB memo matching](ynab-memo-sync.md) for identity rules, explicit confirmation and the boundary with durable integration recovery.

## Normalization write ownership

Automatic receipt-item normalization uses source snapshots and a short guarded write phase, preserving newer edits and atomic audit records. See [Normalization write ownership](normalization-ownership.md) for row revisions, canonical rejection, lock ordering, guarded template updates and trusted creation hints.

## Receipt arithmetic ownership

The client centralizes decimal line rounding, amount sums and balance comparisons in one pure helper while retaining authoritative server aggregates. See [Receipt arithmetic](receipt-arithmetic.md) for calculation order, submission and reconciliation thresholds, and transport limits.

Category suggestions and their historical receipt-label contracts are documented in [Category snapshots](category-snapshots.md).

Client mutation and remote-event projection ownership is described in [Query invalidation](query-invalidation.md).

Connection startup, catch-up and shared HTTP/hub token refresh are described in [Realtime recovery](realtime-recovery.md).

Request and cache error presentation ownership is described in [Request errors](request-errors.md).
