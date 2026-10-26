import { renderHook, waitFor } from "@testing-library/react";
import { createQueryWrapper } from "@/test/test-utils";
import { useUncategorizedItemsReport } from "./useUncategorizedItemsReport";

vi.mock("@/lib/api-client", () => ({
  default: {
    GET: vi.fn(),
  },
}));

import client from "@/lib/api-client";
const mockClient = vi.mocked(client);

describe("useUncategorizedItemsReport", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("fetches report data with default parameters", async () => {
    const mockData = {
      totalCount: 2,
      items: [
        {
          id: "item-1",
          receiptId: "receipt-1",
          receiptItemCode: "ABC",
          description: "Test Item",
          quantity: 1,
          unitPrice: 5.0,
          totalAmount: 5.0,
          totalAmountCurrency: "USD",
          classificationState: "missing_assignment",
          assignmentId: null,
          categoryId: null,
          taxonomyRevisionId: null,
        },
      ],
    };
    mockClient.GET.mockResolvedValue({
      data: mockData,
      error: undefined,
      response: {} as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    const { result } = renderHook(() => useUncategorizedItemsReport(), {
      wrapper: createQueryWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(mockData);
    expect(mockClient.GET).toHaveBeenCalledWith(
      "/api/reports/uncategorized-items",
      {
        params: {
          query: {
            sortBy: undefined,
            sortDirection: undefined,
            page: undefined,
            pageSize: undefined,
          },
        },
      },
    );
  });

  it("passes custom parameters", async () => {
    const mockData = { totalCount: 0, items: [] };
    mockClient.GET.mockResolvedValue({
      data: mockData,
      error: undefined,
      response: {} as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    const { result } = renderHook(
      () =>
        useUncategorizedItemsReport({
          sortBy: "total",
          sortDirection: "desc",
          page: 2,
          pageSize: 25,
        }),
      { wrapper: createQueryWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(mockClient.GET).toHaveBeenCalledWith(
      "/api/reports/uncategorized-items",
      {
        params: {
          query: {
            sortBy: "total",
            sortDirection: "desc",
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

    const { result } = renderHook(() => useUncategorizedItemsReport(), {
      wrapper: createQueryWrapper(),
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error).toEqual(apiError);
  });
});
