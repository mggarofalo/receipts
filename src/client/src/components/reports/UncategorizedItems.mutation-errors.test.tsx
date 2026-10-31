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
import { taxonomyFixture } from "@/test/taxonomy-fixture";
import { chooseCategoryOption } from "@/test/category-picker";

vi.mock("sonner", () => ({ toast: { error: vi.fn(), success: vi.fn() } }));
const items = [
  {
    id: "item-a",
    receiptId: "receipt-a",
    description: "Apples",
    location: "Corner Market",
    quantity: 1,
    unitPrice: 1,
    totalAmount: 1,
    totalAmountCurrency: "USD",
    classificationState: "missing_assignment",
    assignmentId: null,
    categoryId: null,
    taxonomyRevisionId: null,
  },
  {
    id: "item-b",
    receiptId: "receipt-b",
    description: "Bread",
    location: "Corner Market",
    quantity: 1,
    unitPrice: 2,
    totalAmount: 2,
    totalAmountCurrency: "USD",
    classificationState: "unresolved_legacy",
    assignmentId: "prior-assignment",
    categoryId: null,
    taxonomyRevisionId: "prior-revision",
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
  http.get("*/api/taxonomy", () => HttpResponse.json(taxonomyFixture)),
  http.put(
    "*/api/receipt-items/:id/classification",
    async ({ request, params }) => {
      const body = (await request.json()) as Record<string, unknown>;
      writes.push({ id: params.id, body });
      return rejectFirst && params.id === "item-a"
        ? HttpResponse.json(
            { status: 409, detail: "Current assignment changed" },
            { status: 409 },
          )
        : new HttpResponse(null, { status: 204 });
    },
  ),
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
  "preserves per-item settlement and assignment fences when one write conflicts: %s",
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
    await user.click(
      await screen.findByRole("combobox", {
        name: "Category for selected items",
      }),
    );
    await chooseCategoryOption(user, "Food / Dairy");
    await user.click(screen.getByRole("button", { name: "Apply to Selected" }));
    await waitFor(() => expect(writes).toHaveLength(2));
    expect(writes).toEqual(
      expect.arrayContaining([
        {
          id: "item-a",
          body: expect.objectContaining({
            categoryId: taxonomyFixture.nodes[1].id,
            taxonomyRevisionId: taxonomyFixture.id,
            expectedAssignmentId: null,
            operationId: expect.any(String),
            reason: "Category selected from unclassified items report",
          }),
        },
        {
          id: "item-b",
          body: expect.objectContaining({
            categoryId: taxonomyFixture.nodes[1].id,
            taxonomyRevisionId: taxonomyFixture.id,
            expectedAssignmentId: "prior-assignment",
            operationId: expect.any(String),
            reason: "Category selected from unclassified items report",
          }),
        },
      ]),
    );
    await waitFor(() => expect(reads).toBeGreaterThan(1));
    if (fails) {
      await waitFor(() =>
        expect(
          screen.getByRole("button", { name: "Apply to Selected" }),
        ).toBeEnabled(),
      );
      expect(toast.error).toHaveBeenCalledExactlyOnceWith(
        "1 item's category could not be confirmed. Refresh the list and check before retrying.",
      );
      expect(toast.success).not.toHaveBeenCalled();
      expect(screen.getByText("2 selected on this page")).toBeInTheDocument();
    } else {
      await waitFor(() =>
        expect(toast.success).toHaveBeenCalledExactlyOnceWith(
          "Items categorized successfully",
        ),
      );
      expect(toast.error).not.toHaveBeenCalled();
      expect(
        screen.queryByText("2 selected on this page"),
      ).not.toBeInTheDocument();
    }
  },
);
