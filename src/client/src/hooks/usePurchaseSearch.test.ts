import type { components } from "@/generated/api";
import { act, renderHook, waitFor } from "@testing-library/react";
import { createQueryWrapper } from "@/test/test-utils";
import { usePurchaseSearch } from "./usePurchaseSearch";
const { get } = vi.hoisted(() => ({ get: vi.fn() }));
vi.mock("@/lib/api-client", () => ({ default: { GET: get } }));

beforeEach(() => vi.clearAllMocks());

it("sends whole-result controls and separates cached queries when sort or filters change", async () => {
  get.mockImplementation((_path, request) =>
    Promise.resolve({
      data: {
        items: [],
        total: request.params.query.location === "Market" ? 12 : 3,
        offset: 0,
        limit: 25,
      } satisfies components["schemas"]["PurchaseSearchResponse"],
      response: new Response(),
    }),
  );
  const options = {
    sortBy: "unitPrice" as const,
    sortDirection: "asc" as const,
    location: "Market",
    startDate: "2025-01-01",
    endDate: "2025-01-31",
    minUnitPrice: 0,
    maxUnitPrice: 1.2345,
  };
  const { result, rerender } = renderHook(
    ({ location }) =>
      usePurchaseSearch("milk", 0, 25, true, { ...options, location }),
    { initialProps: { location: "Market" }, wrapper: createQueryWrapper() },
  );
  await waitFor(() => expect(result.current.data?.total).toBe(12));
  expect(get).toHaveBeenLastCalledWith("/api/search/purchases", {
    params: { query: { q: "milk", offset: 0, limit: 25, ...options } },
    signal: expect.any(AbortSignal),
  });
  rerender({ location: "Other" });
  await waitFor(() => expect(result.current.data?.total).toBe(3));
  expect(get).toHaveBeenCalledTimes(2);
});

it("sends the literal trimmed phrase and page with an abort signal", async () => {
  get.mockResolvedValue({
    data: {
      items: [],
      total: 0,
      offset: 25,
      limit: 5,
    } satisfies components["schemas"]["PurchaseSearchResponse"],
    response: new Response(),
  });
  const { result, unmount } = renderHook(
    () => usePurchaseSearch("  50%_milk  ", 25, 5),
    { wrapper: createQueryWrapper() },
  );
  await waitFor(() => expect(result.current.isSuccess).toBe(true));
  expect(get).toHaveBeenCalledWith("/api/search/purchases", {
    params: { query: { q: "50%_milk", offset: 25, limit: 5 } },
    signal: expect.any(AbortSignal),
  });
  unmount();
});

it.each(["", " ", "a", "x".repeat(201)])(
  "does not request invalid query %s",
  (q) => {
    renderHook(() => usePurchaseSearch(q), { wrapper: createQueryWrapper() });
    expect(get).not.toHaveBeenCalled();
  },
);

it("does not request while the palette is closed", () => {
  renderHook(() => usePurchaseSearch("milk", 0, 5, false), {
    wrapper: createQueryWrapper(),
  });
  expect(get).not.toHaveBeenCalled();
});

it("cancels the obsolete query and never carries its results into another phrase", async () => {
  let obsoleteSignal: AbortSignal | undefined;
  get.mockImplementationOnce((_path, options) => {
    obsoleteSignal = options?.signal as AbortSignal;
    return new Promise(() => {});
  });
  get.mockResolvedValueOnce({
    data: {
      items: [],
      total: 0,
      offset: 0,
      limit: 25,
    } satisfies components["schemas"]["PurchaseSearchResponse"],
    response: new Response(),
  });
  const { result, rerender } = renderHook(({ q }) => usePurchaseSearch(q), {
    initialProps: { q: "milk" },
    wrapper: createQueryWrapper(),
  });
  await waitFor(() => expect(get).toHaveBeenCalledTimes(1));
  rerender({ q: "bread" });
  expect(obsoleteSignal?.aborted).toBe(true);
  expect(result.current.data).toBeUndefined();
  await waitFor(() =>
    expect(result.current.data).toEqual({
      items: [],
      total: 0,
      offset: 0,
      limit: 25,
    }),
  );
});

