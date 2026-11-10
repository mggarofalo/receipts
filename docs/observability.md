# Observability

The Receipts application uses a hybrid observability strategy:

- **Backend**: OpenTelemetry traces, metrics and logs, sent over OTLP to a collector you run yourself, plus Sentry for error reporting
- **Frontend**: Sentry for error reporting, user feedback, and session replay

This repository does not ship a monitoring stack. Storage and dashboards live in a separate
deployment; the app only exports to it. With no collector configured, the app exports nothing.

## Architecture

```
┌──────────┐  OTLP (http/protobuf or gRPC)   ┌──────────────────────┐
│ API      │────────────────────────────────►│ External OTel        │
│ (.NET)   │  Authorization header           │ collector            │
│          │                                 │ (your monitoring     │
│          │───► Sentry (cloud)              │  stack)              │
└──────────┘     - Error reporting           └──────────────────────┘

┌──────────┐
│ React    │───► Sentry (cloud)
│ Frontend │     - Error reporting
└──────────┘     - Session replay
                 - User feedback
```

## Backend Telemetry (OpenTelemetry)

The .NET API emits three signals:

| Signal  | Source                                                                       |
|---------|------------------------------------------------------------------------------|
| Traces  | OTel SDK in `Receipts.ServiceDefaults`: ASP.NET Core, HttpClient, EF Core     |
| Metrics | OTel SDK in `Receipts.ServiceDefaults`: ASP.NET Core, HttpClient, .NET runtime, `Receipts.Embeddings` |
| Logs    | Serilog OTLP sink (`API.Services.LoggingService`), structured with CorrelationId |

Traces and metrics go through the OTel SDK. Logs go through Serilog's own OTLP sink. Both
read the same standard variables, so one block of settings configures everything.

### Configuration

| Variable | Example | Purpose |
|----------|---------|---------|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | `https://otel.example.com` | Collector base URL. **Unset means no export.** |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `http/protobuf` | `http/protobuf` or `grpc`. Defaults to `grpc`. |
| `OTEL_EXPORTER_OTLP_HEADERS` | `Authorization=Basic <base64>` | Headers on every export request. Comma-separated `key=value` pairs. |
| `OTEL_SERVICE_NAME` | `receipts-api` | `service.name` on every signal. |

Give the **base** URL, without `/v1/traces`. With `http/protobuf` both exporters add
`/v1/traces`, `/v1/metrics` and `/v1/logs` themselves. With `grpc` the URL is used as is.

The app never logs the header value. A malformed `OTEL_EXPORTER_OTLP_HEADERS` stops startup with
an error that names only the position of the bad entry. An unsupported protocol (such as
`http/json`) also stops startup, because the log sink cannot speak it.

### Connecting to a remote collector

Build the credential from the collector's user and password:

```bash
printf '%s' 'app:<secret>' | base64
```

Then add this to the `app` service's `environment` in `docker-compose.yml`:

```yaml
      - OTEL_SERVICE_NAME=receipts-api
      - OTEL_EXPORTER_OTLP_ENDPOINT=https://otel.example.com
      - OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
      - OTEL_EXPORTER_OTLP_HEADERS=Authorization=Basic <base64 of app:secret>
```

The header is a credential. Keep the real value out of git. One way is to put the line in a
`.env` file next to `docker-compose.yml` and reference it as
`OTEL_EXPORTER_OTLP_HEADERS=${OTEL_EXPORTER_OTLP_HEADERS}`.

### Connecting to a collector on the same host

If the monitoring stack runs on the same Docker host, skip the public URL and join its network.
The monitoring stack creates an external network called `observability` with the collector
reachable as `otel-collector`. In `docker-compose.yml`:

```yaml
services:
  app:
    environment:
      - OTEL_SERVICE_NAME=receipts-api
      - OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4318
      - OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
      - OTEL_EXPORTER_OTLP_HEADERS=Authorization=Basic <base64 of app:secret>
    networks:
      - receipts-net
      - observability

networks:
  receipts-net:
    driver: bridge
  observability:
    external: true
```

