vi.hoisted(() => vi.stubEnv("VITE_API_URL", "http://taxonomy-lookups.test"));
import { useState, type ReactNode } from "react";
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
import {
  LineItemsSection,
  type ReceiptLineItem,
} from "@/pages/new-receipt/LineItemsSection";
import { createAppQueryClient } from "@/lib/query-client";
import { clearTokens, setTokens } from "@/lib/auth";
import { clearServerErrorPageFlag } from "@/lib/server-error-bus";
import "@/test/setup-combobox-polyfills";

const categoryId = "95000000-0000-4000-8000-000000000010";
const category = { id: categoryId, name: "Food", isActive: true };
const defaults = {
  receiptId: "receipt",
  receiptItemCode: "MILK",
  description: "Manual milk",
  quantity: 1,
  unitPrice: 2.01,
  category: "Food",
  subcategory: "Fresh dairy",
};
function deferred() {
  let resolve!: () => void;
  const promise = new Promise<void>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}
let subcategoryGate: ReturnType<typeof deferred>;
let subcategoryStarted: boolean;
let categoryFailure: boolean;
let subcategoryFailure: boolean;
let holdSubcategories: boolean;
let inactiveHistoricalName: boolean;
let activeDairy: boolean;
let createGate: ReturnType<typeof deferred>;
let holdCreate: boolean;
let createFailure: boolean;
let writes: unknown[];
let rows: ReceiptLineItem[];
const server = setupServer(
  http.get("*/api/categories", () =>
    categoryFailure
      ? HttpResponse.json(
          { status: 503, detail: "Category lookup unavailable" },
          { status: 503 },
        )
      : HttpResponse.json({
          data: [
            category,
            {
              id: "95000000-0000-4000-8000-000000000020",
              name: "Household",
              isActive: true,
            },
          ],
          total: 2,
          offset: 0,
          limit: 500,
        }),
  ),
  http.get("*/api/subcategories", async ({ request }) => {
    subcategoryStarted = true;
    const scopedCategoryId = new URL(request.url).searchParams.get(
      "categoryId",
    );
    const fail = subcategoryFailure;
    if (holdSubcategories) await subcategoryGate.promise;
    const data =
      inactiveHistoricalName || activeDairy
        ? [
            {
              id: "active-dairy",
              categoryId: scopedCategoryId,
              name: "Dairy",
              isActive: true,
            },
            ...(inactiveHistoricalName &&
            !new URL(request.url).searchParams.has("isActive")
              ? [
                  {
                    id: "inactive-dairy",
                    categoryId: scopedCategoryId,
                    name: "Fresh dairy",
                    isActive: false,
                  },
                ]
              : []),
          ]
        : [];
    return fail
      ? HttpResponse.json(
          { status: 503, detail: "Subcategory lookup unavailable" },
          { status: 503 },
        )
      : HttpResponse.json({ data, total: data.length, offset: 0, limit: 500 });
  }),
  http.post("*/api/subcategories", async ({ request }) => {
    const body = await request.json();
    writes.push(body);
    const failed = createFailure;
    if (holdCreate) await createGate.promise;
    if (failed)
      return HttpResponse.json(
        { status: 503, detail: "Taxonomy save unavailable" },
        { status: 503 },
      );
    return HttpResponse.json({
      id: "created-subcategory",
      ...(body as object),
    });
  }),
  http.get("*/api/item-templates", () =>
    HttpResponse.json({ data: [], total: 0, offset: 0, limit: 500 }),
  ),
  http.get("*/api/item-templates/similar", () => HttpResponse.json([])),
  http.get("*/api/item-templates/category-suggestions", () =>
    HttpResponse.json([]),
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
function Lines({
  edit,
  secondRow = false,
}: {
  edit: boolean;
  secondRow?: boolean;
}) {
  const [items, setItems] = useState<ReceiptLineItem[]>(
    edit
      ? [
          { id: "line", ...defaults, subcategory: "" },
          ...(secondRow
            ? [
                {
                  ...defaults,
                  id: "second-line",
                  description: "Second milk",
                  subcategory: "Dairy",
                },
              ]
            : []),
        ]
      : [],
  );
  return (
    <LineItemsSection
      items={items}
      onChange={(next) => {
        rows = next;
        setItems(next);
      }}
      location="Manual market"
    />
  );
}
type Consumer = "item-form" | "line-add" | "line-edit";
async function prepare(consumer: Consumer) {
  const submitted = vi.fn();
  const mounted = renderFeature(
    consumer === "item-form" ? (
      <ReceiptItemForm
        mode="edit"
        hideReceiptField
        location="Manual market"
        defaultValues={defaults}
        onSubmit={submitted}
        onCancel={() => {}}
      />
    ) : (
      <Lines edit={consumer === "line-edit"} />
    ),
  );
  const user = userEvent.setup();
  await waitFor(() =>
    expect(
      mounted.queryClient.getQueryState(["categories", "all", true])?.status,
    ).toBe("success"),
  );
  if (consumer === "line-add") {
    await user.click(screen.getByText("Select category..."));
    await user.click(await screen.findByRole("option", { name: "Food" }));
    await user.type(
      screen.getByPlaceholderText("Item description"),
      "Manual milk",
    );
    await user.clear(screen.getByLabelText(/^Unit Price/));
    await user.type(screen.getByLabelText(/^Unit Price/), "2.01");
  } else if (consumer === "line-edit") {
    await user.click(screen.getByRole("button", { name: "Edit" }));
    await user.clear(screen.getByLabelText("Edit description"));
    await user.type(screen.getByLabelText("Edit description"), "Edited milk");
  } else {
    await user.clear(screen.getByLabelText(/^Description/));
    await user.type(screen.getByLabelText(/^Description/), "Edited milk");
  }
  await waitFor(() => expect(subcategoryStarted).toBe(true));
  return { ...mounted, user, submitted };
}
function subcategoryPicker(consumer: Consumer) {
  return consumer === "line-edit"
    ? screen.getByRole("combobox", { name: "Edit subcategory" })
    : screen.getByRole("combobox", { name: /^Subcategory/ });
}
async function selectManualSubcategory(
  consumer: Consumer,
  user: ReturnType<typeof userEvent.setup>,
) {
  await user.click(subcategoryPicker(consumer));
  await user.type(
    screen.getByPlaceholderText("Search subcategories..."),
    "Fresh dairy",
  );
  await user.click(screen.getByRole("button", { name: 'Use "Fresh dairy"' }));
}
async function saveManual(
  consumer: Consumer,
  user: ReturnType<typeof userEvent.setup>,
  submitted: ReturnType<typeof vi.fn>,
  expectedCategory = "Food",
  subcategoryAlreadySelected = false,
) {
  if (consumer === "item-form") {
    await user.click(screen.getByRole("button", { name: "Update Item" }));
    await waitFor(() =>
      expect(submitted).toHaveBeenCalledWith({
        ...defaults,
        description: "Edited milk",
        category: expectedCategory,
      }),
    );
  } else {
    if (!subcategoryAlreadySelected)
      await selectManualSubcategory(consumer, user);
    await user.click(
      screen.getByRole("button", {
        name: consumer === "line-edit" ? "Save" : "Add Item",
      }),
    );
    await waitFor(() =>
      expect(rows).toEqual([
        expect.objectContaining({
          description: consumer === "line-edit" ? "Edited milk" : "Manual milk",
          category: expectedCategory,
          subcategory: "Fresh dairy",
          quantity: 1,
          unitPrice: 2.01,
        }),
      ]),
    );
  }
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
  subcategoryGate = deferred();
  subcategoryStarted = false;
  categoryFailure = false;
  subcategoryFailure = false;
  holdSubcategories = false;
  inactiveHistoricalName = false;
  activeDairy = false;
  createGate = deferred();
  holdCreate = false;
  createFailure = false;
  writes = [];
  rows = [];
});
afterEach(() => {
  subcategoryGate.resolve();
  createGate.resolve();
  cleanup();
  routers.splice(0).forEach((router) => router.dispose());
  clients.splice(0).forEach((client) => client.clear());
  toast.dismiss();
  clearTokens();
  server.resetHandlers();
});

for (const consumer of ["item-form", "line-add", "line-edit"] as const) {
  it(`${consumer} preserves valid manual text while a pending lookup cannot prove taxonomy absence`, async () => {
    holdSubcategories = true;
    const { user, submitted, queryClient } = await prepare(consumer);
    await saveManual(consumer, user, submitted);
    expect(writes).toEqual([]);
    await act(async () => {
      holdSubcategories = false;
      subcategoryGate.resolve();
    });
    await waitFor(() => expect(queryClient.isFetching()).toBe(0));
    expect(writes).toEqual([]); // Successful completion must not schedule a deferred creation.
  });
  it(`${consumer} does not reuse the prior parent's successful absence proof while the replacement lookup is pending`, async () => {
    const { user, submitted, queryClient } = await prepare(consumer);
    await waitFor(() => expect(queryClient.isFetching()).toBe(0));
    holdSubcategories = true;
    subcategoryStarted = false;
    await user.click(
      screen.getByRole("combobox", {
        name: consumer === "line-edit" ? "Edit category" : /^Category/,
      }),
    );
    await user.click(await screen.findByRole("option", { name: "Household" }));
    await waitFor(() => expect(subcategoryStarted).toBe(true));
    if (consumer === "item-form") {
      await user.click(screen.getByRole("combobox", { name: /^Subcategory/ }));
      await user.type(
        screen.getByPlaceholderText("Search subcategories..."),
        "Fresh dairy",
      );
      await user.click(
        screen.getByRole("button", { name: 'Use "Fresh dairy"' }),
      );
    }
    await saveManual(consumer, user, submitted, "Household");
    expect(writes).toEqual([]);
    await act(async () => {
      holdSubcategories = false;
      subcategoryGate.resolve();
    });
    await waitFor(() => expect(queryClient.isFetching()).toBe(0));
    expect(writes).toEqual([]);
  });
  it(`${consumer} preserves successful-empty automatic subcategory creation`, async () => {
    const { user, submitted, queryClient } = await prepare(consumer);
    await waitFor(() => expect(queryClient.isFetching()).toBe(0));
    await saveManual(consumer, user, submitted);
    await waitFor(() =>
      expect(writes).toEqual([
        { name: "Fresh dairy", categoryId, isActive: true },
      ]),
    );
  });
  it(`${consumer} preserves an inactive historical name without attempting a conflicting taxonomy create`, async () => {
    inactiveHistoricalName = true;
    const { user, submitted, queryClient } = await prepare(consumer);
    await waitFor(() => expect(queryClient.isFetching()).toBe(0));
    await user.click(subcategoryPicker(consumer));
    expect(screen.getByRole("option", { name: "Dairy" })).toBeVisible();
    expect(
      screen.queryByRole("option", { name: "Fresh dairy" }),
    ).not.toBeInTheDocument();
    await user.keyboard("{Escape}");
    await saveManual(consumer, user, submitted);
    expect(writes).toEqual([]);
  });
  it.each(["category", "subcategory"] as const)(
    `${consumer} recovers a cached %s failure through Retry without deferring a taxonomy write`,
    async (failedFamily) => {
      const { user, submitted, queryClient } = await prepare(consumer);
      await waitFor(() => expect(queryClient.isFetching()).toBe(0));
      categoryFailure = failedFamily === "category";
      subcategoryFailure = failedFamily === "subcategory";
      await act(async () => {
        await queryClient.invalidateQueries({
          queryKey: [
            failedFamily === "category" ? "categories" : "subcategories",
          ],
        });
      });
      const failure = (await screen.findAllByRole("alert")).find((alert) =>
        alert.textContent?.startsWith(
          failedFamily === "category"
            ? "Category choices"
            : "Subcategory choices",
        ),
      );
      expect(failure).toBeDefined();
      if (consumer === "item-form") await saveManual(consumer, user, submitted);
      else await selectManualSubcategory(consumer, user);
      expect(writes).toEqual([]);
      categoryFailure = false;
      subcategoryFailure = false;
      await user.click(within(failure!).getByRole("button", { name: "Retry" }));
      await waitFor(() =>
        expect(screen.queryByRole("alert")).not.toBeInTheDocument(),
      );
      await waitFor(() => expect(queryClient.isFetching()).toBe(0));
      expect(writes).toEqual([]);
      expect(subcategoryPicker(consumer)).toHaveTextContent("Fresh dairy");
      if (consumer !== "item-form")
        await saveManual(consumer, user, submitted, "Food", true);
      expect(writes).toEqual([]);
    },
  );
  it.each(["category", "subcategory"] as const)(
    `${consumer} keeps its draft and skips automatic creation after %s503`,
    async (failedFamily) => {
      const { user, submitted, queryClient, router } = await prepare(consumer);
      await waitFor(() => expect(queryClient.isFetching()).toBe(0));
      categoryFailure = failedFamily === "category";
      subcategoryFailure = failedFamily === "subcategory";
      await act(async () => {
        await queryClient.invalidateQueries({
          queryKey: [
            failedFamily === "category" ? "categories" : "subcategories",
          ],
        });
      });
      expect(router.state.location.pathname).toBe("/");
      expect(
        screen.queryByRole("heading", { name: "Global server error route" }),
      ).not.toBeInTheDocument();
      const alerts = await screen.findAllByRole("alert");
      expect(
        alerts.some((alert) => /unavailable/i.test(alert.textContent ?? "")),
      ).toBe(true);
      expect(
        alerts.some((alert) =>
          within(alert).queryByRole("button", { name: "Retry" }),
        ),
      ).toBe(true);
      expect(
        document.querySelectorAll('[data-sonner-toast][data-type="error"]'),
      ).toHaveLength(0);
      await saveManual(consumer, user, submitted);
      expect(writes).toEqual([]);
    },
  );
}

it.each(["line-add", "line-edit"] as const)(
  "%s keeps a newer parent-scoped choice when an obsolete automatic-create request fails",
  async (consumer) => {
    activeDairy = true;
    const { user, queryClient } = await prepare(consumer);
    await waitFor(() => expect(queryClient.isFetching()).toBe(0));
    holdCreate = true;
    createFailure = true;
    await selectManualSubcategory(consumer, user);
    await waitFor(() =>
      expect(writes).toEqual([
        { name: "Fresh dairy", categoryId, isActive: true },
      ]),
    );
    await user.click(
      screen.getByRole("combobox", {
        name: consumer === "line-edit" ? "Edit category" : /^Category/,
      }),
    );
    await user.click(await screen.findByRole("option", { name: "Household" }));
    await waitFor(() => expect(queryClient.isFetching()).toBe(0));
    await user.click(subcategoryPicker(consumer));
    await user.click(await screen.findByRole("option", { name: "Dairy" }));
    expect(subcategoryPicker(consumer)).toHaveTextContent("Dairy");
    expect(writes).toHaveLength(1);
    await act(async () => {
      createGate.resolve();
    });
    await waitFor(() => expect(queryClient.isMutating()).toBe(0));
    expect(subcategoryPicker(consumer)).toHaveTextContent("Dairy");
    expect(
      screen.getByRole("combobox", {
        name: consumer === "line-edit" ? "Edit category" : /^Category/,
      }),
    ).toHaveTextContent("Household");
    expect(writes).toHaveLength(1);
  },
);

it("keeps a newer row's edit draft when the previous row's automatic-create request fails", async () => {
  activeDairy = true;
  const { queryClient } = renderFeature(<Lines edit secondRow />);
  const user = userEvent.setup();
  await waitFor(() => expect(queryClient.isFetching()).toBe(0));
  await user.click(screen.getAllByRole("button", { name: "Edit" })[0]);
  await waitFor(() => expect(subcategoryStarted).toBe(true));
  await waitFor(() => expect(queryClient.isFetching()).toBe(0));
  holdCreate = true;
  createFailure = true;
  await selectManualSubcategory("line-edit", user);
  await waitFor(() => expect(writes).toHaveLength(1));
  await user.click(screen.getByRole("button", { name: "Cancel" }));
  await user.click(screen.getAllByRole("button", { name: "Edit" })[1]);
  await user.clear(screen.getByLabelText("Edit description"));
  await user.type(
    screen.getByLabelText("Edit description"),
    "New second-row draft",
  );
  expect(subcategoryPicker("line-edit")).toHaveTextContent("Dairy");
  await act(async () => {
    createGate.resolve();
  });
  await waitFor(() => expect(queryClient.isMutating()).toBe(0));
  expect(subcategoryPicker("line-edit")).toHaveTextContent("Dairy");
  expect(screen.getByLabelText("Edit description")).toHaveValue(
    "New second-row draft",
  );
  await user.click(screen.getByRole("button", { name: "Save" }));
  expect(rows.find((row) => row.id === "second-line")).toEqual(
    expect.objectContaining({
      description: "New second-row draft",
      category: "Food",
      subcategory: "Dairy",
    }),
  );
  expect(writes).toHaveLength(1);
});

it("retains the item form draft and handles a rejected automatic taxonomy create without an unhandled submit promise", async () => {
  const unhandled: unknown[] = [];
  const recordUnhandled = (reason: unknown) => unhandled.push(reason);
  process.on("unhandledRejection", recordUnhandled);
  try {
    const { user, submitted, queryClient } = await prepare("item-form");
    await waitFor(() => expect(queryClient.isFetching()).toBe(0));
    holdCreate = true;
    createFailure = true;
    await user.click(screen.getByRole("button", { name: "Update Item" }));
    await waitFor(() => expect(writes).toHaveLength(1));
    expect(submitted).not.toHaveBeenCalled();
    await act(async () => {
      createGate.resolve();
    });
    await waitFor(() => expect(queryClient.isMutating()).toBe(0));
    expect(await screen.findByText("Taxonomy save unavailable")).toBeVisible();
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(
      document.querySelectorAll('[data-sonner-toast][data-type="error"]'),
    ).toHaveLength(1);
    expect(screen.getByLabelText(/^Description/)).toHaveValue("Edited milk");
    expect(subcategoryPicker("item-form")).toHaveTextContent("Fresh dairy");
    expect(submitted).not.toHaveBeenCalled();
    expect(unhandled).toEqual([]);
  } finally {
    process.off("unhandledRejection", recordUnhandled);
  }
});
