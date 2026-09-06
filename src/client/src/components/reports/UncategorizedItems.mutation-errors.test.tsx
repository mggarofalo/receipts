vi.hoisted(() => vi.stubEnv("VITE_API_URL", "http://mutation-errors.test"));
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router";
import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import { toast } from "sonner";
import { AuthProvider } from "@/contexts/AuthContext";
import { createAppQueryClient } from "@/lib/query-client";
import { clearTokens, setTokens } from "@/lib/auth";
import UncategorizedItems from "./UncategorizedItems";
import "@/test/setup-combobox-polyfills";

vi.mock("sonner", () => ({ toast: { error: vi.fn(), success: vi.fn() } }));
const items = [
  {
    id: "item-a",
    receiptId: "receipt-a",
    description: "Apples",
    quantity: 1,
    unitPrice: 1,
    totalAmount: 1,
    category: "Uncategorized",
  },
  {
    id: "item-b",
    receiptId: "receipt-b",
    description: "Bread",
    quantity: 1,
    unitPrice: 2,
    totalAmount: 2,
    category: "Uncategorized",
  },
];
let rejectFirst = true;
let writes: unknown[] = [];
let reads = 0;
const server = setupServer(
  http.get("*/api/reports/uncategorized-items", () => {
    reads++;
    return HttpResponse.json({ totalCount: items.length, items });
  }),
  http.get("*/api/categories", () =>
    HttpResponse.json({
      data: [{ id: "category", name: "Groceries", isActive: true }],
      total: 1,
    }),
  ),
  http.get("*/api/subcategories", () =>
    HttpResponse.json({ data: [], total: 0 }),
  ),
  http.put("*/api/receipt-items/batch", async ({ request }) => {
    const body = (await request.json()) as { id: string }[];
    writes.push(body);
    return rejectFirst && body[0].id === "item-a"
      ? HttpResponse.json(
          { status: 503, detail: "One receipt group is unavailable" },
          { status: 503 },
        )
      : new HttpResponse(null, { status: 204 });
  }),
);
let queryClient: ReturnType<typeof createAppQueryClient>;
beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
beforeEach(() => {
  vi.clearAllMocks();
  writes = [];
  reads = 0;
  setTokens("current-access", "current-refresh");
  queryClient = createAppQueryClient();
});
afterEach(() => {
  cleanup();
  queryClient.clear();
  clearTokens();
  server.resetHandlers();
});
afterAll(() => server.close());

it.each([true, false])(
  "uses one failure owner and preserves grouped settlement when one group fails: %s",
  async (fails) => {
    rejectFirst = fails;
    const user = userEvent.setup();
    render(
      <MemoryRouter>
        <AuthProvider queryClientFactory={() => queryClient}>
          <UncategorizedItems />
        </AuthProvider>
      </MemoryRouter>,
    );
    await screen.findByText("Apples");
    await user.click(
      screen.getByRole("checkbox", { name: "Select all items on this page" }),
    );
    await user.click(screen.getAllByRole("combobox")[0]);
    await user.click(screen.getByRole("option", { name: "Groceries" }));
    await user.click(screen.getByRole("button", { name: "Apply to Selected" }));
    await waitFor(() => expect(writes).toHaveLength(2));
    await waitFor(() => expect(reads).toBeGreaterThan(1));
    if (fails) {
      await waitFor(() =>
        expect(
          screen.getByRole("button", { name: "Apply to Selected" }),
        ).toBeEnabled(),
      );
      expect(toast.error).toHaveBeenCalledExactlyOnceWith(
        "One receipt group is unavailable",
      );
      expect(toast.success).not.toHaveBeenCalled();
      expect(screen.getByText("2 selected")).toBeInTheDocument();
    } else {
      await waitFor(() =>
        expect(toast.success).toHaveBeenCalledExactlyOnceWith(
          "Items categorized successfully",
        ),
      );
      expect(toast.error).not.toHaveBeenCalled();
      expect(screen.queryByText("2 selected")).not.toBeInTheDocument();
    }
  },
);
