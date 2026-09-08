vi.hoisted(() => vi.stubEnv("VITE_API_URL", "http://template-catalog.test"));
import type { ReactNode } from "react";
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
import { ReceiptItemForm } from "./ReceiptItemForm";
import ItemTemplates from "@/pages/ItemTemplates";
import NormalizedDescriptions from "@/pages/NormalizedDescriptions";
import { createAppQueryClient } from "@/lib/query-client";
import { clearTokens, setTokens } from "@/lib/auth";
import {
  clearServerErrorPageFlag,
  markServerErrorPageShown,
} from "@/lib/server-error-bus";
import "@/test/setup-combobox-polyfills";
const template = {
  id: "template",
  name: "Milk template",
  description: "Original description",
  defaultCategory: "Food",
  defaultSubcategory: "Dairy",
  defaultUnitPrice: 3.459,
  defaultItemCode: "MILK",
};
const defaults = {
  receiptId: "receipt",
  receiptItemCode: "MILK",
  description: "Manual milk",
  quantity: 1,
  unitPrice: 3.459,
  category: "Food",
  subcategory: "Dairy",
};
const normalized = {
  id: "raw",
  canonicalName: "Raw milk",
  displayName: "Raw milk",
  displayLabel: null,
  status: "pendingReview",
  createdAt: "2025-01-01T00:00:00Z",
  linkedItemCount: 1,
  sampleRawDescriptions: ["RAW MILK"],
  lastSeen: null,
  nearestNeighbourName: null,
  nearestNeighbourSimilarity: null,
  linkedTemplateId: null,
  linkedTemplateName: null,
  linkedTemplateCount: 0,
};
function deferred() {
  let resolve!: () => void;
  const promise = new Promise<void>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}
