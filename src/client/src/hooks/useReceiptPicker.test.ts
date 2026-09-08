import { createElement, type ReactNode } from "react";
import { act, cleanup, renderHook, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { useReceiptPicker } from "./useReceiptPicker";
import client from "@/lib/api-client";

vi.mock("@/lib/api-client", () => ({ default: { GET: vi.fn() } }));
const get = vi.mocked(client.GET);
const clients: QueryClient[] = [];
function wrapper() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, staleTime: 300_000 } },
  });
  clients.push(queryClient);
  return {
    queryClient,
    wrapper: ({ children }: { children: ReactNode }) =>
      createElement(QueryClientProvider, { client: queryClient }, children),
  };
}
function receipt(id: string, location = `Market ${id}`, date = "2026-01-15") {
  return { id, location, date, taxAmount: 0 };
}
function page(data: ReturnType<typeof receipt>[], total: number, offset = 0) {
  return { data: { data, total, offset, limit: 50 }, error: undefined };
}
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}
beforeEach(() => vi.clearAllMocks());
afterEach(() => {
  cleanup();
  clients.splice(0).forEach((queryClient) => queryClient.clear());
});

it("has no closed-picker request or next-page side effect, then reads one locally owned bounded page", async () => {
  get.mockResolvedValue(page([receipt("1")], 100) as never);
  const fixture = wrapper();
  const { result, rerender } = renderHook(
    ({ open }) => useReceiptPicker(open, ""),
    { initialProps: { open: false }, wrapper: fixture.wrapper },
  );
  act(() => {
    result.current.loadMore();
    result.current.retry();
  });
  expect(get).not.toHaveBeenCalled();
  rerender({ open: true });
  await waitFor(() => expect(result.current.receipts).toHaveLength(1));
  expect(get).toHaveBeenCalledTimes(1);
  expect(get).toHaveBeenCalledWith("/api/receipts", {
    params: {
      query: {
        offset: 0,
        limit: 50,
        sortBy: "date",
        sortDirection: "desc",
        q: undefined,
      },
    },
    signal: expect.any(AbortSignal),
    middleware: expect.any(Array),
  });
  expect(
    fixture.queryClient
      .getQueryCache()
      .find({ queryKey: ["receipts", "picker", "browse", ""] })?.meta,
  ).toEqual({ errorPresentation: "local" });
});

it("loads only on demand and advances raw offsets while deduplicating overlapping receipt IDs", async () => {
  get
    .mockResolvedValueOnce(page([receipt("1"), receipt("2")], 4) as never)
    .mockResolvedValueOnce(page([receipt("2"), receipt("3")], 4, 2) as never);
  const { result } = renderHook(() => useReceiptPicker(true, ""), {
    wrapper: wrapper().wrapper,
  });
  await waitFor(() => expect(result.current.receipts).toHaveLength(2));
  expect(get).toHaveBeenCalledTimes(1);
  act(() => result.current.loadMore());
  await waitFor(() => expect(result.current.complete).toBe(true));
  expect(result.current.receipts.map((item) => item.id)).toEqual([
    "1",
    "2",
    "3",
  ]);
  expect(get).toHaveBeenLastCalledWith(
    "/api/receipts",
    expect.objectContaining({
      params: {
        query: {
          offset: 2,
          limit: 50,
          sortBy: "date",
          sortDirection: "desc",
          q: undefined,
        },
      },
    }),
  );
  act(() => result.current.loadMore());
  expect(get).toHaveBeenCalledTimes(2);
});

it("uses only local sublabel and fuzzy matching after the full browse history is known", async () => {
  get.mockResolvedValue(
    page(
      [
        receipt("1", "Walmart", "2024-03-15"),
        receipt("2", "Target", "2025-02-01"),
      ],
      2,
    ) as never,
  );
  const { result, rerender } = renderHook(
    ({ search }) => useReceiptPicker(true, search),
    { initialProps: { search: "" }, wrapper: wrapper().wrapper },
  );
  await waitFor(() => expect(result.current.complete).toBe(true));
  rerender({ search: "2024-03-15" });
  expect(result.current.receipts.map((item) => item.id)).toEqual(["1"]);
  await waitFor(() => expect(result.current.isDebouncing).toBe(false));
  rerender({ search: "Wlmt" });
  await waitFor(() => expect(result.current.isDebouncing).toBe(false));
  expect(result.current.receipts.map((item) => item.id)).toEqual(["1"]);
  expect(get).toHaveBeenCalledTimes(1);
});

it("cancels an obsolete search and never publishes its late result over the current search", async () => {
  const oldResult = deferred<ReturnType<typeof page>>();
  let oldSignal: AbortSignal | undefined;
  get.mockImplementation((_path, options) => {
    const request = options as
      | { params?: { query?: { q?: string } }; signal?: AbortSignal | null }
      | undefined;
    const q = request?.params?.query?.q;
    if (q === "Old") {
      oldSignal = request?.signal ?? undefined;
      return oldResult.promise as never;
    }
    return Promise.resolve(
      page(
        q ? [receipt("new", "New market")] : [receipt("browse")],
        q ? 1 : 100,
      ),
    ) as never;
  });
  const { result, rerender } = renderHook(
    ({ search }) => useReceiptPicker(true, search),
    { initialProps: { search: "" }, wrapper: wrapper().wrapper },
  );
  await waitFor(() => expect(result.current.receipts).toHaveLength(1));
  rerender({ search: "Old" });
  await waitFor(() => expect(oldSignal).toBeDefined());
  rerender({ search: "New" });
  await waitFor(() =>
    expect(result.current.receipts.map((item) => item.id)).toEqual(["new"]),
  );
  expect(oldSignal?.aborted).toBe(true);
  await act(async () => {
    oldResult.resolve(page([receipt("old", "Old market")], 1));
    await oldResult.promise;
  });
  expect(result.current.receipts.map((item) => item.id)).toEqual(["new"]);
});

it("does not retry a failed query from a closed picker and retains local choices", async () => {
  get.mockResolvedValueOnce(page([receipt("loaded")], 100) as never);
  const { result, rerender } = renderHook(
    ({ open, search }) => useReceiptPicker(open, search),
    { initialProps: { open: true, search: "" }, wrapper: wrapper().wrapper },
  );
  await waitFor(() => expect(result.current.receipts).toHaveLength(1));
  get.mockResolvedValue({
    error: { status: 503, detail: "Search failed" },
  } as never);
  rerender({ open: true, search: "Market" });
  await waitFor(() => expect(result.current.isError).toBe(true));
  expect(result.current.receipts.map((item) => item.id)).toEqual(["loaded"]);
  const count = get.mock.calls.length;
  rerender({ open: false, search: "Market" });
  act(() => {
    result.current.retry();
    result.current.loadMore();
    result.current.retryBrowse();
  });
  expect(get).toHaveBeenCalledTimes(count);
});
