import { describe, it, expect, vi, beforeEach, type Mock } from "vitest";
import { renderHook, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { createElement, type ReactNode } from "react";

vi.mock("@/lib/api-client", () => ({
  default: {
    GET: vi.fn(),
    POST: vi.fn(),
    PUT: vi.fn(),
    DELETE: vi.fn(),
  },
}));

vi.mock("sonner", () => ({
  toast: { success: vi.fn(), error: vi.fn(), info: vi.fn() },
}));

import client from "@/lib/api-client";
import { useTripByReceiptId } from "./useTrips";

function createWrapper() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  });
  return function Wrapper({ children }: { children: ReactNode }) {
    return createElement(QueryClientProvider, { client: queryClient }, children);
  };
}

beforeEach(() => {
  vi.clearAllMocks();
});

describe("useTripByReceiptId", () => {
  it("fetches trip when receiptId is provided", async () => {
    const mockTrip = { id: "t1", name: "Grocery Run", receiptId: "r1" };
    (client.GET as Mock).mockResolvedValue({ data: mockTrip, error: null });

    const { result } = renderHook(() => useTripByReceiptId("r1"), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(client.GET).toHaveBeenCalledWith(
      "/api/trips",
      { middleware: expect.any(Array), params: { query: { receiptId: "r1" } } },
    );
    expect(result.current.data).toEqual(mockTrip);
  });

  it("does not fetch when receiptId is null", () => {
    const { result } = renderHook(() => useTripByReceiptId(null), {
      wrapper: createWrapper(),
    });

    expect(result.current.fetchStatus).toBe("idle");
    expect(client.GET).not.toHaveBeenCalled();
  });

  it("propagates error when GET fails", async () => {
    (client.GET as Mock).mockResolvedValue({
      data: null,
      error: { message: "Not found" },
    });

    const { result } = renderHook(() => useTripByReceiptId("r1"), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
  });
});
