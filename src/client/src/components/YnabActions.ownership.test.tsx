vi.hoisted(() =>
  vi.stubEnv("VITE_API_URL", "http://ynab-action-ownership.test"),
);
import {
  act,
  cleanup,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState, type ReactNode } from "react";
import { createMemoryRouter, RouterProvider } from "react-router";
import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import { toast } from "sonner";
import type { components } from "@/generated/api";
import { AuthProvider } from "@/contexts/AuthContext";
import { AppearanceProvider } from "@/contexts/AppearanceContext";
import { RootLayout } from "@/components/RootLayout";
import { createAppQueryClient } from "@/lib/query-client";
import { clearTokens, setTokens } from "@/lib/auth";
import { clearServerErrorPageFlag } from "@/lib/server-error-bus";
import {
  getCacheErrorPresentation,
  getRequestErrorPresentation,
} from "@/lib/request-error-policy";
import { mockReceiptListItemResponse } from "@/test/mock-api";
import { YnabMemoSyncCard } from "./YnabMemoSyncCard";
import { YnabPushButton } from "./YnabPushButton";
import { YnabBulkSyncCard } from "./YnabBulkSyncCard";
import Receipts from "@/pages/Receipts";
import { CommandPalette } from "./CommandPalette";
import { TooltipProvider } from "./ui/tooltip";
import "@/test/setup-combobox-polyfills";

type Outcome = components["schemas"]["YnabMemoSyncOutcome"];
type MemoResult = components["schemas"]["YnabMemoSyncResultItem"];
const receiptId = "95000000-0000-4000-8000-000000000001";
const localId = "95000000-0000-4000-8000-000000000002";
function memo(outcome: Outcome, index = 0): MemoResult {
  return {
    receiptId,
    localTransactionId: index
      ? "95000000-0000-4000-8000-000000000003"
      : localId,
    outcome,
    ...(outcome === "failed" ? { error: "Remote resolution failed" } : {}),
    ...(outcome === "ambiguous"
      ? {
          ambiguousCandidates: [
            {
              id: "remote-1",
              accountId: "remote-account",
              date: "2026-09-01",
              payeeName: "Candidate store",
              amount: -12500,
              memo: "Candidate memo",
            },
          ],
        }
      : {}),
  };
}
function deferred() {
  let resolve!: () => void;
  const promise = new Promise<void>((done) => {
    resolve = done;
  });
  return { resolve, promise };
}
let results: MemoResult[];
let resolveOutcome: Outcome;
let failurePath: string | undefined;
let failureStatus: number;
let heldPath: string | undefined;
let gate: ReturnType<typeof deferred>;
let started: ReturnType<typeof deferred>;
let heldSignal: AbortSignal | undefined;
let calls: { path: string; body: unknown }[];
const pushResult = {
  success: true,
  operationStatus: "synced",
  pushedTransactions: [
    {
      localTransactionId: localId,
      ynabTransactionId: "remote-1",
      milliunits: -12500,
      subTransactionCount: 1,
    },
  ],
  unmappedCategories: [],
  error: null,
};
const actionPaths = [
  "sync-memos",
  "push-transactions",
  "sync-memos/bulk",
  "push-transactions/bulk",
  "sync-memos/resolve",
];
const server = setupServer(
  http.get("*/api/ynab/connection-status", () =>
    HttpResponse.json({ isConfigured: true, isConnected: true }),
  ),
  http.get("*/api/ynab/settings/budget", () =>
    HttpResponse.json({ selectedBudgetId: "budget-1" }),
  ),
  http.get("*/api/receipts", () =>
    HttpResponse.json({
      data: [
        mockReceiptListItemResponse({
          id: receiptId,
          location: "Action store",
        }),
      ],
      total: 1,
      offset: 0,
      limit: 500,
    }),
  ),
  http.get("*/api/ynab/receipt-sync-statuses", () =>
    HttpResponse.json({ data: [{ receiptId, syncStatus: "notSynced" }] }),
  ),
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
  ...actionPaths.map((path) =>
    http.post(`*/api/ynab/${path}`, async ({ request }) => {
      calls.push({ path, body: await request.json() });
      if (path === heldPath) {
        heldSignal = request.signal;
        started.resolve();
        await gate.promise;
      }
      if (path === failurePath)
        return HttpResponse.json(
          { status: failureStatus, detail: `${path} unavailable` },
          { status: failureStatus },
        );
      if (path === "sync-memos/resolve") {
        const result = memo(resolveOutcome);
        results = [result];
        return HttpResponse.json(result);
      }
      if (path === "push-transactions") return HttpResponse.json(pushResult);
      if (path === "push-transactions/bulk")
        return HttpResponse.json({
          results: [{ receiptId, result: pushResult }],
        });
      return HttpResponse.json({ results });
    }),
  ),
);
let queryClient: ReturnType<typeof createAppQueryClient>;
let clients: ReturnType<typeof createAppQueryClient>[];
let router: ReturnType<typeof createMemoryRouter>;
let successToast: ReturnType<typeof vi.spyOn>;
let infoToast: ReturnType<typeof vi.spyOn>;
let warningToast: ReturnType<typeof vi.spyOn>;
let errorToast: ReturnType<typeof vi.spyOn>;
beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
afterAll(() => server.close());
beforeEach(() => {
  localStorage.clear();
  setTokens("alice-access", "alice-refresh");
  clearServerErrorPageFlag();
  results = [memo("synced")];
  resolveOutcome = "synced";
  failurePath = undefined;
  failureStatus = 503;
  heldPath = undefined;
  heldSignal = undefined;
  gate = deferred();
  started = deferred();
  calls = [];
  clients = [];
  successToast = vi.spyOn(toast, "success");
  infoToast = vi.spyOn(toast, "info");
  warningToast = vi.spyOn(toast, "warning");
  errorToast = vi.spyOn(toast, "error");
});
afterEach(async () => {
  cleanup();
  await act(async () => {
    gate.resolve();
    await gate.promise;
  });
  clients.forEach((client) => client.clear());
  clearTokens();
  toast.dismiss();
  vi.restoreAllMocks();
  server.resetHandlers();
});
function renderAction(feature: ReactNode) {
  router = createMemoryRouter(
    [
      {
        element: <RootLayout />,
        children: [
          {
            path: "/actions",
            element: <section aria-label="YNAB action">{feature}</section>,
          },
          { path: "/error/500", element: <h1>Global error route</h1> },
        ],
      },
    ],
    { initialEntries: ["/actions"] },
  );
  render(
    <AppearanceProvider>
      <TooltipProvider>
        <AuthProvider
          queryClientFactory={() => {
            queryClient = createAppQueryClient();
            clients.push(queryClient);
            return queryClient;
          }}
        >
          <RouterProvider router={router} />
        </AuthProvider>
      </TooltipProvider>
    </AppearanceProvider>,
  );
}

