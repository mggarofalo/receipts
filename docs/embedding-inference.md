# Embedding inference operations

The API owns one `OnnxEmbeddingService` and one bounded inference scheduler per process.
Interactive single-item requests and background batches use separate bounded lanes. The
scheduler runs ONNX work on one dedicated thread because the tokenizer is not documented as
thread-safe. It serves up to four queued requests before giving a waiting background item a
turn; batches enqueue one item at a time so cancellation and request interleaving happen between
inferences. Model warmup and the hosted normalized-description resolver also use the background
lane. This is a fixed worker, not a `Task.Run` queue.

`IEmbeddingModelRuntime.IsProvisioned` checks only the verified provisioning marker and file
sizes. `IEmbeddingService.IsConfigured` becomes true only after the model loads and completes an
inference successfully; a constructed but nonfunctional ONNX session is not ready.
Neither inspection loads the model or waits for the inference queue.
`EmbeddingModelWarmupService` explicitly loads and exercises the model after provisioning; a
failed warmup leaves semantic search unconfigured so guarded callers keep their existing
fallbacks. `/health` therefore reports API readiness independently of this optional capability.

## Capacity and telemetry

The defaults are 32 waiting requests and 8 waiting background items. Override them with
`Embeddings__RequestQueueCapacity` and `Embeddings__BackgroundQueueCapacity`. There is no hidden
producer queue: excess work is rejected immediately when its lane is full, and cancellation
removes admitted work before it consumes an inference turn. Optional request-time enrichment
uses its existing no-vector fallback on rejection. The background description resolver instead
skips the affected group so a later cycle can retry without persisting a misleading unscored row.

The `Receipts.Embeddings` meter exports:

- `receipts.embedding.queue.depth`, tagged with `priority=request|background`
- `receipts.embedding.queue.wait` in milliseconds, with the same priority tag
- `receipts.embedding.inference.duration` in milliseconds, with the same priority tag
- `receipts.embedding.queue.cancelled`, counting work cancelled before inference
- `receipts.embedding.queue.rejected`, counting work rejected at the capacity boundary

The admin-only `GET /api/normalized-descriptions/embedding-coverage` endpoint reports current
fingerprint coverage and the remaining canonical/item backlog. Queue metrics are process-local;
coverage is stored in PostgreSQL.

The deployment currently assumes a single long-running API instance owns the background workers
and model. Each additional API replica would have its own queue, model memory, and workers; a
scale-out design must explicitly choose worker-owning replicas and aggregate per-process metrics.

## 2026-09-10 benchmark

`EmbeddingInferenceBenchmarkTests` measures the pinned `bge-large-en-v1.5` revision on an Apple
M2 Pro (arm64, 16 GiB), .NET SDK 10.0.400. It records one cold warmup, 10 warm requests, and 20
sequential interactive requests while 24 independently admitted background items are active.
Every request sample asserts that at least one admitted background item is still outstanding.
Percentiles use the nearest-rank method. These numbers characterize this machine and test shape;
they are not a general service-level objective.

| Measurement | Result |
| --- | ---: |
| Cold model load and first inference | 2,046.36 ms |
| Warm request p50 | 33.30 ms |
| Warm request p95 | 43.71 ms |
| Request during background batch p50 | 67.53 ms |
| Request during background batch p95 | 81.53 ms |
| Working set before cold load | 130,662,400 bytes |
| Working set after cold load | 1,733,148,672 bytes |
| Working-set increase | 1,602,486,272 bytes |

macOS returned zero for `Process.PrivateMemorySize64`, so the benchmark records
`Process.WorkingSet64` as the resident-memory measure. Re-run with:

```bash
dotnet test tests/Infrastructure.Tests/Infrastructure.Tests.csproj \
  --filter "FullyQualifiedName~EmbeddingInferenceBenchmarkTests"
```