let catalogFailure: boolean;
let linkFailure: boolean;
let searchedCatalog: boolean;
let catalogRequests: string[];
let emptyCatalog: boolean;
let holdCatalog: boolean;
let catalogGate: ReturnType<typeof deferred>;
let catalogStarted: boolean;
let catalogReads: number;
let linkWrites: unknown[];
let otherWrites: string[];
const server = setupServer(
  http.get("*/api/item-templates", async ({ request }) => {
    catalogStarted = true;
    catalogReads++;
    const search = new URL(request.url).searchParams.get("q") ?? "";
    catalogRequests.push(search);
    const failed = catalogFailure;
    if (holdCatalog) await catalogGate.promise;
    const data = emptyCatalog
      ? []
      : searchedCatalog && search
        ? [{ ...template, id: "other-template", name: "Different template" }]
        : [template];
    return failed
      ? HttpResponse.json(
          { status: 503, detail: "Template catalog unavailable" },
          { status: 503 },
        )
      : HttpResponse.json({
          data,
          total: data.length,
          offset: 0,
          limit: Number(new URL(request.url).searchParams.get("limit") ?? 50),
        });
  }),
  http.get("*/api/item-templates/history-candidates", () =>
    HttpResponse.json({ data: [], total: 0, offset: 0, limit: 10 }),
  ),
  http.get("*/api/categories", () =>
    HttpResponse.json({
      data: [{ id: "food", name: "Food", isActive: true }],
      total: 1,
      offset: 0,
      limit: 500,
    }),
  ),
  http.get("*/api/subcategories", () =>
    HttpResponse.json({
      data: [
        { id: "dairy", categoryId: "food", name: "Dairy", isActive: true },
      ],
      total: 1,
      offset: 0,
      limit: 500,
    }),
  ),
  http.get("*/api/receipt-items/suggestions", () => HttpResponse.json([])),
  http.get("*/api/metadata/enums", () =>
    HttpResponse.json({
      adjustmentTypes: [],
      authEventTypes: [],
      auditActions: [],
      entityTypes: [],
    }),
  ),
  http.get("*/api/normalized-descriptions", () =>
    HttpResponse.json({ items: [normalized], totalCount: 1 }),
  ),
  http.post(
    "*/api/normalized-descriptions/raw/link-template",
    async ({ request }) => {
      const body = (await request.json()) as { itemTemplateId: string };
      linkWrites.push(body);
      if (linkFailure)
        return HttpResponse.json(
          { status: 503, detail: "Link request unavailable" },
          { status: 503 },
        );
      const name =
        body.itemTemplateId === "other-template"
          ? "Different template"
          : template.name;
      return HttpResponse.json({
        merged: true,
        itemsRelinkedCount: 1,
        description: {
          ...normalized,
          displayName: name,
          canonicalName: name,
        },
      });
    },
  ),
  http.post("*/api/:entity", ({ params }) => {
    otherWrites.push(String(params.entity));
    return HttpResponse.json({ id: "unexpected" });
  }),
);
const clients: ReturnType<typeof createAppQueryClient>[] = [];
const routers: ReturnType<typeof createMemoryRouter>[] = [];
function renderFeature(feature: ReactNode) {
  const queryClient = createAppQueryClient();
  clients.push(queryClient);
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
        <AuthProvider queryClientFactory={() => queryClient}>
          <RouterProvider router={router} />
        </AuthProvider>
      </TooltipProvider>
    </AppearanceProvider>,
  );
  return { queryClient, router };
}
function noGlobalFailure() {
  expect(
    screen.queryByRole("heading", { name: "Global server error route" }),
  ).not.toBeInTheDocument();
  expect(
    document.querySelectorAll('[data-sonner-toast][data-type="error"]'),
  ).toHaveLength(0);
}
beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
afterAll(() => server.close());
beforeEach(() => {
  localStorage.clear();
  clearServerErrorPageFlag();
  setTokens(
    `header.${btoa(JSON.stringify({ sub: "admin", email: "admin@example.test", role: "Admin", exp: 4102444800 }))}.signature`,
    "refresh",
  );
  catalogFailure = false;
  linkFailure = false;
  searchedCatalog = false;
  catalogRequests = [];
  emptyCatalog = false;
  holdCatalog = false;
  catalogGate = deferred();
  catalogStarted = false;
  catalogReads = 0;
  linkWrites = [];
  otherWrites = [];
});
afterEach(() => {
  catalogGate.resolve();
  cleanup();
  routers.splice(0).forEach((router) => router.dispose());
  clients.splice(0).forEach((client) => client.clear());
  toast.dismiss();
  clearTokens();
  server.resetHandlers();
});
it("retains a receipt-item draft after held optional template503 and allows manual submission", async () => {
  holdCatalog = true;
  catalogFailure = true;
  const submitted = vi.fn();
  const { router } = renderFeature(
    <ReceiptItemForm
      mode="edit"
      hideReceiptField
      location="Manual market"
      defaultValues={defaults}
      onSubmit={submitted}
      onCancel={() => {}}
    />,
  );
  const user = userEvent.setup();
  await waitFor(() => expect(catalogStarted).toBe(true));
  await user.clear(screen.getByLabelText(/^Description/));
  await user.type(
    screen.getByLabelText(/^Description/),
    "Retained receipt draft",
  );
  await act(async () => {
    catalogGate.resolve();
  });
  await waitFor(() => expect(clients[0].isFetching()).toBe(0));
  expect(router.state.location.pathname).toBe("/");
  noGlobalFailure();
  expect(screen.getByLabelText(/^Description/)).toHaveValue(
    "Retained receipt draft",
  );
  expect(await screen.findByRole("alert")).toHaveTextContent(/template/i);
  await user.click(screen.getByRole("button", { name: "Update Item" }));
  await waitFor(() =>
    expect(submitted).toHaveBeenCalledWith({
      ...defaults,
      description: "Retained receipt draft",
    }),
  );
  expect(otherWrites).toEqual([]);
});
it("keeps successful empty optional template lookup compatible with manual receipt values", async () => {
  emptyCatalog = true;
  const submitted = vi.fn();
  const { queryClient } = renderFeature(
    <ReceiptItemForm
      mode="edit"
      hideReceiptField
      location="Manual market"
      defaultValues={defaults}
      onSubmit={submitted}
      onCancel={() => {}}
    />,
  );
  await waitFor(() => expect(queryClient.isFetching()).toBe(0));
  await userEvent
    .setup()
    .click(screen.getByRole("button", { name: "Update Item" }));
  await waitFor(() => expect(submitted).toHaveBeenCalledWith(defaults));
  noGlobalFailure();
  expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  expect(catalogReads).toBe(1);
  expect(otherWrites).toEqual([]);
});
it("retains catalog search, selection and an edited dialog when its list refresh fails", async () => {
  const { queryClient, router } = renderFeature(<ItemTemplates />);
  const user = userEvent.setup();
  const row = (await screen.findByText("Milk template")).closest("tr")!;
  await user.click(screen.getByRole("checkbox", { name: "Select all rows" }));
  await user.type(
    screen.getByRole("textbox", { name: "Search item templates" }),
    "Milk",
  );
  await user.click(within(row).getByRole("button", { name: "Edit" }));
  const dialog = screen.getByRole("dialog", { name: "Edit Item Template" });
  await user.clear(within(dialog).getByLabelText("Description (optional)"));
  await user.type(
    within(dialog).getByLabelText("Description (optional)"),
    "Retained template draft",
  );
  catalogFailure = true;
  await act(async () => {
    await queryClient.invalidateQueries({
      queryKey: ["itemTemplates", "list"],
    });
  });
  expect(router.state.location.pathname).toBe("/");
  expect(dialog).toBeInTheDocument();
  expect(within(dialog).getByLabelText("Description (optional)")).toHaveValue(
    "Retained template draft",
  );
  noGlobalFailure();
  await user.click(within(dialog).getByRole("button", { name: "Cancel" }));
  expect(
    screen.getByRole("textbox", { name: "Search item templates" }),
  ).toHaveValue("Milk");
  expect(screen.getByRole("button", { name: /Delete \(1\)/ })).toBeVisible();
  expect(await screen.findByRole("alert")).toHaveTextContent(/template/i);
  catalogFailure = false;
  await user.click(screen.getByRole("button", { name: "Retry" }));
  await waitFor(() =>
    expect(screen.queryByRole("alert")).not.toBeInTheDocument(),
  );
  expect(screen.getByRole("cell", { name: "Milk template" })).toBeVisible();
  expect(otherWrites).toEqual([]);
});
async function openLinkDialog() {
  const { queryClient, router } = renderFeature(<NormalizedDescriptions />);
  const user = userEvent.setup();
  const row = (await screen.findByText("Raw milk")).closest("tr")!;
  await user.click(
    within(row).getByRole("button", { name: "Link to template…" }),
  );
  const dialog = await screen.findByRole("dialog", {
    name: "Link to an Item Template",
  });
  await user.click(
    await within(dialog).findByRole("radio", { name: /^Milk template/ }),
  );
  expect(
    within(dialog).getByTestId("link-template-consequence"),
  ).toHaveTextContent(/consolidated/i);
  return { queryClient, router, user, dialog };
}
it("does not confirm a cached link target after its verification read fails", async () => {
  const { queryClient, user, dialog } = await openLinkDialog();
  // The first global error page has already been shown in this tab. This exercises
  // the confirmation guard independently of the first-error navigation defect.
  markServerErrorPageShown();
  catalogFailure = true;
  await act(async () => {
    await queryClient.invalidateQueries({
      queryKey: ["itemTemplates", "list"],
    });
  });
  await user.click(within(dialog).getByRole("button", { name: "Link" }));
  await waitFor(() => expect(queryClient.isMutating()).toBe(0));
  expect(linkWrites).toEqual([]);
  expect(dialog).toBeInTheDocument();
  expect(within(dialog).getByRole("button", { name: "Link" })).toBeDisabled();
});
it("preserves explicit successful template linking and consolidation feedback", async () => {
  const { user, dialog } = await openLinkDialog();
  await user.click(within(dialog).getByRole("button", { name: "Link" }));
  await waitFor(() =>
    expect(linkWrites).toEqual([{ itemTemplateId: "template" }]),
  );
  await waitFor(() => expect(dialog).not.toBeInTheDocument());
  expect(
    await screen.findByText(
      'Consolidated into "Milk template" — 1 item re-linked',
    ),
  ).toBeVisible();
  expect(otherWrites).toEqual([]);
});

