# API Guidelines

## OpenAPI Spec-First

The canonical API contract lives in `openapi/spec.yaml` (OpenAPI 3.1.0). All API changes follow a spec-first workflow: edit the spec, lint, build (regenerates DTOs), generate the server contract, check drift.

**Key files:** `openapi/spec.yaml` (canonical spec), `.spectral.yaml` (lint rules), `scripts/check-drift.mjs` (drift detection), `scripts/check-breaking.mjs` (breaking change detection, CI only)

**npm scripts:** `npm run lint:spec`, `npm run check:drift`, `npm run check:breaking -- origin/main`

### Typed enum wire values

Enum literals in `openapi/spec.yaml` are the response wire contract. Generated
DTO properties may carry an NSwag property converter that would otherwise
override the API's camel-case enum policy, so the API removes that override at
the generated-DTO JSON metadata boundary for both MVC and typed `IResult`
serialization. Raw response tests verify the resulting bytes; schema drift
alone cannot prove runtime serialization.

This policy applies to typed generated DTO enum properties. It does not rewrite
query contracts, OAuth literals, metadata labels, currency codes, diagnostic
strings, audit values, persistence, or arbitrary strings that resemble enums.
Generated request DTOs retain case-insensitive and numeric enum input behavior.

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

Send one credential scheme per request. When `X-Api-Key` is present it is selected in preference
to Bearer authentication; an invalid API key fails closed even if the request also carries a valid
Bearer token. This keeps the identity used for rate-limit partitioning identical to the identity
used for authorization.

Authentication runs before rate limiting so authenticated policies can partition by user and
honor the explicit API-key `BypassRateLimit` claim. Rate limiting runs before authorization, so
repeated unauthenticated or forbidden requests to protected endpoints still consume the global
client-IP budget. A rejection is an RFC 9457 problem document with `Retry-After` and a matching
`retryAfterSeconds` extension.

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

## Template update conflicts

Template updates resolve canonical metadata asynchronously before their final audited commit. If the stored template changes during that work, `PUT /api/item-templates/{id}` returns 409 `ProblemDetails` as `application/json`, matching the typed `ApiProblem` helpers, with a reason in `detail`; no requested template fields are partially applied. This is a server-side operation revision check, not a public ETag contract. The client retains the edit draft for a deliberate reload/retry. See [normalization ownership](normalization-ownership.md) for the separate canonical-registry side effects and receipt-item hint contract.

## Template price precision

Template default prices retain four decimal places and must fit the same storage range as receipt-item unit prices. Create/update validation rejects values at or above the upper limit, including values rounded to that limit by the existing JSON-number-to-decimal conversion. Null remains allowed. See [Unit-price precision](unit-price-precision.md) for input/display behavior, migration safeguards and legacy-backup handling.

## Category deletion conflicts

Category and subcategory deletion use historical name snapshots, including trash, to protect suggestions still in use. Subcategory usage is scoped to the current parent category name as well as the child name. Its typed 409 `ProblemDetails` includes an item count and a capped distinct receipt sample; `isDeleted` marks examples that cannot use the ordinary receipt detail route. See [Category snapshots](category-snapshots.md) for matching, diagnostic and concurrency limits.
