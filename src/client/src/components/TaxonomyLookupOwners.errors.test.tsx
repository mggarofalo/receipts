vi.hoisted(() => vi.stubEnv("VITE_API_URL", "http://taxonomy-owners.test"));
import type { ReactNode } from "react";
import { act, cleanup, render, screen, waitFor } from "@testing-library/react";
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
import "@/test/setup-combobox-polyfills";
let categoryFailure: boolean;
let scopedFailure: boolean;
let scopedReads: string[];
let writes: string[];
const categories = [
  { id: "food", name: "Food", isActive: true },
  { id: "home", name: "Home", isActive: true },
];
const dairy = {
  id: "dairy",
  categoryId: "food",
  name: "Dairy",
  description: "Original",
  isActive: true,
};
const items = [
  {
    id: "milk",
    receiptId: "receipt",
    description: "Unsorted milk",
    quantity: 1,
    unitPrice: 3.45,
    totalAmount: 3.45,
    category: "Uncategorized",
    subcategory: "",
  },
];
const server = setupServer(
  http.get("*/api/categories", () => {
    return categoryFailure
      ? HttpResponse.json(
          { status: 503, detail: "Category lookup unavailable" },
          { status: 503 },
        )
      : HttpResponse.json({
          data: categories,
          total: categories.length,
          offset: 0,
          limit: 500,
        });
  }),
  http.get("*/api/subcategories", ({ request }) => {
    const params = new URL(request.url).searchParams;
    const parent = params.get("categoryId");
    if (parent) scopedReads.push(parent);
    if (parent && params.get("limit") === "500" && scopedFailure)
      return HttpResponse.json(
        { status: 503, detail: "Subcategory lookup unavailable" },
        { status: 503 },
      );
    const data =
      parent === "home"
        ? [
            {
              id: "cleaning",
              categoryId: "home",
              name: "Cleaning",
              isActive: true,
            },
          ]
        : [dairy];
    return HttpResponse.json({
      data,
      total: data.length,
      offset: 0,
      limit: Number(params.get("limit") ?? 50),
    });
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
  return client;
}
beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
afterAll(() => server.close());
beforeEach(() => {
  categoryFailure = false;
  scopedFailure = false;
  scopedReads = [];
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
it.each(["report"] as const)(
  "removes the %s owner's obsolete scoped Retry after selecting another parent",
  async (kind) => {
    const client = renderFeature(<UncategorizedItems />);
    const user = userEvent.setup();
    if (kind === "report") {
      await screen.findByText("Unsorted milk");
      await user.click(
        screen.getByRole("checkbox", { name: "Select all items on this page" }),
      );
      await user.click(screen.getAllByRole("combobox")[0]);
      await user.click(await screen.findByRole("option", { name: "Food" }));
    }
    await waitFor(() =>
      expect(
        client.getQueryState([
          "subcategories",
          "byCategory",
          "all",
          "food",
          true,
        ])?.status,
      ).toBe("success"),
    );
    scopedFailure = true;
    await act(async () => {
      await client.invalidateQueries({
        queryKey: ["subcategories", "byCategory", "all", "food"],
      });
    });
    await screen.findByRole("alert");
    scopedFailure = false;
    const oldRequests = scopedReads.filter(
      (parent) => parent === "food",
    ).length;
    await user.click(screen.getAllByRole("combobox")[0]);
    await user.click(await screen.findByRole("option", { name: "Home" }));
    await waitFor(() =>
      expect(
        client.getQueryState([
          "subcategories",
          "byCategory",
          "all",
          "home",
          true,
        ])?.status,
      ).toBe("success"),
    );
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
    expect(
      screen.queryByRole("button", { name: "Retry" }),
    ).not.toBeInTheDocument();
    expect(scopedReads.filter((parent) => parent === "food")).toHaveLength(
      oldRequests,
    );
    expect(writes).toEqual([]);
  },
);
