# Infrastructure

Data access and external service implementations. Depends on Domain and Application.

## Structure

- **`Entities/`** — EF Core database entity classes (separate from Domain entities)
- **`Repositories/`** — Repository pattern implementations over EF Core
- **`Services/`** — Service implementations (audit logging, embeddings, similarity search, cleanup)
- **`Mapping/`** — Mapperly mappers (Domain <-> Entity bidirectional mapping)
- **`Configurations/`** — EF Core entity type configurations (`IEntityTypeConfiguration<T>`)
- **`Migrations/`** — EF Core database migrations
- **`Extensions/`** — Extension methods for query building
- **`Interfaces/`** — Infrastructure-specific interfaces

## Key Patterns

- **Mapperly mappers** convert between Domain entities and EF Core entities at compile time (zero reflection).
- **`ApplicationDbContext`** is configured via `IDbContextFactory` for proper scoped lifetime management.
- **Repositories** use `IDbContextFactory<ApplicationDbContext>` to create short-lived contexts per operation.
- **Vector similarity search** uses pgvector with ONNX Runtime (`bge-large-en-v1.5` model, 1024-dim embeddings, CLS pooling).
- **ASP.NET Identity** is configured here for user/role management with PostgreSQL storage.

## Database

PostgreSQL with EF Core + pgvector. Connection configured via five environment variables: `POSTGRES_HOST`, `POSTGRES_PORT`, `POSTGRES_USER`, `POSTGRES_PASSWORD`, `POSTGRES_DB`.
