vi.hoisted(() => vi.stubEnv("VITE_API_URL", "http://account-card-owners.test"));
import { useState, type ReactNode } from "react";
import {
  act,
  cleanup,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createMemoryRouter, RouterProvider } from "react-router";
import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import { toast } from "sonner";
import { AuthProvider } from "@/contexts/AuthContext";
import { AppearanceProvider } from "@/contexts/AppearanceContext";
import { TooltipProvider } from "@/components/ui/tooltip";
import { RootLayout } from "@/components/RootLayout";
import { CardForm } from "./CardForm";
import Cards from "@/pages/Cards";
import { TransactionsSection } from "@/pages/new-receipt/TransactionsSection";
import { ReceiptTransactionsCard } from "./ReceiptTransactionsCard";
import YnabSettings from "@/pages/settings/YnabSettings";
import { createAppQueryClient } from "@/lib/query-client";
import { clearTokens, setTokens } from "@/lib/auth";
import { clearServerErrorPageFlag } from "@/lib/server-error-bus";
import "@/test/setup-combobox-polyfills";
const account = { id: "account", name: "Everyday account", isActive: true };
const card = {
  id: "card",
  accountId: account.id,
  name: "Everyday card",
  cardCode: "1234",
  isActive: true,
};
const txn = {
  id: "txn",
  accountId: account.id,
  cardId: card.id,
  amount: 8,
  date: "2024-01-15",
};
let failed: "accounts" | "cards" | "account-cards" | undefined;
let mappingWrites: unknown[];
let mapping: {
  id: string;
  receiptsAccountId: string;
  ynabAccountId: string;
  ynabAccountName: string;
  ynabBudgetId: string;
};
const unavailable = () =>
  HttpResponse.json(
    { status: 503, detail: "Lookup unavailable" },
    { status: 503 },
  );
const page = (data: unknown[]) =>
  HttpResponse.json({ data, total: data.length, offset: 0, limit: 500 });