The collector still requires the `Authorization` header on this path. `docker compose up` fails
if the `observability` network does not exist yet, so start the monitoring stack first.

### Verifying export

1. Set the variables above and recreate the app: `docker compose up -d app`
2. Generate some API traffic (log in, browse receipts)
3. In the monitoring stack, look for `service.name = receipts-api` in traces, metrics and logs

A `401` from the collector means the header is missing or wrong. The OTel SDK and the Serilog
sink report export failures through their own diagnostics, not the app's log, so check the
collector's logs first.

### Local development

Under Aspire (`dotnet run --project src/Receipts.AppHost`), the AppHost sets these variables
for the API so all three signals reach the Aspire dashboard. Nothing else is needed. See
[development.md](development.md).

## Backend Error Reporting (Sentry)

The .NET API also reports unhandled exceptions to Sentry via `Sentry.AspNetCore`. This complements OpenTelemetry by providing rich error grouping, stack traces, and user context that Sentry excels at.

### How It Works

Sentry's ASP.NET Core middleware captures exceptions as they propagate up the middleware pipeline. The existing `GlobalExceptionHandlerMiddleware` is unchanged — Sentry captures the exception before the handler converts it to a `ProblemDetails` response.

### Configuration

| Variable     | Where Set                       | Purpose            |
|--------------|---------------------------------|--------------------|
| `SENTRY_DSN` | `.env` / `docker-compose.yml`   | Sentry project DSN |

When `SENTRY_DSN` is empty or missing, the Sentry SDK disables itself gracefully.

### Features

| Feature        | Configuration             |
|----------------|---------------------------|
| Error capture  | All unhandled exceptions  |
| Tracing        | 10% sample rate           |
| PII            | Disabled (`SendDefaultPii = false`) |
| User context   | User ID and email from auth claims (authenticated requests only) |
| Environment    | From `ASPNETCORE_ENVIRONMENT`       |

## Frontend Observability (Sentry)

The React frontend uses `@sentry/react` configured in `src/client/src/lib/sentry.ts`.

### Features

| Feature          | Integration                        | Sampling                        |
|------------------|------------------------------------|---------------------------------|
| Error capture    | `Sentry.ErrorBoundary` + global    | 100%                            |
| Browser tracing  | React Router v7 integration        | 20% prod, 100% dev             |
| Session replay   | `replayIntegration`                | 1% sessions, 100% on error     |
| User feedback    | `feedbackIntegration` (auto-inject)| On demand                       |

### Configuration

All Sentry config is via environment variables (no hardcoded DSN):

| Variable                  | Where Set                  | Purpose                    |
|---------------------------|----------------------------|----------------------------|
| `VITE_SENTRY_DSN`         | `.env` / Docker build arg  | Sentry project DSN         |
| `VITE_SENTRY_ENVIRONMENT` | `.env` / Docker build arg  | Environment tag            |
| `SENTRY_AUTH_TOKEN`        | GitHub Actions secret      | Source map uploads          |
| `SENTRY_ORG`              | GitHub Actions secret      | Sentry organization slug   |
| `SENTRY_PROJECT`           | GitHub Actions secret      | Sentry project slug        |

### Source Maps

Source maps are uploaded to Sentry during Docker image builds via `@sentry/vite-plugin`. They are deleted from the production bundle after upload, so they are not publicly accessible.

### Required Secrets (Production)

Add these to your GitHub repository secrets:

1. `SENTRY_DSN` - your Sentry project DSN (e.g., `https://examplePublicKey@o0.ingest.sentry.io/0`). Used by both the API (runtime env var) and the frontend (Docker build arg).
2. `SENTRY_AUTH_TOKEN` - Sentry auth token with `project:releases` and `org:read` scopes (frontend source map uploads only)
3. `SENTRY_ORG` - your Sentry organization slug (frontend source map uploads only)
4. `SENTRY_PROJECT` - your Sentry project slug (frontend source map uploads only)
