import { createElement, type ReactNode } from "react";
import { act, cleanup, renderHook, waitFor } from "@testing-library/react";
import { QueryClientProvider, useQuery } from "@tanstack/react-query";
import { http, HttpResponse } from "msw";
import { clearTokens, setTokens } from "@/lib/auth";
import { createAppQueryClient } from "@/lib/query-client";
import { addServerErrorListener } from "@/lib/server-error-bus";
import { showError, showSuccess } from "@/lib/toast";
import { server } from "@/test/msw/server";
import { useBackupImport } from "./useBackupImport";

vi.hoisted(() => vi.stubEnv("VITE_API_URL", "http://backup-import.test"));
vi.mock("@/lib/toast", () => ({ showSuccess: vi.fn(), showError: vi.fn() }));
function deferred() {
  let resolve!: () => void;
  const promise = new Promise<void>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}
let client: ReturnType<typeof createAppQueryClient>;
let status: number;
let importGate: ReturnType<typeof deferred> | undefined;
let bodyStarted: ReturnType<typeof deferred>;
const file = () => new File(["backup"], "backup.sqlite");
function Wrapper({ children }: { children: ReactNode }) {
  return createElement(QueryClientProvider, { client }, children);
}
beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
afterAll(() => server.close());
beforeEach(() => {
  vi.clearAllMocks();
  setTokens("Alice-access", "Alice-refresh");
  client = createAppQueryClient();
  status = 200;
  importGate = undefined;
  bodyStarted = deferred();
  server.use(
    http.post("*/api/backup/import", async () => {
      bodyStarted.resolve();
      await importGate?.promise;
      return HttpResponse.json(
        status === 200
          ? { totalCreated: 0, totalUpdated: 0 }
          : { status, detail: "Unavailable" },
        { status },
      );
    }),
  );
});
afterEach(() => {
  importGate?.resolve();
  cleanup();
  client.clear();
  clearTokens();
  server.resetHandlers();
  vi.restoreAllMocks();
});

it("repairs current restored data once per active query, including overlapping YNAB keys, while excluding private caches", async () => {
  let restored = false;
  const reads = vi.fn(async () =>
    restored ? "restored-budget" : "old-budget",
  );
  client.setQueryData(["api-keys", "Alice"], "private-key");
  client.setQueryData(["users"], "private-user");
  client.setQueryData(["normalized-descriptions", "settings"], "old-settings");
  client.setQueryData(["receipts"], "old-receipts");
  const { result } = renderHook(
    () => ({
      action: useBackupImport(),
      budget: useQuery({
        queryKey: ["ynab", "sync-status", "receipt"],
        queryFn: reads,
        staleTime: Infinity,
      }),
    }),
    { wrapper: Wrapper },
  );
  await waitFor(() => expect(result.current.budget.data).toBe("old-budget"));
  restored = true;
  await act(async () => {
    expect(await result.current.action.mutateAsync(file())).toMatchObject({
      totalCreated: 0,
    });
  });
  await waitFor(() =>
    expect(result.current.budget.data).toBe("restored-budget"),
  );
  expect(reads).toHaveBeenCalledTimes(2);
  expect(
    client.getQueryState(["normalized-descriptions", "settings"])
      ?.isInvalidated,
  ).toBe(true);
  expect(client.getQueryState(["receipts"])?.isInvalidated).toBe(true);
  expect(client.getQueryState(["api-keys", "Alice"])?.isInvalidated).toBe(
    false,
  );
  expect(client.getQueryState(["users"])?.isInvalidated).toBe(false);
  expect(showSuccess).toHaveBeenCalledOnce();
});

it.each([400, 403, 503])(
  "owns one local%s error without clearing restored-data caches",
  async (failure) => {
    status = failure;
    client.setQueryData(["receipts"], "unchanged");
    const globalError = vi.fn();
    const unsubscribe = addServerErrorListener(globalError);
    try {
      const { result } = renderHook(() => useBackupImport(), {
        wrapper: Wrapper,
      });
      await act(async () => {
        await result.current.mutateAsync(file()).catch(() => {});
      });
      expect(showError).toHaveBeenCalledOnce();
      expect(globalError).not.toHaveBeenCalled();
      expect(showSuccess).not.toHaveBeenCalled();
      expect(client.getQueryState(["receipts"])?.isInvalidated).toBe(false);
    } finally {
      unsubscribe();
    }
  },
);

it("does not turn a post-commit read failure into a failed import", async () => {
  let fail = false;
  const read = vi.fn(async () => {
    if (fail) throw new Error("Read temporarily unavailable");
    return "old";
  });
  const { result } = renderHook(
    () => ({
      action: useBackupImport(),
      read: useQuery({
        queryKey: ["receipts"],
        queryFn: read,
        retry: false,
        meta: { errorPresentation: "local" },
      }),
    }),
    { wrapper: Wrapper },
  );
  await waitFor(() => expect(result.current.read.data).toBe("old"));
  fail = true;
  await act(async () => {
    expect(await result.current.action.mutateAsync(file())).toMatchObject({
      totalUpdated: 0,
    });
  });
  await waitFor(() => expect(result.current.read.isError).toBe(true));
  expect(showSuccess).toHaveBeenCalledOnce();
  expect(showError).not.toHaveBeenCalled();
});

it("keeps mutation dispatch callbacks stable across an unchanged rerender", () => {
  const { result, rerender } = renderHook(() => useBackupImport(), {
    wrapper: Wrapper,
  });
  const mutate = result.current.mutate;
  const mutateAsync = result.current.mutateAsync;
  rerender();
  expect(result.current.mutate).toBe(mutate);
  expect(result.current.mutateAsync).toBe(mutateAsync);
});

it("does not continue cache repair after cancellation yields to a replacement session", async () => {
  const gate = deferred();
  const started = deferred();
  const cancel = client.cancelQueries.bind(client);
  vi.spyOn(client, "cancelQueries").mockImplementation(async (...args) => {
    await cancel(...args);
    started.resolve();
    await gate.promise;
  });
  client.setQueryData(["receipts"], "Alice-receipts");
  const { result } = renderHook(() => useBackupImport(), { wrapper: Wrapper });
  try {
    await act(async () => {
      await result.current.mutateAsync(file());
      await started.promise;
    });
    act(() => {
      clearTokens();
      setTokens("Bob-access", "Bob-refresh");
    });
    await act(async () => gate.resolve());
    expect(client.getQueryState(["receipts"])?.isInvalidated).toBe(false);
  } finally {
    gate.resolve();
  }
});

it.each([200, 503])(
  "suppresses an obsolete import's late%s callbacks and cache repair",
  async (finalStatus) => {
    status = finalStatus;
    importGate = deferred();
    client.setQueryData(["receipts"], "Alice-receipts");
    const success = vi.fn();
    const failure = vi.fn();
    const { result } = renderHook(() => useBackupImport(), {
      wrapper: Wrapper,
    });
    let completion!: Promise<unknown>;
    await act(async () => {
      completion = result.current
        .mutateAsync(file(), { onSuccess: success, onError: failure })
        .catch((error: unknown) => error);
      await bodyStarted.promise;
    });
    act(() => {
      clearTokens();
      setTokens("Bob-access", "Bob-refresh");
    });
    await act(async () => {
      importGate?.resolve();
      expect(await completion).toMatchObject({ name: "AbortError" });
    });
    for (const callback of [success, failure, showError, showSuccess])
      expect(callback).not.toHaveBeenCalled();
    expect(client.getQueryState(["receipts"])?.isInvalidated).toBe(false);
  },
);
