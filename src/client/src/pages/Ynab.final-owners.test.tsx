vi.hoisted(() => vi.stubEnv("VITE_API_URL", "http://ynab-final-owners.test"));
import { useState, type ReactNode } from "react";
import { act, cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createMemoryRouter, RouterProvider } from "react-router";
import { http, HttpResponse, type JsonBodyType } from "msw";
import { setupServer } from "msw/node";
import { toast } from "sonner";
import { AuthProvider } from "@/contexts/AuthContext";
import { AppearanceProvider } from "@/contexts/AppearanceContext";
import { TooltipProvider } from "@/components/ui/tooltip";
import { RootLayout } from "@/components/RootLayout";
import { YnabBulkSyncCard } from "@/components/YnabBulkSyncCard";
import { CommandPalette } from "@/components/CommandPalette";
import { useYnabStatus } from "@/hooks/useYnabStatus";
import { useYnabEvents } from "@/hooks/useYnabEvents";
import { useAllReceiptIds } from "@/hooks/useYnab";
import { createAppQueryClient } from "@/lib/query-client";
import { clearTokens, setTokens } from "@/lib/auth";
import { clearServerErrorPageFlag } from "@/lib/server-error-bus";
import {
  getCacheErrorPresentation,
  getRequestErrorPresentation,
} from "@/lib/request-error-policy";
import { mockReceiptListItemResponse } from "@/test/mock-api";
import Ynab from "./Ynab";
import "@/test/setup-combobox-polyfills";

