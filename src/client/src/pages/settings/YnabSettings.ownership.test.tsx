vi.hoisted(() =>
  vi.stubEnv("VITE_API_URL", "http://ynab-settings-ownership.test"),
);
import {
  act,
  cleanup,
  render,
  renderHook,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClientProvider } from "@tanstack/react-query";
import { createMemoryRouter, RouterProvider } from "react-router";
import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import { toast } from "sonner";
import { AuthProvider } from "@/contexts/AuthContext";
import { AppearanceProvider } from "@/contexts/AppearanceContext";
import { RootLayout } from "@/components/RootLayout";
import { createAppQueryClient } from "@/lib/query-client";
import { clearTokens, setTokens } from "@/lib/auth";
import { clearServerErrorPageFlag } from "@/lib/server-error-bus";
import { useYnabSplitComparison } from "@/hooks/useYnab";
import YnabSettings from "./YnabSettings";
import "@/test/setup-combobox-polyfills";

const reads = [
  ["connection", "ynab/connection-status", ["ynab", "connection-status"]],
  ["budgets", "ynab/budgets", ["ynab", "budgets"]],
  ["selected budget", "ynab/settings/budget", ["ynab", "settings", "budget"]],
  ["remote accounts", "ynab/accounts", ["ynab", "accounts"]],
  ["account mappings", "ynab/account-mappings", ["ynab", "account-mappings"]],
  ["remote categories", "ynab/categories", ["ynab", "categories"]],
  [
    "receipt categories",
    "receipt-items/distinct-categories",
    ["receipt-items", "distinct-categories"],
  ],
  [
    "category mappings",
    "ynab/category-mappings",
    ["ynab", "category-mappings"],
  ],
  [
    "unmapped categories",
    "ynab/category-mappings/unmapped",
    ["ynab", "category-mappings", "unmapped"],
  ],
  ["rate limit", "ynab/rate-limit-status", ["ynab", "rate-limit-status"]],
  ["stale mappings", "ynab/stale-mappings", ["ynab", "stale-mappings"]],
] as const;

function deferred() {
  let resolve!: () => void;
  const promise = new Promise<void>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}

const accountMapping = {
  id: "mapping-account",
  receiptsAccountId: "receipt-account",
  ynabAccountId: "remote-a",
  ynabAccountName: "Remote account A",
  ynabBudgetId: "budget-a",
};
const categoryMapping = {
  id: "mapping-category",
  receiptsCategory: "Groceries",
  ynabCategoryId: "category-a",
  ynabCategoryName: "Food A",
  ynabCategoryGroupName: "Living",
  ynabBudgetId: "budget-a",
};
let selected: string | null;
let empty: boolean;
let failed: string | undefined;
let failedStatus: number;
let heldPath: string | undefined;
let gate: ReturnType<typeof deferred>;
let started: ReturnType<typeof deferred>;
let heldSignal: AbortSignal | undefined;
let writes: { method: string; path: string; body?: unknown }[];
let readRequests: string[];

function bodyFor(path: string) {
  switch (path) {
    case "ynab/connection-status":
      return { isConfigured: true, isConnected: true };
    case "ynab/budgets":
      return {
        data: [
          { id: "budget-a", name: "Budget A" },
          { id: "budget-b", name: "Budget B" },
        ],
      };
    case "ynab/settings/budget":
      return { selectedBudgetId: selected };
    case "ynab/accounts":
      return {
        data: empty
          ? []
          : [
              { id: "remote-a", name: "Remote account A" },
              { id: "remote-b", name: "Remote account B" },
            ],
      };
    case "ynab/account-mappings":
      return { data: empty ? [] : [accountMapping] };
    case "ynab/categories":
      return {
        data: empty
          ? []
          : [
              { id: "category-a", name: "Food A", categoryGroupName: "Living" },
              { id: "category-b", name: "Food B", categoryGroupName: "Living" },
            ],
      };
    case "ynab/category-mappings":
      return { data: empty ? [] : [categoryMapping] };
    case "receipt-items/distinct-categories":
      return { categories: empty ? [] : ["Groceries"] };
    case "ynab/category-mappings/unmapped":
      return { unmappedCategories: [] };
    case "ynab/rate-limit-status":
      return {
        requestsUsed: 10,
        maxRequests: 200,
        remainingRequests: 190,
        windowResetAt: null,
        oldestRequestAt: null,
      };
    case "ynab/stale-mappings":
      return {
        staleAccountMappingCount: 1,
        staleCategoryMappingCount: 1,
        currentBudgetId: selected,
      };
    default:
      throw new Error(`Unexpected fixture path ${path}`);
  }
}