// Layout.tsx conditionally mounts the palette. Closing must not remove the
// pending command's error owner when its native request finishes later.
function PaletteHost() {
  const [open, setOpen] = useState(true);
  return (
    <>
      <button onClick={() => setOpen(true)}>Open command palette</button>
      {open && <CommandPalette open={open} onOpenChange={setOpen} />}
    </>
  );
}

it("keeps selected receipts and presents one local bulk-push transport failure", async () => {
  failurePath = "push-transactions/bulk";
  renderAction(<Receipts />);
  const selection = await screen.findByRole("checkbox", {
    name: "Select Action store",
  });
  await userEvent.setup().click(selection);
  await clickAction("Push to YNAB");
  await waitFor(() =>
    expect(errorToast).toHaveBeenCalledWith(
      "push-transactions/bulk unavailable",
    ),
  );
  await settled();
  expect(errorToast).toHaveBeenCalledTimes(1);
  expect(successToast).not.toHaveBeenCalled();
  expect(infoToast).not.toHaveBeenCalled();
  expect(warningToast).not.toHaveBeenCalled();
  expect(selection).toBeChecked();
  expect(screen.getByRole("button", { name: "Push to YNAB" })).toBeEnabled();
  expect(calls).toEqual([
    { path: "push-transactions/bulk", body: { receiptIds: [receiptId] } },
  ]);
  expect(router.state.location.pathname).toBe("/actions");
  expect(screen.queryByText("Global error route")).not.toBeInTheDocument();
});