function deferred() {
  let resolve!: () => void;
  const promise = new Promise<void>((done) => {
    resolve = done;
  });
  return { resolve, promise };
}
const keys = {
  status: ["ynab", "status"],
  events: ["ynab", "events"],
  receipts: ["receipts", "all-ids"],
};
type Read = keyof typeof keys;
let failed: Read | undefined;
let held: Read | undefined;
let receiptFailureOffset: number;
let empty: boolean;
let gate: ReturnType<typeof deferred>;
let started: ReturnType<typeof deferred>;
let receiptOffsets: number[];
let writes: unknown[];
let nativeReads: Request[];
let queryClient: ReturnType<typeof createAppQueryClient>;
let router: ReturnType<typeof createMemoryRouter>;
const status = {
  isConfigured: true,
  pushCountLast24h: 3,
  pushCountLast7d: 7,
  pushCountLast30d: 11,
  pushSuccessLast30d: 10,
  pushFailureLast30d: 1,
};
async function read(kind: Read, data: JsonBodyType, offset = 0) {
  if (held === kind) {
    started.resolve();
    await gate.promise;
  }
  return failed === kind &&
    (kind !== "receipts" || offset === receiptFailureOffset)
    ? HttpResponse.json(
        { status: 503, detail: `${kind} temporarily unavailable` },
        { status: 503 },
      )
    : HttpResponse.json(data);
}
const server = setupServer(
  http.get("*/api/ynab/connection-status", () =>
    HttpResponse.json({ isConfigured: true, isConnected: true }),
  ),
  http.get("*/api/ynab/rate-limit-status", () =>
    HttpResponse.json({
      requestsUsed: 1,
      maxRequests: 200,
      remainingRequests: 199,
      isThrottled: false,
    }),
  ),
  http.get("*/api/ynab/status", () =>
    read(
      "status",
      empty
        ? {
            ...status,
            pushCountLast24h: 0,
            pushCountLast7d: 0,
            pushCountLast30d: 0,
            pushSuccessLast30d: 0,
            pushFailureLast30d: 0,
          }
        : status,
    ),
  ),
  http.get("*/api/ynab/events", () =>
    read("events", {
      data: empty
        ? []
        : [
            {
              id: "event-1",
              occurredAt: "2026-09-01T12:00:00Z",
              eventType: "Memo probe",
              success: true,
              httpStatus: 200,
            },
          ],
      total: empty ? 0 : 61,
      offset: 0,
      limit: 50,
    }),
  ),
  http.get("*/api/receipts", ({ request }) => {
    const offset = Number(new URL(request.url).searchParams.get("offset") ?? 0);
    receiptOffsets.push(offset);
    const count = empty
      ? 0
      : receiptFailureOffset === 500 && offset === 0
        ? 500
        : 1;
    return read(
      "receipts",
      {
        data: Array.from({ length: count }, (_, i) =>
          mockReceiptListItemResponse({ id: `receipt-${offset + i}` }),
        ),
        total: empty ? 0 : receiptFailureOffset === 500 ? 501 : 1,
        offset,
        limit: 500,
      },
      offset,
    );
  }),
  ...[
    "accounts",
    "cards",
    "categories",
    "subcategories",
    "item-templates",
    "receipt-items",
  ].map((entity) =>
    http.get(`*/api/${entity}`, () =>
      HttpResponse.json({ data: [], total: 0, offset: 0, limit: 500 }),
    ),
  ),
  ...["push-transactions/bulk", "sync-memos/bulk"].map((path) =>
    http.post(`*/api/ynab/${path}`, async ({ request }) => {
      writes.push(await request.json());
      return HttpResponse.json({ results: [] });
    }),
  ),
);
beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
afterAll(() => server.close());
beforeEach(() => {
  localStorage.clear();
  setTokens("alice-access", "alice-refresh");
  clearServerErrorPageFlag();
  failed = undefined;
  held = undefined;
  receiptFailureOffset = 0;
  empty = false;
  gate = deferred();
  started = deferred();
  receiptOffsets = [];
  writes = [];
  nativeReads = [];
  for (const name of ["error", "success", "warning", "info"] as const)
    vi.spyOn(toast, name);
  const actual = globalThis.fetch;
  vi.spyOn(globalThis, "fetch").mockImplementation((input, init) => {
    if (input instanceof Request && input.method === "GET")
      nativeReads.push(input);
    return actual(input, init);
  });
});
afterEach(async () => {
  cleanup();
  await act(async () => {
    gate.resolve();
    await gate.promise;
  });
  queryClient?.clear();
  clearTokens();
  toast.dismiss();
  vi.restoreAllMocks();
  server.resetHandlers();
});
function renderFeature(feature: ReactNode) {
  router = createMemoryRouter(
    [
      {
        element: <RootLayout />,
        children: [
          { path: "/ynab", element: feature },
          { path: "/error/500", element: <h1>Global error route</h1> },
        ],
      },
    ],
    { initialEntries: ["/ynab"] },
  );
  render(
    <AppearanceProvider>
      <TooltipProvider>
        <AuthProvider
          queryClientFactory={() => {
            queryClient = createAppQueryClient();
            return queryClient;
          }}
        >
          <RouterProvider router={router} />
        </AuthProvider>
      </TooltipProvider>
    </AppearanceProvider>,
  );
}
function assertLocal() {
  expect(router.state.location.pathname).toBe("/ynab");
  expect(screen.queryByText("Global error route")).not.toBeInTheDocument();
}
function noFeedback() {
  for (const name of ["error", "success", "warning", "info"] as const)
    expect(toast[name]).not.toHaveBeenCalled();
}
async function awaitFailure(kind: Read) {
  await waitFor(() =>
    expect(
      queryClient.getQueryCache().find({ queryKey: keys[kind], exact: false })
        ?.state.status,
    ).toBe("error"),
  );
}
async function retryHeld(kind: Read) {
  const query = queryClient
    .getQueryCache()
    .find({ queryKey: keys[kind], exact: false });
  const hadData = query?.state.data !== undefined;
  const path = kind === "receipts" ? "/api/receipts" : `/api/ynab/${kind}`;
  const previousRequests = nativeReads.filter(
    (r) => new URL(r.url).pathname === path,
  ).length;
  failed = undefined;
  held = kind;
  await userEvent.click(screen.getByRole("button", { name: "Retry" }));
  await started.promise;
  expect(
    nativeReads.filter((r) => new URL(r.url).pathname === path),
  ).toHaveLength(previousRequests + 1);
  expect(query?.state.fetchStatus).toBe("fetching");
  if (hadData) {
    expect(screen.getByRole("button", { name: /Retrying/ })).toBeDisabled();
  } else {
    expect(query?.state.status).toBe("pending");
    expect(query?.state.data).toBeUndefined();
    expect(
      screen.queryByRole("button", { name: "Retry" }),
    ).not.toBeInTheDocument();
    expect(screen.queryByText("0 / 0 / 0")).not.toBeInTheDocument();
    expect(screen.queryByText("No YNAB activity yet.")).not.toBeInTheDocument();
    expect(screen.queryByText(/No receipts found/)).not.toBeInTheDocument();
  }
  assertLocal();
}
async function finishRetry() {
  held = undefined;
  await act(async () => {
    gate.resolve();
  });
  await waitFor(() => expect(queryClient.isFetching()).toBe(0));
}

