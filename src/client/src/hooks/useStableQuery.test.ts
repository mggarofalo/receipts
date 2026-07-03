import { describe, it, expect } from "vitest";
import { renderHook, waitFor } from "@testing-library/react";
import {
  QueryClient,
  QueryClientProvider,
  useQuery,
} from "@tanstack/react-query";
import { createElement, type ReactNode } from "react";
import { useStableQuery } from "./useStableQuery";

function createWrapper() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  });
  return function Wrapper({ children }: { children: ReactNode }) {
    return createElement(QueryClientProvider, { client: queryClient }, children);
  };
}

describe("useStableQuery", () => {
  it("returns a referentially stable object across renders when query state is unchanged", async () => {
    const { result, rerender } = renderHook(
      () => {
        const query = useQuery({
          queryKey: ["stable-query-test"],
          queryFn: async () => ({ value: 42 }),
        });
        return useStableQuery(query);
      },
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    const first = result.current;
    rerender();

    // Same reference: unlike a `[query]` dependency (which recomputes every
    // render because TanStack returns a fresh result object each time), the
    // field-level deps keep the projection stable when nothing changed.
    expect(result.current).toBe(first);
    expect(result.current.data).toEqual({ value: 42 });
  });

  it("exposes the standard query state fields", async () => {
    const { result } = renderHook(
      () => {
        const query = useQuery({
          queryKey: ["stable-query-fields"],
          queryFn: async () => ({ value: 1 }),
        });
        return useStableQuery(query);
      },
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(result.current).toMatchObject({
      isLoading: false,
      isPending: false,
      isError: false,
      isSuccess: true,
      isFetching: false,
      status: "success",
      fetchStatus: "idle",
    });
    expect(typeof result.current.refetch).toBe("function");
  });
});
