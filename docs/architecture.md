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
  - **Client** (`src/client/`) - React/Vite SPA (TypeScript, TanStack Query/Router, Tailwind CSS, shadcn/ui)
- **AppHost** (`src/Receipts.AppHost/`) - .NET Aspire orchestration (API + PostgreSQL + React dev server)

## Key Patterns

- **CQRS**: Commands and Queries are separate with dedicated handlers
- **Mediator Pattern**: martinothamar/Mediator dispatches commands/queries to handlers via source-generated dispatch (no runtime reflection)
- **Validation Pipeline**: `ValidationBehavior<TMessage, TResponse>` intercepts Mediator requests and runs registered `IValidator<T>` instances before handlers execute. `FluentValidationActionFilter` validates controller DTOs. `ValidationExceptionMiddleware` catches `ValidationException` and returns 400 ProblemDetails.
- **Repository Pattern**: Infrastructure repositories abstract EF Core
- **Mapping**: Mapperly handles Domain <-> Entity (Infrastructure) and Domain <-> generated DTOs (API)
- **Service Registration**: Each layer has a static extension method (`RegisterApplicationServices`, `RegisterInfrastructureServices`) for DI setup
- **Soft Delete**: Entities support soft delete with restore capabilities and trash management
- **Audit Logging**: All mutations are logged with user/API key attribution

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

Migrations run automatically on API startup via `IDatabaseMigratorService`.

### Vector Similarity Search

The system uses pgvector for semantic similarity search on item names and descriptions. Embeddings are generated locally via ONNX Runtime using the `bge-large-en-v1.5` model (1024-dimensional vectors, CLS pooling). No external API keys are required.

The 1.34 GB model is deliberately not shipped in the container image or copied into build output — doing so made the image 2.54 GB, most of it three copies of the same file (RECEIPTS-929). It is fetched once into a persistent directory and loaded from there.

- **`EmbeddingModelProvisioningService`** — Background service that downloads the model on first start if absent, verifying size and SHA-256 against a pinned upstream revision before the file is moved into place. Failures are logged and retried, never fatal. Configured via `Embeddings__ModelPath` (the container points it at `/data/models`, so budget ~1.4 GB of volume space) and `Embeddings__AutoDownload`
- **`OnnxEmbeddingService`** — Singleton service that loads the ONNX model and tokenizer lazily on first use, generates 1024-dim L2-normalized embeddings. Reports `IsConfigured == false` while the model is still missing, which every consumer already handles by degrading rather than failing
- **`EmbeddingGenerationService`** — Background service that polls every 30s, generates embeddings for new/changed ItemTemplates and ReceiptItems in batches of 50
- **`ItemTemplateSimilarityService`** — Hybrid search combining trigram similarity (0.4 weight) and cosine vector similarity (0.6 weight) with HNSW indexing

## Test Project Structure

Tests mirror src structure. `SampleData` project provides shared test fixtures across test projects.

```
tests/
  Common.Tests/
  Domain.Tests/
  Application.Tests/
  Infrastructure.Tests/
  Presentation.API.Tests/
  SampleData/
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
