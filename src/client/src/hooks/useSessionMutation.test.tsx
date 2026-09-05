import { act, cleanup, renderHook, waitFor } from "@testing-library/react";
import {
  onlineManager,
  QueryClient,
  QueryClientProvider,
} from "@tanstack/react-query";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { clearTokens, setTokens } from "@/lib/auth";
import { useSessionMutation } from "./useSessionMutation";

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((done, fail) => {
    resolve = done;
    reject = fail;
  });
  return { promise, resolve, reject };
}

let queryClient: QueryClient;
function Wrapper({ children }: { children: ReactNode }) {
  return (
    <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  );
}

beforeEach(() => {
  setTokens("Alice-access", "Alice-refresh");
  queryClient = new QueryClient({
    defaultOptions: { mutations: { retry: false } },
  });
});
afterEach(() => {
  cleanup();
  onlineManager.setOnline(true);
  queryClient.clear();
  clearTokens();
});

describe("useSessionMutation", () => {
  it.each(["global", "keyed"] as const)(
    "retains the %s default mutation function and callbacks",
    async (level) => {
      const onSuccess = vi.fn();
      const defaults = {
        mutationFn: async (name: unknown) => `${String(name)} saved`,
        onSuccess,
      };
      if (level === "global")
        queryClient.setDefaultOptions({ mutations: defaults });
      else queryClient.setMutationDefaults(["save"], defaults);
      const { result } = renderHook(
        () =>
          useSessionMutation<string, Error, string>({ mutationKey: ["save"] }),
        { wrapper: Wrapper },
      );

      await act(async () => {
        expect(await result.current.mutateAsync("receipt")).toBe(
          "receipt saved",
        );
      });

      expect(onSuccess).toHaveBeenCalledOnce();
    },
  );

  it("keeps its result stable across unrelated rerenders", () => {
    const { result, rerender } = renderHook(
      () => useSessionMutation({ mutationFn: async () => "saved" }),
      { wrapper: Wrapper },
    );
    const before = result.current;

    rerender();

    expect(result.current).toBe(before);
    expect(result.current.mutate).toBe(before.mutate);
    expect(result.current.mutateAsync).toBe(before.mutateAsync);
  });

  it("completes a mutation and callbacks while its session remains current", async () => {
    const onSuccess = vi.fn();
    const onSettled = vi.fn();
    const { result } = renderHook(
      () =>
        useSessionMutation({
          mutationFn: async (name: string) => `${name} saved`,
          onSuccess,
          onSettled,
        }),
      { wrapper: Wrapper },
    );

    await act(async () => {
      expect(await result.current.mutateAsync("receipt")).toBe("receipt saved");
    });

    expect(onSuccess).toHaveBeenCalledOnce();
    expect(onSettled).toHaveBeenCalledOnce();
  });

  it("does not dispatch work after an asynchronous optimistic update crosses a session boundary", async () => {
    const optimistic = deferred<void>();
    const started = deferred<void>();
    const mutationFn = vi.fn(async (_name: string) => "saved");
    const onError = vi.fn();
    const { result } = renderHook(
      () =>
        useSessionMutation({
          mutationFn,
          onMutate: async () => {
            started.resolve();
            await optimistic.promise;
            return { oldDraft: "Alice" };
          },
          onError,
        }),
      { wrapper: Wrapper },
    );
    let pending!: Promise<unknown>;
    await act(async () => {
      pending = result.current
        .mutateAsync("Alice receipt")
        .catch((error: unknown) => error);
      await started.promise;
    });
    act(() => {
      clearTokens();
      setTokens("Bob-access", "Bob-refresh");
    });

    await act(async () => {
      optimistic.resolve();
      expect(await pending).toMatchObject({ name: "AbortError" });
    });

    expect(mutationFn).not.toHaveBeenCalled();
    expect(onError).not.toHaveBeenCalled();
  });

  it("rejects promptly when its optimistic update remains pending across logout", async () => {
    const optimistic = deferred<void>();
    const started = deferred<void>();
    const mutationFn = vi.fn(async () => "saved");
    const { result } = renderHook(
      () =>
        useSessionMutation({
          mutationFn,
          onMutate: async () => {
            started.resolve();
            await optimistic.promise;
          },
        }),
      { wrapper: Wrapper },
    );
    let pending!: Promise<unknown>;
    await act(async () => {
      pending = result.current
        .mutateAsync(undefined)
        .catch((error: unknown) => error);
      await started.promise;
    });

    await act(async () => {
      clearTokens();
      expect(await pending).toMatchObject({ name: "AbortError" });
    });

    expect(mutationFn).not.toHaveBeenCalled();
  });

  it("rejects an offline mutation on session change and never dispatches it when connectivity resumes", async () => {
    onlineManager.setOnline(false);
    const mutationFn = vi.fn(async () => "saved");
    const onSuccess = vi.fn();
    const { result } = renderHook(
      () => useSessionMutation({ mutationFn, onSuccess }),
      { wrapper: Wrapper },
    );
    let pending!: Promise<unknown>;
    await act(async () => {
      pending = result.current
        .mutateAsync(undefined)
        .catch((error: unknown) => error);
    });
    await waitFor(() => expect(result.current.isPaused).toBe(true));

    await act(async () => {
      setTokens("Bob-access", "Bob-refresh");
      expect(await pending).toMatchObject({ name: "AbortError" });
      onlineManager.setOnline(true);
      await queryClient.resumePausedMutations();
    });

    expect(mutationFn).not.toHaveBeenCalled();
    expect(onSuccess).not.toHaveBeenCalled();
  });

  it.each(["success", "failure"] as const)(
    "suppresses hook and per-call callbacks for an obsolete %s",
    async (outcome) => {
      const response = deferred<string>();
      const started = deferred<void>();
      const onSuccess = vi.fn();
      const onError = vi.fn();
      const onSettled = vi.fn();
      const navigate = vi.fn();
      const toast = vi.fn();
      const afterCall = vi.fn();
      const { result } = renderHook(
        () =>
          useSessionMutation({
            mutationFn: async (_name: string) => {
              started.resolve();
              return response.promise;
            },
            onSuccess,
            onError,
            onSettled,
          }),
        { wrapper: Wrapper },
      );
      let pending!: Promise<unknown>;
      await act(async () => {
        pending = result.current
          .mutateAsync("Alice receipt", {
            onSuccess: navigate,
            onError: toast,
            onSettled: afterCall,
          })
          .catch((error: unknown) => error);
        await started.promise;
      });
      act(() => {
        clearTokens();
        setTokens("Bob-access", "Bob-refresh");
      });

      await act(async () => {
        if (outcome === "success") response.resolve("Alice private result");
        else response.reject(new Error("Alice request failed"));
        expect(await pending).toMatchObject({ name: "AbortError" });
      });

      for (const callback of [
        onSuccess,
        onError,
        onSettled,
        navigate,
        toast,
        afterCall,
      ]) {
        expect(callback).not.toHaveBeenCalled();
      }
    },
  );
});
