import { renderHook, waitFor } from "@testing-library/react";
import { createQueryWrapper } from "@/test/test-utils";
import { useSpendingByNormalizedDescription } from "./useSpendingByNormalizedDescription";

vi.mock("@/lib/api-client", () => ({
  default: {
    GET: vi.fn(),
  },
}));

import client from "@/lib/api-client";
const mockClient = vi.mocked(client);

describe("useSpendingByNormalizedDescription", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("fetches report data with default parameters", async () => {
    const mockData = {
      totalCount: 2,
      grandTotal: 52.5,
      items: [
        {
          canonicalName: "Apples",
          totalAmount: 12.5,
          currency: "USD",
          itemCount: 3,
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
      () => useSpendingByNormalizedDescription(),
      { wrapper: createQueryWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(mockData);
    expect(mockClient.GET).toHaveBeenCalledWith(
      "/api/reports/spending-by-normalized-description",
      {
        params: {
          query: {
            from: undefined,
            to: undefined,
            sortBy: undefined,
            sortDirection: undefined,
            page: undefined,
            pageSize: undefined,
          },
        },
      },
    );
  });

  it("passes custom sort/page parameters through to the request", async () => {
    const mockData = { totalCount: 0, grandTotal: 0, items: [] };
    mockClient.GET.mockResolvedValue({
      data: mockData,
      error: undefined,
      response: {} as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    const { result } = renderHook(
      () =>
        useSpendingByNormalizedDescription({
          from: "2025-01-01",
          to: "2025-12-31",
          sortBy: "canonicalName",
          sortDirection: "asc",
          page: 2,
          pageSize: 25,
        }),
      { wrapper: createQueryWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(mockClient.GET).toHaveBeenCalledWith(
      "/api/reports/spending-by-normalized-description",
      {
        params: {
          query: {
            from: "2025-01-01",
            to: "2025-12-31",
            sortBy: "canonicalName",
            sortDirection: "asc",
            page: 2,
            pageSize: 25,
          },
        },
      },
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

    const { result } = renderHook(
      () => useSpendingByNormalizedDescription(),
      { wrapper: createQueryWrapper() },
    );

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error).toEqual(apiError);
  });

  it("treats sortBy/sortDirection/page as part of the query key (distinct cache entries)", async () => {
    const pageOneData = { totalCount: 2, grandTotal: 10, items: [] };
    const pageTwoData = { totalCount: 2, grandTotal: 10, items: [] };
    mockClient.GET.mockImplementation((async (
      _path: string,
      options: { params: { query: { page?: number } } },
    ) => {
      const data = options.params.query.page === 2 ? pageTwoData : pageOneData;
      return { data, error: undefined, response: {} as Response };
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    }) as any);

    const wrapper = createQueryWrapper();
    const { result, rerender } = renderHook(
      ({ page }: { page: number }) =>
        useSpendingByNormalizedDescription({
          sortBy: "totalAmount",
          sortDirection: "desc",
          page,
          pageSize: 50,
        }),
      { wrapper, initialProps: { page: 1 } },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(mockClient.GET).toHaveBeenCalledTimes(1);

    rerender({ page: 2 });

    // A distinct query key for page 2 triggers a fresh fetch instead of
    // reusing page 1's cached result.
    await waitFor(() => expect(mockClient.GET).toHaveBeenCalledTimes(2));
    expect(mockClient.GET).toHaveBeenLastCalledWith(
      "/api/reports/spending-by-normalized-description",
      {
        params: {
          query: {
            from: undefined,
            to: undefined,
            sortBy: "totalAmount",
            sortDirection: "desc",
            page: 2,
            pageSize: 50,
          },
        },
      },
    );
  });
});
