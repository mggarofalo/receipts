vi.hoisted(() =>
  vi.stubEnv("VITE_API_URL", "http://optional-suggestions.test"),
);
import type { ReactNode } from "react";
import { act, cleanup, renderHook, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import { toast } from "sonner";
import { AuthProvider } from "@/contexts/AuthContext";
import { createAppQueryClient } from "@/lib/query-client";
import { clearTokens, setTokens } from "@/lib/auth";
import {
  addServerErrorListener,
  clearServerErrorPageFlag,
} from "@/lib/server-error-bus";
import { useLocationHistory } from "./useLocationHistory";
import { useLocationSuggestions } from "./useReceipts";
import { useReceiptItemSuggestions } from "./useReceiptItemSuggestions";
import { useSimilarItems, useCategoryRecommendations } from "./useSimilarItems";
import { useTemplateHistoryCandidates } from "./useTemplateHistoryCandidates";
vi.mock("sonner", () => ({ toast: { error: vi.fn() } }));
let failed: boolean;
let reads: string[];
let queryClient: ReturnType<typeof createAppQueryClient>;
let errors: number[];
let unsubscribe: () => void;
const endpoints = [
  "receipts/locations",
  "receipt-items/suggestions",
  "item-templates/similar",
  "item-templates/category-suggestions",
  "item-templates/history-candidates",
];
const server = setupServer(
  ...endpoints.map((endpoint) =>
    http.get(`*/api/${endpoint}`, () => {
      reads.push(endpoint);
      if (failed)
        return HttpResponse.json(
          { status: 503, detail: `${endpoint} unavailable` },
          { status: 503 },
        );
      if (endpoint === "receipts/locations")
        return HttpResponse.json({ locations: ["API market"] });
      if (endpoint === "item-templates/history-candidates")
        return HttpResponse.json({ data: [], total: 0, offset: 0, limit: 10 });
      return HttpResponse.json([]);
    }),
  ),
);
function Wrapper({ children }: { children: ReactNode }) {
  return (
    <AuthProvider queryClientFactory={() => queryClient}>
      {children}
    </AuthProvider>
  );
}
function useAllSuggestions() {
  return {
    location: useLocationHistory(),
    item: useReceiptItemSuggestions("MILK", "Market"),
    similar: useSimilarItems("Milk"),
    category: useCategoryRecommendations("Milk"),
    history: useTemplateHistoryCandidates(),
  };
}
beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
afterAll(() => server.close());
beforeEach(() => {
  failed = false;
  reads = [];
  errors = [];
  vi.clearAllMocks();
  localStorage.clear();
  clearServerErrorPageFlag();
  setTokens("access", "refresh");
  queryClient = createAppQueryClient();
  unsubscribe = addServerErrorListener((status) => errors.push(status));
});
afterEach(() => {
  cleanup();
  queryClient.clear();
  unsubscribe();
  clearTokens();
  server.resetHandlers();
});
it("keeps each extended suggestion result and retry callback stable across an unrelated rerender", async () => {
  const { result, rerender } = renderHook(useAllSuggestions, {
    wrapper: Wrapper,
  });
  await waitFor(() => expect(queryClient.isFetching()).toBe(0));
  await waitFor(() =>
    expect(result.current.location.options).toEqual([
      { value: "API market", label: "API market" },
    ]),
  );
  expect(reads).toHaveLength(5);
  const previous = result.current;
  rerender();
  for (const key of Object.keys(previous) as (keyof typeof previous)[]) {
    expect(result.current[key]).toBe(previous[key]);
    expect(result.current[key].refetch).toBe(previous[key].refetch);
  }
  expect(reads).toHaveLength(5);
});
it("makes all five actual shared query families local and manually recoverable without automatic retries", async () => {
  failed = true;
  const { result } = renderHook(useAllSuggestions, { wrapper: Wrapper });
  await waitFor(() =>
    expect(Object.values(result.current).every((query) => query.isError)).toBe(
      true,
    ),
  );
  expect(reads.sort()).toEqual([...endpoints].sort());
  expect(errors).toEqual([]);
  expect(toast.error).not.toHaveBeenCalled();
  failed = false;
  await act(async () => {
    await Promise.all(
      Object.values(result.current).map((query) => query.refetch()),
    );
  });
  await waitFor(() =>
    expect(Object.values(result.current).every((query) => !query.isError)).toBe(
      true,
    ),
  );
  expect(reads).toHaveLength(10);
  expect(result.current.location.options).toEqual([
    { value: "API market", label: "API market" },
  ]);
});
it("cancels the actual location request when its last observer unmounts and keeps its late reply out of cache", async () => {
  let release!: () => void;
  let started = false;
  let aborted = false;
  const held = new Promise<void>((resolve) => {
    release = resolve;
  });
  server.use(
    http.get("*/api/receipts/locations", async ({ request }) => {
      started = true;
      request.signal.addEventListener("abort", () => {
        aborted = true;
      });
      await held;
      return HttpResponse.json({ locations: ["Late market"] });
    }),
  );
  const { unmount } = renderHook(() => useLocationSuggestions("held"), {
    wrapper: Wrapper,
  });
  try {
    await waitFor(() => expect(started).toBe(true));
    unmount();
    await waitFor(() => expect(aborted).toBe(true));
    await act(async () => {
      release();
      await held;
    });
    expect(
      queryClient.getQueryData(["receipts", "locations", "held"]),
    ).toBeUndefined();
    expect(errors).toEqual([]);
    expect(toast.error).not.toHaveBeenCalled();
  } finally {
    release();
  }
});
it("aborts the previous item-code query when its location changes and never uses the old location's reply", async () => {
  let release!: () => void;
  const held = new Promise<void>((resolve) => {
    release = resolve;
  });
  let firstStarted = false;
  let firstAborted = false;
  server.use(
    http.get("*/api/receipt-items/suggestions", async ({ request }) => {
      const location = new URL(request.url).searchParams.get("location");
      if (location === "Old market") {
        firstStarted = true;
        request.signal.addEventListener("abort", () => {
          firstAborted = true;
        });
        await held;
      }
      return HttpResponse.json([
        {
          itemCode: "MILK",
          description: location,
          matchType: "same-location",
          unitPrice: 2.01,
        },
      ]);
    }),
  );
  const { result, rerender } = renderHook(
    ({ location }) => useReceiptItemSuggestions("MILK", location),
    { wrapper: Wrapper, initialProps: { location: "Old market" } },
  );
  try {
    await waitFor(() => expect(firstStarted).toBe(true));
    rerender({ location: "New market" });
    await waitFor(() => expect(firstAborted).toBe(true));
    await waitFor(() =>
      expect(result.current.data?.[0].description).toBe("New market"),
    );
    await act(async () => {
      release();
      await held;
    });
    expect(result.current.data?.[0].description).toBe("New market");
    expect(
      queryClient.getQueryData([
        "receiptItemSuggestions",
        "MILK",
        "Old market",
        undefined,
      ]),
    ).toBeUndefined();
  } finally {
    release();
  }
});
