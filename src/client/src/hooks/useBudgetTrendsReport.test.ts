import { renderHook, waitFor } from "@testing-library/react";
import { createQueryWrapper } from "@/test/test-utils";
import {
  useBudgetTrendsReport,
  type BudgetTrendsParams,
} from "./useBudgetTrendsReport";
import client from "@/lib/api-client";
vi.mock("@/lib/api-client", () => ({ default: { GET: vi.fn() } }));
const get = vi.mocked(client.GET);
beforeEach(() => vi.clearAllMocks());

const report = {
  groupBy: "envelope",
  series: [
    {
      id: "11111111-1111-1111-1111-111111111111",
      name: "Groceries",
      isActive: true,
      currency: "USD",
      isOther: false,
      total: 120,
      purchaseCount: 3,
    },
  ],
  buckets: [{ period: "2025-01", amounts: [120] }],
};

it("sends every filter, isolates query caches and forwards cancellation", async () => {
  get.mockResolvedValue({ data: report, response: new Response() } as never);
  const initial: BudgetTrendsParams = {
    groupBy: "envelope",
    granularity: "monthly",
    topN: 7,
  };
  const { result, rerender } = renderHook(
    (params: BudgetTrendsParams) => useBudgetTrendsReport(params),
    { initialProps: initial, wrapper: createQueryWrapper() },
  );
  await waitFor(() => expect(result.current.isSuccess).toBe(true));
  expect(result.current.data).toEqual(report);
  expect(get).toHaveBeenCalledWith("/api/reports/budget-trends", {
    params: { query: initial },
    signal: expect.any(AbortSignal),
  });
  const changes: Partial<BudgetTrendsParams>[] = [
    { groupBy: "occasion" },
    { envelopeId: "22222222-2222-2222-2222-222222222222" },
    { occasionId: "33333333-3333-3333-3333-333333333333" },
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
    expect(get).toHaveBeenLastCalledWith("/api/reports/budget-trends", {
      params: { query: params },
      signal: expect.any(AbortSignal),
    });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
  }
});

it("surfaces the problem document as the query error", async () => {
  const problem = { status: 400, detail: "topN must be between 1 and 50" };
  get.mockResolvedValue({
    error: problem,
    response: new Response(null, { status: 400 }),
  } as never);
  const { result } = renderHook(
    () => useBudgetTrendsReport({ groupBy: "envelope" }),
    { wrapper: createQueryWrapper() },
  );
  await waitFor(() => expect(result.current.isError).toBe(true));
  expect(result.current.error).toEqual(problem);
});

it("clears the prior response while loading a different grouping", async () => {
  get.mockResolvedValueOnce({
    data: report,
    response: new Response(),
  } as never);
  const { result, rerender } = renderHook(
    (params: BudgetTrendsParams) => useBudgetTrendsReport(params),
    {
      initialProps: { groupBy: "envelope" } as BudgetTrendsParams,
      wrapper: createQueryWrapper(),
    },
  );
  await waitFor(() => expect(result.current.isSuccess).toBe(true));
  get.mockImplementationOnce(() => new Promise(() => {}));
  rerender({ groupBy: "occasion" });
  await waitFor(() => expect(result.current.isLoading).toBe(true));
  expect(result.current.data).toBeUndefined();
});
