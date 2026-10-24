import { act, renderHook, waitFor } from "@testing-library/react";
import { createElement, type ReactNode } from "react";
import { QueryClientProvider } from "@tanstack/react-query";
import { createQueryClient } from "@/test/test-utils";
import client from "@/lib/api-client";
import {
  normalizationInboxKey,
  useNormalizationInbox,
  useNormalizationInboxActions,
  useNormalizationInboxPreferences,
} from "./useNormalizationInbox";

vi.mock("@/lib/api-client", () => ({
  default: { GET: vi.fn(), PUT: vi.fn(), POST: vi.fn() },
}));
function setup() {
  const cache = createQueryClient();
  return {
    cache,
    wrapper: ({ children }: { children: ReactNode }) =>
      createElement(QueryClientProvider, { client: cache }, children),
  };
}
beforeEach(() => vi.resetAllMocks());

it("uses server pagination and search while passing cancellation to the request", async () => {
  const page = { items: [], total: 0, offset: 25, limit: 25 };
  vi.mocked(client.GET).mockResolvedValue({
    data: page,
    response: new Response(),
  } as never);
  const { result } = renderHook(
    () => useNormalizationInbox("milk", 25, 25),
    setup(),
  );
  await waitFor(() => expect(result.current.isSuccess).toBe(true));
  expect(result.current.data).toEqual(page);
  expect(client.GET).toHaveBeenCalledWith("/api/normalization-inbox", {
    middleware: expect.any(Array),
    signal: expect.any(AbortSignal),
    params: { query: { q: "milk", offset: 25, limit: 25 } },
  });
});

it("retains failed preference reads as errors rather than silently treating them as disabled consent", async () => {
  vi.mocked(client.GET).mockResolvedValue({
    error: { detail: "Settings unavailable" },
    response: new Response(null, { status: 503 }),
  } as never);
  const { result } = renderHook(
    () => useNormalizationInboxPreferences(),
    setup(),
  );
  await waitFor(() => expect(result.current.isError).toBe(true));
  expect(result.current.error).toMatchObject({
    status: 503,
    detail: "Settings unavailable",
  });
  expect(result.current.data).toBeUndefined();
});

it("saves the observed preference revision and invalidates both inbox and preferences only on success", async () => {
  const { cache, wrapper } = setup();
  cache.setQueryDefaults(normalizationInboxKey, { gcTime: Infinity });
  cache.setQueryData([...normalizationInboxKey, "preferences"], {
    enabled: false,
  });
  cache.setQueryData([...normalizationInboxKey, "sources", "", 0, 25], {
    items: [],
  });
  const body = {
    enabled: true,
    model: "test-model",
    dailyLimit: 10,
    expectedRevision: "revision-a",
  };
  vi.mocked(client.PUT)
    .mockResolvedValueOnce({
      error: { detail: "Consent changed" },
      response: new Response(null, { status: 409 }),
    } as never)
    .mockResolvedValueOnce({
      data: { ...body, revision: "revision-b" },
      response: new Response(),
    } as never);
  const { result } = renderHook(() => useNormalizationInboxActions(), {
    wrapper,
  });
  await act(async () => {
    await expect(
      result.current.savePreferences.mutateAsync(body),
    ).rejects.toMatchObject({ status: 409, detail: "Consent changed" });
  });
  expect(
    cache.getQueryState([...normalizationInboxKey, "preferences"])
      ?.isInvalidated,
  ).toBe(false);
  await act(async () => {
    await result.current.savePreferences.mutateAsync(body);
  });
  expect(client.PUT).toHaveBeenCalledWith(
    "/api/normalization-inbox/preferences",
    { body, middleware: expect.any(Array) },
  );
  expect(
    cache.getQueryState([...normalizationInboxKey, "preferences"])
      ?.isInvalidated,
  ).toBe(true);
  expect(
    cache.getQueryState([...normalizationInboxKey, "sources", "", 0, 25])
      ?.isInvalidated,
  ).toBe(true);
});

it("retries only the explicitly selected source and observed attempt revision", async () => {
  vi.mocked(client.POST).mockResolvedValue({
    data: {},
    response: new Response(),
  } as never);
  const { result } = renderHook(() => useNormalizationInboxActions(), setup());
  await act(async () => {
    await result.current.retryAdmission.mutateAsync({
      sourceHash: "source-a",
      expectedRevision: "attempt-a",
    });
  });
  expect(client.POST).toHaveBeenCalledWith(
    "/api/normalization-inbox/sources/{sourceHash}/retry-admission",
    {
      middleware: expect.any(Array),
      params: { path: { sourceHash: "source-a" } },
      body: { expectedRevision: "attempt-a" },
    },
  );
});
