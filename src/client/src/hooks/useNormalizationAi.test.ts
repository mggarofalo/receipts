import { act, renderHook, waitFor } from "@testing-library/react";
import { createQueryWrapper } from "@/test/test-utils";
import client from "@/lib/api-client";
import { repairDomainChange } from "@/lib/query-invalidation";
import { extractErrorMessage } from "@/lib/problem-details";
import {
  useAiActions,
  useAiCandidates,
  useAiConnection,
  useAiRun,
  useAiRuns,
} from "./useNormalizationAi";

vi.mock("@/lib/api-client", () => ({
  default: { GET: vi.fn(), POST: vi.fn() },
}));
vi.mock("@/lib/query-invalidation", () => ({
  repairDomainChange: vi.fn().mockResolvedValue(undefined),
}));
const api = vi.mocked(client);
const id = "10000000-0000-0000-0000-000000000001";
const revision = "20000000-0000-0000-0000-000000000001";
const run = {
  id,
  revision,
  clientRequestId: id,
  state: "ready",
  model: "test-model",
  createdAt: "2026-01-01T00:00:00Z",
  completedAt: null,
  errorCode: null,
  errorDetail: null,
  proposals: [],
  usage: null,
};
const ok = (data: unknown) => ({
  data,
  error: undefined,
  response: new Response(null, { status: 200 }),
});

beforeEach(() => vi.clearAllMocks());

it("loads connection status and forwards an abort signal", async () => {
  const connection = {
    configured: true,
    isOwner: true,
    credentialsPresent: true,
    generation: id,
    models: ["test-model"],
  };
  api.GET.mockResolvedValue(ok(connection) as never);
  const { result } = renderHook(() => useAiConnection(), {
    wrapper: createQueryWrapper(),
  });
  await waitFor(() => expect(result.current.data).toEqual(connection));
  expect(api.GET).toHaveBeenCalledWith("/api/normalization-ai/connection", {
    signal: expect.any(AbortSignal),
  });
});

it("does not retry or hide the connection's RFC9457 error reason", async () => {
  api.GET.mockResolvedValue({
    data: undefined,
    error: { status: 503, detail: "Reconnect the hosted account." },
    response: new Response(null, { status: 503 }),
  } as never);
  const { result } = renderHook(() => useAiConnection(), {
    wrapper: createQueryWrapper(),
  });
  await waitFor(() => expect(result.current.isError).toBe(true));
  expect(extractErrorMessage(result.current.error)).toBe(
    "Reconnect the hosted account.",
  );
  expect(api.GET).toHaveBeenCalledTimes(1);
});

it("keeps candidate fetching disabled until authorized and keys the requested page", async () => {
  api.GET.mockResolvedValue(ok({ items: [], total: 0 }) as never);
  const { result, rerender } = renderHook(
    ({ enabled, q }) => useAiCandidates(q, 25, 25, enabled),
    {
      initialProps: { enabled: false, q: "milk" },
      wrapper: createQueryWrapper(),
    },
  );
  expect(api.GET).not.toHaveBeenCalled();
  rerender({ enabled: true, q: "milk" });
  await waitFor(() => expect(result.current.isSuccess).toBe(true));
  expect(api.GET).toHaveBeenLastCalledWith("/api/normalization-ai/candidates", {
    signal: expect.any(AbortSignal),
    params: { query: { q: "milk", offset: 25, limit: 25 } },
  });
  rerender({ enabled: true, q: "bread" });
  await waitFor(() =>
    expect(api.GET).toHaveBeenLastCalledWith(
      "/api/normalization-ai/candidates",
      {
        signal: expect.any(AbortSignal),
        params: { query: { q: "bread", offset: 25, limit: 25 } },
      },
    ),
  );
});

