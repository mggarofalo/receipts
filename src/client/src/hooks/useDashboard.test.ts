import { renderHook, waitFor } from "@testing-library/react";
import { createQueryWrapper } from "@/test/test-utils";
import {
  useDashboardSummary,
  useDashboardSpendingOverTime,
  useDashboardSpendingByCategory,
  useDashboardSpendingByAccount,
  useDashboardEarliestReceiptYear,
} from "./useDashboard";

vi.mock("@/lib/api-client", () => ({
  default: {
    GET: vi.fn(),
  },
}));

import client from "@/lib/api-client";
const mockClient = vi.mocked(client);

describe("useDashboard hooks", () => {
  const dateRange = { startDate: "2024-01-01", endDate: "2024-01-31" };

  beforeEach(() => {
    vi.clearAllMocks();
  });

  describe("useDashboardSummary", () => {
    it("fetches summary data", async () => {
      const mockData = {
        totalReceipts: 10,
        totalSpent: 500,
        averageTripAmount: 50,
        mostUsedAccount: { name: "Visa", count: 5 },
        mostUsedCategory: { name: "Food", count: 7 },
      };
      mockClient.GET.mockResolvedValue({
        data: mockData,
        error: undefined,
        response: {} as Response,
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any);

      const { result } = renderHook(() => useDashboardSummary(dateRange), {
        wrapper: createQueryWrapper(),
      });

      await waitFor(() => expect(result.current.isSuccess).toBe(true));
      expect(result.current.data).toEqual(mockData);
      expect(mockClient.GET).toHaveBeenCalledWith("/api/dashboard/summary", {
        params: { query: dateRange },
      });
    });

    it("throws when API returns an error", async () => {
      const apiError = { message: "Server error" };
      mockClient.GET.mockResolvedValue({
        data: undefined,
        error: apiError,
        response: {} as Response,
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any);

      const { result } = renderHook(() => useDashboardSummary(dateRange), {
        wrapper: createQueryWrapper(),
      });

      await waitFor(() => expect(result.current.isError).toBe(true));
      expect(result.current.error).toEqual(apiError);
    });

    it("passes undefined dates when dateRange has no values", async () => {
      const mockData = { totalReceipts: 0 };
      mockClient.GET.mockResolvedValue({
        data: mockData,
        error: undefined,
        response: {} as Response,
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any);

      const { result } = renderHook(() => useDashboardSummary({}), {
        wrapper: createQueryWrapper(),
      });

      await waitFor(() => expect(result.current.isSuccess).toBe(true));
      expect(mockClient.GET).toHaveBeenCalledWith("/api/dashboard/summary", {
        params: { query: { startDate: undefined, endDate: undefined } },
      });
    });
  });

  describe("useDashboardSpendingOverTime", () => {
    it("fetches spending over time data", async () => {
      const mockData = {
        buckets: [
          { period: "2024-01", amount: 100 },
          { period: "2024-02", amount: 200 },
        ],
      };
      mockClient.GET.mockResolvedValue({
        data: mockData,
        error: undefined,
        response: {} as Response,
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any);

      const { result } = renderHook(
        () => useDashboardSpendingOverTime(dateRange, "monthly"),
        { wrapper: createQueryWrapper() },
      );

      await waitFor(() => expect(result.current.isSuccess).toBe(true));
      expect(result.current.data).toEqual(mockData);
    });

    it("throws when API returns an error", async () => {
      const apiError = { message: "Server error" };
      mockClient.GET.mockResolvedValue({
        data: undefined,
        error: apiError,
        response: {} as Response,
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any);

      const { result } = renderHook(
        () => useDashboardSpendingOverTime(dateRange, "quarterly"),
        { wrapper: createQueryWrapper() },
      );

      await waitFor(() => expect(result.current.isError).toBe(true));
      expect(result.current.error).toEqual(apiError);
    });

    it("fetches without granularity parameter", async () => {
      const mockData = { buckets: [] };
      mockClient.GET.mockResolvedValue({
        data: mockData,
        error: undefined,
        response: {} as Response,
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any);

      const { result } = renderHook(
        () => useDashboardSpendingOverTime(dateRange),
        { wrapper: createQueryWrapper() },
      );

      await waitFor(() => expect(result.current.isSuccess).toBe(true));
      expect(mockClient.GET).toHaveBeenCalledWith(
        "/api/dashboard/spending-over-time",
        {
          params: {
            query: {
              startDate: dateRange.startDate,
              endDate: dateRange.endDate,
              granularity: undefined,
            },
          },
        },
      );
    });
  });

  describe("useDashboardSpendingByCategory", () => {
    it("fetches spending by category data", async () => {
      const mockData = {
        items: [{ categoryName: "Food", amount: 300, percentage: 60 }],
      };
      mockClient.GET.mockResolvedValue({
        data: mockData,
        error: undefined,
        response: {} as Response,
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any);

      const { result } = renderHook(
        () => useDashboardSpendingByCategory(dateRange),
        { wrapper: createQueryWrapper() },
      );

      await waitFor(() => expect(result.current.isSuccess).toBe(true));
      expect(result.current.data).toEqual(mockData);
    });

    it("throws when API returns an error", async () => {
      const apiError = { message: "Server error" };
      mockClient.GET.mockResolvedValue({
        data: undefined,
        error: apiError,
        response: {} as Response,
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any);

      const { result } = renderHook(
        () => useDashboardSpendingByCategory(dateRange),
        { wrapper: createQueryWrapper() },
      );

      await waitFor(() => expect(result.current.isError).toBe(true));
      expect(result.current.error).toEqual(apiError);
    });

    it("passes custom limit parameter", async () => {
      const mockData = { items: [] };
      mockClient.GET.mockResolvedValue({
        data: mockData,
        error: undefined,
        response: {} as Response,
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any);

      const { result } = renderHook(
        () => useDashboardSpendingByCategory(dateRange, 5),
        { wrapper: createQueryWrapper() },
      );

      await waitFor(() => expect(result.current.isSuccess).toBe(true));
      expect(mockClient.GET).toHaveBeenCalledWith(
        "/api/dashboard/spending-by-category",
        {
          signal: expect.any(AbortSignal),
          params: {
            query: {
              startDate: dateRange.startDate,
              endDate: dateRange.endDate,
              limit: 5,
              categoryView: "asClassified",
              rollup: "assignedCategory",
            },
          },
        },
      );
    });
  });

  describe("useDashboardSpendingByAccount", () => {
    it("fetches spending by account data", async () => {
      const mockData = {
        items: [
          {
            accountId: "abc",
            accountName: "Visa",
            amount: 500,
            percentage: 100,
          },
        ],
      };
      mockClient.GET.mockResolvedValue({
        data: mockData,
        error: undefined,
        response: {} as Response,
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any);

      const { result } = renderHook(
        () => useDashboardSpendingByAccount(dateRange),
        { wrapper: createQueryWrapper() },
      );

      await waitFor(() => expect(result.current.isSuccess).toBe(true));
      expect(result.current.data).toEqual(mockData);
    });

    it("throws when API returns an error", async () => {
      const apiError = { message: "Server error" };
      mockClient.GET.mockResolvedValue({
        data: undefined,
        error: apiError,
        response: {} as Response,
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any);

      const { result } = renderHook(
        () => useDashboardSpendingByAccount(dateRange),
        { wrapper: createQueryWrapper() },
      );

      await waitFor(() => expect(result.current.isError).toBe(true));
      expect(result.current.error).toEqual(apiError);
    });
  });

  describe("useDashboardEarliestReceiptYear", () => {
    it("fetches earliest receipt year", async () => {
      const mockData = { year: 2020 };
      mockClient.GET.mockResolvedValue({
        data: mockData,
        error: undefined,
        response: {} as Response,
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any);

      const { result } = renderHook(() => useDashboardEarliestReceiptYear(), {
        wrapper: createQueryWrapper(),
      });

      await waitFor(() => expect(result.current.isSuccess).toBe(true));
      expect(result.current.data).toEqual(mockData);
      expect(mockClient.GET).toHaveBeenCalledWith(
        "/api/dashboard/earliest-receipt-year",
      );
    });

    it("throws when API returns an error", async () => {
      const apiError = { message: "Server error" };
      mockClient.GET.mockResolvedValue({
        data: undefined,
        error: apiError,
        response: {} as Response,
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any);

      const { result } = renderHook(() => useDashboardEarliestReceiptYear(), {
        wrapper: createQueryWrapper(),
      });

      await waitFor(() => expect(result.current.isError).toBe(true));
      expect(result.current.error).toEqual(apiError);
    });
  });
});

it("separates selection caches and sends category filters with cancellation", async () => {
  const { useQueryClient } = await import("@tanstack/react-query");
  const { repairDomainChange } = await import("@/lib/query-invalidation");
  mockClient.GET.mockResolvedValue({
    data: { items: [], view: "currentHierarchy" },
    response: new Response(),
  } as never);
  const filters = {
    rollup: "root" as const,
    categoryId: "category",
    knowledgeCutoff: "2026-01-01T00:00:00Z",
    capturedCurrentRevisionId: "revision",
  };
  const { result, rerender } = renderHook(
    ({
      categoryView,
    }: {
      categoryView: "asClassified" | "currentHierarchy";
    }) => {
      const cache = useQueryClient();
      cache.setQueryDefaults(["dashboard", "spending-by-category"], {
        gcTime: Infinity,
      });
      return {
        query: useDashboardSpendingByCategory({}, 5, {
          ...filters,
          categoryView,
        }),
        cache,
      };
    },
    {
      initialProps: { categoryView: "currentHierarchy" },
      wrapper: createQueryWrapper(),
    },
  );
  await waitFor(() => expect(result.current.query.isSuccess).toBe(true));
  expect(mockClient.GET).toHaveBeenLastCalledWith(
    "/api/dashboard/spending-by-category",
    expect.objectContaining({
      signal: expect.any(AbortSignal),
      params: {
        query: {
          startDate: undefined,
          endDate: undefined,
          limit: 5,
          ...filters,
          categoryView: "currentHierarchy",
        },
      },
    }),
  );
  rerender({ categoryView: "asClassified" });
  await waitFor(() => expect(result.current.query.isSuccess).toBe(true));
  const cached = result.current.cache
    .getQueryCache()
    .findAll({ queryKey: ["dashboard", "spending-by-category"] });
  expect(cached).toHaveLength(2);
  await repairDomainChange(result.current.cache, "receipt-item", () => true);
  expect(
    cached.find((query) => query.queryKey[2] === "currentHierarchy")?.state
      .isInvalidated,
  ).toBe(true);
});