it("exposes a failed request and retries the same query", async () => {
  const error = { detail: "Search temporarily unavailable", status: 503 };
  get.mockResolvedValueOnce({
    error,
    response: new Response(null, { status: 503 }),
  });
  get.mockResolvedValueOnce({
    data: {
      items: [],
      total: 0,
      offset: 0,
      limit: 25,
    } satisfies components["schemas"]["PurchaseSearchResponse"],
    response: new Response(),
  });
  const { result } = renderHook(() => usePurchaseSearch("milk"), {
    wrapper: createQueryWrapper(),
  });
  await waitFor(() => expect(result.current.isError).toBe(true));
  expect(result.current.error).toEqual(error);
  await act(async () => {
    await result.current.refetch();
  });
  await waitFor(() => expect(result.current.isSuccess).toBe(true));
  expect(get).toHaveBeenCalledTimes(2);
});

it.each(["receipt-item", "normalized-description"] as const)(
  "refreshes mounted purchase results after %s changes",
  async (change) => {
    const { useQueryClient } = await import("@tanstack/react-query");
    const { invalidateDomainChange } = await import("@/lib/query-invalidation");
    get.mockResolvedValue({
      data: {
        items: [],
        total: 0,
        offset: 0,
        limit: 25,
      } satisfies components["schemas"]["PurchaseSearchResponse"],
      response: new Response(),
    });
    const { result } = renderHook(
      () => ({
        search: usePurchaseSearch("hot dogs"),
        client: useQueryClient(),
      }),
      { wrapper: createQueryWrapper() },
    );
    await waitFor(() => expect(result.current.search.isSuccess).toBe(true));
    expect(get).toHaveBeenCalledTimes(1);
    act(() => invalidateDomainChange(result.current.client, change));
    await waitFor(() => expect(get).toHaveBeenCalledTimes(2));
  },
);

it("does not display a completed previous search as the new query's results", async () => {
  const prior: components["schemas"]["PurchaseSearchResponse"] = {
    items: [
      {
        id: "old",
        receiptId: "receipt-old",
        date: "2020-01-15",
        description: "Milk",
        location: "Store",
        quantity: 1,
        unitPrice: 2,
        unitPriceCurrency: "USD",
        totalAmount: 2,
        totalAmountCurrency: "USD",
      },
    ],
    total: 1,
    offset: 0,
    limit: 25,
  };
  get.mockResolvedValueOnce({ data: prior, response: new Response() });
  get.mockImplementationOnce(() => new Promise(() => {}));
  const { result, rerender } = renderHook(({ q }) => usePurchaseSearch(q), {
    initialProps: { q: "milk" },
    wrapper: createQueryWrapper(),
  });
  await waitFor(() => expect(result.current.data).toEqual(prior));
  rerender({ q: "hot dogs" });
  expect(result.current.data).toBeUndefined();
  expect(result.current.isPending).toBe(true);
  expect(result.current.isPlaceholderData).toBe(false);
});


it("separates identity and review scope in the cache and permits GUID history without q", async () => {
  get.mockResolvedValue({ data: { items: [], total: 1, offset: 0, limit: 25 }, response: new Response() });
  const firstId = "0d196918-51ca-4ddd-a4cd-039720bab253";
  const secondId = "cd6a97fd-381d-4230-ad56-f027192b57ba";
  const { result, rerender } = renderHook(
    ({ id, includeUnreviewed }) => usePurchaseSearch("", 0, 25, true,
      { normalizedDescriptionId: id, includeUnreviewed }),
    { initialProps: { id: firstId, includeUnreviewed: false }, wrapper: createQueryWrapper() },
  );
  await waitFor(() => expect(result.current.isSuccess).toBe(true));
  expect(get).toHaveBeenLastCalledWith("/api/search/purchases", {
    params: { query: { q: "", offset: 0, limit: 25, normalizedDescriptionId: firstId, includeUnreviewed: false } },
    signal: expect.any(AbortSignal),
  });
  rerender({ id: secondId, includeUnreviewed: false });
  await waitFor(() => expect(get).toHaveBeenCalledTimes(2));
  rerender({ id: secondId, includeUnreviewed: true });
  await waitFor(() => expect(get).toHaveBeenCalledTimes(3));
  expect(get.mock.calls[2][1].params.query).toMatchObject({ normalizedDescriptionId: secondId, includeUnreviewed: true });
});
