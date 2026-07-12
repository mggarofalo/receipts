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
import {
  useMyAuthAuditLog,
  useRecentAuthAuditLogs,
  useFailedAuthAttempts,
} from "./useAuthAudit";

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

describe("useMyAuthAuditLog", () => {
  it("fetches my auth audit logs with default params", async () => {
    const mockLogs = [{ id: "log1", event: "Login" }];
    (client.GET as Mock).mockResolvedValue({ data: { data: mockLogs, total: 1, offset: 0, limit: 50 }, error: null });

    const { result } = renderHook(() => useMyAuthAuditLog(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(client.GET).toHaveBeenCalledWith("/api/auth/audit/me", {
      params: {
        query: {
          offset: 0,
          limit: 50,
          sortBy: undefined,
          sortDirection: undefined,
        },
      },
    });
    expect(result.current.data).toEqual(mockLogs);
  });

  it("fetches my auth audit logs with custom offset and limit", async () => {
    const mockLogs = [{ id: "log1" }];
    (client.GET as Mock).mockResolvedValue({ data: { data: mockLogs, total: 1, offset: 10, limit: 25 }, error: null });

    const { result } = renderHook(() => useMyAuthAuditLog(10, 25), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(client.GET).toHaveBeenCalledWith("/api/auth/audit/me", {
      params: {
        query: {
          offset: 10,
          limit: 25,
          sortBy: undefined,
          sortDirection: undefined,
        },
      },
    });
  });
});

describe("useRecentAuthAuditLogs", () => {
  it("fetches recent auth audit logs with default params", async () => {
    const mockLogs = [{ id: "log2", event: "Logout" }];
    (client.GET as Mock).mockResolvedValue({ data: { data: mockLogs, total: 1, offset: 0, limit: 50 }, error: null });

    const { result } = renderHook(() => useRecentAuthAuditLogs(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(client.GET).toHaveBeenCalledWith("/api/auth/audit/recent", {
      params: {
        query: {
          offset: 0,
          limit: 50,
          sortBy: undefined,
          sortDirection: undefined,
        },
      },
    });
    expect(result.current.data).toEqual(mockLogs);
  });

  it("fetches recent auth audit logs with custom offset and limit", async () => {
    (client.GET as Mock).mockResolvedValue({ data: { data: [], total: 0, offset: 5, limit: 20 }, error: null });

    const { result } = renderHook(() => useRecentAuthAuditLogs(5, 20), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(client.GET).toHaveBeenCalledWith("/api/auth/audit/recent", {
      params: {
        query: {
          offset: 5,
          limit: 20,
          sortBy: undefined,
          sortDirection: undefined,
        },
      },
    });
  });

  it("does not fetch when enabled is false", () => {
    const { result } = renderHook(
      () => useRecentAuthAuditLogs(0, 50, undefined, undefined, { enabled: false }),
      { wrapper: createWrapper() },
    );

    expect(result.current.fetchStatus).toBe("idle");
    expect(client.GET).not.toHaveBeenCalled();
  });
});

describe("useFailedAuthAttempts", () => {
  it("fetches failed auth attempts with default params", async () => {
    const mockAttempts = [{ id: "f1", reason: "InvalidPassword" }];
    (client.GET as Mock).mockResolvedValue({
      data: { data: mockAttempts, total: 1, offset: 0, limit: 50 },
      error: null,
    });

    const { result } = renderHook(() => useFailedAuthAttempts(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(client.GET).toHaveBeenCalledWith("/api/auth/audit/failed", {
      params: {
        query: {
          offset: 0,
          limit: 50,
          sortBy: undefined,
          sortDirection: undefined,
        },
      },
    });
    expect(result.current.data).toEqual(mockAttempts);
  });

  it("fetches failed auth attempts with custom offset and limit", async () => {
    (client.GET as Mock).mockResolvedValue({ data: { data: [], total: 0, offset: 100, limit: 10 }, error: null });

    const { result } = renderHook(() => useFailedAuthAttempts(100, 10), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(client.GET).toHaveBeenCalledWith("/api/auth/audit/failed", {
      params: {
        query: {
          offset: 100,
          limit: 10,
          sortBy: undefined,
          sortDirection: undefined,
        },
      },
    });
  });

  it("fetches failed auth attempts with sort params", async () => {
    (client.GET as Mock).mockResolvedValue({ data: { data: [], total: 0, offset: 0, limit: 50 }, error: null });

    const { result } = renderHook(
      () => useFailedAuthAttempts(0, 50, "timestamp", "asc"),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(client.GET).toHaveBeenCalledWith("/api/auth/audit/failed", {
      params: {
        query: {
          offset: 0,
          limit: 50,
          sortBy: "timestamp",
          sortDirection: "asc",
        },
      },
    });
  });

  it("does not fetch when enabled is false", () => {
    const { result } = renderHook(
      () => useFailedAuthAttempts(0, 50, undefined, undefined, { enabled: false }),
      { wrapper: createWrapper() },
    );

    expect(result.current.fetchStatus).toBe("idle");
    expect(client.GET).not.toHaveBeenCalled();
  });
});