const server = setupServer(
  ...reads.map(([, path]) =>
    http.get(`*/api/${path}`, async ({ request }) => {
      readRequests.push(path);
      const body = bodyFor(path);
      if (path === heldPath) {
        heldSignal = request.signal;
        started.resolve();
        await gate.promise;
      }
      return path === failed
        ? HttpResponse.json(
            { status: failedStatus, detail: `${path} unavailable` },
            { status: failedStatus },
          )
        : HttpResponse.json(body);
    }),
  ),
  http.get("*/api/accounts", () =>
    HttpResponse.json({
      data: empty
        ? []
        : [{ id: "receipt-account", name: "Everyday", isActive: true }],
      total: empty ? 0 : 1,
      offset: 0,
      limit: 500,
    }),
  ),
  http.get("*/api/receipts", () =>
    HttpResponse.json({ data: [], total: 0, offset: 0, limit: 50 }),
  ),
  http.put("*/api/ynab/settings/budget", async ({ request }) => {
    const body = (await request.json()) as { budgetId: string };
    writes.push({ method: "PUT", path: "ynab/settings/budget", body });
    selected = body.budgetId;
    return new HttpResponse(null, { status: 204 });
  }),
  http.delete("*/api/ynab/account-mappings/:id", () => {
    writes.push({ method: "DELETE", path: "ynab/account-mappings" });
    return new HttpResponse(null, { status: 204 });
  }),
  http.delete("*/api/ynab/category-mappings/:id", () => {
    writes.push({ method: "DELETE", path: "ynab/category-mappings" });
    return new HttpResponse(null, { status: 204 });
  }),
  http.delete("*/api/ynab/stale-mappings", () => {
    writes.push({ method: "DELETE", path: "ynab/stale-mappings" });
    return HttpResponse.json({
      deletedAccountMappings: 1,
      deletedCategoryMappings: 1,
    });
  }),
);

let queryClient: ReturnType<typeof createAppQueryClient>;
let router: ReturnType<typeof createMemoryRouter>;
let errorToast: ReturnType<typeof vi.spyOn>;
beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
afterAll(() => server.close());
beforeEach(() => {
  selected = "budget-a";
  empty = false;
  failed = undefined;
  failedStatus = 503;
  heldPath = undefined;
  heldSignal = undefined;
  gate = deferred();
  started = deferred();
  writes = [];
  readRequests = [];
  clearServerErrorPageFlag();
  setTokens("alice-access", "alice-refresh");
  queryClient = createAppQueryClient();
  errorToast = vi.spyOn(toast, "error");
});
afterEach(async () => {
  cleanup();
  await act(async () => {
    gate.resolve();
    await gate.promise;
  });
  queryClient.clear();
  clearTokens();
  toast.dismiss();
  vi.restoreAllMocks();
  server.resetHandlers();
});

function renderSettings(queryClientFactory = () => queryClient) {
  router = createMemoryRouter(
    [
      {
        element: <RootLayout />,
        children: [
          { path: "/settings/ynab", element: <YnabSettings /> },
          { path: "/error/500", element: <h1>Global server error route</h1> },
        ],
      },
    ],
    { initialEntries: ["/settings/ynab"] },
  );
  return render(
    <AppearanceProvider>
      <AuthProvider queryClientFactory={queryClientFactory}>
        <RouterProvider router={router} />
      </AuthProvider>
    </AppearanceProvider>,
  );
}

