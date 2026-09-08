vi.hoisted(() =>
  vi.stubEnv("VITE_API_URL", "http://curation-projections.test"),
);
import { act, cleanup, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClientProvider } from "@tanstack/react-query";
import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import { toast } from "sonner";
import { renderWithProviders } from "@/test/test-utils";
import { createAppQueryClient } from "@/lib/query-client";
import { clearTokens } from "@/lib/auth";
import { ReceiptItemsCard } from "@/components/ReceiptItemsCard";
import { useReceiptItem } from "./useReceiptItems";
import { useSpendingByNormalizedDescription } from "./useSpendingByNormalizedDescription";
import { useRenameMutation } from "./useNormalizedDescriptionActions";
import { useRequeuePendingMutation } from "./useNormalizedDescriptionMaintenance";
import {
  useAcceptDuplicateGroup,
  useAcceptedDuplicates,
  useUnacceptDuplicateGroup,
} from "./useDuplicateAcceptance";
import { useDuplicateDetectionReport } from "./useDuplicateDetectionReport";
import { useSignalR } from "./useSignalR";
import "@/test/setup-combobox-polyfills";

const hub = vi.hoisted(() => ({
  state: "Disconnected",
  connectionId: "same-session",
  start: vi.fn(),
  stop: vi.fn(),
  on: vi.fn(),
  off: vi.fn(),
  onreconnecting: vi.fn(),
  onreconnected: vi.fn(),
  onclose: vi.fn(),
}));
const buffered = vi.hoisted(() => vi.fn());
vi.mock("@/lib/signalr-toast-buffer", () => ({
  bufferToast: buffered,
  clearBufferedToasts: vi.fn(),
}));
vi.mock("@microsoft/signalr", () => ({
  HubConnectionBuilder: class {
    withUrl() {
      return this;
    }
    withAutomaticReconnect() {
      return this;
    }
    configureLogging() {
      return this;
    }
    build() {
      return hub;
    }
  },
  LogLevel: { Debug: 1, None: 6 },
  HubConnectionState: {
    Disconnected: "Disconnected",
    Connecting: "Connecting",
    Connected: "Connected",
    Reconnecting: "Reconnecting",
    Disconnecting: "Disconnecting",
  },
}));
let label: string;
let accepted: boolean;
let writes: unknown[];
let itemReads: number;
let reportReads: number;
let queryClient: ReturnType<typeof createAppQueryClient>;
const receiptIds = ["receipt-1", "receipt-2"];
const receiptRows = receiptIds.map((receiptId) => ({
  receiptId,
  location: "Shop",
  date: "2026-09-01",
  transactionTotal: 10,
}));
const server = setupServer(
  http.get("*/api/receipt-items/item-1", () => {
    itemReads++;
    return HttpResponse.json({
      id: "item-1",
      description: "MILK RAW",
      quantity: 1,
      unitPrice: 10,
      category: "Food",
      normalizedDescriptionName: label === "(Not Normalized)" ? null : label,
    });
  }),
  http.get("*/api/reports/spending-by-normalized-description", () => {
    reportReads++;
    return HttpResponse.json({
      items: [
        {
          canonicalName: label,
          totalAmount: 10,
          currency: "USD",
          itemCount: 1,
        },
      ],
      totalCount: 1,
      grandTotal: 10,
    });
  }),
  http.patch(
    "*/api/normalized-descriptions/normalized-1/rename",
    async ({ request }) => {
      writes.push(await request.json());
      label = "Whole Milk";
      return HttpResponse.json({ id: "normalized-1", displayName: label });
    },
  ),
  http.post(
    "*/api/normalized-descriptions/requeue-pending",
    async ({ request }) => {
      writes.push(await request.json());
      label = "(Not Normalized)";
      return HttpResponse.json({
        deletedDescriptionCount: 1,
        unlinkedItemCount: 1,
        clearedMatchScoreCount: 1,
      });
    },
  ),
  http.get("*/api/reports/duplicates", () =>
    HttpResponse.json({
      groupCount: accepted ? 0 : 1,
      totalDuplicateReceipts: accepted ? 0 : 2,
      groups: accepted
        ? []
        : [
            {
              matchKey: "2026-09-01 @ Shop",
              isAccepted: false,
              receipts: receiptRows,
            },
          ],
    }),
  ),
  http.get("*/api/reports/duplicates/accepted", () =>
    HttpResponse.json({
      groupCount: accepted ? 1 : 0,
      groups: accepted
        ? [{ acceptedAt: "2026-09-01T00:00:00Z", receipts: receiptRows }]
        : [],
    }),
  ),
  http.post("*/api/reports/duplicates/accepted", async ({ request }) => {
    writes.push(await request.json());
    accepted = true;
    return HttpResponse.json({ acceptedPairCount: 1 });
  }),
  http.post("*/api/reports/duplicates/accepted/remove", async ({ request }) => {
    writes.push(await request.json());
    accepted = false;
    return HttpResponse.json({ removedPairCount: 1 });
  }),
);
beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
afterAll(() => server.close());
beforeEach(() => {
  vi.clearAllMocks();
  clearTokens();
  localStorage.clear();
  label = "Milk";
  accepted = false;
  writes = [];
  itemReads = 0;
  reportReads = 0;
  hub.state = "Disconnected";
  hub.start.mockImplementation(async () => {
    hub.state = "Connected";
  });
  hub.stop.mockImplementation(async () => {
    hub.state = "Disconnected";
  });
  queryClient = createAppQueryClient();
});
afterEach(() => {
  cleanup();
  queryClient.clear();
  server.resetHandlers();
  toast.dismiss();
});

