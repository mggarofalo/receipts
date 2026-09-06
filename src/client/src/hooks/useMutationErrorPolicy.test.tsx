vi.hoisted(() => vi.stubEnv("VITE_API_URL", "http://mutation-policy.test"));
import type { ReactNode } from "react";
import { act, cleanup, renderHook, waitFor } from "@testing-library/react";
import { QueryClientProvider } from "@tanstack/react-query";
import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import { toast } from "sonner";
import { usePromoteToTemplate } from "./usePromoteToTemplate";
import { useDeleteReceipts } from "./useReceipts";
import { createAppQueryClient } from "@/lib/query-client";
import { clearTokens, setTokens } from "@/lib/auth";
import { addServerErrorListener } from "@/lib/server-error-bus";

vi.mock("sonner", () => ({
  toast: { error: vi.fn(), success: vi.fn(), info: vi.fn() },
}));
const server = setupServer();
let queryClient: ReturnType<typeof createAppQueryClient>;
let bridge: number[];
let unsubscribe: () => void;
function wrapper({ children }: { children: ReactNode }) {
  return (
    <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  );
}
beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
beforeEach(() => {
  vi.clearAllMocks();
  setTokens("current-access", "current-refresh");
  queryClient = createAppQueryClient();
  bridge = [];
  unsubscribe = addServerErrorListener((status) => bridge.push(status));
});
afterEach(() => {
  cleanup();
  unsubscribe();
  queryClient.clear();
  clearTokens();
  server.resetHandlers();
});
afterAll(() => server.close());

it.each(["lookup", "create"] as const)(
  "gives promotion's %s request one toast owner without global navigation",
  async (failure) => {
    const requests: string[] = [];
    server.use(
      http.get("*/api/item-templates/similar", () => {
        requests.push("lookup");
        return failure === "lookup"
          ? HttpResponse.json(
              { status: 503, detail: "Similarity lookup unavailable" },
              { status: 503 },
            )
          : HttpResponse.json([]);
      }),
      http.post("*/api/item-templates", () => {
        requests.push("create");
        return HttpResponse.json(
          { status: 503, detail: "Template creation unavailable" },
          { status: 503 },
        );
      }),
    );
    const { result } = renderHook(() => usePromoteToTemplate(), { wrapper });
    await act(async () => {
      await expect(
        result.current.mutateAsync({ name: "Bread" }),
      ).rejects.toMatchObject({ status: 503 });
    });
    expect(requests).toEqual(
      failure === "lookup" ? ["lookup"] : ["lookup", "create"],
    );
    expect(toast.error).toHaveBeenCalledExactlyOnceWith(
      failure === "lookup"
        ? "Similarity lookup unavailable"
        : "Template creation unavailable",
    );
    expect(bridge).toEqual([]);
    expect(toast.success).not.toHaveBeenCalled();
  },
);

it("preserves promotion's duplicate control and skips the business write", async () => {
  let creates = 0;
  server.use(
    http.get("*/api/item-templates/similar", () =>
      HttpResponse.json([{ source: "template", name: "BREAD" }]),
    ),
    http.post("*/api/item-templates", () => {
      creates++;
      return HttpResponse.json({ id: "unexpected" });
    }),
  );
  const { result } = renderHook(() => usePromoteToTemplate(), { wrapper });
  await act(async () => {
    await expect(
      result.current.mutateAsync({ name: "Bread" }),
    ).resolves.toEqual({ created: false, name: "Bread" });
  });
  expect(creates).toBe(0);
  expect(toast.info).toHaveBeenCalledExactlyOnceWith(
    'A template named "Bread" already exists',
  );
  expect(toast.error).not.toHaveBeenCalled();
  expect(bridge).toEqual([]);
});

it("rolls an optimistic receipt deletion back while the cache presents its one503 error", async () => {
  let release!: () => void;
  const pending = new Promise<void>((resolve) => {
    release = resolve;
  });
  let writes = 0;
  server.use(
    http.delete("*/api/receipts", async () => {
      writes++;
      await pending;
      return HttpResponse.json(
        { status: 503, detail: "Delete unavailable" },
        { status: 503 },
      );
    }),
  );
  const key = ["receipts", "list", 0, 50];
  const original = {
    data: [{ id: "receipt-a" }, { id: "receipt-b" }],
    total: 2,
    offset: 0,
    limit: 50,
  };
  queryClient.setQueryData(key, original);
  const { result } = renderHook(() => useDeleteReceipts(), { wrapper });
  let operation!: Promise<unknown>;
  act(() => {
    operation = result.current
      .mutateAsync(["receipt-a"])
      .catch((error: unknown) => error);
  });
  try {
    await waitFor(() => expect(writes).toBe(1));
    expect(queryClient.getQueryData(key)).toEqual({
      ...original,
      data: [{ id: "receipt-b" }],
      total: 1,
    });
  } finally {
    release();
  }
  await act(async () => {
    expect(await operation).toMatchObject({ status: 503 });
  });
  expect(queryClient.getQueryData(key)).toEqual(original);
  expect(queryClient.getQueryState(key)?.isInvalidated).toBe(true);
  expect(toast.error).toHaveBeenCalledExactlyOnceWith("Delete unavailable");
  expect(toast.success).not.toHaveBeenCalled();
  expect(bridge).toEqual([]);
  expect(writes).toBe(1);
});
