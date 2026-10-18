vi.hoisted(() => vi.stubEnv("VITE_API_URL", "http://local-errors.test"));
import { act, cleanup, renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import { AuthProvider } from "@/contexts/AuthContext";
import { createAppQueryClient } from "@/lib/query-client";
import { clearTokens, setTokens } from "@/lib/auth";
import { useReceiptYnabSyncStatuses } from "./useYnab";

const id = "95000000-0000-4000-8000-000000000001";
let failed = false;
let transactionStatus = 503;
const server = setupServer(
  http.get("*/api/ynab/receipt-sync-statuses", () =>
    failed
      ? HttpResponse.json(
          { status: 503, detail: "Sync status unavailable" },
          { status: 503 },
        )
      : HttpResponse.json({ data: [{ receiptId: id, syncStatus: "synced" }] }),
  ),
  http.get("*/api/ynab/sync-status/:id", () =>
    HttpResponse.json(
      { status: transactionStatus, detail: "Status unavailable" },
      { status: transactionStatus },
    ),
  ),
);
const clients: ReturnType<typeof createAppQueryClient>[] = [];
function setup() {
  const client = createAppQueryClient();
  clients.push(client);
  function Wrapper({ children }: { children: ReactNode }) {
    return (
      <AuthProvider queryClientFactory={() => client}>{children}</AuthProvider>
    );
  }
  return { client, wrapper: Wrapper };
}
beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
afterAll(() => server.close());
beforeEach(() => {
  failed = false;
  transactionStatus = 503;
  setTokens("access", "refresh");
});
afterEach(() => {
  cleanup();
  clients.splice(0).forEach((c) => c.clear());
  clearTokens();
  server.resetHandlers();
});

it("retains the last successful receipt sync status and exposes a failed refetch", async () => {
  const { client, wrapper } = setup();
  const { result } = renderHook(() => useReceiptYnabSyncStatuses([id]), {
    wrapper,
  });
  await waitFor(() => expect(result.current.statusMap.get(id)).toBe("synced"));
  failed = true;
  await act(async () => {
    await client.invalidateQueries({
      queryKey: ["ynab", "receipt-sync-statuses"],
    });
  });
  await waitFor(() => expect(result.current.isError).toBe(true));
  expect(result.current.statusMap.get(id)).toBe("synced");
});
