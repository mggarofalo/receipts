vi.hoisted(() =>
  vi.stubEnv("VITE_API_URL", "http://receipt-mutation-errors.test"),
);
import {
  cleanup,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Link, MemoryRouter, Route, Routes } from "react-router";
import { useQuery } from "@tanstack/react-query";
import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import { toast } from "sonner";
import { AuthProvider } from "@/contexts/AuthContext";
import { AppearanceProvider } from "@/contexts/AppearanceContext";
import { TooltipProvider } from "@/components/ui/tooltip";
import { RootLayout } from "@/components/RootLayout";
import client from "@/lib/api-client";
import { createAppQueryClient } from "@/lib/query-client";
import { clearTokens, setTokens } from "@/lib/auth";
import { clearServerErrorPageFlag } from "@/lib/server-error-bus";
import ReceiptDetail from "./ReceiptDetail";
import "@/test/setup-combobox-polyfills";

const receiptId = "95000000-0000-4000-8000-000000000002";
const failureDetail = "Receipt service temporarily unavailable";
let writeStatus: number;
let savedTax: number;
let writes: Array<{
  id: string;
  location: string;
  date: string;
  taxAmount: number;
}>;
let tripReads: number;
let globalReads: number;
const server = setupServer(
  http.get("*/api/trips", () => {
    tripReads++;
    return HttpResponse.json({
      receipt: {
        receipt: {
          id: receiptId,
          location: "Draft grocery",
          date: "2026-09-01",
          taxAmount: savedTax,
        },
        items: [],
        adjustments: [],
        warnings: [],
        subtotal: 0,
        adjustmentTotal: 0,
        expectedTotal: savedTax,
      },
      transactions: [],
      warnings: [],
    });
  }),
  http.put("*/api/receipts/:id", async ({ request, params }) => {
    const body = (await request.json()) as (typeof writes)[number];
    expect(params.id).toBe(receiptId);
    writes.push(body);
    if (writeStatus === 400)
      return HttpResponse.json(
        {
          status: 400,
          detail: "One or more validation errors occurred.",
          errors: {
            taxAmount: ["Tax exceeds this receipt's permitted amount"],
          },
        },
        { status: 400 },
      );
    if (writeStatus === 409)
      return HttpResponse.json(
        {
          status: 409,
          detail: "Receipt changed; review your values before saving",
        },
        { status: 409 },
      );
    if (writeStatus !== 204)
      return HttpResponse.json(
        { status: writeStatus, detail: failureDetail },
        { status: writeStatus },
      );
    savedTax = body.taxAmount;
    return new HttpResponse(null, { status: 204 });
  }),
  http.get("*/api/accounts", () => {
    globalReads++;
    return HttpResponse.json(
      { status: 503, detail: "Default account read unavailable" },
      { status: 503 },
    );
  }),
  http.get("*/api/ynab/connection-status", () =>
    HttpResponse.json({ isConfigured: false, isConnected: false }),
  ),
  http.get("*/api/ynab/settings/budget", () =>
    HttpResponse.json({ selectedBudgetId: null }),
  ),
  http.get("*/api/metadata/enums", () =>
    HttpResponse.json({
      adjustmentTypes: [],
      authEventTypes: [],
      auditActions: [],
      entityTypes: [],
    }),
  ),
  http.get("*/api/cards", () =>
    HttpResponse.json({ data: [], total: 0, offset: 0, limit: 500 }),
  ),
  http.get("*/api/audit", () =>
    HttpResponse.json({ data: [], total: 0, offset: 0, limit: 50 }),
  ),
  http.get("*/api/receipts/locations", () => HttpResponse.json({ data: [] })),
);
const clients: ReturnType<typeof createAppQueryClient>[] = [];
function DefaultGlobalRead() {
  useQuery({
    queryKey: ["mutation-error-global-control"],
    queryFn: async () => {
      const result = await client.GET("/api/accounts");
      if (result.error) throw result.error;
      return result.data;
    },
  });
  return <p>Loading default account read</p>;
}
function renderReceipt() {
  const queryClient = createAppQueryClient();
  clients.push(queryClient);
  render(
    <MemoryRouter initialEntries={[`/receipts/${receiptId}`]}>
      <AppearanceProvider>
        <TooltipProvider>
          <AuthProvider queryClientFactory={() => queryClient}>
            <Routes>
              <Route element={<RootLayout />}>
                <Route
                  path="/receipts/:id"
                  element={
                    <>
                      <Link to="/global-read">Load default global read</Link>
                      <ReceiptDetail />
                    </>
                  }
                />
                <Route path="/global-read" element={<DefaultGlobalRead />} />
                <Route
                  path="/error/500"
                  element={<h1>Global server error route</h1>}
                />
              </Route>
            </Routes>
          </AuthProvider>
        </TooltipProvider>
      </AppearanceProvider>
    </MemoryRouter>,
  );
  return queryClient;
}
beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
afterAll(() => server.close());
beforeEach(() => {
  writeStatus = 503;
  savedTax = 0;
  writes = [];
  tripReads = 0;
  globalReads = 0;
  clearServerErrorPageFlag();
  setTokens(
    `header.${btoa(JSON.stringify({ sub: "alice", email: "alice@example.test", exp: 4102444800 }))}.signature`,
    "alice-refresh",
  );
});
afterEach(() => {
  cleanup();
  clients.splice(0).forEach((queryClient) => queryClient.clear());
  toast.dismiss();
  clearTokens();
  server.resetHandlers();
});
async function enterTaxAndSave() {
  const user = userEvent.setup();
  await screen.findByRole("heading", { name: "Draft grocery" });
  await user.click(screen.getByRole("button", { name: /^Edit$/ }));
  const dialog = screen.getByRole("dialog", { name: "Edit receipt" });
  await user.clear(within(dialog).getByLabelText("Tax Amount"));
  await user.type(within(dialog).getByLabelText("Tax Amount"), "42.25");
  await user.click(
    within(dialog).getByRole("button", { name: "Update Receipt" }),
  );
  return user;
}
async function waitForMutationFailure(
  queryClient: ReturnType<typeof createAppQueryClient>,
) {
  await waitFor(() =>
    expect(queryClient.getMutationCache().getAll().at(-1)?.state.status).toBe(
      "error",
    ),
  );
}
function expectPreservedDraft() {
  expect(
    screen.queryByRole("heading", { name: "Global server error route" }),
  ).not.toBeInTheDocument();
  expect(screen.getByRole("dialog", { name: "Edit receipt" })).toBeVisible();
  expect(screen.getByLabelText("Tax Amount")).toHaveValue("42.25");
  expect(writes).toEqual([
    {
      id: receiptId,
      location: "Draft grocery",
      date: "2026-09-01",
      taxAmount: 42.25,
    },
  ]);
}

