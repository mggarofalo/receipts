import { act, cleanup, renderHook, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { createElement, type ReactNode } from "react";
import { clearTokens, setTokens } from "@/lib/auth";
import { useAllAccounts } from "./useAccounts";
import { useMergeAccountMaintenance } from "./useMergeAccountMaintenance";

const transport = vi.hoisted(() => ({
  GET: vi.fn(),
  PUT: vi.fn(),
  DELETE: vi.fn(),
}));
vi.mock("@/lib/api-client", () => ({ default: transport }));
beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  clearTokens();
});
afterEach(() => cleanup());
function setup() {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, staleTime: Infinity, gcTime: 0 },
    },
  });
  const wrapper = ({ children }: { children: ReactNode }) =>
    createElement(QueryClientProvider, { client }, children);
  return { client, wrapper };
}
function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((yes, no) => {
    resolve = yes;
    reject = no;
  });
  return { promise, resolve, reject };
}

it.each(["rename", "discard"] as const)(
  "refreshes actual account reads after successful %s and preserves callback identity",
  async (operation) => {
    const { client, wrapper } = setup();
    let rows = [{ id: "account", name: "Temporary", isActive: true }];
    transport.GET.mockImplementation(async () => ({
      data: { data: rows, total: rows.length, offset: 0, limit: 500 },
    }));
    transport.PUT.mockImplementation(async () => {
      rows = [{ ...rows[0], name: "Final" }];
      return {};
    });
    transport.DELETE.mockImplementation(async () => {
      rows = [];
      return {};
    });
    const { result, rerender, unmount } = renderHook(
      () => ({
        accounts: useAllAccounts(),
        maintenance: useMergeAccountMaintenance(),
      }),
      { wrapper },
    );
    try {
      await waitFor(() =>
        expect(result.current.accounts.data?.[0]?.name).toBe("Temporary"),
      );
      const stable = result.current.maintenance;
      rerender();
      expect(result.current.maintenance).toBe(stable);
      await act(async () => {
        if (operation === "rename")
          await result.current.maintenance.renameAccount("account", "Final");
        else await result.current.maintenance.discardAccount("account");
      });
      await waitFor(() => expect(result.current.accounts.data).toEqual(rows));
      expect(result.current.maintenance).toBe(stable);
      if (operation === "rename")
        expect(transport.PUT).toHaveBeenCalledWith("/api/accounts/{id}", {
          middleware: expect.any(Array),
          params: { path: { id: "account" } },
          body: { id: "account", name: "Final", isActive: true },
        });
      else
        expect(transport.DELETE).toHaveBeenCalledWith("/api/accounts/{id}", {
          middleware: expect.any(Array),
          params: { path: { id: "account" } },
        });
    } finally {
      unmount();
      client.clear();
    }
  },
);

it.each(["response", "transport"] as const)(
  "preserves current-session %s failures for the caller without invalidating successful caches",
  async (kind) => {
    const { client, wrapper } = setup();
    const failure =
      kind === "response"
        ? { status: 409, detail: "Account is still in use" }
        : new TypeError("connection unavailable");
    if (kind === "response")
      transport.DELETE.mockResolvedValue({ error: failure });
    else transport.DELETE.mockRejectedValue(failure);
    client.setQueryData(["accounts", "all"], [{ name: "Temporary" }]);
    const { result, unmount } = renderHook(useMergeAccountMaintenance, {
      wrapper,
    });
    try {
      await expect(result.current.discardAccount("account")).rejects.toBe(
        failure,
      );
      expect(client.getQueryState(["accounts", "all"])?.isInvalidated).toBe(
        false,
      );
    } finally {
      unmount();
      client.clear();
    }
  },
);

it.each(["rename", "discard"] as const)(
  "does not deliver late %s success or failure into a replacement session",
  async (operation) => {
    for (const outcome of ["success", "failure"] as const) {
      const { client, wrapper } = setup();
      const held = deferred<{ error?: unknown }>();
      (operation === "rename"
        ? transport.PUT
        : transport.DELETE
      ).mockReturnValueOnce(held.promise);
      client.setQueryData(["accounts", "all"], [{ name: "Old account" }]);
      const { result, unmount } = renderHook(useMergeAccountMaintenance, {
        wrapper,
      });
      try {
        const pending = (
          operation === "rename"
            ? result.current.renameAccount("account", "Renamed")
            : result.current.discardAccount("account")
        ).catch((error: unknown) => error);
        // Deliberately ignore transport cancellation to exercise this hook's own completion guard.
        setTokens("new-access", `new-refresh-${operation}-${outcome}`);
        held.resolve(
          outcome === "success"
            ? {}
            : { error: { status: 409, detail: "obsolete failure" } },
        );
        await expect(pending).resolves.toMatchObject({ name: "AbortError" });
        expect(client.getQueryState(["accounts", "all"])?.isInvalidated).toBe(
          false,
        );
        const callsBefore =
          transport.PUT.mock.calls.length + transport.DELETE.mock.calls.length;
        await expect(
          result.current.discardAccount("account"),
        ).rejects.toMatchObject({ name: "AbortError" });
        expect(
          transport.PUT.mock.calls.length + transport.DELETE.mock.calls.length,
        ).toBe(callsBefore);
      } finally {
        unmount();
        client.clear();
      }
    }
  },
);
