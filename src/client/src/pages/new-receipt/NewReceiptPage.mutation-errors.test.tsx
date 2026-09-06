vi.hoisted(() =>
  vi.stubEnv("VITE_API_URL", "http://complete-mutation-errors.test"),
);
import { cleanup, render, screen, waitFor } from "@testing-library/react";
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
import type { ReceiptLineItem } from "./LineItemsSection";
import type { ReceiptTransaction } from "./TransactionsSection";
import NewReceiptPage from "./NewReceiptPage";
import "@/test/setup-combobox-polyfills";

// Supply valid rows at child input boundaries. The actual page/header validation,
// blocker, mutation, request middleware, cache error owner and Sonner stay real.
vi.mock("./LineItemsSection", () => ({
  LineItemsSection: ({
    onChange,
  }: {
    onChange: (items: ReceiptLineItem[]) => void;
  }) => (
    <button
      onClick={() =>
        onChange([
          {
            id: "line",
            receiptItemCode: "",
            description: "Milk",
            quantity: 1,
            unitPrice: 5,
            category: "Food",
            subcategory: "",
          },
        ])
      }
    >
      Enter Milk line
    </button>
  ),
}));
vi.mock("./TransactionsSection", () => ({
  TransactionsSection: ({
    onChange,
  }: {
    onChange: (transactions: ReceiptTransaction[]) => void;
  }) => (
    <button
      onClick={() =>
        onChange([
          {
            id: "payment",
            accountId: "95000000-0000-4000-8000-000000000010",
            cardId: "95000000-0000-4000-8000-000000000011",
            amount: 5,
            date: "2024-01-15",
          },
        ])
      }
    >
      Enter five dollar payment
    </button>
  ),
}));

let status: number;
let bodies: unknown[];
const message = "The receipt could not be saved; keep your entered values";
const server = setupServer(
  http.get("*/api/receipts/locations", () =>
    HttpResponse.json({ locations: ["Market"] }),
  ),
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
    return status === 200
      ? HttpResponse.json({
          receipt: { id: "created-receipt" },
          transactions: [],
          items: [],
          adjustments: [],
        })
      : HttpResponse.json({ status, detail: message }, { status });
  }),
);
const clients: ReturnType<typeof createAppQueryClient>[] = [];
const routers: ReturnType<typeof createMemoryRouter>[] = [];
function renderPage() {
  const queryClient = createAppQueryClient();
  clients.push(queryClient);
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
        <AuthProvider queryClientFactory={() => queryClient}>
          <RouterProvider router={router} />
        </AuthProvider>
      </TooltipProvider>
    </AppearanceProvider>,
  );
  return { queryClient, router };
}
beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
afterAll(() => server.close());
beforeEach(() => {
  status = 400;
  bodies = [];
  clearServerErrorPageFlag();
  setTokens(
    `header.${btoa(JSON.stringify({ sub: "alice", email: "alice@example.test", exp: 4102444800 }))}.signature`,
    "alice-refresh",
  );
});
afterEach(() => {
  cleanup();
  routers.splice(0).forEach((router) => router.dispose());
  clients.splice(0).forEach((queryClient) => queryClient.clear());
  toast.dismiss();
  clearTokens();
  server.resetHandlers();
});
async function enterAndSubmit() {
  const user = userEvent.setup();
  await user.click(screen.getByRole("combobox"));
  await user.click(await screen.findByText("Market"));
  const date = screen.getByPlaceholderText("MM/DD/YYYY");
  await user.click(date);
  await user.type(date, "01/15/2024");
  await user.click(screen.getByRole("button", { name: "Enter Milk line" }));
  await user.click(
    screen.getByRole("button", { name: "Enter five dollar payment" }),
  );
  await user.click(
    screen.getAllByRole("button", { name: "Submit Receipt" })[0],
  );
  return user;
}

it.each([400, 503])(
  "keeps the complete-create page's single error owner for POST%s without asking to discard its draft",
  async (code) => {
    status = code;
    const { queryClient, router } = renderPage();
    await enterAndSubmit();
    await waitFor(() =>
      expect(queryClient.getMutationCache().getAll().at(-1)?.state.status).toBe(
        "error",
      ),
    );
    await waitFor(() =>
      expect(
        document.querySelectorAll('[data-sonner-toast][data-type="error"]'),
      ).toHaveLength(1),
    );
    expect(
      document.querySelector('[data-sonner-toast][data-type="error"]'),
    ).toHaveTextContent(message);
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    expect(router.state.location.pathname).toBe("/receipts/new");
    expect(screen.getByRole("heading", { name: "New Receipt" })).toBeVisible();
    expect(screen.getByRole("combobox")).toHaveTextContent("Market");
    expect(screen.getByPlaceholderText("MM/DD/YYYY")).toHaveValue("01/15/2024");
    expect(
      document.querySelector('[aria-live="polite"][aria-atomic="true"]'),
    ).toHaveTextContent(message);
    expect(
      screen.getAllByRole("button", { name: "Submit Receipt" })[0],
    ).toBeEnabled();
    expect(bodies).toEqual([
      {
        receipt: { location: "Market", date: "2024-01-15", taxAmount: 0 },
        transactions: [
          {
            cardId: "95000000-0000-4000-8000-000000000011",
            amount: 5,
            date: "2024-01-15",
          },
        ],
        items: [
          {
            receiptItemCode: "",
            description: "Milk",
            quantity: 1,
            unitPrice: 5,
            category: "Food",
            subcategory: "",
          },
        ],
        adjustments: [],
      },
    ]);
  },
);

it("preserves the complete-create page's one success toast and saved navigation", async () => {
  status = 200;
  renderPage();
  await enterAndSubmit();
  expect(
    await screen.findByRole("heading", { name: "Saved receipt destination" }),
  ).toBeVisible();
  expect(
    await screen.findByText("Receipt created successfully!"),
  ).toBeVisible();
  expect(
    document.querySelectorAll('[data-sonner-toast][data-type="success"]'),
  ).toHaveLength(1);
  expect(
    document.querySelectorAll('[data-sonner-toast][data-type="error"]'),
  ).toHaveLength(0);
  expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
  expect(bodies).toHaveLength(1);
});