it.each([1, 2])(
  "owns feedback for %i palette bulk-push invocation(s) after close, including overlapping requests",
  async (count) => {
    const pending = Array.from({ length: count }, () => ({
      started: deferred(),
      release: deferred(),
    }));
    const requests: unknown[] = [];
    server.use(
      http.post("*/api/ynab/push-transactions/bulk", async ({ request }) => {
        const index = requests.length;
        requests.push(await request.json());
        pending[index].started.resolve();
        await pending[index].release.promise;
        return HttpResponse.json(
          { status: 503, detail: `Palette push ${index + 1} unavailable` },
          { status: 503 },
        );
      }),
    );
    renderAction(<PaletteHost />);
    const user = userEvent.setup();
    try {
      for (let index = 0; index < count; index++) {
        if (index)
          await user.click(
            screen.getByRole("button", { name: "Open command palette" }),
          );
        await user.type(
          await screen.findByPlaceholderText("Type a command or search…"),
          "Sync YNAB Now",
        );
        await user.click(
          await screen.findByRole("option", { name: /Sync YNAB Now/ }),
        );
        await pending[index].started.promise;
        expect(
          screen.queryByPlaceholderText("Type a command or search…"),
        ).not.toBeInTheDocument();
      }
      expect(queryClient.isMutating()).toBe(count);
      expect(errorToast).not.toHaveBeenCalled();
      // Finish in reverse order to prove feedback belongs to each invocation.
      for (let index = count - 1; index >= 0; index--) {
        await act(async () => {
          pending[index].release.resolve();
        });
        await waitFor(() =>
          expect(errorToast).toHaveBeenCalledWith(
            `Palette push ${index + 1} unavailable`,
          ),
        );
      }
      await settled();
      expect(errorToast).toHaveBeenCalledTimes(count);
      expect(requests).toEqual(
        Array.from({ length: count }, () => ({ receiptIds: [receiptId] })),
      );
      expect(successToast).not.toHaveBeenCalled();
      expect(infoToast).not.toHaveBeenCalled();
      expect(warningToast).not.toHaveBeenCalled();
      expect(router.state.location.pathname).toBe("/actions");
      expect(
        screen.getByRole("button", { name: "Open command palette" }),
      ).toBeVisible();
      expect(screen.queryByText("Global error route")).not.toBeInTheDocument();
    } finally {
      await act(async () => {
        pending.forEach(({ release }) => release.resolve());
      });
    }
  },
);
async function clickAction(name: string) {
  const button = await screen.findByRole("button", { name });
  await waitFor(() => expect(button).toBeEnabled());
  await userEvent.setup().click(button);
}
async function settled() {
  await waitFor(() => expect(queryClient.isMutating()).toBe(0));
}
const memoModes = [
  {
    name: "single",
    button: "Sync Memos",
    path: "sync-memos",
    component: <YnabMemoSyncCard receiptId={receiptId} />,
  },
  {
    name: "bulk",
    button: "Sync All Memos",
    path: "sync-memos/bulk",
    component: <YnabBulkSyncCard />,
  },
];

it.each([
  ...memoModes,
  {
    name: "single push",
    button: "Push to YNAB",
    path: "push-transactions",
    component: <YnabPushButton receiptId={receiptId} hasTransactions />,
  },
  {
    name: "bulk push",
    button: "Push All to YNAB",
    path: "push-transactions/bulk",
    component: <YnabBulkSyncCard />,
  },
])(
  "keeps $name HTTP503 in its component without global feedback",
  async ({ component, button, path }) => {
    failurePath = path;
    renderAction(component);
    await clickAction(button);
    await settled();
    expect.soft(router.state.location.pathname).toBe("/actions");
    expect
      .soft(screen.queryByRole("heading", { name: "Global error route" }))
      .not.toBeInTheDocument();
    expect.soft(errorToast).not.toHaveBeenCalled();
    expect.soft(successToast).not.toHaveBeenCalled();
    expect.soft(infoToast).not.toHaveBeenCalled();
    const owner = screen.queryByRole("region", { name: "YNAB action" });
    expect(owner).toBeInTheDocument();
    expect(within(owner!).getAllByRole("alert")).toHaveLength(1);
    expect(within(owner!).getByRole("alert")).toHaveTextContent(
      /failed|unavailable/i,
    );
    expect(calls).toEqual([
      {
        path,
        body: path.endsWith("/bulk")
          ? { receiptIds: [receiptId] }
          : { receiptId },
      },
    ]);
  },
);