function CurationViews({ remote }: { remote: boolean }) {
  const connection = useSignalR(remote);
  const item = useReceiptItem("item-1");
  const report = useSpendingByNormalizedDescription();
  const rename = useRenameMutation();
  const requeue = useRequeuePendingMutation();
  return (
    <>
      <output aria-label="Connection">{connection.connectionState}</output>
      {item.data && (
        <ReceiptItemsCard
          receiptId="receipt-1"
          items={[item.data]}
          subtotal={10}
        />
      )}
      <output aria-label="Spending bucket">
        {report.data?.items[0]?.canonicalName}
      </output>
      <output aria-label="Spending total">{report.data?.grandTotal}</output>
      <button
        onClick={() =>
          rename.mutate({ id: "normalized-1", displayLabel: "Whole Milk" })
        }
      >
        Rename bucket
      </button>
      <button
        onClick={() => requeue.mutate({ expectedFingerprint: "snapshot-1" })}
      >
        Requeue bucket
      </button>
      <output aria-label="Mutation state">
        {rename.status}/{requeue.status}
      </output>
    </>
  );
}
function DuplicateViews({ remote }: { remote: boolean }) {
  const connection = useSignalR(remote);
  const report = useDuplicateDetectionReport();
  const allowed = useAcceptedDuplicates();
  const accept = useAcceptDuplicateGroup();
  const unaccept = useUnacceptDuplicateGroup();
  return (
    <>
      <output aria-label="Connection">{connection.connectionState}</output>
      <output aria-label="Duplicate groups">{report.data?.groupCount}</output>
      <output aria-label="Accepted groups">{allowed.data?.groupCount}</output>
      <button onClick={() => accept.mutate(receiptIds)}>Accept group</button>
      <button onClick={() => unaccept.mutate(receiptIds)}>
        Undo acceptance
      </button>
      <output aria-label="Mutation state">
        {accept.status}/{unaccept.status}
      </output>
    </>
  );
}
async function ready(remote: boolean) {
  if (remote)
    await waitFor(() =>
      expect(
        screen.getByRole("status", { name: "Connection" }),
      ).toHaveTextContent(/^connected$/),
    );
  await waitFor(() => expect(queryClient.isFetching()).toBe(0));
  // Initial connection catch-up has finished. These are fresh controls for the
  // specific producer event rather than evidence accidentally inherited from it.
  queryClient.setQueryData(["api-keys", "private"], {
    token: "private-fixture",
  });
  queryClient.setQueryData(["ynab", "budgets"], { data: [] });
  queryClient.setQueryData(["receipt-items", "inactive-fixture"], {
    normalizedDescriptionName: label,
  });
  buffered.mockClear();
}
function deliver(entityType: string, origin: "remote" | "same-session") {
  const handler = hub.on.mock.calls.find(
    ([name]) => name === "EntityChanged",
  )?.[1] as (event: object) => void;
  expect(handler).toBeDefined();
  act(() =>
    handler({
      entityType,
      changeType: "updated",
      id: null,
      connectionId: origin,
    }),
  );
}
function preservePrivate() {
  expect(
    queryClient.getQueryState(["api-keys", "private"])?.isInvalidated,
  ).toBe(false);
  expect(queryClient.getQueryState(["ynab", "budgets"])?.isInvalidated).toBe(
    false,
  );
}

