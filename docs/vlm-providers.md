# VLM providers (RECEIPTS-652)

The receipt-extraction pipeline can be backed by either of two interchangeable
Vision-Language-Model providers. Both implement the same
`IReceiptExtractionService` contract, share the
[`ReceiptExtractionPrompt`](../src/Infrastructure/Services/ReceiptExtractionPrompt.cs)
and the [`VlmReceiptPayload`](../src/Infrastructure/Services/VlmReceiptPayload.cs)
output schema, and validate `schema_version: 1` after extraction
([RECEIPTS-639](https://plane.wallingford.me/dev/browse/RECEIPTS-639)).

## Switching providers

Set `Ocr:Vlm:Provider` (env var: `Ocr__Vlm__Provider`) to one of:

| Value         | Implementation                            | Default endpoint              |
|---------------|-------------------------------------------|-------------------------------|
| `ollama`      | `OllamaReceiptExtractionService`          | `http://localhost:11434`      |
| `anthropic`   | `AnthropicReceiptExtractionService`       | `https://api.anthropic.com`   |

The default is `ollama`. Both implementations register the same
`IReceiptExtractionService` interface — only one is active at a time, selected
in `InfrastructureService.RegisterReceiptExtractionService`.

## Ollama (default)

A local Ollama daemon serving a vision model
(default: `glm-ocr:q8_0`, configurable via `VLM_MODEL`).
Aspire and `docker-compose` both spin up an Ollama container and a one-shot
sidecar that pulls the configured model on first start. See
[architecture.md](architecture.md) for the full container topology.

**Cost:** zero per-request — only the local compute.

**Accuracy:** historically variable on real-world receipts. Each model has
needed tuning (Tesseract → PaddleOCR → glm-ocr) and still misreads store
names, drops items, or hallucinates transactions on photos other Claude
instances parse correctly.

**When to use:**

- Receipt volume too high to make hosted-VLM cost realistic.
- Strict offline / no-egress requirement.
- Active prompt iteration where round-trip cost would dominate.

## Anthropic (POC)

Routes extraction through the [Anthropic Messages
API](https://docs.anthropic.com/en/api/messages) on Claude Haiku
(default: `claude-haiku-4-5`, configurable via `Anthropic:Model`).

The implementation:

- Posts a base64 PNG plus the canonical prompt to `/v1/messages`.
- Forces structured-JSON output via tool-use: a single tool `submit_receipt`
  whose `input_schema` mirrors `VlmReceiptPayload`. `tool_choice` is pinned to
  this tool, so the response always contains the parsed payload as a
  `tool_use` block — no JSON-in-text parsing.
- Marks the prompt as `cache_control: { type: "ephemeral" }`. The prompt is
  constant per schema version; cache hits drop input cost ~10x for repeat
  scans within the cache TTL.
- Logs token usage (input / output / cache_creation / cache_read) so cost and
  cache-hit ratio are observable over time.

**Cost (approximate, current Haiku pricing):**

A typical receipt photo is ~50-150 KB after rasterization and produces ~300
output tokens. With prompt caching warm, repeat scans cost roughly:

- $0.001-$0.003 per receipt (input + output tokens)
- Well under $1/month at the user's reference volume of ~10-15 receipts/month.

The first scan after a cache TTL expiry pays the full prompt-input cost;
subsequent scans within the TTL get the cached rate.

**Accuracy:** demonstrably correct on the user's reference receipts during
ad-hoc evaluation (store name correct, item count matches, single transaction
preserved).

**When to use:**

- Personal / low-volume deployments where accuracy matters more than per-call
  cost.
- Receipts where the local model has been observed to fail.
- Cost-bounded production where prompt caching keeps the bill near zero.

### Required configuration

The Anthropic provider needs an API key. Get one from
[console.anthropic.com](https://console.anthropic.com).

```bash
# Local dev (no Aspire)
export Anthropic__ApiKey="sk-ant-..."
export Ocr__Vlm__Provider="anthropic"

# .env (docker-compose)
ANTHROPIC_API_KEY=sk-ant-...
OCR_VLM_PROVIDER=anthropic
```

The API key is bound via `IOptions<AnthropicOptions>` with
`ValidateDataAnnotations().ValidateOnStart()` — a missing key fails the host
at startup rather than producing a confusing 401 on the first upload.

### Optional knobs

| Setting                      | Default                       | Notes |
|------------------------------|-------------------------------|-------|
| `Anthropic:Model`            | `claude-haiku-4-5`            | Override to pin a specific Haiku revision. |
| `Anthropic:BaseUrl`          | `https://api.anthropic.com`   | For local mocking / enterprise proxies. |
| `Anthropic:ApiVersion`       | `2023-06-01`                  | Bumps require a code review per Anthropic migration notes. |
| `Anthropic:MaxTokens`        | `4096`                        | Tool-use responses for receipts typically run a few hundred tokens. |
| `Anthropic:TimeoutSeconds`   | `120`                         | Per-attempt budget; retry resets the clock. |
| `Anthropic:MaxImageBytes`    | `15 MB`                       | Rejected before base64 encoding to avoid memory-spike on large camera dumps. |
| `Anthropic:LogRawResponses`  | `false`                       | PII gate — leave `false` in production. |

## Comparing providers

`VlmEval` accepts a `--provider` flag so the same fixtures can be scored
against both providers back-to-back:

```bash
# Score the Ollama path
dotnet run --project src/Tools/VlmEval -- \
    --provider ollama \
    --output json \
    --report-path runs/ollama.json

# Score the Anthropic path (requires Anthropic__ApiKey)
dotnet run --project src/Tools/VlmEval -- \
    --provider anthropic \
    --output json \
    --report-path runs/anthropic.json
```

The JSON artifact's `run.provider` field distinguishes the two reports, so an
external diff script can compute per-fixture deltas without coupling to the
process that produced them.

## Migration plan

The default stays `ollama` until accuracy + cost data justify a flip. Both
providers will continue to ship — pinning the default to whichever holds up
better on real receipts is a follow-up decision, tracked via the
[RECEIPTS-616 epic](https://plane.wallingford.me/dev/browse/RECEIPTS-616).