it("blocks link confirmation while a selected target's background verification is pending", async () => {
  const { queryClient, user, dialog } = await openLinkDialog();
  holdCatalog = true;
  let refresh!: Promise<void>;
  act(() => {
    refresh = queryClient.invalidateQueries({
      queryKey: ["itemTemplates", "list"],
    });
  });
  await waitFor(() => expect(queryClient.isFetching()).toBeGreaterThan(0));
  const confirm = within(dialog).getByRole("button", { name: "Link" });
  expect(confirm).toBeDisabled();
  await user.click(confirm);
  expect(linkWrites).toEqual([]);
  await act(async () => {
    catalogGate.resolve();
    await refresh;
  });
  await waitFor(() => expect(confirm).toBeEnabled());
  expect(
    within(dialog).getByRole("radio", { name: /^Milk template/ }),
  ).toBeChecked();
  expect(linkWrites).toEqual([]);
});

it("does not reuse a selected target during search debounce, pending search, or a page that excludes it", async () => {
  const { queryClient, user, dialog } = await openLinkDialog();
  searchedCatalog = true;
  holdCatalog = true;
  await user.type(
    within(dialog).getByLabelText("Search templates"),
    "Different",
  );
  const confirm = within(dialog).getByRole("button", { name: "Link" });
  expect(confirm).toBeDisabled();
  expect(linkWrites).toEqual([]);
  await waitFor(() => expect(catalogRequests).toContain("Different"));
  expect(confirm).toBeDisabled();
  await act(async () => {
    catalogGate.resolve();
  });
  await waitFor(() => expect(queryClient.isFetching()).toBe(0));
  expect(confirm).toBeDisabled();
  await user.click(confirm);
  expect(linkWrites).toEqual([]);
  expect(within(dialog).getByLabelText("Search templates")).toHaveValue(
    "Different",
  );
  await user.click(
    await within(dialog).findByRole("radio", { name: /^Different template/ }),
  );
  expect(confirm).toBeEnabled();
  await user.click(confirm);
  await waitFor(() =>
    expect(linkWrites).toEqual([{ itemTemplateId: "other-template" }]),
  );
  await waitFor(() => expect(dialog).not.toBeInTheDocument());
});