function ConnectionOnly() {
  const connection = useSignalR(true);
  return (
    <output aria-label="Live connection">{connection.connectionState}</output>
  );
}

function holdFirstResponse(
  path: string,
  snapshot: () => Record<string, unknown>,
) {
  const deferred = () => {
    let resolve!: () => void;
    const promise = new Promise<void>((complete) => {
      resolve = complete;
    });
    return { promise, resolve };
  };
  const started = deferred();
  const release = deferred();
  const responded = deferred();
  let reads = 0;
  server.use(
    http.get(path, async () => {
      const body = snapshot();
      reads++;
      if (reads === 1) {
        started.resolve();
        await release.promise;
        responded.resolve();
      }
      return HttpResponse.json(body);
    }),
  );
  return { started, release, responded, reads: () => reads };
}

it.each(["local", "remote", "same-session"] as const)(
  "replaces held initial item/report reads after %s normalized-description change",
  async (origin) => {
    const item = holdFirstResponse("*/api/receipt-items/item-1", () => ({
      id: "item-1",
      description: "MILK RAW",
      quantity: 1,
      unitPrice: 10,
      category: "Food",
      normalizedDescriptionName: label,
    }));
    const report = holdFirstResponse(
      "*/api/reports/spending-by-normalized-description",
      () => ({
        items: [
          {
            canonicalName: label,
            totalAmount: 10,
            currency: "USD",
            itemCount: 1,
          },
        ],
        totalCount: 1,
        grandTotal: 10,
      }),
    );
    const view = (show: boolean) => (
      <QueryClientProvider client={queryClient}>
        {origin !== "local" && <ConnectionOnly />}
        {show && <CurationViews remote={false} />}
      </QueryClientProvider>
    );
    const rendered = renderWithProviders(view(false));
    // Finish the initial connection catch-up before mounting either held read.
    if (origin !== "local")
      await waitFor(() =>
        expect(
          screen.getByRole("status", { name: "Live connection" }),
        ).toHaveTextContent(/^connected$/),
      );
    await act(async () => {});
    rendered.rerender(view(true));
    try {
      await Promise.all([item.started.promise, report.started.promise]);
      expect(
        queryClient.getQueryState(["receipt-items", "item-1"]),
      ).toMatchObject({ data: undefined, fetchStatus: "fetching" });
      expect(
        queryClient.getQueriesData({
          queryKey: ["reports", "spending-by-normalized-description"],
        }),
      ).toEqual([[expect.any(Array), undefined]]);
      if (origin === "local") {
        await userEvent.click(
          screen.getByRole("button", { name: "Rename bucket" }),
        );
        await waitFor(() =>
          expect(
            screen.getByRole("status", { name: "Mutation state" }),
          ).toHaveTextContent("success/idle"),
        );
        expect(writes).toEqual([{ displayLabel: "Whole Milk" }]);
      } else {
        label = "Whole Milk";
        deliver("normalized-description", origin);
      }
      await act(async () => {
        item.release.resolve();
        report.release.resolve();
        await Promise.all([item.responded.promise, report.responded.promise]);
      });
      await waitFor(() => expect(queryClient.isFetching()).toBe(0));
      await waitFor(() =>
        expect(screen.getByTestId("normalized-as-item-1")).toHaveTextContent(
          "normalized as Whole Milk",
        ),
      );
      await waitFor(() =>
        expect(
          screen.getByRole("status", { name: "Spending bucket" }),
        ).toHaveTextContent(/^Whole Milk$/),
      );
      expect(item.reads()).toBe(2);
      expect(report.reads()).toBe(2);
      expect(
        queryClient.getQueryData(["receipt-items", "item-1"]),
      ).toMatchObject({ normalizedDescriptionName: "Whole Milk" });
    } finally {
      item.release.resolve();
      report.release.resolve();
    }
  },
);