it("restarts the initial connection request after a committed budget change", async () => {
  heldPath = "ynab/connection-status";
  const successToast = vi.spyOn(toast, "success");
  renderSettings();
  await started.promise;
  expect(heldSignal?.aborted).toBe(false);
  expect(screen.getByText("Checking connection...")).toBeInTheDocument();
  const user = userEvent.setup();
  const section = await screen.findByText("Budget Selection");
  const selector = await within(
    section.closest('[data-slot="card"]')! as HTMLElement,
  ).findByRole("combobox");
  await waitFor(() => expect(selector).toBeEnabled());
  act(() => selector.focus());
  await user.keyboard("{Enter}");
  await user.click(screen.getByRole("option", { name: "Budget B" }));
  try {
    await waitFor(() =>
      expect(successToast).toHaveBeenCalledExactlyOnceWith(
        "YNAB budget selected",
      ),
    );
    expect(writes).toEqual([
      {
        method: "PUT",
        path: "ynab/settings/budget",
        body: { budgetId: "budget-b" },
      },
    ]);
    expect(heldSignal?.aborted).toBe(false);
    expect(
      queryClient.getQueryState(["ynab", "connection-status"]),
    ).toMatchObject({
      data: undefined,
      status: "pending",
      fetchStatus: "fetching",
    });
    expect(screen.getByText("Checking connection...")).toBeInTheDocument();
    expect(screen.queryByText("Not Configured")).not.toBeInTheDocument();
    expect(readRequests.filter((path) => path === heldPath)).toHaveLength(2);
  } finally {
    await act(async () => {
      gate.resolve();
      await gate.promise;
    });
  }
  expect(await screen.findByText("Connected")).toBeInTheDocument();
  await waitFor(() => expect(queryClient.isFetching()).toBe(0));
  expect(queryClient.getQueryData(["ynab", "settings", "budget"])).toEqual({
    selectedBudgetId: "budget-b",
  });
  expect(screen.queryByText("Not Configured")).not.toBeInTheDocument();
  expect(router.state.location.pathname).toBe("/settings/ynab");
  expect(errorToast).not.toHaveBeenCalled();
  expect(readRequests.filter((path) => path === heldPath)).toHaveLength(2);
  expect(writes).toHaveLength(1);
});

it("invalidates a fresh inactive split comparison after budget change without eagerly refetching it", async () => {
  let splitRequests = 0;
  const comparison = {
    canComputeExpected: true,
    expectedUnavailableReason: null,
    unmappedCategories: [],
    transactionComparisons: [],
  };
  server.use(
    http.get("*/api/ynab/receipts/receipt-1/split-comparison", () => {
      splitRequests += 1;
      return HttpResponse.json(comparison);
    }),
  );
  const split = renderHook(() => useYnabSplitComparison("receipt-1"), {
    wrapper: ({ children }) => (
      <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
    ),
  });
  await waitFor(() => expect(split.result.current.data).toEqual(comparison));
  split.unmount();
  const cached = queryClient.getQueryCache().find({
    queryKey: ["ynab", "split-comparison", "receipt-1"],
    exact: true,
  })!;
  expect(cached.getObserversCount()).toBe(0);
  expect(cached.isStale()).toBe(false);
  expect(splitRequests).toBe(1);
  const successToast = vi.spyOn(toast, "success");
  renderSettings();
  const user = userEvent.setup();
  const section = await screen.findByText("Budget Selection");
  const selector = await within(
    section.closest('[data-slot="card"]')! as HTMLElement,
  ).findByRole("combobox");
  await waitFor(() => expect(selector).toBeEnabled());
  act(() => selector.focus());
  await user.keyboard("{Enter}");
  await user.click(screen.getByRole("option", { name: "Budget B" }));
  await waitFor(() =>
    expect(successToast).toHaveBeenCalledWith("YNAB budget selected"),
  );
  expect(writes).toEqual([
    {
      method: "PUT",
      path: "ynab/settings/budget",
      body: { budgetId: "budget-b" },
    },
  ]);
  expect(cached.state.isInvalidated).toBe(true);
  expect(cached.isStale()).toBe(true);
  expect(cached.state.data).toEqual(comparison);
  expect(cached.state.fetchStatus).toBe("idle");
  expect(cached.getObserversCount()).toBe(0);
  expect(splitRequests).toBe(1);
});