it("keeps the catalog page and a new dialog draft during its first successful held Retry", async () => {
  catalogFailure = true;
  const { queryClient, router } = renderFeature(<ItemTemplates />);
  const user = userEvent.setup();
  const failure = await screen.findByRole("alert");
  noGlobalFailure();
  expect(
    screen.queryByText("No item templates yet. Create one to get started."),
  ).not.toBeInTheDocument();
  catalogFailure = false;
  holdCatalog = true;
  await user.click(within(failure).getByRole("button", { name: "Retry" }));
  await waitFor(() => expect(queryClient.isFetching()).toBeGreaterThan(0));
  await user.click(screen.getByRole("button", { name: "New template" }));
  const dialog = screen.getByRole("dialog", { name: "Create Item Template" });
  await user.type(
    within(dialog).getByLabelText(/^Name/),
    "Unsaved template draft",
  );
  expect(dialog).toBeInTheDocument();
  expect(router.state.location.pathname).toBe("/");
  await act(async () => {
    catalogGate.resolve();
  });
  await waitFor(() => expect(queryClient.isFetching()).toBe(0));
  expect(dialog).toBeInTheDocument();
  expect(within(dialog).getByLabelText(/^Name/)).toHaveValue(
    "Unsaved template draft",
  );
  expect(otherWrites).toEqual([]);
});

it("retains selected link target after one local write failure and permits a deliberate retry", async () => {
  const { queryClient, router, user, dialog } = await openLinkDialog();
  linkFailure = true;
  await user.click(within(dialog).getByRole("button", { name: "Link" }));
  await waitFor(() => expect(queryClient.isMutating()).toBe(0));
  expect(router.state.location.pathname).toBe("/");
  expect(dialog).toBeInTheDocument();
  expect(await screen.findByText("Link request unavailable")).toBeVisible();
  expect(
    document.querySelectorAll('[data-sonner-toast][data-type="error"]'),
  ).toHaveLength(1);
  expect(
    within(dialog).getByRole("radio", { name: /^Milk template/ }),
  ).toBeChecked();
  expect(linkWrites).toEqual([{ itemTemplateId: "template" }]);
  linkFailure = false;
  await user.click(within(dialog).getByRole("button", { name: "Link" }));
  await waitFor(() => expect(dialog).not.toBeInTheDocument());
  expect(linkWrites).toEqual([
    { itemTemplateId: "template" },
    { itemTemplateId: "template" },
  ]);
  expect(
    await screen.findByText(
      'Consolidated into "Milk template" — 1 item re-linked',
    ),
  ).toBeVisible();
});