it.each(["local", "remote", "same-session"] as const)(
  "replaces held initial duplicate reads after %s acceptance",
  async (origin) => {
    const report = holdFirstResponse("*/api/reports/duplicates", () => ({
      groupCount: accepted ? 0 : 1,
      totalDuplicateReceipts: accepted ? 0 : 2,
      groups: accepted
        ? []
        : [
            {
              matchKey: "2026-09-01 @ Shop",
              isAccepted: false,
              receipts: receiptRows,
            },
          ],
    }));
    const allowed = holdFirstResponse(
      "*/api/reports/duplicates/accepted",
      () => ({
        groupCount: accepted ? 1 : 0,
        groups: accepted
          ? [{ acceptedAt: "2026-09-01T00:00:00Z", receipts: receiptRows }]
          : [],
      }),
    );
    const view = (show: boolean) => (
      <QueryClientProvider client={queryClient}>
        {origin !== "local" && <ConnectionOnly />}
        {show && <DuplicateViews remote={false} />}
      </QueryClientProvider>
    );
    const rendered = renderWithProviders(view(false));
    if (origin !== "local")
      await waitFor(() =>
        expect(
          screen.getByRole("status", { name: "Live connection" }),
        ).toHaveTextContent(/^connected$/),
      );
    await act(async () => {});
    rendered.rerender(view(true));
    try {
      await Promise.all([report.started.promise, allowed.started.promise]);
      expect(
        queryClient.getQueryState(["reports", "accepted-duplicates"]),
      ).toMatchObject({ data: undefined, fetchStatus: "fetching" });
      expect(
        queryClient.getQueriesData({ queryKey: ["reports", "duplicates"] }),
      ).toEqual([[expect.any(Array), undefined]]);
      if (origin === "local") {
        await userEvent.click(
          screen.getByRole("button", { name: "Accept group" }),
        );
        await waitFor(() =>
          expect(
            screen.getByRole("status", { name: "Mutation state" }),
          ).toHaveTextContent("success/idle"),
        );
        expect(writes).toEqual([{ receiptIds }]);
      } else {
        accepted = true;
        deliver("duplicate-acceptance", origin);
      }
      await act(async () => {
        report.release.resolve();
        allowed.release.resolve();
        await Promise.all([
          report.responded.promise,
          allowed.responded.promise,
        ]);
      });
      await waitFor(() => expect(queryClient.isFetching()).toBe(0));
      await waitFor(() =>
        expect(
          screen.getByRole("status", { name: "Duplicate groups" }),
        ).toHaveTextContent(/^0$/),
      );
      await waitFor(() =>
        expect(
          screen.getByRole("status", { name: "Accepted groups" }),
        ).toHaveTextContent(/^1$/),
      );
      expect(report.reads()).toBe(2);
      expect(allowed.reads()).toBe(2);
      expect(
        queryClient.getQueryData(["reports", "accepted-duplicates"]),
      ).toMatchObject({ groupCount: 1 });
    } finally {
      report.release.resolve();
      allowed.release.resolve();
    }
  },
);

