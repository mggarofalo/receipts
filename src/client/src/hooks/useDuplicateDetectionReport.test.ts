import { renderHook, waitFor } from "@testing-library/react";
import { createQueryWrapper } from "@/test/test-utils";
import { useDuplicateDetectionReport } from "./useDuplicateDetectionReport";

vi.mock("@/lib/api-client", () => ({
  default: {
    GET: vi.fn(),
  },
}));

import client from "@/lib/api-client";
const mockClient = vi.mocked(client);

const emptyReport = {
  groupCount: 0,
  totalDuplicateReceipts: 0,
  groups: [],
};

describe("useDuplicateDetectionReport", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("fetches report data with default parameters", async () => {
    const mockData = {
      groupCount: 1,
      totalDuplicateReceipts: 2,
      groups: [
        {
          matchKey: "2025-03-01 @ Store A",
          isAccepted: false,
          receipts: [
            {
              receiptId: "id-1",
              location: "Store A",
              date: "2025-03-01",
              transactionTotal: 25.5,
            },
            {
              receiptId: "id-2",
              location: "Store A",
              date: "2025-03-01",
              transactionTotal: 30.0,
            },
          ],
        },
      ],
    };
    mockClient.GET.mockResolvedValue({
      data: mockData,
      error: undefined,
      response: {} as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    const { result } = renderHook(() => useDuplicateDetectionReport(), {
      wrapper: createQueryWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(mockData);
    expect(mockClient.GET).toHaveBeenCalledWith("/api/reports/duplicates", {
      params: {
        query: {
          matchOn: undefined,
          locationTolerance: undefined,
          totalTolerance: undefined,
          includeAccepted: undefined,
        },
      },
    });
  });

  it("passes custom parameters", async () => {
    mockClient.GET.mockResolvedValue({
      data: emptyReport,
      error: undefined,
      response: {} as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    const { result } = renderHook(
      () =>
        useDuplicateDetectionReport({
          matchOn: "dateAndTotal",
          locationTolerance: "normalized",
          totalTolerance: 0.05,
        }),
      { wrapper: createQueryWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(mockClient.GET).toHaveBeenCalledWith("/api/reports/duplicates", {
      params: {
        query: {
          matchOn: "dateAndTotal",
          locationTolerance: "normalized",
          totalTolerance: 0.05,
          includeAccepted: undefined,
        },
      },
    });
  });

  it("forwards includeAccepted in the query string", async () => {
    mockClient.GET.mockResolvedValue({
      data: emptyReport,
      error: undefined,
      response: {} as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    const { result } = renderHook(
      () =>
        useDuplicateDetectionReport({
          matchOn: "dateAndLocation",
          locationTolerance: "exact",
          totalTolerance: 0,
          includeAccepted: true,
        }),
      { wrapper: createQueryWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(mockClient.GET).toHaveBeenCalledWith("/api/reports/duplicates", {
      params: {
        query: {
          matchOn: "dateAndLocation",
          locationTolerance: "exact",
          totalTolerance: 0,
          includeAccepted: true,
        },
      },
    });
  });

  it("caches includeAccepted variants separately", async () => {
    mockClient.GET.mockResolvedValue({
      data: emptyReport,
      error: undefined,
      response: {} as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    // One wrapper => one QueryClient shared across rerenders, so a second
    // fetch can only happen if the query key actually changed.
    const wrapper = createQueryWrapper();
    const { result, rerender } = renderHook(
      ({ includeAccepted }: { includeAccepted?: boolean }) =>
        useDuplicateDetectionReport({
          matchOn: "dateAndLocation",
          includeAccepted,
        }),
      { wrapper, initialProps: {} as { includeAccepted?: boolean } },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(mockClient.GET).toHaveBeenCalledTimes(1);

    rerender({ includeAccepted: true });

    await waitFor(() => expect(mockClient.GET).toHaveBeenCalledTimes(2));
    expect(mockClient.GET).toHaveBeenLastCalledWith("/api/reports/duplicates", {
      params: {
        query: {
          matchOn: "dateAndLocation",
          locationTolerance: undefined,
          totalTolerance: undefined,
          includeAccepted: true,
        },
      },
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

    const { result } = renderHook(() => useDuplicateDetectionReport(), {
      wrapper: createQueryWrapper(),
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error).toEqual(apiError);
  });
});
