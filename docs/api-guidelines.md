# API Guidelines

## OpenAPI Spec-First

The canonical API contract lives in `openapi/spec.yaml` (OpenAPI 3.1.0). All API changes follow a spec-first workflow: edit the spec, lint, build (regenerates DTOs), check drift.

**Key files:** `openapi/spec.yaml` (canonical spec), `.spectral.yaml` (lint rules), `scripts/check-drift.mjs` (drift detection), `scripts/check-breaking.mjs` (breaking change detection, CI only)

**npm scripts:** `npm run lint:spec`, `npm run check:drift`, `npm run check:breaking -- origin/main`

## Validation Ownership

Schema-expressible constraints belong in `openapi/spec.yaml`. Generated DTO DataAnnotations enforce them through MVC model validation; for example, receipt location length is 1–200 characters. Keep whitespace-only rejection and rules relative to today's date in the API FluentValidation validators. Do not duplicate a generated length limit in a handwritten validator.

The API registers its own DTO validators. `FluentValidationActionFilter` validates both collection-level rules and each list element before invoking an action. An empty list or a null element is invalid; element errors use paths such as `[1].Date`. A list validator does not replace the element validators. Nested DTO business rules remain the responsibility of their owning validator, such as `CreateCompleteReceiptRequestValidator`. Validation honors request cancellation, including a final check before action dispatch.

Application validators are registered by `ApplicationService.RegisterApplicationServices` from the Application assembly. The Mediator validation behavior therefore applies query rules to HTTP requests and other Mediator callers. Do not rely on the API's assembly scan or query-parameter annotations to register or replace these rules.

Automatic model-state failures and FluentValidation failures use `ApiValidationProblem`: HTTP 400, `application/problem+json`, field errors in `errors`, and a human-readable reason in `detail`. Tests should exercise configured MVC/Mediator composition and use a business rule as well as a schema constraint when proving batch traversal; generated annotations alone can hide a missing element validator. Invalid-batch tests must establish that persistence did not begin.

## Endpoint Return Types

Use `TypedResults` with concrete `Results<T1, T2, ...>` union return types on all endpoints (see MGG-227). This provides compile-time enforcement of response types and eliminates the need for `[ProducesResponseType]` attributes.

## List Search Filters

Entity list endpoints use the optional `q` query parameter for picker and table
search. Search is a trimmed, case-insensitive substring match across the
user-visible identity fields documented by that endpoint. Missing or
whitespace-only `q` values mean no search filter. Apply search and other filters
before counting, sorting, and pagination so `total` describes the filtered set.

## Authentication Standards

Token-based authentication must conform to these RFCs:
- **RFC 6749** — OAuth 2.0 Authorization Framework: token issuance, response format, error codes
- **RFC 7662** — OAuth 2.0 Token Introspection: token validation endpoint semantics
- **RFC 7009** — OAuth 2.0 Token Revocation: revocation endpoint behavior and response codes

### Dual Authentication Scheme

The API supports two authentication schemes, both valid on all protected endpoints:

| Scheme | Use Case | Header |
|--------|----------|--------|
| **JWT Bearer** | Browser clients (login flow) | `Authorization: Bearer <token>` |
| **API Key** | Programmatic access (scripts, integrations) | `X-Api-Key: <key>` |

### JWT Implementation

- Tokens are issued via `POST /api/auth/login` with email + password
- Access tokens are short-lived; refresh tokens enable session continuity via `POST /api/auth/refresh`
- JWT signing key is auto-generated on first deployment (stored in Docker secrets volume)
- Claims include user ID, email, and roles — role claims drive authorization policies

### Authorization

- All data-mutating endpoints require authentication
- Role-based authorization uses ASP.NET Identity roles (`Admin`, `User`)
- Admin-only endpoints: user management, password resets, auth audit logs
- API keys inherit the roles of the user who created them

### Rate Limiting

All endpoints are rate-limited at the application level (see [docs/deployment.md](deployment.md#application-rate-limiting) for thresholds). Rate limit violations return HTTP 429 with a `Retry-After` header and are logged to the auth audit trail.
