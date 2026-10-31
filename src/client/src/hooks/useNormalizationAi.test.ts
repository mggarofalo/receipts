import { act, renderHook, waitFor } from "@testing-library/react";
import { createQueryWrapper } from "@/test/test-utils";
import client from "@/lib/api-client";
import { extractErrorMessage } from "@/lib/problem-details";
import {
  useAiActions,
  useAiConnection,
  useAiRun,
  useAiRuns,
} from "./useNormalizationAi";

vi.mock("@/lib/api-client", () => ({
  default: { GET: vi.fn(), POST: vi.fn() },
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

it("cancels and explicitly retries using the supplied operation identity", async () => {
  api.POST.mockResolvedValue(ok(run) as never);
  const created = vi.fn();
  const { result } = renderHook(() => useAiActions(created), {
    wrapper: createQueryWrapper(),
  });
  await act(async () => {
    await result.current.cancel.mutateAsync(id);
  });
  expect(api.POST).toHaveBeenLastCalledWith(
    "/api/normalization-ai/runs/{id}/cancel",
    { params: { path: { id } } },
  );
  expect(created).not.toHaveBeenCalled();
  await act(async () => {
    await result.current.retry.mutateAsync({ id, clientRequestId: revision });
  });
  expect(api.POST).toHaveBeenLastCalledWith(
    "/api/normalization-ai/runs/{id}/retry",
    { params: { path: { id } }, body: { clientRequestId: revision } },
  );
  expect(created).toHaveBeenCalledWith(run);
});

it("exposes no way to start per-purchase runs or apply their suggestions", () => {
  const { result } = renderHook(() => useAiActions(), {
    wrapper: createQueryWrapper(),
  });
  expect(Object.keys(result.current).sort()).toEqual(["cancel", "retry"]);
});

it("preserves a retired-workflow retry rejection as an error without notifying the caller", async () => {
  api.POST.mockResolvedValue({
    data: undefined,
    error: {
      status: 409,
      detail: "Per-purchase ChatGPT suggestions are retired.",
    },
    response: new Response(null, { status: 409 }),
  } as never);
  const created = vi.fn();
  const { result } = renderHook(() => useAiActions(created), {
    wrapper: createQueryWrapper(),
  });
  await act(async () => {
    await expect(
      result.current.retry.mutateAsync({ id, clientRequestId: revision }),
    ).rejects.toMatchObject({
      status: 409,
      detail: "Per-purchase ChatGPT suggestions are retired.",
    });
  });
  expect(api.POST).toHaveBeenCalledTimes(1);
  expect(created).not.toHaveBeenCalled();
});