it.each(["status", "events"] as const)(
  "owns initial %s failure on the real diagnostics route and Retry recovers",
  async (kind) => {
    failed = kind;
    renderFeature(<Ynab />);
    await awaitFailure(kind);
    assertLocal();
    expect(screen.getByRole("alert")).toHaveTextContent(/unavailable/i);
    if (kind === "status")
      expect(screen.queryByText("0 / 0 / 0")).not.toBeInTheDocument();
    else
      expect(
        screen.queryByText("No YNAB activity yet."),
      ).not.toBeInTheDocument();
    await retryHeld(kind);
    assertLocal();
    await finishRetry();
    expect(
      await screen.findByText(kind === "status" ? "3 / 7 / 11" : "Memo probe"),
    ).toBeVisible();
    expect(
      screen.queryByRole("button", { name: "Retry" }),
    ).not.toBeInTheDocument();
    noFeedback();
  },
);

it.each(["status", "events"] as const)(
  "retains cached %s values and totals through a failed refresh and held Retry",
  async (kind) => {
    renderFeature(<Ynab />);
    const prior = await screen.findByText(
      kind === "status" ? "3 / 7 / 11" : "Memo probe",
    );
    await waitFor(() => expect(queryClient.isFetching()).toBe(0));
    const oldData = queryClient
      .getQueryCache()
      .find({ queryKey: keys[kind], exact: false })?.state.data;
    failed = kind;
    await act(async () => {
      await queryClient.invalidateQueries({ queryKey: keys[kind] });
    });
    await awaitFailure(kind);
    assertLocal();
    expect(prior).toBeVisible();
    expect(
      queryClient.getQueryCache().find({ queryKey: keys[kind], exact: false })
        ?.state.data,
    ).toEqual(oldData);
    if (kind === "events") expect(screen.getByText(/of 61/)).toBeVisible();
    await retryHeld(kind);
    expect(prior).toBeVisible();
    await finishRetry();
    expect(prior).toBeVisible();
    noFeedback();
  },
);

it("distinguishes successful empty diagnostics from unavailable reads", async () => {
  empty = true;
  renderFeature(<Ynab />);
  expect(await screen.findByText("No YNAB activity yet.")).toBeVisible();
  await waitFor(() => expect(queryClient.isFetching()).toBe(0));
  expect(screen.getByText("0 / 0 / 0")).toBeVisible();
  expect(
    queryClient.getQueryCache().find({ queryKey: keys.status })?.state.status,
  ).toBe("success");
  expect(
    queryClient.getQueryCache().find({ queryKey: keys.events, exact: false })
      ?.state.status,
  ).toBe("success");
  expect(
    screen.queryByRole("button", { name: "Retry" }),
  ).not.toBeInTheDocument();
  assertLocal();
  noFeedback();
});

it.each([0, 500])(
  "blocks both bulk writes when receipt enumeration page %i fails, then retries the complete list",
  async (offset) => {
    receiptFailureOffset = offset;
    failed = "receipts";
    renderFeature(<YnabBulkSyncCard />);
    await awaitFailure("receipts");
    expect(receiptOffsets).toEqual(offset ? [0, 500] : [0]);
    expect(queryClient.getQueryData(keys.receipts)).toBeUndefined();
    assertLocal();
    expect(screen.queryByText(/No receipts found/)).not.toBeInTheDocument();
    expect(screen.getByRole("alert")).toHaveTextContent(/unavailable|failed/i);
    for (const name of ["Push All to YNAB", "Sync All Memos"])
      expect(screen.getByRole("button", { name })).toBeDisabled();
    expect(writes).toEqual([]);
    await retryHeld("receipts");
    for (const name of ["Push All to YNAB", "Sync All Memos"])
      expect(screen.getByRole("button", { name })).toBeDisabled();
    expect(writes).toEqual([]);
    await finishRetry();
    const push = screen.getByRole("button", { name: "Push All to YNAB" });
    await waitFor(() => expect(push).toBeEnabled());
    expect(writes).toEqual([]);
    noFeedback();
    await userEvent.click(push);
    await waitFor(() => expect(writes).toHaveLength(1));
    expect(writes[0]).toEqual({
      receiptIds: Array.from(
        { length: offset ? 501 : 1 },
        (_, i) => `receipt-${i}`,
      ),
    });
    assertLocal();
  },
);

it("shows a successful empty receipt list without enabling bulk writes", async () => {
  empty = true;
  renderFeature(<YnabBulkSyncCard />);
  expect(await screen.findByText(/No receipts found/)).toBeVisible();
  expect(queryClient.getQueryData(keys.receipts)).toEqual({
    ids: [],
    total: 0,
  });
  for (const name of ["Push All to YNAB", "Sync All Memos"])
    expect(screen.getByRole("button", { name })).toBeDisabled();
  assertLocal();
  noFeedback();
  expect(writes).toEqual([]);
});

