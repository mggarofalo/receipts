vi.hoisted(() => vi.stubEnv("VITE_API_URL", "http://backup-hook.test"));
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { renderHook, act, cleanup } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { createElement, type ReactNode } from "react";

import { clearTokens, setTokens } from "@/lib/auth";

vi.mock("@/lib/toast", () => ({
  showSuccess: vi.fn(),
  showError: vi.fn(),
}));

import { useBackupExport } from "./useBackup";
import { showSuccess, showError } from "@/lib/toast";

function createWrapper() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  });
  return function Wrapper({ children }: { children: ReactNode }) {
    return createElement(QueryClientProvider, { client: queryClient }, children);
  };
}

describe("useBackupExport", () => {
  const originalFetch = globalThis.fetch;
  const originalCreateObjectURL = globalThis.URL.createObjectURL;
  const originalRevokeObjectURL = globalThis.URL.revokeObjectURL;
  const originalCreateElement = document.createElement.bind(document);

  beforeEach(() => {
    vi.clearAllMocks();
    setTokens("mock-token", "Alice-refresh");
    // Minimal URL mocks — jsdom doesn't implement blob URLs.
    globalThis.URL.createObjectURL = vi.fn(() => "blob:mock");
    globalThis.URL.revokeObjectURL = vi.fn();
    // jsdom warns/throws on anchor.click() because it cannot navigate. Stub
    // the created anchor's click with a no-op so the happy-path test is stable.
    vi.spyOn(document, "createElement").mockImplementation((tagName: string) => {
      const el = originalCreateElement(tagName);
      if (tagName.toLowerCase() === "a") {
        (el as HTMLAnchorElement).click = vi.fn();
      }
      return el;
    });
  });

  afterEach(() => {
    cleanup();
    clearTokens();
    globalThis.fetch = originalFetch;
    globalThis.URL.createObjectURL = originalCreateObjectURL;
    globalThis.URL.revokeObjectURL = originalRevokeObjectURL;
    vi.restoreAllMocks();
  });

  it("downloads the blob and toasts success on 200", async () => {
    const fakeBlob = { size: 12, type: "application/octet-stream" } as Blob;
    // Preserve the native Response contract, overriding only the downloadable body boundary.
    globalThis.fetch = vi.fn(async () => Object.assign(new Response("backup", {
      headers: { "Content-Disposition": 'attachment; filename="my-backup.sqlite"' },
    }), { blob: async () => fakeBlob }));

    const { result } = renderHook(() => useBackupExport(), {
      wrapper: createWrapper(),
    });

    // mutateAsync awaits the mutationFn; onSuccess/onError run synchronously
    // after resolution, so showSuccess/URL spies are observable immediately.
    await act(async () => {
      expect(await result.current.mutateAsync()).toBeUndefined();
    });

    expect(result.current.data).toBeUndefined();
    expect(showSuccess).toHaveBeenCalledWith("Backup exported successfully.");
    expect(showError).not.toHaveBeenCalled();
    expect(globalThis.URL.createObjectURL).toHaveBeenCalledWith(fakeBlob);
    expect(globalThis.URL.revokeObjectURL).toHaveBeenCalledWith("blob:mock");
  });

  it("toasts a permission error on 403", async () => {
    globalThis.fetch = vi.fn(async () => new Response(JSON.stringify({ status: 403 }), { status: 403, headers: { "Content-Type": "application/json" } }));

    const { result } = renderHook(() => useBackupExport(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      await result.current.mutateAsync().catch(() => {});
    });
    expect(showError).toHaveBeenCalledWith(
      "You do not have permission to export backups.",
    );
  });

  it("toasts a generic error on other failures", async () => {
    globalThis.fetch = vi.fn(async () => new Response(JSON.stringify({ status: 500 }), { status: 500, headers: { "Content-Type": "application/json" } }));

    const { result } = renderHook(() => useBackupExport(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      await result.current.mutateAsync().catch(() => {});
    });
    expect(showError).toHaveBeenCalledWith("Export failed (500).");
  });

  it.each(["success", "failure"] as const)("aborts response-body ownership and suppresses a prior session's delayed %s", async (outcome) => {
    let resolveBlob!: (blob: Blob) => void;
    let rejectBlob!: (error: Error) => void;
    let markBodyStarted!: () => void;
    const bodyStarted = new Promise<void>((resolve) => { markBodyStarted = resolve; });
    const body = new Promise<Blob>((resolve, reject) => { resolveBlob = resolve; rejectBlob = reject; });
    let requestSignal: AbortSignal | null | undefined;
    globalThis.fetch = vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
      requestSignal = init?.signal ?? (_input instanceof Request ? _input.signal : undefined);
      return Object.assign(new Response("backup"), {
        // Let this body adapter settle after abort to prove guarded download ownership.
        blob: () => { markBodyStarted(); return body; },
      });
    }) as unknown as typeof fetch;
    const onSuccess = vi.fn();
    const onError = vi.fn();
    const onSettled = vi.fn();
    const { result } = renderHook(() => useBackupExport(), { wrapper: createWrapper() });
    let completion!: Promise<unknown>;
    await act(async () => {
      completion = result.current.mutateAsync(undefined, { onSuccess, onError, onSettled }).catch((error: unknown) => error);
      await bodyStarted;
    });
    act(() => { clearTokens(); setTokens("Bob-access", "Bob-refresh"); });
    expect(requestSignal?.aborted).toBe(true);
    await act(async () => {
      if (outcome === "success") resolveBlob(new Blob(["Alice private backup"]));
      else rejectBlob(new Error("Alice backup failed"));
      expect(await completion).toMatchObject({ name: "AbortError" });
    });
    expect(globalThis.URL.createObjectURL).not.toHaveBeenCalled();
    for (const callback of [showSuccess, showError, onSuccess, onError, onSettled]) expect(callback).not.toHaveBeenCalled();
  });

  it("keeps public void results and per-call callback data while downloading normally", async () => {
    globalThis.fetch = vi.fn(async () => Object.assign(new Response("backup"), { blob: async () => new Blob(["backup"]) }));
    const onSuccess = vi.fn();
    const onSettled = vi.fn();
    const { result } = renderHook(() => useBackupExport(), { wrapper: createWrapper() });
    await act(async () => {
      expect(await result.current.mutateAsync(undefined, { onSuccess, onSettled })).toBeUndefined();
    });
    expect(onSuccess).toHaveBeenCalledOnce();
    expect(onSuccess.mock.calls[0][0]).toBeUndefined();
    expect(onSettled).toHaveBeenCalledOnce();
    expect(onSettled.mock.calls[0][0]).toBeUndefined();
    expect(result.current.data).toBeUndefined();
    expect(globalThis.URL.createObjectURL).toHaveBeenCalledOnce();
  });
});