it.each([false, true])(
  "finishes held budget repair only for its owning session (session changed: %s)",
  async (changeSession) => {
    let holdRevalidation = false;
    let factoryCalls = 0;
    const oldClient = queryClient;
    const successToast = vi.spyOn(toast, "success");
    server.use(
      http.get("*/api/ynab/settings/budget", async ({ request }) => {
        const body = { selectedBudgetId: selected };
        if (
          holdRevalidation &&
          request.headers.get("Authorization") === "Bearer alice-access"
        ) {
          heldSignal = request.signal;
          started.resolve();
          await gate.promise;
        }
        return HttpResponse.json(body);
      }),
    );
    renderSettings(() => {
      factoryCalls += 1;
      if (factoryCalls > 1) queryClient = createAppQueryClient();
      return queryClient;
    });
    const user = userEvent.setup();
    const section = await screen.findByText("Budget Selection");
    const selector = await within(
      section.closest('[data-slot="card"]')! as HTMLElement,
    ).findByRole("combobox");
    await waitFor(() => expect(selector).toBeEnabled());
    await waitFor(() => expect(queryClient.isFetching()).toBe(0));
    holdRevalidation = true;
    act(() => selector.focus());
    await user.keyboard("{Enter}");
    await user.click(screen.getByRole("option", { name: "Budget B" }));
    await started.promise;
    expect(heldSignal?.aborted).toBe(false);
    const mutation = oldClient
      .getMutationCache()
      .getAll()
      .find((entry) => entry.state.status === "pending")!;
    expect(mutation).toBeDefined();
    expect(successToast).toHaveBeenCalledExactlyOnceWith(
      "YNAB budget selected",
    );
    if (changeSession) {
      // Real token publication remounts AuthProvider's subtree and replaces its
      // QueryClient. The next user's server budget is deliberately different.
      await act(async () => {
        selected = "budget-a";
        setTokens("bob-access", "bob-refresh");
      });
      expect(factoryCalls).toBe(2);
      expect(queryClient).not.toBe(oldClient);
      expect(heldSignal?.aborted).toBe(true);
      expect(oldClient.getQueryCache().getAll()).toEqual([]);
    }
    await act(async () => {
      gate.resolve();
      await gate.promise;
    });
    await waitFor(() =>
      expect(mutation.state.status).toBe("success"),
    );
    await waitFor(() => expect(queryClient.isFetching()).toBe(0));
    expect(queryClient.getQueryData(["ynab", "settings", "budget"])).toEqual({
      selectedBudgetId: changeSession ? "budget-a" : "budget-b",
    });
    expect(writes).toEqual([
      {
        method: "PUT",
        path: "ynab/settings/budget",
        body: { budgetId: "budget-b" },
      },
    ]);
    expect(errorToast).not.toHaveBeenCalled();
    if (changeSession) {
      expect(successToast).toHaveBeenCalledExactlyOnceWith(
        "YNAB budget selected",
      );
      expect(queryClient.getMutationCache().getAll()).toEqual([]);
    } else {
      expect(successToast).toHaveBeenCalledExactlyOnceWith(
        "YNAB budget selected",
      );
    }
  },
);

function currentRead(key: readonly string[]) {
  return (
    queryClient.getQueryCache().find({
      queryKey: [...key, selected ?? undefined],
      exact: true,
    }) ?? queryClient.getQueryCache().find({ queryKey: key, exact: true })
  );
}

it.each(reads)(
  "keeps an initial %s failure local and retryable",
  async (_label, path, key) => {
    failed = path;
    renderSettings();
    await waitFor(() => expect(currentRead(key)?.state.status).toBe("error"));
    expect(router.state.location.pathname).toBe("/settings/ynab");
    expect(screen.getAllByText(/unavailable/i).length).toBeGreaterThan(0);
    expect(
      screen.getAllByRole("button", { name: "Retry" }).length,
    ).toBeGreaterThan(0);
    expect(errorToast).not.toHaveBeenCalled();
    expect(writes).toEqual([]);
  },
);

it.each(reads)(
  "owns a non-5xx %s read failure in the query cache without a second toast",
  async (_label, path, key) => {
    failed = path;
    failedStatus = 400;
    renderSettings();
    await waitFor(() => expect(currentRead(key)?.state.status).toBe("error"));
    expect(router.state.location.pathname).toBe("/settings/ynab");
    expect(errorToast).not.toHaveBeenCalled();
    expect(
      screen.getAllByRole("button", { name: "Retry" }).length,
    ).toBeGreaterThan(0);
    expect(writes).toEqual([]);
  },
);

