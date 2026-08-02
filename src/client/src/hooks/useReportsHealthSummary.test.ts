import { renderHook, waitFor } from "@testing-library/react";
import { createQueryWrapper } from "@/test/test-utils";
import { useReportsHealthSummary } from "./useReportsHealthSummary";

vi.mock("@/lib/api-client", () => ({
  default: {
    GET: vi.fn(),
  },
}));

import client from "@/lib/api-client";
const mockClient = vi.mocked(client);

describe("useReportsHealthSummary", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("fetches the health summary counts", async () => {
    const mockData = {
      outOfBalanceCount: 3,
      duplicateGroupCount: 2,
      uncategorizedItemCount: 17,
    };
    mockClient.GET.mockResolvedValue({
      data: mockData,
      error: undefined,
      response: {} as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    const { result } = renderHook(() => useReportsHealthSummary(), {
      wrapper: createQueryWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(mockData);
    expect(mockClient.GET).toHaveBeenCalledWith(
      "/api/reports/health-summary",
    );
  });

  it("throws when the API returns an error", async () => {
    const apiError = { message: "Server error" };
    mockClient.GET.mockResolvedValue({
      data: undefined,
      error: apiError,
      response: {} as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    const { result } = renderHook(() => useReportsHealthSummary(), {
      wrapper: createQueryWrapper(),
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error).toEqual(apiError);
  });
});
