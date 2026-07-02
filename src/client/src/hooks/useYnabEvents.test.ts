import { describe, it, expect, vi, beforeEach, type Mock } from "vitest";
import { renderHook, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { createElement, type ReactNode } from "react";

vi.mock("@/lib/api-client", () => ({
  default: { GET: vi.fn(), POST: vi.fn(), PUT: vi.fn(), DELETE: vi.fn() },
}));

import client from "@/lib/api-client";
import { useYnabEvents } from "./useYnabEvents";

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

describe("useYnabEvents", () => {
  it("returns paginated data and total, and forwards the outcome filter", async () => {
    const page = {
      data: [
        {
          id: "1",
          occurredAt: "2026-06-01T00:00:00Z",
          eventType: "Push",
          receiptId: null,
          transactionId: null,
          httpStatus: 201,
          success: true,
          errorMessage: null,
          requestId: null,
        },
      ],
      total: 1,
      offset: 0,
      limit: 50,
    };
    (client.GET as Mock).mockResolvedValue({ data: page, error: undefined });

    const { result } = renderHook(() => useYnabEvents({ outcome: "success" }), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toHaveLength(1);
    expect(result.current.total).toBe(1);
    expect((client.GET as Mock).mock.calls[0][1].params.query.outcome).toBe("success");
  });

  it("throws (sets error state) when the API returns an error", async () => {
    (client.GET as Mock).mockResolvedValue({ data: undefined, error: new Error("boom") });

    const { result } = renderHook(() => useYnabEvents(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.data).toEqual([]);
  });
});