it("retains the header draft after PUT503, shows one error without automatic retry, and saves a deliberate retry", async () => {
  const queryClient = renderReceipt();
  const user = await enterTaxAndSave();
  await waitForMutationFailure(queryClient);
  expectPreservedDraft();
  expect(await screen.findByText(failureDetail)).toBeVisible();
  expect(
    document.querySelectorAll('[data-sonner-toast][data-type="error"]'),
  ).toHaveLength(1);
  expect(tripReads).toBe(1);
  writeStatus = 204;
  await user.click(screen.getByRole("button", { name: "Update Receipt" }));
  await waitFor(() =>
    expect(
      screen.queryByRole("dialog", { name: "Edit receipt" }),
    ).not.toBeInTheDocument(),
  );
  await waitFor(() => expect(tripReads).toBe(2));
  expect(writes).toHaveLength(2);
  expect(writes[1]).toEqual(writes[0]);
  expect(await screen.findByText("Receipt updated")).toBeVisible();
  await user.click(screen.getByRole("button", { name: /^Edit$/ }));
  expect(screen.getByLabelText("Tax Amount")).toHaveValue("42.25");
});

it("keeps PUT400 field validation beside the entered value with one generic error toast", async () => {
  writeStatus = 400;
  const queryClient = renderReceipt();
  await enterTaxAndSave();
  await waitForMutationFailure(queryClient);
  expectPreservedDraft();
  const tax = screen.getByLabelText("Tax Amount");
  expect(tax).toHaveAttribute("aria-invalid", "true");
  expect(tax).toHaveAccessibleDescription(
    /Tax exceeds this receipt's permitted amount/,
  );
  expect(
    within(screen.getByRole("dialog", { name: "Edit receipt" })).getByText(
      "Tax exceeds this receipt's permitted amount",
    ),
  ).toBeVisible();
  expect(
    document.querySelectorAll('[data-sonner-toast][data-type="error"]'),
  ).toHaveLength(1);
});

it("keeps PUT409 server detail and draft instead of navigating or closing the editor", async () => {
  writeStatus = 409;
  const queryClient = renderReceipt();
  await enterTaxAndSave();
  await waitForMutationFailure(queryClient);
  expectPreservedDraft();
  expect(
    await screen.findByText(
      "Receipt changed; review your values before saving",
    ),
  ).toBeVisible();
  expect(
    document.querySelectorAll('[data-sonner-toast][data-type="error"]'),
  ).toHaveLength(1);
});

it("closes the editor and refreshes the authoritative receipt after a successful header update", async () => {
  writeStatus = 204;
  renderReceipt();
  await enterTaxAndSave();
  await waitFor(() =>
    expect(
      screen.queryByRole("dialog", { name: "Edit receipt" }),
    ).not.toBeInTheDocument(),
  );
  await waitFor(() => expect(tripReads).toBe(2));
  expect(savedTax).toBe(42.25);
  expect(writes).toHaveLength(1);
  expect(await screen.findByText("Receipt updated")).toBeVisible();
  expect(
    document.querySelectorAll('[data-sonner-toast][data-type="error"]'),
  ).toHaveLength(0);
});

it("retains the first-error route for an unrelated default global read", async () => {
  renderReceipt();
  await screen.findByRole("heading", { name: "Draft grocery" });
  await userEvent
    .setup()
    .click(screen.getByRole("link", { name: "Load default global read" }));
  expect(
    await screen.findByRole("heading", { name: "Global server error route" }),
  ).toBeVisible();
  expect(globalReads).toBe(1);
  expect(writes).toHaveLength(0);
  expect(
    document.querySelectorAll('[data-sonner-toast][data-type="error"]'),
  ).toHaveLength(0);
});
