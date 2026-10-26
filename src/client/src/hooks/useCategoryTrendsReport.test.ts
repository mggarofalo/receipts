import { renderHook, waitFor } from "@testing-library/react";
import { createQueryWrapper } from "@/test/test-utils";
import {
  useCategoryTrendsReport,
  type CategoryTrendsParams,
} from "./useCategoryTrendsReport";
import client from "@/lib/api-client";
vi.mock("@/lib/api-client", () => ({ default: { GET: vi.fn() } }));
const get = vi.mocked(client.GET);
beforeEach(() => vi.clearAllMocks());
it("sends every filter, isolates query caches and forwards cancellation", async () => {
  get.mockResolvedValue({
    data: { categories: [], buckets: [], series: [] },
    response: new Response(),
  } as never);
  const initial: CategoryTrendsParams = {
    categoryView: "asClassified",
    rollup: "assignedCategory",
    topN: 5,
    granularity: "monthly",
  };
  const { result, rerender } = renderHook(
    (params: CategoryTrendsParams) => useCategoryTrendsReport(params),
    { initialProps: initial, wrapper: createQueryWrapper() },
  );
  await waitFor(() => expect(result.current.isSuccess).toBe(true));
  const changes: Partial<CategoryTrendsParams>[] = [
    { categoryView: "currentHierarchy" },
    { rollup: "root" },
    { categoryId: "11111111-1111-1111-1111-111111111111" },
    { knowledgeCutoff: "2026-01-01T00:00:00Z" },
    { capturedCurrentRevisionId: "22222222-2222-2222-2222-222222222222" },
    { startDate: "2025-01-01" },
    { endDate: "2025-12-31" },
    { granularity: "quarterly" },
    { topN: 10 },
  ];
  let params = initial;
  for (const change of changes) {
    params = { ...params, ...change };
    const count = get.mock.calls.length;
    rerender(params);
    await waitFor(() => expect(get).toHaveBeenCalledTimes(count + 1));
    expect(get).toHaveBeenLastCalledWith("/api/reports/category-trends", {
      params: { query: params },
      signal: expect.any(AbortSignal),
    });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
  }
});
it("clears the prior response while loading a different view", async () => {
  get.mockResolvedValueOnce({
    data: { categories: ["old"], buckets: [] },
    response: new Response(),
  } as never);
  const { result, rerender } = renderHook(
    (params: CategoryTrendsParams) => useCategoryTrendsReport(params),
    {
      initialProps: { categoryView: "asClassified" } as CategoryTrendsParams,
      wrapper: createQueryWrapper(),
    },
  );
  await waitFor(() => expect(result.current.isSuccess).toBe(true));
  get.mockImplementationOnce(() => new Promise(() => {}));
  rerender({ categoryView: "currentHierarchy" });
  await waitFor(() => expect(result.current.isLoading).toBe(true));
  expect(result.current.data).toBeUndefined();
});
