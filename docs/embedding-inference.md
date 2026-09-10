# Embedding inference operations

The API owns one `OnnxEmbeddingService` and one bounded inference scheduler per process.
Interactive single-item requests and background batches use separate bounded lanes. The
scheduler runs ONNX work on one dedicated thread because the tokenizer is not documented as
thread-safe. It serves up to four queued requests before giving a waiting background item a
turn; batches enqueue one item at a time so cancellation and request interleaving happen between
inferences. This is a fixed worker, not a `Task.Run` queue.

`IEmbeddingService.IsConfigured` checks only the verified provisioning marker and file sizes. It
does not load the model or wait for the inference queue. `EmbeddingModelWarmupService` explicitly
loads and exercises the model after provisioning. `/health` therefore continues to report API
readiness independently of this optional semantic-search capability; callers fall back to
trigram matching while the model is unavailable.

## Capacity and telemetry

The defaults are 32 waiting requests and 8 waiting background items. Override them with
`Embeddings__RequestQueueCapacity` and `Embeddings__BackgroundQueueCapacity`. Producers wait on
the bounded lane when it is full, and cancellation removes queued work before it consumes an
inference turn.

The `Receipts.Embeddings` meter exports:

- `receipts.embedding.queue.depth`, tagged with `priority=request|background`
- `receipts.embedding.queue.wait` in milliseconds, with the same priority tag
- `receipts.embedding.inference.duration` in milliseconds, with the same priority tag
- `receipts.embedding.queue.cancelled`, counting work cancelled before inference

The admin-only `GET /api/normalized-descriptions/embedding-coverage` endpoint reports current
fingerprint coverage and the remaining canonical/item backlog. Queue metrics are process-local;
coverage is stored in PostgreSQL.

The deployment currently assumes a single long-running API instance owns the background workers
and model. Each additional API replica would have its own queue, model memory, and workers; a
scale-out design must explicitly choose worker-owning replicas and aggregate per-process metrics.

## 2026-09-10 benchmark

`EmbeddingInferenceBenchmarkTests` measures the pinned `bge-large-en-v1.5` revision on an Apple
M2 Pro (arm64, 16 GiB), .NET SDK 10.0.400. It records one cold warmup, 10 warm requests, and 20
sequential interactive requests while a 24-item background batch is active. Percentiles use the
nearest-rank method. These numbers characterize this machine and test shape; they are not a
general service-level objective.

| Measurement | Result |
| --- | ---: |
| Cold model load and first inference | 1,169.86 ms |
| Warm request p50 | 49.14 ms |
| Warm request p95 | 92.43 ms |
| Request during background batch p50 | 83.70 ms |
| Request during background batch p95 | 127.56 ms |
| Working set before cold load | 129,843,200 bytes |
| Working set after cold load | 1,813,004,288 bytes |
| Working-set increase | 1,683,161,088 bytes |

macOS returned zero for `Process.PrivateMemorySize64`, so the benchmark records
`Process.WorkingSet64` as the resident-memory measure. Re-run with:

```bash
dotnet test tests/Infrastructure.Tests/Infrastructure.Tests.csproj \
  --filter "FullyQualifiedName~EmbeddingInferenceBenchmarkTests"
```
