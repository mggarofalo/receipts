vi.hoisted(() => vi.stubEnv("VITE_API_URL", "http://focus.test"));
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router";
import { delay, http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import { AuthProvider } from "@/contexts/AuthContext";
import { createAppQueryClient } from "@/lib/query-client";
import { clearTokens, setTokens } from "@/lib/auth";
import UncategorizedItems from "./UncategorizedItems";
import "@/test/setup-combobox-polyfills";
import { taxonomyFixture } from "@/test/taxonomy-fixture";
import { chooseCategoryOption } from "@/test/category-picker";
import type { components } from "@/generated/api";

vi.mock("sonner", () => ({ toast: { error: vi.fn(), success: vi.fn() } }));

type Item = components["schemas"]["UncategorizedItem"];
const item = (id: string, description: string): Item => ({
  id,
  receiptId: `receipt-${id}`,
  description,
  location: "Corner Market",
  quantity: 1,
  unitPrice: 1,
  totalAmount: 1,
  totalAmountCurrency: "USD",
  category: "",
  classificationState: "missing_assignment",
  assignmentId: null,
  categoryId: null,
  taxonomyRevisionId: null,
  suggestion: {
    attemptId: `attempt-${id}`,
    state: "suggested",
    categoryId: taxonomyFixture.nodes[1].id,
    confidence: 0.9,
    abstentionReason: null,
    rootCandidates: [],
    leafCandidates: [],
  },
});

// The server's list: a classified item leaves it, a rejected one stays.
let remaining: Item[] = [];
let rejectWrites = false;
const server = setupServer(
  http.get("*/api/reports/uncategorized-items", () =>
    HttpResponse.json({
      totalCount: remaining.length,
      items: remaining,
      suggestionsAvailable: true,
    }),
  ),
  http.get("*/api/taxonomy", () => HttpResponse.json(taxonomyFixture)),
  http.put("*/api/receipt-items/:id/classification", async ({ params }) => {
    // Real writes take a moment; the report sees the write in flight.
    await delay(20);
    if (rejectWrites)
      return HttpResponse.json(
        { status: 409, detail: "Current assignment changed" },
        { status: 409 },
      );
    remaining = remaining.filter((row) => row.id !== params.id);
    return new HttpResponse(null, { status: 204 });
  }),
);
let queryClient: ReturnType<typeof createAppQueryClient>;
beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
beforeEach(() => {
  remaining = [
    item("item-a", "Apples"),
    item("item-b", "Bread"),
    item("item-c", "Cheese"),
  ];
  rejectWrites = false;
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

async function renderReport() {
  render(
    <MemoryRouter>
      <AuthProvider queryClientFactory={() => queryClient}>
        <UncategorizedItems />
      </AuthProvider>
    </MemoryRouter>,
  );
  await screen.findByRole("button", { name: "Use Jev's choice for Cheese" });
}

const useButton = (description: string) =>
  screen.getByRole("button", { name: `Use Jev's choice for ${description}` });

it("moves focus to the row that takes a corrected row's place", async () => {
  const user = userEvent.setup();
  await renderReport();
  await user.click(useButton("Apples"));
  await waitFor(() => expect(screen.queryByText("Apples")).toBeNull());
  await waitFor(() => expect(useButton("Bread")).toHaveFocus());
});

it("moves focus to the new last row when the last row is corrected", async () => {
  const user = userEvent.setup();
  await renderReport();
  await user.click(useButton("Cheese"));
  await waitFor(() => expect(screen.queryByText("Cheese")).toBeNull());
  await waitFor(() => expect(useButton("Bread")).toHaveFocus());
});

it("moves focus the same way after a correction from the picker", async () => {
  const user = userEvent.setup();
  await renderReport();
  await user.click(
    screen.getByRole("combobox", { name: "Category for Bread" }),
  );
  await chooseCategoryOption(user, "Food / Dairy");
  await waitFor(() => expect(screen.queryByText("Bread")).toBeNull());
  await waitFor(() => expect(useButton("Cheese")).toHaveFocus());
});

it("returns focus to the same row's first control when the write fails", async () => {
  rejectWrites = true;
  const user = userEvent.setup();
  await renderReport();
  // The picker hands focus back to its own trigger as it closes; the row's
  // first control is the Use button before it.
  await user.click(
    screen.getByRole("combobox", { name: "Category for Bread" }),
  );
  await chooseCategoryOption(user, "Food / Dairy");
  await waitFor(() => expect(useButton("Bread")).toHaveFocus());
  expect(screen.getByText("Apples")).toBeInTheDocument();
  expect(screen.getByText("Cheese")).toBeInTheDocument();
});