it.each(["local", "remote", "same-session"] as const)(
  "repairs a rendered receipt-item normalization label after %s rename",
  async (origin) => {
    renderWithProviders(
      <QueryClientProvider client={queryClient}>
        <CurationViews remote={origin !== "local"} />
      </QueryClientProvider>,
    );
    expect(await screen.findByTestId("normalized-as-item-1")).toHaveTextContent(
      "normalized as Milk",
    );
    expect(
      await screen.findByRole("status", { name: "Spending bucket" }),
    ).toHaveTextContent(/^Milk$/);
    await ready(origin !== "local");
    const before = itemReads;
    if (origin === "local") {
      await userEvent.click(
        screen.getByRole("button", { name: "Rename bucket" }),
      );
      await waitFor(() =>
        expect(
          screen.getByRole("status", { name: "Mutation state" }),
        ).toHaveTextContent("success/idle"),
      );
      expect(writes).toEqual([{ displayLabel: "Whole Milk" }]);
    } else {
      label = "Whole Milk";
      deliver("normalized-description", origin);
    }
    await waitFor(() =>
      expect(screen.getByTestId("normalized-as-item-1")).toHaveTextContent(
        "normalized as Whole Milk",
      ),
    );
    expect(itemReads).toBeGreaterThan(before);
    await waitFor(() =>
      expect(
        screen.getByRole("status", { name: "Spending bucket" }),
      ).toHaveTextContent(/^Whole Milk$/),
    );
    expect(queryClient.getQueryData(["receipt-items", "item-1"])).toMatchObject(
      { normalizedDescriptionName: "Whole Milk" },
    );
    expect(
      queryClient.getQueryState(["receipt-items", "inactive-fixture"])
        ?.isInvalidated,
    ).toBe(true);
    preservePrivate();
    expect(buffered).toHaveBeenCalledTimes(origin === "remote" ? 1 : 0);
  },
);

it.each(["local", "remote", "same-session"] as const)(
  "repairs actual spending report data after %s requeue",
  async (origin) => {
    renderWithProviders(
      <QueryClientProvider client={queryClient}>
        <CurationViews remote={origin !== "local"} />
      </QueryClientProvider>,
    );
    await waitFor(() =>
      expect(
        screen.getByRole("status", { name: "Spending bucket" }),
      ).toHaveTextContent(/^Milk$/),
    );
    await ready(origin !== "local");
    const before = reportReads;
    if (origin === "local") {
      await userEvent.click(
        screen.getByRole("button", { name: "Requeue bucket" }),
      );
      await waitFor(() =>
        expect(
          screen.getByRole("status", { name: "Mutation state" }),
        ).toHaveTextContent("idle/success"),
      );
      expect(writes).toEqual([{ expectedFingerprint: "snapshot-1" }]);
    } else {
      label = "(Not Normalized)";
      deliver("normalized-description", origin);
    }
    await waitFor(() =>
      expect(
        screen.getByRole("status", { name: "Spending bucket" }),
      ).toHaveTextContent(/Not Normalized/),
    );
    expect(reportReads).toBeGreaterThan(before);
    expect(
      screen.getByRole("status", { name: "Spending total" }),
    ).toHaveTextContent(/^10$/);
    await waitFor(() =>
      expect(
        screen.queryByTestId("normalized-as-item-1"),
      ).not.toBeInTheDocument(),
    );
    preservePrivate();
    expect(buffered).toHaveBeenCalledTimes(origin === "remote" ? 1 : 0);
  },
);

it.each(["local", "remote", "same-session"] as const)(
  "repairs duplicate and accepted report caches for %s accept and undo",
  async (origin) => {
    renderWithProviders(
      <QueryClientProvider client={queryClient}>
        <DuplicateViews remote={origin !== "local"} />
      </QueryClientProvider>,
    );
    await waitFor(() =>
      expect(
        screen.getByRole("status", { name: "Duplicate groups" }),
      ).toHaveTextContent(/^1$/),
    );
    await waitFor(() =>
      expect(
        screen.getByRole("status", { name: "Accepted groups" }),
      ).toHaveTextContent(/^0$/),
    );
    await ready(origin !== "local");
    for (const action of ["Accept group", "Undo acceptance"]) {
      if (origin === "local")
        await userEvent.click(screen.getByRole("button", { name: action }));
      else {
        accepted = action === "Accept group";
        deliver("duplicate-acceptance", origin);
      }
      await waitFor(() =>
        expect(
          screen.getByRole("status", { name: "Duplicate groups" }),
        ).toHaveTextContent(action === "Accept group" ? /^0$/ : /^1$/),
      );
      await waitFor(() =>
        expect(
          screen.getByRole("status", { name: "Accepted groups" }),
        ).toHaveTextContent(action === "Accept group" ? /^1$/ : /^0$/),
      );
    }
    expect(writes).toEqual(
      origin === "local" ? [{ receiptIds }, { receiptIds }] : [],
    );
    preservePrivate();
    expect(buffered).toHaveBeenCalledTimes(origin === "remote" ? 2 : 0);
  },
);