it.each(reads)(
  "cancels the actual %s HTTP request when its query is cancelled",
  async (_label, path, key) => {
    heldPath = path;
    renderSettings();
    await started.promise;
    expect(heldSignal?.aborted).toBe(false);
    await act(async () => {
      await queryClient.cancelQueries({ queryKey: key });
    });
    expect(heldSignal?.aborted).toBe(true);
    await act(async () => {
      heldPath = undefined;
      gate.resolve();
      await gate.promise;
    });
    expect(currentRead(key)?.state.data).toBeUndefined();
    expect(writes).toEqual([]);
  },
);

it.each([
  [
    "account",
    "Account Mapping",
    "ynab/account-mappings",
    ["ynab", "account-mappings"],
  ],
  [
    "category",
    "Category Mapping",
    "ynab/category-mappings",
    ["ynab", "category-mappings"],
  ],
] as const)(
  "does not delete a cached %s mapping while its proof is refetching",
  async (_kind, title, path, key) => {
    renderSettings();
    await screen.findByText("Groceries");
    await screen.findByText("Everyday");
    const section = screen.getByText(title).closest('[data-slot="card"]')!;
    const remove = within(section as HTMLElement).getByRole("button", {
      name: "Remove",
    });
    heldPath = path;
    let refetch!: Promise<void>;
    await act(async () => {
      refetch = queryClient.invalidateQueries({
        queryKey: currentRead(key)!.queryKey,
        exact: true,
      });
    });
    await started.promise;
    await userEvent.setup().click(remove);
    await waitFor(() => expect(queryClient.isMutating()).toBe(0));
    expect(writes).toEqual([]);
    expect(remove).toBeDisabled();
    await act(async () => {
      heldPath = undefined;
      gate.resolve();
      await refetch;
    });
    await waitFor(() => expect(remove).toBeEnabled());
    expect(writes).toEqual([]);
  },
);

it("does not request budget-dependent remote choices before a selected budget is verified", async () => {
  selected = null;
  renderSettings();
  await screen.findByText("Select a budget above to map accounts.");
  await waitFor(() => expect(queryClient.isFetching()).toBe(0));
  expect(readRequests).not.toContain("ynab/accounts");
  expect(readRequests).not.toContain("ynab/categories");
  expect(writes).toEqual([]);
});

it("does not treat a selected budget absent from the current list as proof for mapping reads", async () => {
  server.use(
    http.get("*/api/ynab/budgets", () =>
      HttpResponse.json({
        data: [{ id: "budget-b", name: "Budget B" }],
      }),
    ),
  );
  renderSettings();
  await waitFor(() =>
    expect(currentRead(["ynab", "settings", "budget"])?.state.status).toBe(
      "success",
    ),
  );
  await waitFor(() => expect(queryClient.isFetching()).toBe(0));
  expect(selected).toBe("budget-a");
  expect(readRequests).not.toContain("ynab/accounts");
  expect(readRequests).not.toContain("ynab/categories");
  expect(writes).toEqual([]);
  const section = screen
    .getByText("Budget Selection")
    .closest('[data-slot="card"]') as HTMLElement;
  const user = userEvent.setup();
  within(section).getByRole("combobox").focus();
  await user.keyboard("{Enter}");
  expect(
    screen.queryByRole("option", { name: "Budget A" }),
  ).not.toBeInTheDocument();
  await user.click(screen.getByRole("option", { name: "Budget B" }));
  await waitFor(() =>
    expect(writes).toEqual([
      {
        method: "PUT",
        path: "ynab/settings/budget",
        body: { budgetId: "budget-b" },
      },
    ]),
  );
  await waitFor(() => expect(readRequests).toContain("ynab/accounts"));
  expect(selected).toBe("budget-b");
});

