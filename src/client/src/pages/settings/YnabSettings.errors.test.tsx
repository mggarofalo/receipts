vi.hoisted(() => vi.stubEnv("VITE_API_URL", "http://settings-errors.test"));
import { act, cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes } from "react-router";
import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import { toast } from "sonner";
import { AuthProvider } from "@/contexts/AuthContext";
import { AppearanceProvider } from "@/contexts/AppearanceContext";
import { RootLayout } from "@/components/RootLayout";
import { createAppQueryClient } from "@/lib/query-client";
import { clearTokens, setTokens } from "@/lib/auth";
import { clearServerErrorPageFlag } from "@/lib/server-error-bus";
import YnabSettings from "./YnabSettings";
import "@/test/setup-combobox-polyfills";

let failed = false;
let selected = "budget-a";
let writes: unknown[] = [];
const server = setupServer(
  http.get("*/api/ynab/connection-status", () =>
    HttpResponse.json({ isConfigured: true, isConnected: true }),
  ),
  http.get("*/api/ynab/settings/budget", () =>
    failed
      ? HttpResponse.json(
          { status: 503, detail: "Budget read unavailable" },
          { status: 503 },
        )
      : HttpResponse.json({ selectedBudgetId: selected }),
  ),
  http.put("*/api/ynab/settings/budget", async ({ request }) => {
    const body = (await request.json()) as { budgetId: string };
    writes.push(body);
    selected = body.budgetId;
    return new HttpResponse(null, { status: 204 });
  }),
  http.get("*/api/ynab/budgets", () =>
    HttpResponse.json({
      data: [
        { id: "budget-a", name: "Budget A" },
        { id: "budget-b", name: "Budget B" },
      ],
    }),
  ),
  ...[
    "accounts",
    "receipts",
    "ynab/accounts",
    "ynab/account-mappings",
    "ynab/categories",
    "ynab/category-mappings",
  ].map((path) =>
    http.get(`*/api/${path}`, () =>
      HttpResponse.json({ data: [], total: 0, offset: 0, limit: 500 }),
    ),
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
beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
afterAll(() => server.close());
beforeEach(() => {
  failed = false;
  selected = "budget-a";
  writes = [];
  clearServerErrorPageFlag();
  setTokens("alice-access", "alice-refresh");
  queryClient = createAppQueryClient();
});
afterEach(() => {
  cleanup();
  queryClient.clear();
  clearTokens();
  toast.dismiss();
  server.resetHandlers();
});

it("blocks an already-open budget menu after selected-budget failure and allows changing it after Retry", async () => {
  render(
    <MemoryRouter>
      <AppearanceProvider>
        <AuthProvider queryClientFactory={() => queryClient}>
          <Routes>
            <Route element={<RootLayout />}>
              <Route path="/" element={<YnabSettings />} />
              <Route
                path="/error/500"
                element={<h1>Global server error route</h1>}
              />
            </Route>
          </Routes>
        </AuthProvider>
      </AppearanceProvider>
    </MemoryRouter>,
  );
  const user = userEvent.setup();
  const selector = await screen.findByRole("combobox");
  await waitFor(() => expect(selector).toHaveTextContent("Budget A"));
  selector.focus();
  await user.keyboard("{Enter}");
  expect(screen.getByRole("option", { name: "Budget B" })).toBeVisible();
  failed = true;
  await act(async () => {
    await queryClient.invalidateQueries({
      queryKey: ["ynab", "settings", "budget"],
    });
  });
  await waitFor(() =>
    expect(
      screen.getByText(
        "The selected YNAB budget is unavailable. Any selection shown is last known.",
      ),
    ).toBeInTheDocument(),
  );
  await user.click(screen.getByRole("option", { name: "Budget B" }));
  expect(writes).toEqual([]);
  expect(selected).toBe("budget-a");
  expect(
    screen.queryByRole("heading", { name: "Global server error route" }),
  ).not.toBeInTheDocument();
  await user.keyboard("{Escape}");
  failed = false;
  await user.click(screen.getByRole("button", { name: "Retry" }));
  await waitFor(() => expect(screen.getByRole("combobox")).toBeEnabled());
  screen.getByRole("combobox").focus();
  await user.keyboard("{Enter}");
  await user.click(screen.getByRole("option", { name: "Budget B" }));
  await waitFor(() => expect(writes).toEqual([{ budgetId: "budget-b" }]));
  expect(selected).toBe("budget-b");
});