function PaletteHost() {
  const [open, setOpen] = useState(true);
  return (
    <>
      <button onClick={() => setOpen(true)}>Open palette</button>
      {open && <CommandPalette open={open} onOpenChange={setOpen} />}
    </>
  );
}

it.each([true, false])(
  "requires one consistent total across receipt pages (total drift: %s)",
  async (drifting) => {
    let drift = drifting;
    server.use(
      http.get("*/api/receipts", ({ request }) => {
        const offset = Number(
          new URL(request.url).searchParams.get("offset") ?? 0,
        );
        receiptOffsets.push(offset);
        const count = offset === 0 ? 500 : drift ? 0 : 1;
        return read(
          "receipts",
          {
            data: Array.from({ length: count }, (_, i) =>
              mockReceiptListItemResponse({ id: `receipt-${offset + i}` }),
            ),
            total: offset === 500 && drift ? 500 : 501,
            offset,
            limit: 500,
          },
          offset,
        );
      }),
    );
    renderFeature(<YnabBulkSyncCard />);
    if (drifting) {
      await awaitFailure("receipts");
      assertLocal();
      expect(screen.getByRole("alert")).toHaveTextContent(/unavailable/i);
      expect(queryClient.getQueryData(keys.receipts)).toBeUndefined();
      expect(receiptOffsets).toEqual([0, 500]);
      for (const name of ["Push All to YNAB", "Sync All Memos"])
        expect(screen.getByRole("button", { name })).toBeDisabled();
      expect(writes).toEqual([]);
      noFeedback();
      drift = false;
      await retryHeld("receipts");
      expect(writes).toEqual([]);
      for (const name of ["Push All to YNAB", "Sync All Memos"])
        expect(screen.getByRole("button", { name })).toBeDisabled();
      await finishRetry();
    }
    const push = screen.getByRole("button", { name: "Push All to YNAB" });
    await waitFor(() => expect(push).toBeEnabled());
    expect(receiptOffsets).toEqual(drifting ? [0, 500, 0, 500] : [0, 500]);
    expect(queryClient.getQueryData(keys.receipts)).toEqual({
      ids: Array.from({ length: 501 }, (_, i) => `receipt-${i}`),
      total: 501,
    });
    expect(writes).toEqual([]);
    noFeedback();
    assertLocal();
    await userEvent.click(push);
    await waitFor(() => expect(writes).toHaveLength(1));
    expect(writes[0]).toEqual({
      receiptIds: Array.from({ length: 501 }, (_, i) => `receipt-${i}`),
    });
  },
);

function incompleteReceipts(shape: "short" | "duplicate") {
  let complete = false;
  server.use(
    http.get("*/api/receipts", ({ request }) => {
      const offset = Number(
        new URL(request.url).searchParams.get("offset") ?? 0,
      );
      receiptOffsets.push(offset);
      const total = shape === "short" ? 2 : 501;
      const count =
        shape === "short" ? (complete ? 2 : 1) : offset === 0 ? 500 : 1;
      const data = Array.from({ length: count }, (_, i) =>
        mockReceiptListItemResponse({
          id: `receipt-${!complete && shape === "duplicate" && offset === 0 && i === 499 ? 498 : offset + i}`,
        }),
      );
      return read("receipts", { data, total, offset, limit: 500 }, offset);
    }),
  );
  return () => {
    complete = true;
  };
}

it.each(["short", "duplicate"] as const)(
  "blocks successful %s enumeration until a complete Retry, without deferred bulk writes",
  async (shape) => {
    const complete = incompleteReceipts(shape);
    const total = shape === "short" ? 2 : 501;
    renderFeature(<YnabBulkSyncCard />);
    expect(await screen.findByRole("alert")).toHaveTextContent(
      `Loaded ${total - 1} receipt IDs, but the server reported ${total} receipts`,
    );
    const snapshot = queryClient.getQueryData<{ ids: string[]; total: number }>(
      keys.receipts,
    );
    expect(snapshot?.ids).toHaveLength(total - 1);
    expect(new Set(snapshot?.ids).size).toBe(total - 1);
    expect(snapshot?.total).toBe(total);
    expect(receiptOffsets).toEqual(shape === "short" ? [0] : [0, 500]);
    for (const name of ["Push All to YNAB", "Sync All Memos"])
      expect(screen.getByRole("button", { name })).toBeDisabled();
    expect(writes).toEqual([]);
    assertLocal();
    noFeedback();
    complete();
    await retryHeld("receipts");
    for (const name of ["Push All to YNAB", "Sync All Memos"])
      expect(screen.getByRole("button", { name })).toBeDisabled();
    expect(writes).toEqual([]);
    await finishRetry();
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
    expect(receiptOffsets).toEqual(
      shape === "short" ? [0, 0] : [0, 500, 0, 500],
    );
    expect(writes).toEqual([]);
    await userEvent.click(
      screen.getByRole("button", { name: "Sync All Memos" }),
    );
    await waitFor(() => expect(writes).toHaveLength(1));
    expect(writes[0]).toEqual({
      receiptIds: Array.from({ length: total }, (_, i) => `receipt-${i}`),
    });
  },
);