it("loads run history only when enabled", async () => {
  api.GET.mockResolvedValue(ok([run]) as never);
  const { result, rerender } = renderHook(({ enabled }) => useAiRuns(enabled), {
    initialProps: { enabled: false },
    wrapper: createQueryWrapper(),
  });
  expect(api.GET).not.toHaveBeenCalled();
  rerender({ enabled: true });
  await waitFor(() => expect(result.current.data).toEqual([run]));
  expect(api.GET).toHaveBeenCalledWith("/api/normalization-ai/runs", {
    signal: expect.any(AbortSignal),
  });
});

it("does not request a null run and loads the selected run with its ID", async () => {
  api.GET.mockResolvedValue(ok(run) as never);
  const { result, rerender } = renderHook(
    ({ selected }) => useAiRun(selected, true),
    {
      initialProps: { selected: null as string | null },
      wrapper: createQueryWrapper(),
    },
  );
  expect(api.GET).not.toHaveBeenCalled();
  rerender({ selected: id });
  await waitFor(() => expect(result.current.data).toEqual(run));
  expect(api.GET).toHaveBeenCalledWith("/api/normalization-ai/runs/{id}", {
    signal: expect.any(AbortSignal),
    params: { path: { id } },
  });
});

it("starts, cancels and explicitly retries using the supplied operation identity", async () => {
  api.POST.mockResolvedValue(ok(run) as never);
  const { result } = renderHook(useAiActions, {
    wrapper: createQueryWrapper(),
  });
  const body = { clientRequestId: id, itemIds: [id], model: "test-model" };
  await act(async () => {
    await result.current.start.mutateAsync(body);
  });
  expect(api.POST).toHaveBeenLastCalledWith("/api/normalization-ai/runs", {
    body,
  });
  await act(async () => {
    await result.current.cancel.mutateAsync(id);
  });
  expect(api.POST).toHaveBeenLastCalledWith(
    "/api/normalization-ai/runs/{id}/cancel",
    { params: { path: { id } } },
  );
  await act(async () => {
    await result.current.retry.mutateAsync({ id, clientRequestId: revision });
  });
  expect(api.POST).toHaveBeenLastCalledWith(
    "/api/normalization-ai/runs/{id}/retry",
    { params: { path: { id } }, body: { clientRequestId: revision } },
  );
  expect(repairDomainChange).not.toHaveBeenCalled();
});

it("applies the selected proposals and repairs dependent domain projections only after success", async () => {
  api.POST.mockResolvedValue(ok({ ...run, state: "applied" }) as never);
  const { result } = renderHook(useAiActions, {
    wrapper: createQueryWrapper(),
  });
  await act(async () => {
    await result.current.apply.mutateAsync({
      id,
      proposalIds: [id],
      expectedRevision: revision,
    });
  });
  expect(api.POST).toHaveBeenCalledWith(
    "/api/normalization-ai/runs/{id}/apply",
    {
      params: { path: { id } },
      body: { proposalIds: [id], expectedRevision: revision },
    },
  );
  expect(repairDomainChange).toHaveBeenCalledTimes(1);
  expect(repairDomainChange).toHaveBeenCalledWith(
    expect.anything(),
    "normalized-description",
    expect.any(Function),
  );
});

it("preserves failed apply as an error without success invalidation or automatic retry", async () => {
  api.POST.mockResolvedValue({
    data: undefined,
    error: { status: 409, detail: "Selected items changed; refresh this run." },
    response: new Response(null, { status: 409 }),
  } as never);
  const { result } = renderHook(useAiActions, {
    wrapper: createQueryWrapper(),
  });
  await act(async () => {
    await expect(
      result.current.apply.mutateAsync({
        id,
        proposalIds: [id],
        expectedRevision: revision,
      }),
    ).rejects.toMatchObject({
      status: 409,
      detail: "Selected items changed; refresh this run.",
    });
  });
  expect(api.POST).toHaveBeenCalledTimes(1);
  expect(repairDomainChange).not.toHaveBeenCalled();
});
