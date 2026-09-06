vi.hoisted(() =>
  vi.stubEnv("VITE_API_URL", "http://new-receipt-suggestions.test"),
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
import { createMemoryRouter, RouterProvider } from "react-router";
import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import { toast } from "sonner";
import { AuthProvider } from "@/contexts/AuthContext";
import { AppearanceProvider } from "@/contexts/AppearanceContext";
import { TooltipProvider } from "@/components/ui/tooltip";
import { RootLayout } from "@/components/RootLayout";
import { createAppQueryClient } from "@/lib/query-client";
import { clearTokens, setTokens } from "@/lib/auth";
import { clearServerErrorPageFlag } from "@/lib/server-error-bus";
import { locationHistory } from "@/lib/location-history";
import type { ReceiptLineItem } from "./LineItemsSection";
import type { ReceiptTransaction } from "./TransactionsSection";
import NewReceiptPage from "./NewReceiptPage";
import "@/test/setup-combobox-polyfills";

// Typed child-input fixtures supply valid rows. The actual page/header, MRU,
// unsaved-work blocker, auth/session/query ownership and complete POST remain real.
vi.mock("./LineItemsSection", () => ({
  LineItemsSection: ({
    onChange,
  }: {
    onChange: (items: ReceiptLineItem[]) => void;
  }) => (
    <button
      type="button"
      onClick={() =>
        onChange([
          {
            id: "line",
            receiptItemCode: "MILK",
            description: "Milk",
            quantity: 1,
            unitPrice: 5,
            category: "Food",
            subcategory: "Dairy",
          },
        ])
      }
    >
      Enter valid item
    </button>
  ),
}));
vi.mock("./TransactionsSection", () => ({
  TransactionsSection: ({
    onChange,
  }: {
    onChange: (items: ReceiptTransaction[]) => void;
  }) => (
    <button
      type="button"
      onClick={() =>
        onChange([
          {
            id: "payment",
            accountId: "95000000-0000-4000-8000-000000000010",
            cardId: "95000000-0000-4000-8000-000000000011",
            amount: 22.42,
            date: "2024-01-15",
          },
        ])
      }
    >
      Enter matching payment
    </button>
  ),
}));
function deferred() {
  let resolve!: () => void;
  const promise = new Promise<void>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}
let locationGate: ReturnType<typeof deferred>;
let locationStarted: ReturnType<typeof deferred>;
let failed: boolean;
let reads: number;
let bodies: unknown[];
const server = setupServer(
  http.get("*/api/receipts/locations", async () => {
    reads++;
    const capturedFailure = failed;
    locationStarted.resolve();
    await locationGate.promise;
    return capturedFailure
      ? HttpResponse.json(
          { status: 503, detail: "Location suggestions are unavailable" },
          { status: 503 },
        )
      : HttpResponse.json({ locations: ["Recovered API market"] });
  }),
  http.get("*/api/metadata/enums", () =>
    HttpResponse.json({
      adjustmentTypes: [],
      authEventTypes: [],
      auditActions: [],
      entityTypes: [],
    }),
  ),
  http.post("*/api/receipts/complete", async ({ request }) => {
    bodies.push(await request.json());
    return HttpResponse.json({
      receipt: { id: "created-receipt" },
      transactions: [],
      items: [],
      adjustments: [],
    });
  }),
);
const clients: ReturnType<typeof createAppQueryClient>[] = [];
const routers: ReturnType<typeof createMemoryRouter>[] = [];
function renderPage() {
  const client = createAppQueryClient();
  clients.push(client);
  const router = createMemoryRouter(
    [
      {
        element: <RootLayout />,
        children: [
          { path: "/receipts/new", element: <NewReceiptPage /> },
          {
            path: "/receipts/created-receipt",
            element: <h1>Saved receipt destination</h1>,
          },
          { path: "/error/500", element: <h1>Global server error route</h1> },
        ],
      },
    ],
    { initialEntries: ["/receipts/new"] },
  );
  routers.push(router);
  render(
    <AppearanceProvider>
      <TooltipProvider>
        <AuthProvider queryClientFactory={() => client}>
          <RouterProvider router={router} />
        </AuthProvider>
      </TooltipProvider>
    </AppearanceProvider>,
  );
  return router;
}
beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
afterAll(() => server.close());
beforeEach(() => {
  localStorage.clear();
  clearServerErrorPageFlag();
  setTokens(
    `header.${btoa(JSON.stringify({ sub: "alice", email: "alice@example.test", exp: 4102444800 }))}.signature`,
    "alice-refresh",
  );
  locationHistory.addEntry("Local MRU market");
  locationGate = deferred();
  locationStarted = deferred();
  failed = true;
  reads = 0;
  bodies = [];
});
afterEach(() => {
  locationGate.resolve();
  cleanup();
  routers.splice(0).forEach((router) => router.dispose());
  clients.splice(0).forEach((client) => client.clear());
  toast.dismiss();
  clearTokens();
  server.resetHandlers();
});

it.each([false, true])(
  "preserves a manually entered complete-receipt draft after held location503 and submits with retry=%s",
  async (retry) => {
    const router = renderPage();
    const user = userEvent.setup();
    await locationStarted.promise;
    await user.click(screen.getByRole("combobox"));
    expect(
      screen.getByRole("option", { name: "Local MRU market" }),
    ).toBeVisible();
    await user.type(
      screen.getByPlaceholderText("Search locations..."),
      "Manual market",
    );
    await user.click(
      screen.getByRole("button", { name: 'Use "Manual market"' }),
    );
    await user.type(screen.getByPlaceholderText("MM/DD/YYYY"), "01/15/2024");
    await user.clear(screen.getByLabelText("Tax Amount"));
    await user.type(screen.getByLabelText("Tax Amount"), "17.42");
    await user.click(screen.getByRole("button", { name: "Enter valid item" }));
    await user.click(
      screen.getByRole("button", { name: "Enter matching payment" }),
    );
    await act(async () => locationGate.resolve());
    const failure = await screen.findByRole("alert");
    expect(failure).toHaveTextContent(
      "Location suggestions unavailable. You can enter a location manually.",
    );
    expect(
      within(failure).getByRole("button", { name: "Retry" }),
    ).toBeEnabled();
    expect(router.state.location.pathname).toBe("/receipts/new");
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    expect(
      document.querySelectorAll('[data-sonner-toast][data-type="error"]'),
    ).toHaveLength(0);
    expect(screen.getByRole("combobox")).toHaveTextContent("Manual market");
    expect(screen.getByPlaceholderText("MM/DD/YYYY")).toHaveValue("01/15/2024");
    expect(screen.getByLabelText("Tax Amount")).toHaveValue("17.42");
    await user.click(screen.getByRole("combobox"));
    expect(
      screen.getByRole("option", { name: "Local MRU market" }),
    ).toBeVisible();
    await user.keyboard("{Escape}");
    if (retry) {
      failed = false;
      await user.click(within(failure).getByRole("button", { name: "Retry" }));
      await waitFor(() =>
        expect(screen.queryByRole("alert")).not.toBeInTheDocument(),
      );
      expect(reads).toBe(2);
      await user.click(screen.getByRole("combobox"));
      expect(
        screen.getByRole("option", { name: "Recovered API market" }),
      ).toBeVisible();
      expect(
        screen.getByRole("option", { name: "Local MRU market" }),
      ).toBeVisible();
      await user.keyboard("{Escape}");
      expect(screen.getByRole("combobox")).toHaveTextContent("Manual market");
      expect(screen.getByLabelText("Tax Amount")).toHaveValue("17.42");
    } else expect(reads).toBe(1);
    for (const button of screen.getAllByRole("button", {
      name: "Submit Receipt",
    }))
      expect(button).toBeEnabled();
    await user.click(
      screen.getAllByRole("button", { name: "Submit Receipt" })[0],
    );
    expect(
      await screen.findByRole("heading", { name: "Saved receipt destination" }),
    ).toBeVisible();
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    expect(bodies).toEqual([
      {
        receipt: {
          location: "Manual market",
          date: "2024-01-15",
          taxAmount: 17.42,
        },
        transactions: [
          {
            cardId: "95000000-0000-4000-8000-000000000011",
            amount: 22.42,
            date: "2024-01-15",
          },
        ],
        items: [
          {
            receiptItemCode: "MILK",
            description: "Milk",
            quantity: 1,
            unitPrice: 5,
            category: "Food",
            subcategory: "Dairy",
          },
        ],
        adjustments: [],
      },
    ]);
    expect(locationHistory.getHistory()).toContain("Manual market");
  },
);