const server = setupServer(
  http.get("*/api/accounts", () =>
    failed === "accounts" ? unavailable() : page([account]),
  ),
  http.get("*/api/cards", () =>
    failed === "cards" ? unavailable() : page([card]),
  ),
  http.get("*/api/accounts/:id/cards", () =>
    failed === "account-cards" ? unavailable() : HttpResponse.json([card]),
  ),
  http.get("*/api/ynab/connection-status", () =>
    HttpResponse.json({ isConfigured: true, isConnected: true }),
  ),
  http.get("*/api/ynab/settings/budget", () =>
    HttpResponse.json({ selectedBudgetId: "budget" }),
  ),
  http.get("*/api/ynab/budgets", () =>
    page([{ id: "budget", name: "Current budget" }]),
  ),
  http.get("*/api/ynab/accounts", () =>
    page([
      { id: "remote-a", name: "Remote A" },
      { id: "remote-b", name: "Remote B" },
    ]),
  ),
  http.get("*/api/ynab/account-mappings", () => page([mapping])),
  http.put("*/api/ynab/account-mappings/:id", async ({ request }) => {
    const body = (await request.json()) as Partial<typeof mapping>;
    mappingWrites.push(body);
    mapping = { ...mapping, ...body };
    return HttpResponse.json(mapping);
  }),
  http.delete("*/api/ynab/account-mappings/:id", () => {
    mappingWrites.push("delete");
    return new HttpResponse(null, { status: 204 });
  }),
  ...["receipts", "ynab/categories", "ynab/category-mappings"].map((path) =>
    http.get(`*/api/${path}`, () => page([])),
  ),
  http.get("*/api/receipt-items/distinct-categories", () =>
    HttpResponse.json({ categories: [] }),
  ),
  http.get("*/api/ynab/category-mappings/unmapped", () =>
    HttpResponse.json({ categories: [] }),
  ),
  http.get("*/api/ynab/rate-limit-status", () =>
    HttpResponse.json({ requestsUsed: 0, requestsRemaining: 200 }),
  ),
  http.get("*/api/ynab/stale-mappings", () =>
    HttpResponse.json({
      staleAccountMappingCount: 0,
      staleCategoryMappingCount: 0,
    }),
  ),
);
let queryClient: ReturnType<typeof createAppQueryClient>;
let router: ReturnType<typeof createMemoryRouter>;
function renderFeature(feature: ReactNode) {
  queryClient = createAppQueryClient();
  router = createMemoryRouter([
    {
      element: <RootLayout />,
      children: [
        { path: "/", element: feature },
        { path: "/error/500", element: <h1>Global server error route</h1> },
      ],
    },
  ]);
  render(
    <AppearanceProvider>
      <TooltipProvider>
        <AuthProvider queryClientFactory={() => queryClient}>
          <RouterProvider router={router} />
        </AuthProvider>
      </TooltipProvider>
    </AppearanceProvider>,
  );
}
function TransactionRows() {
  const [transactions, setTransactions] = useState([txn]);
  return (
    <TransactionsSection
      transactions={transactions}
      defaultDate={txn.date}
      onChange={setTransactions}
    />
  );
}
async function failRead(
  kind: NonNullable<typeof failed>,
  key: readonly unknown[],
) {
  failed = kind;
  await act(async () => {
    await queryClient.invalidateQueries({ queryKey: key });
  });
  await screen.findAllByRole("alert");
  expect(router.state.location.pathname).toBe("/");
  expect(
    document.querySelectorAll('[data-sonner-toast][data-type="error"]'),
  ).toHaveLength(0);
}
async function retryRead() {
  failed = undefined;
  await userEvent
    .setup()
    .click(screen.getAllByRole("button", { name: /retry/i })[0]);
  await waitFor(() => expect(screen.queryAllByRole("alert")).toHaveLength(0));
}
beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
afterAll(() => server.close());
beforeEach(() => {
  failed = undefined;
  mappingWrites = [];
  mapping = {
    id: "mapping",
    receiptsAccountId: account.id,
    ynabAccountId: "remote-a",
    ynabAccountName: "Remote A",
    ynabBudgetId: "budget",
  };
  localStorage.clear();
  clearServerErrorPageFlag();
  setTokens(
    `header.${btoa(JSON.stringify({ sub: "admin", email: "admin@example.test", role: "Admin", exp: 4102444800 }))}.signature`,
    "refresh",
  );
});
afterEach(() => {
  cleanup();
  router?.dispose();
  queryClient?.clear();
  toast.dismiss();
  clearTokens();
  server.resetHandlers();
});
it("retains CardForm values and selected account through lookup failure, manual submit and Retry", async () => {
  const submitted = vi.fn();
  const user = userEvent.setup();
  const defaults = {
    cardCode: card.cardCode,
    name: card.name,
    isActive: true,
    accountId: account.id,
  };
  renderFeature(
    <CardForm
      mode="edit"
      defaultValues={defaults}
      onSubmit={submitted}
      onCancel={() => {}}
    />,
  );
  await waitFor(() =>
    expect(screen.getByRole("combobox", { name: "Account" })).toHaveTextContent(
      account.name,
    ),
  );
  await user.clear(screen.getByLabelText(/^Name/));
  await user.type(screen.getByLabelText(/^Name/), "Unsaved card name");
  await failRead("accounts", ["accounts", "all"]);
  expect(screen.getByRole("combobox", { name: "Account" })).toHaveTextContent(
    account.name,
  );
  expect(screen.getByLabelText(/^Name/)).toHaveValue("Unsaved card name");
  await user.click(screen.getByRole("button", { name: "Update Card" }));
  await waitFor(() =>
    expect(submitted.mock.calls.map(([value]) => value)).toEqual([
      { ...defaults, name: "Unsaved card name" },
    ]),
  );
  await retryRead();
  expect(screen.getByLabelText(/^Name/)).toHaveValue("Unsaved card name");
});
it("keeps Cards account labels and active search/filter selection through account lookup Retry", async () => {
  renderFeature(<Cards />);
  const user = userEvent.setup();
  expect(await screen.findByRole("cell", { name: account.name })).toBeVisible();
  await user.type(
    screen.getByRole("textbox", { name: "Search cards" }),
    "Everyday",
  );
  await failRead("accounts", ["accounts", "all"]);
  expect(screen.getByRole("textbox", { name: "Search cards" })).toHaveValue(
    "Everyday",
  );
  expect(screen.getByRole("tab", { name: "Active" })).toHaveAttribute(
    "aria-selected",
    "true",
  );
  expect(screen.getByRole("cell", { name: account.name })).toBeVisible();
  await retryRead();
  expect(screen.getByRole("cell", { name: account.name })).toBeVisible();
});
it.each(["accounts", "account-cards"] as const)(
  "retains transaction rows and draft amount when summary %s lookup fails",
  async (kind) => {
    renderFeature(<TransactionRows />);
    const user = userEvent.setup();
    expect(await screen.findByRole("cell", { name: card.name })).toBeVisible();
    await user.clear(screen.getByLabelText(/^Amount/));
    await user.type(screen.getByLabelText(/^Amount/), "17.42");
    await failRead(
      kind,
      kind === "accounts"
        ? ["accounts", "all"]
        : ["cards", "byAccount", account.id],
    );
    expect(
      screen.getByRole("cell", { name: new RegExp(`^${card.name}`) }),
    ).toBeVisible();
    expect(screen.getByRole("cell", { name: account.name })).toBeVisible();
    expect(screen.getByLabelText(/^Amount/)).toHaveValue("17.42");
    await retryRead();
    expect(screen.getByRole("cell", { name: "$8.00" })).toBeVisible();
    expect(screen.getByLabelText(/^Amount/)).toHaveValue("17.42");
  },
);
it("retains receipt transaction labels, selection and edited amount after complete card lookup failure", async () => {
  renderFeature(
    <ReceiptTransactionsCard
      receiptId="receipt"
      receiptDate={txn.date}
      transactions={[{ transaction: txn, account }]}
      transactionsTotal={txn.amount}
    />,
  );
  const user = userEvent.setup();
  expect(await screen.findByRole("cell", { name: card.name })).toBeVisible();
  await user.click(
    screen.getByRole("checkbox", { name: "Select all transactions" }),
  );
  await user.click(screen.getByRole("button", { name: "Edit" }));
  const dialog = screen.getByRole("dialog", { name: "Edit Transaction" });
  await user.clear(within(dialog).getByLabelText(/^Amount/));
  await user.type(within(dialog).getByLabelText(/^Amount/), "19.24");
  // The failed card-name map is outside the modal; allow hidden failure ownership to remain observable.
  failed = "cards";
  await act(async () => {
    await queryClient.invalidateQueries({ queryKey: ["cards", "all"] });
  });
  await waitFor(() =>
    expect(queryClient.getQueryState(["cards", "all", undefined])?.status).toBe(
      "error",
    ),
  );
  expect(router.state.location.pathname).toBe("/");
  expect(dialog).toBeInTheDocument();
  expect(within(dialog).getByLabelText(/^Amount/)).toHaveValue("19.24");
  await user.click(within(dialog).getByRole("button", { name: "Cancel" }));
  expect(
    screen.getByRole("cell", { name: new RegExp(`^${card.name}`) }),
  ).toBeVisible();
  expect(
    screen.getByRole("checkbox", { name: "Select all transactions" }),
  ).toBeChecked();
  await screen.findByRole("alert");
  await retryRead();
  expect(
    screen.getByRole("checkbox", { name: "Select all transactions" }),
  ).toBeChecked();
});
it("blocks an already-open YNAB mapping menu after receipts-account failure and restores explicit changes after Retry", async () => {
  renderFeature(<YnabSettings />);
  const user = userEvent.setup();
  const row = (await screen.findByText(account.name)).parentElement!;
  const selector = within(row).getByRole("combobox");
  await waitFor(() => expect(selector).toHaveTextContent("Remote A"));
  selector.focus();
  await user.keyboard("{Enter}");
  expect(screen.getByRole("option", { name: "Remote B" })).toBeVisible();
  failed = "accounts";
  await act(async () => {
    await queryClient.invalidateQueries({ queryKey: ["accounts", "all"] });
  });
  await waitFor(() =>
    expect(
      queryClient.getQueryState(["accounts", "all", undefined])?.status,
    ).toBe("error"),
  );
  await user.click(screen.getByRole("option", { name: "Remote B" }));
  expect(mappingWrites).toEqual([]);
  await user.keyboard("{Escape}");
  expect(router.state.location.pathname).toBe("/");
  expect(within(row).getByRole("combobox")).toHaveTextContent("Remote A");
  expect(within(row).getByRole("button", { name: "Remove" })).toBeDisabled();
  await retryRead();
  within(row).getByRole("combobox").focus();
  await user.keyboard("{Enter}");
  await user.click(screen.getByRole("option", { name: "Remote B" }));
  await waitFor(() =>
    expect(mappingWrites).toEqual([
      {
        ynabAccountId: "remote-b",
        ynabAccountName: "Remote B",
        ynabBudgetId: "budget",
      },
    ]),
  );
});
it("distinguishes unavailable receipts accounts from no accounts in YNAB settings and retries the current list", async () => {
  failed = "accounts";
  renderFeature(<YnabSettings />);
  await screen.findByRole("alert");
  expect(
    screen.queryByText("No receipts accounts found. Create accounts first."),
  ).not.toBeInTheDocument();
  expect(router.state.location.pathname).toBe("/");
  await retryRead();
  expect(await screen.findByText(account.name)).toBeVisible();
  expect(mappingWrites).toEqual([]);
});
it("keeps receipt transactions visible when card names have never loaded and recovers them on Retry", async () => {
  failed = "cards";
  renderFeature(
    <ReceiptTransactionsCard
      receiptId="receipt"
      transactions={[{ transaction: txn, account }]}
      transactionsTotal={txn.amount}
    />,
  );
  await screen.findByRole("alert");
  expect(screen.getByRole("cell", { name: account.name })).toBeVisible();
  const row = screen.getByRole("cell", { name: account.name }).closest("tr")!;
  expect(within(row).getByRole("cell", { name: "$8.00" })).toBeVisible();
  expect(router.state.location.pathname).toBe("/");
  await retryRead();
  expect(screen.getByRole("cell", { name: card.name })).toBeVisible();
});
it("blocks open mapping options and removal during receipts-account revalidation before any failure exists", async () => {
  renderFeature(<YnabSettings />);
  const user = userEvent.setup();
  const row = (await screen.findByText(account.name)).parentElement!;
  const selector = within(row).getByRole("combobox");
  await waitFor(() => expect(selector).toHaveTextContent("Remote A"));
  selector.focus();
  await user.keyboard("{Enter}");
  let release!: () => void;
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });
  server.use(
    http.get("*/api/accounts", async () => {
      await gate;
      return page([account]);
    }),
  );
  let refresh!: Promise<void>;
  act(() => {
    refresh = queryClient.invalidateQueries({ queryKey: ["accounts", "all"] });
  });
  try {
    await waitFor(() =>
      expect(
        queryClient.getQueryState(["accounts", "all", undefined])?.fetchStatus,
      ).toBe("fetching"),
    );
    await user.click(screen.getByRole("option", { name: "Remote B" }));
    expect(mappingWrites).toEqual([]);
    await user.keyboard("{Escape}");
    expect(within(row).getByRole("button", { name: "Remove" })).toBeDisabled();
    expect(within(row).getByRole("combobox")).toHaveTextContent("Remote A");
  } finally {
    await act(async () => {
      release();
      await refresh;
    });
  }
  await waitFor(() => expect(within(row).getByRole("combobox")).toBeEnabled());
  within(row).getByRole("combobox").focus();
  await user.keyboard("{Enter}");
  await user.click(screen.getByRole("option", { name: "Remote B" }));
  await waitFor(() => expect(mappingWrites).toHaveLength(1));
  expect(mappingWrites[0]).toEqual({
    ynabAccountId: "remote-b",
    ynabAccountName: "Remote B",
    ynabBudgetId: "budget",
  });
});