it.each([
  [
    "account",
    "Account Mapping",
    "ynab/accounts",
    ["ynab", "accounts"],
    "Remote account A",
  ],
  [
    "category",
    "Category Mapping",
    "ynab/categories",
    ["ynab", "categories"],
    "Food A",
  ],
] as const)(
  "retains a cached %s choice through failure and held Retry without a deferred write",
  async (_kind, title, path, key, label) => {
    renderSettings();
    await screen.findByText("Groceries");
    await screen.findByText("Everyday");
    await waitFor(() => expect(queryClient.isFetching()).toBe(0));
    const section = screen
      .getByText(title)
      .closest('[data-slot="card"]') as HTMLElement;
    const selector = within(section).getByRole("combobox");
    expect(selector).toHaveTextContent(label);
    failed = path;
    await act(async () => {
      await queryClient.invalidateQueries({ queryKey: key });
    });
    await waitFor(() => expect(currentRead(key)?.state.status).toBe("error"));
    expect(router.state.location.pathname).toBe("/settings/ynab");
    expect(selector).toBeInTheDocument();
    expect(selector).toHaveTextContent(label);
    expect(selector).toBeDisabled();
    expect(errorToast).not.toHaveBeenCalled();
    failed = undefined;
    heldPath = path;
    await userEvent
      .setup()
      .click(within(section).getByRole("button", { name: "Retry" }));
    await started.promise;
    expect(
      within(section).getByRole("button", { name: "Retrying…" }),
    ).toBeDisabled();
    expect(selector).toHaveTextContent(label);
    expect(selector).toBeDisabled();
    expect(writes).toEqual([]);
    await act(async () => {
      heldPath = undefined;
      gate.resolve();
      await gate.promise;
    });
    await waitFor(() => expect(selector).toBeEnabled());
    expect(currentRead(key)?.state.status).toBe("success");
    expect(writes).toEqual([]);
  },
);

it("cancels a held budget-A account read and never offers it as a budget-B mapping", async () => {
  const firstRead = deferred();
  const oldResponse = deferred();
  let oldSignal: AbortSignal | undefined;
  server.use(
    http.get("*/api/ynab/account-mappings", () =>
      HttpResponse.json({ data: [] }),
    ),
    http.get("*/api/ynab/accounts", async ({ request }) => {
      const requestedBudget = selected;
      if (requestedBudget === "budget-a") {
        oldSignal = request.signal;
        firstRead.resolve();
        await oldResponse.promise;
      }
      return HttpResponse.json({
        data: [
          {
            id: requestedBudget === "budget-a" ? "old-a" : "current-b",
            name:
              requestedBudget === "budget-a"
                ? "Account only in A"
                : "Account only in B",
          },
        ],
      });
    }),
    http.post("*/api/ynab/account-mappings", async ({ request }) => {
      const body = await request.json();
      writes.push({ method: "POST", path: "ynab/account-mappings", body });
      return HttpResponse.json({ id: "created", ...(body as object) });
    }),
  );
  renderSettings();
  await firstRead.promise;
  const user = userEvent.setup();
  const budgetSection = screen
    .getByText("Budget Selection")
    .closest('[data-slot="card"]')!;
  const budgetSelector = within(budgetSection as HTMLElement).getByRole(
    "combobox",
  );
  budgetSelector.focus();
  await user.keyboard("{Enter}");
  await user.click(screen.getByRole("option", { name: "Budget B" }));
  try {
    await waitFor(() => expect(selected).toBe("budget-b"));
    await waitFor(() =>
      expect(queryClient.getQueryData(["ynab", "settings", "budget"])).toEqual({
        selectedBudgetId: "budget-b",
      }),
    );
    expect(oldSignal?.aborted).toBe(true);
  } finally {
    await act(async () => {
      oldResponse.resolve();
      await oldResponse.promise;
    });
  }
  const section = screen
    .getByText("Account Mapping")
    .closest('[data-slot="card"]')!;
  const selector = await within(section as HTMLElement).findByRole("combobox");
  await waitFor(() => expect(selector).toBeEnabled());
  selector.focus();
  await user.keyboard("{Enter}");
  expect(
    screen.queryByRole("option", { name: "Account only in A" }),
  ).not.toBeInTheDocument();
  await user.click(
    await screen.findByRole("option", { name: "Account only in B" }),
  );
  await waitFor(() =>
    expect(writes.filter((write) => write.method === "POST")).toEqual([
      {
        method: "POST",
        path: "ynab/account-mappings",
        body: {
          receiptsAccountId: "receipt-account",
          ynabAccountId: "current-b",
          ynabAccountName: "Account only in B",
          ynabBudgetId: "budget-b",
        },
      },
    ]),
  );
});