it.each(["short", "duplicate"] as const)(
  "palette refuses successful %s enumeration with accurate no-push feedback",
  async (shape) => {
    incompleteReceipts(shape);
    const total = shape === "short" ? 2 : 501;
    renderFeature(<PaletteHost />);
    await userEvent.click(
      await screen.findByRole("option", { name: /Sync YNAB Now/ }),
    );
    await waitFor(() =>
      expect(toast.error).toHaveBeenCalledWith(
        `Only ${total - 1} of ${total} receipts could be loaded. No receipts were pushed.`,
      ),
    );
    expect(toast.error).toHaveBeenCalledTimes(1);
    expect(receiptOffsets).toEqual(shape === "short" ? [0] : [0, 500]);
    expect(writes).toEqual([]);
    assertLocal();
    expect(toast.success).not.toHaveBeenCalled();
    expect(toast.info).not.toHaveBeenCalled();
  },
);
it.each([0, 500])(
  "owns palette enumeration page %i failure after close with one accurate error and no partial push",
  async (offset) => {
    receiptFailureOffset = offset;
    failed = "receipts";
    renderFeature(<PaletteHost />);
    // The command is in the initial Actions group: no search-triggered receipt query.
    await userEvent.click(
      await screen.findByRole("option", { name: /Sync YNAB Now/ }),
    );
    await waitFor(() =>
      expect(toast.error).toHaveBeenCalledWith(
        "Failed to load receipts for YNAB sync",
      ),
    );
    assertLocal();
    expect(
      screen.queryByPlaceholderText("Type a command or search…"),
    ).not.toBeInTheDocument();
    expect(receiptOffsets).toEqual(offset ? [0, 500] : [0]);
    expect(writes).toEqual([]);
    expect(toast.error).toHaveBeenCalledTimes(1);
    expect(toast.success).not.toHaveBeenCalled();
    expect(toast.info).not.toHaveBeenCalled();
  },
);

function StatusProbe() {
  const query = useYnabStatus();
  return <span>{query.status}</span>;
}
function EventsProbe() {
  const query = useYnabEvents();
  return <span>{query.status}</span>;
}
function ReceiptsProbe() {
  const query = useAllReceiptIds();
  return <span>{query.status}</span>;
}
const reads = [
  { kind: "status", path: "/api/ynab/status", component: <StatusProbe /> },
  { kind: "events", path: "/api/ynab/events", component: <EventsProbe /> },
  { kind: "receipts", path: "/api/receipts", component: <ReceiptsProbe /> },
] as const;
it.each(reads)(
  "pairs local query metadata and native Request policy for $kind",
  async ({ kind, path, component }) => {
    renderFeature(component);
    expect(await screen.findByText("success")).toBeVisible();
    const requests = nativeReads.filter(
      (r) => new URL(r.url).pathname === path,
    );
    expect(requests).toHaveLength(1);
    expect(getRequestErrorPresentation(requests[0])).toBe("local");
    expect(
      getCacheErrorPresentation(
        queryClient.getQueryCache().find({ queryKey: keys[kind], exact: false })
          ?.meta,
      ),
    ).toBe("local");
  },
);
it.each(reads)(
  "forwards $kind query cancellation to the held native request",
  async ({ kind, path, component }) => {
    held = kind;
    renderFeature(component);
    await started.promise;
    const requests = nativeReads.filter(
      (r) => new URL(r.url).pathname === path,
    );
    expect(requests).toHaveLength(1);
    expect(requests[0].signal.aborted).toBe(false);
    await act(async () => {
      await queryClient.cancelQueries({ queryKey: keys[kind] });
    });
    expect(requests[0].signal.aborted).toBe(true);
    await finishRetry();
    expect(
      queryClient.getQueryCache().find({ queryKey: keys[kind], exact: false })
        ?.state.data,
    ).toBeUndefined();
    assertLocal();
    noFeedback();
  },
);