for (const mode of memoModes) {
  it(`${mode.name} all-failed HTTP200 memo result is an error, never a success or no-op`, async () => {
    results = [memo("failed")];
    renderAction(mode.component);
    await clickAction(mode.button);
    await settled();
    expect(screen.getByText("1 failed")).toBeVisible();
    expect.soft(successToast).not.toHaveBeenCalled();
    expect.soft(infoToast).not.toHaveBeenCalled();
    expect(errorToast).toHaveBeenCalledTimes(1);
    expect(String(errorToast.mock.calls[0][0])).toMatch(/1.*fail|fail.*1/i);
    expect(router.state.location.pathname).toBe("/actions");
  });
  it(`${mode.name} mixed HTTP200 memo result warns with both exact counts`, async () => {
    results = [memo("synced"), memo("failed", 1)];
    renderAction(mode.component);
    await clickAction(mode.button);
    await settled();
    expect(screen.getByText("1 synced")).toBeVisible();
    expect(screen.getByText("1 failed")).toBeVisible();
    expect.soft(successToast).not.toHaveBeenCalled();
    expect.soft(infoToast).not.toHaveBeenCalled();
    expect(warningToast).toHaveBeenCalledTimes(1);
    expect(String(warningToast.mock.calls[0][0])).toMatch(/1.*sync|sync.*1/i);
    expect(String(warningToast.mock.calls[0][0])).toMatch(/1.*fail|fail.*1/i);
  });
  it(`${mode.name} alreadySynced no-op remains informational`, async () => {
    results = [memo("alreadySynced")];
    renderAction(mode.component);
    await clickAction(mode.button);
    await settled();
    expect(screen.getByText("1 already synced")).toBeVisible();
    expect.soft(successToast).not.toHaveBeenCalled();
    expect(errorToast).not.toHaveBeenCalled();
    expect(warningToast).not.toHaveBeenCalled();
    expect(infoToast).toHaveBeenCalledTimes(1);
  });
}

async function openCandidate(candidateCount = 1) {
  const ambiguous = memo("ambiguous");
  if (candidateCount === 2) {
    ambiguous.ambiguousCandidates?.push({
      id: "remote-2",
      accountId: "remote-account",
      date: "2026-09-01",
      payeeName: "Another candidate",
      amount: -12500,
      memo: "Second candidate",
    });
  }
  results = [ambiguous];
  renderAction(<YnabMemoSyncCard receiptId={receiptId} />);
  await clickAction("Sync Memos");
  await settled();
  await clickAction("Resolve");
  const dialog = await screen.findByRole("dialog", {
    name: "Resolve Ambiguous Match",
  });
  expect(within(dialog).getByText("Candidate store")).toBeVisible();
  successToast.mockClear();
  infoToast.mockClear();
  warningToast.mockClear();
  errorToast.mockClear();
  return dialog;
}
it.each(["failed", "reconciledSkipped", "synced", "alreadySynced"] as const)(
  "uses the resolve HTTP200 %s outcome before deciding whether to close and resync",
  async (outcome) => {
    resolveOutcome = outcome;
    const dialog = await openCandidate();
    await userEvent
      .setup()
      .click(within(dialog).getByRole("button", { name: "Select" }));
    await settled();
    expect(calls.filter((call) => call.path === "sync-memos/resolve")).toEqual([
      {
        path: "sync-memos/resolve",
        body: { localTransactionId: localId, ynabTransactionId: "remote-1" },
      },
    ]);
    if (outcome === "failed" || outcome === "reconciledSkipped") {
      expect.soft(dialog).toBeInTheDocument();
      expect
        .soft(calls.filter((call) => call.path === "sync-memos"))
        .toHaveLength(1);
      expect.soft(successToast).not.toHaveBeenCalled();
      expect.soft(infoToast).not.toHaveBeenCalled();
      const feedback = outcome === "failed" ? errorToast : warningToast;
      expect(feedback).toHaveBeenCalledTimes(1);
      expect(String(feedback.mock.calls[0][0])).toMatch(
        outcome === "failed" ? /fail/i : /reconcil/i,
      );
    } else {
      expect(dialog).not.toBeInTheDocument();
      expect(calls.filter((call) => call.path === "sync-memos")).toEqual([
        { path: "sync-memos", body: { receiptId } },
        { path: "sync-memos", body: { receiptId } },
      ]);
      expect(errorToast).not.toHaveBeenCalled();
    }
  },
);