it.each(["connection failure", "invalid budget membership"])(
  "does not project unknown mapping reads as successful empty data after %s",
  async (reason) => {
    if (reason === "connection failure") failed = "ynab/connection-status";
    else selected = "retired-budget";
    renderSettings();
    await waitFor(() =>
      expect(queryClient.getQueryState(["ynab", "budgets"])?.status).toBe(
        "success",
      ),
    );
    await waitFor(() => expect(queryClient.isFetching()).toBe(0));
    expect(
      queryClient.getQueryState(["ynab", "connection-status"])?.status,
    ).toBe(reason === "connection failure" ? "error" : "success");
    expect(queryClient.getQueryData(["ynab", "settings", "budget"])).toEqual({
      selectedBudgetId: selected,
    });
    // Receipts accounts genuinely exist; suppressing the unknown mapping
    // projection is not explained by an empty receipts-account response.
    expect(queryClient.getQueryData(["accounts", "all", undefined])).toEqual([
      { id: "receipt-account", name: "Everyday", isActive: true },
    ]);
    for (const key of [
      ["ynab", "accounts"],
      ["ynab", "account-mappings"],
      ["ynab", "categories"],
      ["receipt-items", "distinct-categories"],
      ["ynab", "category-mappings"],
    ]) {
      expect(currentRead(key)?.state).toMatchObject({
        data: undefined,
        status: "pending",
        fetchStatus: "idle",
      });
    }
    const accountSection = screen
      .getByText("Account Mapping")
      .closest('[data-slot="card"]')!;
    const categorySection = screen
      .getByText("Category Mapping")
      .closest('[data-slot="card"]')!;
    expect
      .soft(within(accountSection as HTMLElement).queryByText("Not mapped"))
      .not.toBeInTheDocument();
    expect
      .soft(
        within(categorySection as HTMLElement).queryByText(
          "No receipt item categories found. Create some receipts first.",
        ),
      )
      .not.toBeInTheDocument();
    expect(readRequests).not.toContain("ynab/account-mappings");
    expect(readRequests).not.toContain("ynab/category-mappings");
    expect(writes).toEqual([]);
    expect(errorToast).not.toHaveBeenCalled();
    expect(router.state.location.pathname).toBe("/settings/ynab");
  },
);

it("shows successful empty receipt data without claiming an outage", async () => {
  empty = true;
  renderSettings();
  expect(
    await screen.findByText(
      "No receipts accounts found. Create accounts first.",
    ),
  ).toBeVisible();
  expect(
    await screen.findByText(
      "No receipt item categories found. Create some receipts first.",
    ),
  ).toBeVisible();
  expect(screen.queryByText(/unavailable/i)).not.toBeInTheDocument();
  expect(router.state.location.pathname).toBe("/settings/ynab");
  expect(writes).toEqual([]);
});

it("keeps independent rate telemetry available while the budget list is unavailable", async () => {
  failed = "ynab/budgets";
  renderSettings();
  expect(
    await screen.findByText(
      "YNAB budgets are unavailable. Any choices shown are last known.",
    ),
  ).toBeVisible();
  expect(await screen.findByText("10 / 200 requests used")).toBeVisible();
  expect(screen.getByText("190 remaining")).toBeVisible();
  expect(
    screen.getByRole("progressbar", { name: "API rate limit usage" }),
  ).toHaveAttribute("aria-valuenow", "10");
  expect(router.state.location.pathname).toBe("/settings/ynab");
  expect(writes).toEqual([]);
});

it.each(["Account Mapping", "Category Mapping"])(
  "allows a deliberate verified %s removal",
  async (title) => {
    renderSettings();
    await screen.findByText("Groceries");
    await screen.findByText("Everyday");
    const section = screen.getByText(title).closest('[data-slot="card"]')!;
    await userEvent
      .setup()
      .click(
        within(section as HTMLElement).getByRole("button", { name: "Remove" }),
      );
    await waitFor(() => expect(writes).toHaveLength(1));
    expect(writes[0]).toEqual({
      method: "DELETE",
      path:
        title === "Account Mapping"
          ? "ynab/account-mappings"
          : "ynab/category-mappings",
    });
  },
);

