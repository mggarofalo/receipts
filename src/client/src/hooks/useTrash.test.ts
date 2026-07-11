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
import { toast } from "sonner";
import { usePurgeTrash } from "./useTrash";

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

describe("usePurgeTrash", () => {
  it("posts purge and shows success toast", async () => {
    (client.POST as Mock).mockResolvedValue({ error: null });

    const { result } = renderHook(() => usePurgeTrash(), {
      wrapper: createWrapper(),
    });

    result.current.mutate();

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(client.POST).toHaveBeenCalledWith("/api/trash/purge");
    expect(toast.success).toHaveBeenCalledWith("Trash emptied successfully");
  });

  it("shows error toast on failure", async () => {
    (client.POST as Mock).mockResolvedValue({
      error: { message: "Server error" },
    });

    const { result } = renderHook(() => usePurgeTrash(), {
      wrapper: createWrapper(),
    });

    result.current.mutate();

    await waitFor(() => expect(result.current.isError).toBe(true));

    expect(toast.error).not.toHaveBeenCalled();
  });

  it("invalidates all deleted query keys on success", async () => {
    (client.POST as Mock).mockResolvedValue({ error: null });

    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false, gcTime: 0 } },
    });
    const invalidateSpy = vi.spyOn(queryClient, "invalidateQueries");

    function Wrapper({ children }: { children: ReactNode }) {
      return createElement(
        QueryClientProvider,
        { client: queryClient },
        children,
      );
    }

    const { result } = renderHook(() => usePurgeTrash(), {
      wrapper: Wrapper,
    });

    result.current.mutate();

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith({
      queryKey: ["accounts", "deleted"],
    });
    expect(invalidateSpy).toHaveBeenCalledWith({
      queryKey: ["receipts", "deleted"],
    });
    expect(invalidateSpy).toHaveBeenCalledWith({
      queryKey: ["receipt-items", "deleted"],
    });
    expect(invalidateSpy).toHaveBeenCalledWith({
      queryKey: ["transactions", "deleted"],
    });
  });
});