it("blocks concurrent memo actions and refuses dialog dismissal while resolution remains pending", async () => {
  heldPath = "sync-memos/resolve";
  const dialog = await openCandidate(2);
  const selects = within(dialog).getAllByRole("button", { name: "Select" });
  expect(selects).toHaveLength(2);
  const user = userEvent.setup();
  await user.click(selects[0]);
  await started.promise;
  try {
    for (const select of selects) {
      expect(select).toBeDisabled();
      await user.click(select);
    }
    expect(
      calls.filter((call) => call.path === "sync-memos/resolve"),
    ).toHaveLength(1);
    const sync = screen.getByText("Sync Memos").closest("button")!;
    expect.soft(sync).toBeDisabled();
    // Escape must not erase the pending operation's ownership. The dialog now
    // refuses dismissal until its request settles.
    await user.keyboard("{Escape}");
    expect(dialog).toBeInTheDocument();
    expect.soft(sync).toBeDisabled();
    expect
      .soft(calls.filter((call) => call.path === "sync-memos"))
      .toHaveLength(1);
  } finally {
    await act(async () => {
      gate.resolve();
      await gate.promise;
    });
  }
  await settled();
  expect(dialog).not.toBeInTheDocument();
  expect(calls.filter((call) => call.path === "sync-memos")).toHaveLength(2);
});

it("cannot dispatch an obsolete candidate choice while a new memo sync is pending", async () => {
  const dialog = await openCandidate(2);
  const user = userEvent.setup();
  await user.keyboard("{Escape}");
  await waitFor(() => expect(dialog).not.toBeInTheDocument());
  heldPath = "sync-memos";
  await clickAction("Sync Memos");
  await started.promise;
  try {
    const resolve = screen.getByRole("button", { name: "Resolve" });
    await user.click(resolve);
    const staleDialog = screen.queryByRole("dialog", {
      name: "Resolve Ambiguous Match",
    });
    if (staleDialog) {
      const select = within(staleDialog).getAllByRole("button", {
        name: "Select",
      })[0];
      expect.soft(select).toBeDisabled();
      await user.click(select);
    }
    expect
      .soft(calls.filter((call) => call.path === "sync-memos/resolve"))
      .toHaveLength(0);
  } finally {
    await act(async () => {
      gate.resolve();
      await gate.promise;
    });
  }
  await settled();
});

it("suppresses obsolete resolution feedback and follow-up after a real session transition", async () => {
  heldPath = "sync-memos/resolve";
  const dialog = await openCandidate();
  const oldClient = queryClient;
  await userEvent
    .setup()
    .click(within(dialog).getByRole("button", { name: "Select" }));
  await started.promise;
  const mutation = oldClient
    .getMutationCache()
    .getAll()
    .find((entry) => entry.state.status === "pending")!;
  expect(mutation).toBeDefined();
  expect(heldSignal?.aborted).toBe(false);
  await act(async () => {
    setTokens("bob-access", "bob-refresh");
  });
  expect(queryClient).not.toBe(oldClient);
  expect(heldSignal?.aborted).toBe(true);
  await act(async () => {
    gate.resolve();
    await gate.promise;
  });
  await waitFor(() => expect(mutation.state.status).toBe("error"));
  await waitFor(() => expect(queryClient.isFetching()).toBe(0));
  expect(calls.filter((call) => call.path === "sync-memos")).toHaveLength(1);
  expect(successToast).not.toHaveBeenCalled();
  expect(infoToast).not.toHaveBeenCalled();
  expect(warningToast).not.toHaveBeenCalled();
  expect(errorToast).not.toHaveBeenCalled();
  expect(queryClient.getMutationCache().getAll()).toEqual([]);
});

it("shows both currencySkipped and reconciledSkipped bulk memo outcomes", async () => {
  results = [memo("currencySkipped"), memo("reconciledSkipped", 1)];
  renderAction(<YnabBulkSyncCard />);
  await clickAction("Sync All Memos");
  await settled();
  const owner = screen.getByRole("region", { name: "YNAB action" });
  expect.soft(within(owner).queryByText(/1.*currency/i)).toBeVisible();
  expect.soft(within(owner).queryByText(/1.*reconcil/i)).toBeVisible();
});

