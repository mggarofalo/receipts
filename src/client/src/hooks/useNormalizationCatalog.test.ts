import { act, renderHook, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { createElement, type PropsWithChildren } from "react";
import client from "@/lib/api-client";
import { repairDomainChange } from "@/lib/query-invalidation";
import { extractErrorMessage } from "@/lib/problem-details";
import { createQueryWrapper } from "@/test/test-utils";
import {
  normalizationCatalogKey,
  useCatalogActions,
  useCatalogLabelActions,
  useCatalogCandidates,
  useCatalogCoverage,
  useCatalogMappings,
  useCatalogOperation,
  useCatalogOperations,
  useCatalogSources,
  useCatalogSuggestions,
  type CatalogOperation,
} from "./useNormalizationCatalog";

vi.mock("@/lib/api-client", () => ({
  default: { GET: vi.fn(), POST: vi.fn() },
}));
vi.mock("@/lib/query-invalidation", () => ({
  repairDomainChange: vi.fn().mockResolvedValue(undefined),
}));
const api = vi.mocked(client);
const id = "10000000-0000-0000-0000-000000000001";
const revision = "20000000-0000-0000-0000-000000000001";
const source = {
  location: "Store A",
  description: "BF FRANKS",
  itemCode: null,
};
const operation = {
  id,
  revision,
  source,
  state: "preview",
  mappingId: null,
  targetId: id,
  targetName: "Beef Franks",
  newCanonicalName: null,
  includeLegacyUnknown: false,
  total: 120,
  eligible: 120,
  protected: 0,
  applied: 0,
  conflicted: 0,
  remaining: 120,
  undone: 0,
  createdAt: "2026-01-01T00:00:00Z",
  exclude: false,
} satisfies CatalogOperation;
const ok = (data: unknown) => ({
  data,
  error: undefined,
  response: new Response(null, { status: 200 }),
});
beforeEach(() => vi.clearAllMocks());

it("previews global labels without invalidation and approves exactly the reviewed impact and optional proposal", async () => {
  const review = {
    targetId: id,
    targetRevision: revision,
    canonicalName: "BF FRANKS",
    currentDisplayLabel: null,
    proposedDisplayLabel: "Beef franks",
    affectedItemCount: 12,
  };
  api.POST.mockResolvedValue(ok(review) as never);
  const { result } = renderHook(() => useCatalogLabelActions(), {
    wrapper: createQueryWrapper(),
  });
  await act(async () => {
    await result.current.preview.mutateAsync({
      targetId: id,
      displayLabel: "Beef franks",
    });
  });
  expect(repairDomainChange).not.toHaveBeenCalled();
  const suggestion = { runId: id, proposalId: revision, runRevision: id };
  await act(async () => {
    await result.current.approve.mutateAsync({ review, suggestion });
  });
  expect(api.POST).toHaveBeenLastCalledWith(
    "/api/normalization-catalog/targets/{id}/approve-label",
    {
      params: { path: { id } },
      body: {
        displayLabel: "Beef franks",
        expectedTargetRevision: revision,
        expectedAffectedItemCount: 12,
        suggestionRunId: id,
        suggestionProposalId: revision,
        suggestionRunRevision: id,
      },
    },
  );
  expect(repairDomainChange).toHaveBeenCalledWith(
    expect.any(QueryClient),
    "normalized-description",
    expect.any(Function),
  );
});

it("does not invalidate domain projections when a global label approval is stale", async () => {
  api.POST.mockResolvedValue({
    data: undefined,
    error: { status: 409, title: "Conflict", detail: "Target changed." },
    response: new Response(null, { status: 409 }),
  } as never);
  const { result } = renderHook(() => useCatalogLabelActions(), {
    wrapper: createQueryWrapper(),
  });
  await act(async () => {
    await expect(
      result.current.approve.mutateAsync({
        review: {
          targetId: id,
          targetRevision: revision,
          canonicalName: "BF FRANKS",
          currentDisplayLabel: null,
          proposedDisplayLabel: "Beef franks",
          affectedItemCount: 12,
        },
      }),
    ).rejects.toBeDefined();
  });
  await waitFor(() =>
    expect(extractErrorMessage(result.current.approve.error)).toBe(
      "Target changed.",
    ),
  );
  expect(repairDomainChange).not.toHaveBeenCalled();
});

it("loads coverage and history with cancellation signals", async () => {
  api.GET.mockResolvedValue(ok([]) as never);
  const { result } = renderHook(
    () => ({ coverage: useCatalogCoverage(), history: useCatalogOperations() }),
    { wrapper: createQueryWrapper() },
  );
  await waitFor(() =>
    expect(
      result.current.coverage.isSuccess && result.current.history.isSuccess,
    ).toBe(true),
  );
  for (const path of ["coverage", "operations"])
    expect(api.GET).toHaveBeenCalledWith(`/api/normalization-catalog/${path}`, {
      signal: expect.any(AbortSignal),
    });
});

it("keys source pages by query, disposition and paging and forwards exact filters", async () => {
  api.GET.mockResolvedValue(ok({ items: [], total: 0 }) as never);
  const { result, rerender } = renderHook(
    ({ q, disposition }) => useCatalogSources(q, disposition, 25, 25),
    {
      initialProps: { q: "BF", disposition: "needs-review" },
      wrapper: createQueryWrapper(),
    },
  );
  await waitFor(() => expect(result.current.isSuccess).toBe(true));
  expect(api.GET).toHaveBeenLastCalledWith(
    "/api/normalization-catalog/sources",
    {
      signal: expect.any(AbortSignal),
      params: {
        query: {
          q: "BF",
          disposition: "needs-review",
          offset: 25,
          limit: 25,
        },
      },
    },
  );
  rerender({ q: "BEEF", disposition: "all" });
  await waitFor(() =>
    expect(api.GET).toHaveBeenLastCalledWith(
      "/api/normalization-catalog/sources",
      {
        signal: expect.any(AbortSignal),
        params: {
          query: {
            q: "BEEF",
            disposition: "all",
            offset: 25,
            limit: 25,
          },
        },
      },
    ),
  );
});

it("keeps store and item-code scope on candidate and mapping requests", async () => {
  api.GET.mockResolvedValue(ok([]) as never);
  const { result, rerender } = renderHook(
    ({ key }) => ({
      candidates: useCatalogCandidates(key),
      mappings: useCatalogMappings(key),
    }),
    {
      initialProps: {
        key: source as {
          location: string;
          description: string;
          itemCode: string | null;
        },
      },
      wrapper: createQueryWrapper(),
    },
  );
  await waitFor(() =>
    expect(
      result.current.candidates.isSuccess && result.current.mappings.isSuccess,
    ).toBe(true),
  );
  expect(api.GET).toHaveBeenCalledWith(
    "/api/normalization-catalog/candidates",
    {
      signal: expect.any(AbortSignal),
      params: { query: { ...source, itemCode: undefined } },
    },
  );
  rerender({ key: { ...source, location: "Store B", itemCode: "001" } });
  await waitFor(() =>
    expect(api.GET).toHaveBeenCalledWith(
      "/api/normalization-catalog/mappings",
      {
        signal: expect.any(AbortSignal),
        params: { query: { ...source, location: "Store B", itemCode: "001" } },
      },
    ),
  );
});

it("does not request an absent operation and repairs read projections only when committed progress changes", async () => {
  const cache = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  const wrapper = ({ children }: PropsWithChildren) =>
    createElement(QueryClientProvider, { client: cache }, children);
  api.GET.mockResolvedValue(ok(operation) as never);
  const { result, rerender } = renderHook(
    ({ selected }) => useCatalogOperation(selected),
    { initialProps: { selected: null as string | null }, wrapper },
  );
  expect(api.GET).not.toHaveBeenCalled();
  rerender({ selected: id });
  await waitFor(() => expect(result.current.data).toEqual(operation));
  expect(repairDomainChange).not.toHaveBeenCalled();
  await act(async () => {
    cache.setQueryData([...normalizationCatalogKey, "operation", id], {
      ...operation,
      applied: 100,
      remaining: 20,
      state: "applying",
    });
  });
  await waitFor(() => expect(repairDomainChange).toHaveBeenCalledTimes(1));
  await act(async () => {
    cache.setQueryData([...normalizationCatalogKey, "operation", id], {
      ...operation,
      applied: 100,
      remaining: 20,
      state: "paused",
      revision: id,
    });
  });
  expect(repairDomainChange).toHaveBeenCalledTimes(1);
  await act(async () => {
    cache.setQueryData([...normalizationCatalogKey, "operation", id], {
      ...operation,
      applied: 100,
      undone: 100,
      state: "undone",
    });
  });
  await waitFor(() => expect(repairDomainChange).toHaveBeenCalledTimes(2));
  cache.clear();
});

it("preserves reasoned conflicts and does not publish failed decisions as success", async () => {
  api.POST.mockResolvedValue({
    error: {
      status: 409,
      detail: "Source changed since preview.",
      code: "source_changed",
    },
    response: new Response(null, { status: 409 }),
  } as never);
  const callback = vi.fn();
  const { result } = renderHook(() => useCatalogActions(callback), {
    wrapper: createQueryWrapper(),
  });
  await act(async () => {
    await expect(
      result.current.operate.mutateAsync({ operation, action: "approve" }),
    ).rejects.toBeDefined();
  });
  await waitFor(() =>
    expect(extractErrorMessage(result.current.operate.error)).toBe(
      "Source changed since preview.",
    ),
  );
  expect(callback).not.toHaveBeenCalled();
  expect(repairDomainChange).not.toHaveBeenCalled();
});

it.each(["approve", "cancel", "undo", "resume"] as const)(
  "sends %s against the selected operation identity and revision contract",
  async (action) => {
    api.POST.mockResolvedValue(ok(operation) as never);
    const { result } = renderHook(useCatalogActions, {
      wrapper: createQueryWrapper(),
    });
    await act(async () => {
      await result.current.operate.mutateAsync({ operation, action });
    });
    expect(api.POST).toHaveBeenCalledWith(
      `/api/normalization-catalog/operations/{id}/${action}`,
      {
        params: { path: { id } },
        ...(action === "resume"
          ? {}
          : { body: { expectedRevision: revision } }),
      },
    );
    expect(repairDomainChange).toHaveBeenCalledTimes(1);
  },
);

it("publishes preview identity before cache refresh and revokes the exact mapping revision", async () => {
  api.POST.mockResolvedValue(ok(operation) as never);
  const callback = vi.fn();
  const { result } = renderHook(() => useCatalogActions(callback), {
    wrapper: createQueryWrapper(),
  });
  const body = {
    clientRequestId: id,
    source,
    targetId: id,
    targetRevision: revision,
    newCanonicalName: null,
    includeLegacyUnknown: false,
    exclude: false,
  };
  await act(async () => {
    await result.current.preview.mutateAsync(body);
  });
  expect(callback).toHaveBeenCalledWith(operation);
  expect(api.POST).toHaveBeenCalledWith(
    "/api/normalization-catalog/operations",
    { body },
  );
  await act(async () => {
    await result.current.revoke.mutateAsync({
      id,
      revision,
      source,
      targetId: id,
      targetName: "Beef Franks",
      state: "active",
      approvedAt: operation.createdAt,
    });
  });
  expect(api.POST).toHaveBeenLastCalledWith(
    "/api/normalization-catalog/mappings/{id}/revoke",
    { params: { path: { id } }, body: { expectedRevision: revision } },
  );
});

it("starts a source suggestion request with its full scoped selection and stable request identity", async () => {
  const run = { id, state: "queued" };
  api.POST.mockResolvedValue(ok(run) as never);
  const started = vi.fn();
  const { result } = renderHook(() => useCatalogSuggestions(started), {
    wrapper: createQueryWrapper(),
  });
  const body = {
    clientRequestId: id,
    model: "explicit-model",
    sources: [{ source, includeLegacyUnknown: true }],
  };
  await act(async () => {
    await result.current.mutateAsync(body);
  });
  expect(api.POST).toHaveBeenCalledWith("/api/normalization-ai/source-runs", {
    body,
  });
  expect(started).toHaveBeenCalledWith(run);
});

it("repairs read projections when history polling completes while operation detail is closed", async () => {
  const cache = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  const wrapper = ({ children }: PropsWithChildren) =>
    createElement(QueryClientProvider, { client: cache }, children);
  api.GET.mockResolvedValue(ok([{ ...operation, state: "applying" }]) as never);
  const { result } = renderHook(useCatalogOperations, { wrapper });
  await waitFor(() => expect(result.current.isSuccess).toBe(true));
  const before = vi.mocked(repairDomainChange).mock.calls.length;
  await act(async () => {
    cache.setQueryData(
      [...normalizationCatalogKey, "operations"],
      [{ ...operation, state: "completed", applied: 120, remaining: 0 }],
    );
  });
  await waitFor(() =>
    expect(repairDomainChange).toHaveBeenCalledTimes(before + 1),
  );
  await act(async () => {
    cache.setQueryData(
      [...normalizationCatalogKey, "operations"],
      [
        {
          ...operation,
          state: "completed",
          applied: 120,
          remaining: 0,
          revision: id,
        },
      ],
    );
  });
  expect(repairDomainChange).toHaveBeenCalledTimes(before + 1);
  cache.clear();
});