it.each(["Account Mapping", "Category Mapping", "stale"])(
  "blocks %s deletion while a budget change is awaiting its response",
  async (title) => {
    const budgetWrite = deferred();
    const finishBudgetWrite = deferred();
    server.use(
      http.put("*/api/ynab/settings/budget", async ({ request }) => {
        const body = (await request.json()) as { budgetId: string };
        writes.push({ method: "PUT", path: "ynab/settings/budget", body });
        budgetWrite.resolve();
        await finishBudgetWrite.promise;
        selected = body.budgetId;
        return new HttpResponse(null, { status: 204 });
      }),
    );
    renderSettings();
    await screen.findByText("Groceries");
    await screen.findByText("Everyday");
    const user = userEvent.setup();
    const budgetSection = screen
      .getByText("Budget Selection")
      .closest('[data-slot="card"]') as HTMLElement;
    within(budgetSection).getByRole("combobox").focus();
    await user.keyboard("{Enter}");
    await user.click(screen.getByRole("option", { name: "Budget B" }));
    await budgetWrite.promise;
    try {
      const remove =
        title === "stale"
          ? screen.getByRole("button", { name: "Delete previous-budget mappings" })
          : within(
              screen
                .getByText(title)
                .closest('[data-slot="card"]') as HTMLElement,
            ).getByRole("button", { name: "Remove" });
      await user.click(remove);
      expect(remove).toBeDisabled();
      expect(writes.filter((write) => write.method === "DELETE")).toEqual([]);
    } finally {
      await act(async () => {
        finishBudgetWrite.resolve();
        await finishBudgetWrite.promise;
      });
    }
    await waitFor(() => expect(queryClient.isMutating()).toBe(0));
    expect(writes.filter((write) => write.method === "DELETE")).toEqual([]);
  },
);

it.each([
  ["account", "Account Mapping", "ynab/account-mappings", "Remote account B"],
  ["category", "Category Mapping", "ynab/category-mappings", "Food B"],
] as const)(
  "blocks another %s choice and Remove while an update in that family is pending",
  async (kind, title, path, option) => {
    const writeStarted = deferred();
    const finishWrite = deferred();
    server.use(
      http.put(`*/api/${path}/:id`, async ({ request }) => {
        writes.push({ method: "PUT", path, body: await request.json() });
        writeStarted.resolve();
        await finishWrite.promise;
        return new HttpResponse(null, { status: 204 });
      }),
    );
    renderSettings();
    await screen.findByText("Groceries");
    await screen.findByText("Everyday");
    await waitFor(() => expect(queryClient.isFetching()).toBe(0));
    const section = screen
      .getByText(title)
      .closest('[data-slot="card"]') as HTMLElement;
    const selector = within(section).getByRole("combobox");
    const remove = within(section).getByRole("button", { name: "Remove" });
    const user = userEvent.setup();
    selector.focus();
    await user.keyboard("{Enter}");
    await user.click(screen.getByRole("option", { name: option }));
    await writeStarted.promise;
    try {
      expect(writes).toEqual([
        {
          method: "PUT",
          path,
          body:
            kind === "account"
              ? {
                  ynabAccountId: "remote-b",
                  ynabAccountName: "Remote account B",
                  ynabBudgetId: "budget-a",
                }
              : {
                  ynabCategoryId: "category-b",
                  ynabCategoryName: "Food B",
                  ynabCategoryGroupName: "Living",
                  ynabBudgetId: "budget-a",
                },
        },
      ]);
      expect(selector).toBeDisabled();
      expect(remove).toBeDisabled();
      await user.click(remove);
      selector.focus();
      await user.keyboard("{Enter}");
      expect(
        screen.queryByRole("option", { name: option }),
      ).not.toBeInTheDocument();
      expect(writes).toHaveLength(1);
      expect(queryClient.isMutating()).toBe(1);
    } finally {
      await act(async () => {
        finishWrite.resolve();
        await finishWrite.promise;
      });
    }
    await waitFor(() => expect(queryClient.isMutating()).toBe(0));
    await waitFor(() => expect(selector).toBeEnabled());
    expect(writes).toHaveLength(1);
  },
);
