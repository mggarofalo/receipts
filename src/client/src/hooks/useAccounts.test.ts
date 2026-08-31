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
import {
  useAccounts,
  useAllAccounts,
  useAccount,
  useAccountCards,
  useCreateAccount,
  useUpdateAccount,
  useDeleteAccount,
} from "./useAccounts";

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

describe("useAccounts", () => {
  it("list query returns data on success", async () => {
    const accounts = [{ id: "a1", name: "Apple Card", isActive: true }];
    (client.GET as Mock).mockResolvedValue({
      data: { data: accounts, total: 1, offset: 0, limit: 50 },
      error: undefined,
    });

    const { result } = renderHook(() => useAccounts(), { wrapper: createWrapper() });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(accounts);
    expect(client.GET).toHaveBeenCalledWith("/api/accounts", {
      params: { query: { offset: 0, limit: 50 } },
    });
  });

  it("list query passes isActive filter", async () => {
    (client.GET as Mock).mockResolvedValue({ data: { data: [], total: 0, offset: 0, limit: 50 }, error: undefined });

    const { result } = renderHook(() => useAccounts(0, 50, null, null, true), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(client.GET).toHaveBeenCalledWith("/api/accounts", {
      params: { query: { offset: 0, limit: 50, isActive: true } },
    });
  });

  it("list query trims q and sends it to the API", async () => {
    (client.GET as Mock).mockResolvedValue({ data: { data: [], total: 0, offset: 0, limit: 50 } });
    const { result } = renderHook(
      () => useAccounts(0, 50, null, null, null, { q: "  chase  " }),
      { wrapper: createWrapper() },
    );
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(client.GET).toHaveBeenCalledWith("/api/accounts", {
      params: { query: { offset: 0, limit: 50, q: "chase" } },
    });
  });

  it("includes q in the cache key so different searches make different requests", async () => {
    (client.GET as Mock).mockResolvedValue({ data: { data: [], total: 0, offset: 0, limit: 50 } });
    const { result, rerender } = renderHook(
      ({ q }) => useAccounts(0, 50, null, null, null, { q }),
      { initialProps: { q: "chase" }, wrapper: createWrapper() },
    );
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    rerender({ q: "amex" });
    await waitFor(() => expect(client.GET).toHaveBeenCalledTimes(2));
    expect(client.GET).toHaveBeenLastCalledWith("/api/accounts", {
      params: { query: { offset: 0, limit: 50, q: "amex" } },
    });
  });

  it("list query throws when API returns error", async () => {
    (client.GET as Mock).mockResolvedValue({ data: undefined, error: { message: "boom" } });

    const { result } = renderHook(() => useAccounts(), { wrapper: createWrapper() });

    await waitFor(() => expect(result.current.isError).toBe(true));
  });
});

describe("useAllAccounts", () => {
  it("auto-paginates across multiple pages", async () => {
    const pageOne = Array.from({ length: 500 }, (_, index) => ({
      id: `account-${index}`,
      name: `Account ${index}`,
    }));
    const pageTwo = Array.from({ length: 100 }, (_, index) => ({
      id: `account-${500 + index}`,
      name: `Account ${500 + index}`,
    }));
    (client.GET as Mock).mockImplementation((_path, options) => {
      const offset = options.params.query.offset;
      return Promise.resolve({
        data: {
          data: offset === 0 ? pageOne : pageTwo,
          total: 600,
          offset,
          limit: 500,
        },
        error: undefined,
      });
    });

    const { result } = renderHook(() => useAllAccounts(true), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toHaveLength(600);
    expect(client.GET).toHaveBeenCalledTimes(2);
    expect(client.GET).toHaveBeenLastCalledWith("/api/accounts", {
      params: {
        query: {
          offset: 500,
          limit: 500,
          sortBy: "name",
          sortDirection: "asc",
          isActive: true,
        },
      },
      signal: expect.any(AbortSignal),
    });
  });
});

describe("useAccount", () => {
  it("fetches a single account by id", async () => {
    const account = { id: "a1", name: "Apple Card", isActive: true };
    (client.GET as Mock).mockResolvedValue({ data: account, error: undefined });

    const { result } = renderHook(() => useAccount("a1"), { wrapper: createWrapper() });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(account);
    expect(client.GET).toHaveBeenCalledWith("/api/accounts/{id}", {
      params: { path: { id: "a1" } },
    });
  });

  it("does not fire query when id is null", () => {
    renderHook(() => useAccount(null), { wrapper: createWrapper() });

    expect(client.GET).not.toHaveBeenCalled();
  });
});

describe("useAccountCards", () => {
  it("fetches cards for the given account", async () => {
    const cards = [
      { id: "c1", cardCode: "VISA1", name: "Physical A", isActive: true },
      { id: "c2", cardCode: "VISA2", name: "Physical B", isActive: true },
    ];
    (client.GET as Mock).mockResolvedValue({ data: cards, error: undefined });

    const { result } = renderHook(() => useAccountCards("a1"), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.data).toEqual(cards));
    expect(client.GET).toHaveBeenCalledWith("/api/accounts/{id}/cards", {
      params: { path: { id: "a1" } },
    });
  });

  it("does not fire query when accountId is null", () => {
    renderHook(() => useAccountCards(null), { wrapper: createWrapper() });

    expect(client.GET).not.toHaveBeenCalled();
  });

  it("throws when API returns error", async () => {
    (client.GET as Mock).mockResolvedValue({ data: undefined, error: { message: "not found" } });

    const { result } = renderHook(() => useAccountCards("a1"), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
  });
});

describe("useCreateAccount", () => {
  it("creates an account and toasts success", async () => {
    const created = { id: "a1", name: "Apple Card", isActive: true };
    (client.POST as Mock).mockResolvedValue({ data: created, error: undefined });

    const { result } = renderHook(() => useCreateAccount(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync({ name: "Apple Card", isActive: true });

    expect(client.POST).toHaveBeenCalledWith("/api/accounts", {
      body: { name: "Apple Card", isActive: true },
    });
    expect(toast.success).toHaveBeenCalledWith("Account created");
  });

  it("does not toast on failure (surfaced by the global handler)", async () => {
    (client.POST as Mock).mockResolvedValue({ data: undefined, error: { message: "boom" } });

    const { result } = renderHook(() => useCreateAccount(), {
      wrapper: createWrapper(),
    });

    await expect(
      result.current.mutateAsync({ name: "Apple Card", isActive: true }),
    ).rejects.toBeDefined();
    expect(toast.error).not.toHaveBeenCalled();
  });
});

describe("useUpdateAccount", () => {
  it("updates an account and toasts success", async () => {
    (client.PUT as Mock).mockResolvedValue({ data: undefined, error: undefined });

    const { result } = renderHook(() => useUpdateAccount(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync({ id: "a1", name: "Apple", isActive: false });

    expect(client.PUT).toHaveBeenCalledWith("/api/accounts/{id}", {
      params: { path: { id: "a1" } },
      body: { id: "a1", name: "Apple", isActive: false },
    });
    expect(toast.success).toHaveBeenCalledWith("Account updated");
  });
});

describe("useDeleteAccount", () => {
  it("deletes an account and toasts success", async () => {
    (client.DELETE as Mock).mockResolvedValue({
      data: undefined,
      error: undefined,
      response: { status: 204 },
    });

    const { result } = renderHook(() => useDeleteAccount(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync("a1");

    expect(client.DELETE).toHaveBeenCalledWith("/api/accounts/{id}", {
      params: { path: { id: "a1" } },
    });
    expect(toast.success).toHaveBeenCalledWith("Account deleted");
  });

  it("surfaces the 409 card-count conflict reason", async () => {
    // RECEIPTS-886: the body is a problem document now. The prose moved from
    // `message` to `detail`; `cardCount` stayed put, because RFC 9457 extension
    // members serialise at the top level of the object.
    (client.DELETE as Mock).mockResolvedValue({
      data: undefined,
      error: {
        type: "https://tools.ietf.org/html/rfc9110#section-15.5.10",
        title: "Conflict",
        status: 409,
        detail: "Cannot delete — 3 card(s) reference this account",
        cardCount: 3,
      },
      response: { status: 409 },
    });

    const { result } = renderHook(() => useDeleteAccount(), {
      wrapper: createWrapper(),
    });

    await expect(result.current.mutateAsync("a1")).rejects.toMatchObject({
      conflict: true,
      cardCount: 3,
    });
    expect(toast.error).toHaveBeenCalledWith(
      "Cannot delete — 3 card(s) reference this account",
    );
  });
});
