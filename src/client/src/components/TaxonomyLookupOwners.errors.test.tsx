vi.hoisted(() => vi.stubEnv("VITE_API_URL", "http://taxonomy-owners.test"));
import type { ReactNode } from "react";
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
import UncategorizedItems from "./reports/UncategorizedItems";
import { createAppQueryClient } from "@/lib/query-client";
import { clearTokens, setTokens } from "@/lib/auth";
import { clearServerErrorPageFlag } from "@/lib/server-error-bus";
import { taxonomyFixture } from "@/test/taxonomy-fixture";
import "@/test/setup-combobox-polyfills";

let taxonomyFailure: boolean;
let taxonomyReads: number;
let writes: string[];
const items = [
  {
    id: "milk",
    receiptId: "receipt",
    receiptItemCode: null,
    description: "Unsorted milk",
    quantity: 1,
    unitPrice: 3.45,
    totalAmount: 3.45,
    totalAmountCurrency: "USD",
    classificationState: "missing_assignment",
    assignmentId: null,
    categoryId: null,
    taxonomyRevisionId: null,
  },
];
const server = setupServer(
  http.get("*/api/taxonomy", () => {
    taxonomyReads++;
    return taxonomyFailure
      ? HttpResponse.json(
          { status: 503, detail: "Taxonomy lookup unavailable" },
          { status: 503 },
        )
      : HttpResponse.json(taxonomyFixture);
  }),
  http.get("*/api/reports/uncategorized-items", () =>
    HttpResponse.json({ totalCount: items.length, items }),
  ),
  http.post("*/api/:entity", ({ params }) => {
    writes.push(String(params.entity));
    return HttpResponse.json({ id: "unexpected" });
  }),
  http.put("*/api/:entity/:id", ({ params }) => {
    writes.push(String(params.entity));
    return new HttpResponse(null, { status: 204 });
  }),
);
const clients: ReturnType<typeof createAppQueryClient>[] = [];
const routers: ReturnType<typeof createMemoryRouter>[] = [];
function renderFeature(feature: ReactNode) {
  const client = createAppQueryClient();
  clients.push(client);
  const router = createMemoryRouter([
    {
      element: <RootLayout />,
      children: [
        { path: "/", element: feature },
        { path: "/error/500", element: <h1>Global server error route</h1> },
      ],
    },
  ]);
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
}
beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
afterAll(() => server.close());
beforeEach(() => {
  taxonomyFailure = true;
  taxonomyReads = 0;
  writes = [];
  localStorage.clear();
  clearServerErrorPageFlag();
  setTokens(
    `header.${btoa(JSON.stringify({ sub: "admin", email: "admin@example.test", role: "Admin", exp: 4102444800 }))}.signature`,
    "refresh",
  );
});
afterEach(() => {
  cleanup();
  routers.splice(0).forEach((router) => router.dispose());
  clients.splice(0).forEach((client) => client.clear());
  toast.dismiss();
  clearTokens();
  server.resetHandlers();
});

it("keeps report selection and restores nested category choices after taxonomy retry", async () => {
  renderFeature(<UncategorizedItems />);
  const user = userEvent.setup();
  await screen.findByText("Unsorted milk");
  await user.click(
    screen.getByRole("checkbox", { name: "Select all items on this page" }),
  );
  expect(screen.getByText("1 selected on this page")).toBeInTheDocument();
  expect(await screen.findByRole("alert")).toHaveTextContent(
    "Category choices unavailable. Your selection is retained.",
  );
  expect(
    screen.getByRole("combobox", { name: "Category for selected items" }),
  ).toBeDisabled();
  expect(
    screen.getByRole("button", { name: "Apply to Selected" }),
  ).toBeDisabled();
  expect(
    screen.queryByText("Global server error route"),
  ).not.toBeInTheDocument();

  taxonomyFailure = false;
  await user.click(screen.getByRole("button", { name: "Retry" }));
  await waitFor(() => expect(taxonomyReads).toBe(2));
  await waitFor(() =>
    expect(screen.queryByRole("alert")).not.toBeInTheDocument(),
  );
  expect(screen.getByText("1 selected on this page")).toBeInTheDocument();
  const picker = screen.getByRole("combobox", {
    name: "Category for selected items",
  });
  expect(picker).toBeEnabled();
  await user.click(picker);
  await user.click(screen.getByRole("option", { name: "Food / Dairy" }));
  expect(picker).toHaveTextContent("Food / Dairy");
  expect(
    screen.getByRole("button", { name: "Apply to Selected" }),
  ).toBeEnabled();
  expect(writes).toEqual([]);
});