it("retains prior bulk memo results while a later request is pending and fails", async () => {
  renderAction(<YnabBulkSyncCard />);
  await clickAction("Sync All Memos");
  await settled();
  const prior = screen.getByText("1 synced");
  expect(prior).toBeVisible();
  failurePath = "sync-memos/bulk";
  heldPath = failurePath;
  await clickAction("Sync All Memos");
  await started.promise;
  try {
    expect.soft(prior).toBeInTheDocument();
  } finally {
    await act(async () => {
      gate.resolve();
      await gate.promise;
    });
  }
  await settled();
  expect.soft(prior).toBeInTheDocument();
  expect(calls.filter((call) => call.path === "sync-memos/bulk")).toHaveLength(
    2,
  );
});

it.each([
  {
    path: "sync-memos",
    status: 400,
    button: "Sync Memos",
    component: <YnabMemoSyncCard receiptId={receiptId} />,
  },
  {
    path: "push-transactions",
    status: 400,
    button: "Push to YNAB",
    component: <YnabPushButton receiptId={receiptId} hasTransactions />,
  },
  {
    path: "sync-memos/bulk",
    status: 400,
    button: "Sync All Memos",
    component: <YnabBulkSyncCard />,
  },
  {
    path: "push-transactions/bulk",
    status: 400,
    button: "Push All to YNAB",
    component: <YnabBulkSyncCard />,
  },
  {
    path: "sync-memos/resolve",
    status: 400,
    button: "Select",
    component: null,
  },
  {
    path: "sync-memos/resolve",
    status: 503,
    button: "Select",
    component: null,
  },
])(
  "pairs native Request and mutation-cache local policy for $path HTTP$status",
  async ({ path, status, button, component }) => {
    failurePath = path;
    failureStatus = status;
    const actualFetch = globalThis.fetch;
    const requests: Request[] = [];
    // Observe the actual SDK Request, then forward unchanged into the real MSW
    // transport. No fabricated fetch response or substituted hook/client method.
    vi.spyOn(globalThis, "fetch").mockImplementation((input, init) => {
      if (
        input instanceof Request &&
        input.method === "POST" &&
        new URL(input.url).pathname === `/api/ynab/${path}`
      )
        requests.push(input);
      return actualFetch(input, init);
    });
    let owner: HTMLElement;
    if (component) {
      renderAction(component);
      await clickAction(button);
      owner = screen.getByRole("region", { name: "YNAB action" });
    } else {
      owner = await openCandidate();
      await userEvent
        .setup()
        .click(within(owner).getByRole("button", { name: "Select" }));
    }
    await settled();
    expect(requests).toHaveLength(1);
    expect(getRequestErrorPresentation(requests[0])).toBe("local");
    const failedMutations = queryClient
      .getMutationCache()
      .getAll()
      .filter((mutation) => mutation.state.status === "error");
    expect(failedMutations).toHaveLength(1);
    expect(getCacheErrorPresentation(failedMutations[0].options.meta)).toBe(
      "local",
    );
    expect(router.state.location.pathname).toBe("/actions");
    expect(owner).toBeInTheDocument();
    expect(within(owner).getAllByRole("alert")).toHaveLength(1);
    expect(within(owner).getByRole("alert")).toHaveTextContent(
      /failed|unavailable/i,
    );
    expect(errorToast).not.toHaveBeenCalled();
    expect(successToast).not.toHaveBeenCalled();
    expect(infoToast).not.toHaveBeenCalled();
    expect(warningToast).not.toHaveBeenCalled();
    expect(calls.filter((call) => call.path === path)).toEqual([
      {
        path,
        body: path.endsWith("/resolve")
          ? { localTransactionId: localId, ynabTransactionId: "remote-1" }
          : path.endsWith("/bulk")
            ? { receiptIds: [receiptId] }
            : { receiptId },
      },
    ]);
    if (path.endsWith("/resolve"))
      expect(calls.filter((call) => call.path === "sync-memos")).toHaveLength(
        1,
      );
  },
);

it("keeps a successful push result and exact request as a positive control", async () => {
  renderAction(<YnabPushButton receiptId={receiptId} hasTransactions />);
  await clickAction("Push to YNAB");
  await settled();
  expect(screen.getByRole("button", { name: "Pushed to YNAB" })).toBeDisabled();
  expect(calls).toEqual([{ path: "push-transactions", body: { receiptId } }]);
  expect(errorToast).not.toHaveBeenCalled();
  expect(successToast).toHaveBeenCalledTimes(1);
});
