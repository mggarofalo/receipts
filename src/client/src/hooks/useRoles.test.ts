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
import { useUserRoles, useAssignRole, useRemoveRole } from "./useRoles";

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

describe("useUserRoles", () => {
  it("fetches roles for a given userId", async () => {
    const mockRoles = ["Admin", "User"];
    (client.GET as Mock).mockResolvedValue({ data: mockRoles, error: null });

    const { result } = renderHook(() => useUserRoles("u1"), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(client.GET).toHaveBeenCalledWith("/api/users/{userId}/roles", {
      params: { path: { userId: "u1" } },
    });
    expect(result.current.data).toEqual(mockRoles);
  });

  it("does not fetch when userId is null", () => {
    const { result } = renderHook(() => useUserRoles(null), {
      wrapper: createWrapper(),
    });

    expect(result.current.fetchStatus).toBe("idle");
    expect(client.GET).not.toHaveBeenCalled();
  });
});

describe("useAssignRole", () => {
  it("posts role assignment and shows success toast with role name", async () => {
    (client.POST as Mock).mockResolvedValue({ error: null });

    const { result } = renderHook(() => useAssignRole(), {
      wrapper: createWrapper(),
    });

    result.current.mutate({ userId: "u1", role: "Admin" });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(client.POST).toHaveBeenCalledWith(
      "/api/users/{userId}/roles/{role}",
      { params: { path: { userId: "u1", role: "Admin" } } },
    );
    expect(toast.success).toHaveBeenCalledWith('Role "Admin" assigned');
  });
});

describe("useRemoveRole", () => {
  it("deletes role and shows success toast with role name", async () => {
    (client.DELETE as Mock).mockResolvedValue({ error: null });

    const { result } = renderHook(() => useRemoveRole(), {
      wrapper: createWrapper(),
    });

    result.current.mutate({ userId: "u1", role: "Admin" });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(client.DELETE).toHaveBeenCalledWith(
      "/api/users/{userId}/roles/{role}",
      { params: { path: { userId: "u1", role: "Admin" } } },
    );
    expect(toast.success).toHaveBeenCalledWith('Role "Admin" removed');
  });

  it("shows error toast on failure", async () => {
    (client.DELETE as Mock).mockResolvedValue({
      error: { message: "Forbidden" },
    });

    const { result } = renderHook(() => useRemoveRole(), {
      wrapper: createWrapper(),
    });

    result.current.mutate({ userId: "u1", role: "Admin" });

    await waitFor(() => expect(result.current.isError).toBe(true));

    expect(toast.error).not.toHaveBeenCalled();
  });
});
