vi.hoisted(() => vi.stubEnv("VITE_API_URL", "http://taxonomy-lookups.test"));
import { useEffect, useState, type ReactNode } from "react";
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

import { taxonomyFixture, dairyClassification } from "@/test/taxonomy-fixture";
import { chooseCategoryOption } from "@/test/category-picker";

const defaults = {
  receiptId: "receipt",
  receiptItemCode: "MILK",
  description: "Manual milk",
  quantity: 1,
  unitPrice: 2.01,
  category: "Food",
  subcategory: "Dairy",
};
function deferred() {
  let resolve!: () => void;
  const promise = new Promise<void>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}
let gate: ReturnType<typeof deferred>;
let hold = false;
let failure = false;
let writes: string[];
let rows: ReceiptLineItem[];
const server = setupServer(
  http.get("*/api/taxonomy", async () => {
    const fail = failure;
    if (hold) await gate.promise;
    return fail
      ? HttpResponse.json(
          { status: 503, detail: "Taxonomy unavailable" },
          { status: 503 },
        )
      : HttpResponse.json(taxonomyFixture);
  }),
  http.all("*/api/categories", ({ request }) => {
    writes.push(request.method);
    return HttpResponse.json({});
  }),
  http.all("*/api/subcategories", ({ request }) => {
    writes.push(request.method);
    return HttpResponse.json({});
  }),
  http.get("*/api/item-templates", () =>
    HttpResponse.json({ data: [], total: 0, offset: 0, limit: 50 }),
  ),
  http.get("*/api/item-templates/similar", () => HttpResponse.json([])),
  http.get("*/api/item-templates/category-suggestions", () =>
    HttpResponse.json([]),
  ),
  http.get("*/api/receipt-items/suggestions", () => HttpResponse.json([])),
  http.get("*/api/receipt-items/normalization-candidates", () =>
    HttpResponse.json([]),
  ),
  http.post("*/api/receipt-items/normalization-preview", () =>
    HttpResponse.json({
      sourceFingerprint: "taxonomy-draft",
      sourceAmbiguous: false,
      excluded: false,
      candidates: [],
      total: 0,
      offset: 0,
      limit: 10,
    }),
  ),
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
          { id: "line", ...defaults },
          ...(secondRow
            ? [{ id: "second", ...defaults, description: "Second milk" }]
            : []),
        ]
      : [],
  );
  useEffect(() => {
    rows = items;
  }, [items]);
  return (
    <LineItemsSection
      items={items}
      onChange={setItems}
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
        currentClassification={dairyClassification}
        onSubmit={submitted}
        onCancel={() => {}}
      />
    ) : (
      <Lines edit={consumer === "line-edit"} />
    ),
  );
  const user = userEvent.setup();
  if (consumer === "line-edit")
    await user.click(screen.getByRole("button", { name: "Edit" }));
  const description =
    consumer === "item-form"
      ? screen.getByLabelText(/^Description/)
      : consumer === "line-edit"
        ? screen.getByLabelText("Edit description")
        : screen.getByPlaceholderText("Item description");
  await user.clear(description);
  await user.type(description, "Retained milk");
  if (consumer === "line-add") {
    await user.clear(screen.getByLabelText(/^Unit Price/));
    await user.type(screen.getByLabelText(/^Unit Price/), "2.01");
  }
  const picker = screen.getByRole("combobox", {
    name: consumer === "line-edit" ? "Edit category" : /^Category/,
  });
  return { ...mounted, submitted, user, description, picker };
}
async function select(
  context: Awaited<ReturnType<typeof prepare>>,
  label = "Food / Dairy",
) {
  await waitFor(() => expect(context.picker).toBeEnabled());
  await context.user.click(context.picker);
  await chooseCategoryOption(context.user, label);
}
async function save(
  consumer: Consumer,
  context: Awaited<ReturnType<typeof prepare>>,
) {
  await context.user.click(
    screen.getByRole("button", {
      name:
        consumer === "item-form"
          ? "Update Item"
          : consumer === "line-edit"
            ? "Save"
            : "Add Item",
    }),
  );
  await waitFor(() =>
    expect(
      consumer === "item-form"
        ? context.submitted.mock.calls.length
        : rows.length,
    ).toBe(1),
  );
  return consumer === "item-form"
    ? context.submitted.mock.calls[0][0]
    : rows[0];
}
beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
afterAll(() => server.close());
beforeEach(() => {
  localStorage.clear();
  clearServerErrorPageFlag();
  setTokens(
    `header.${btoa(JSON.stringify({ sub: "alice", email: "alice@example.test", exp: 4102444800 }))}.signature`,
    "refresh",
  );
  gate = deferred();
  hold = false;
  failure = false;
  writes = [];
  rows = [];
});
afterEach(() => {
  gate.resolve();
  cleanup();
  routers.splice(0).forEach((r) => r.dispose());
  clients.splice(0).forEach((c) => c.clear());
  toast.dismiss();
  clearTokens();
  server.resetHandlers();
});
for (const consumer of ["item-form", "line-add", "line-edit"] as const) {
  it(`${consumer} retains the money and description draft during a held taxonomy lookup`, async () => {
    hold = true;
    const c = await prepare(consumer);
    expect(c.picker).toBeDisabled();
    expect(c.description).toHaveValue("Retained milk");
    expect(writes).toEqual([]);
    await act(async () => {
      hold = false;
      gate.resolve();
    });
    await select(c);
    const value = await save(consumer, c);
    expect(value).toMatchObject({
      description: "Retained milk",
      quantity: 1,
      unitPrice: 2.01,
      category: "Food",
      subcategory: "Dairy",
    });
    expect(writes).toEqual([]);
  });
  it(`${consumer} owns an initial taxonomy failure locally and recovers through Retry`, async () => {
    failure = true;
    const c = await prepare(consumer);
    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent(/categor/i);
    expect(c.router.state.location.pathname).toBe("/");
    expect(c.picker).toBeDisabled();
    expect(c.description).toHaveValue("Retained milk");
    expect(
      document.querySelectorAll('[data-sonner-toast][data-type="error"]'),
    ).toHaveLength(0);
    failure = false;
    await c.user.click(within(alert).getByRole("button", { name: "Retry" }));
    await select(c);
    await save(consumer, c);
    expect(writes).toEqual([]);
  });
  it(`${consumer} keeps a deliberate nested selection through a failed background refresh`, async () => {
    const c = await prepare(consumer);
    await select(c);
    failure = true;
    await act(async () => {
      await c.queryClient.invalidateQueries({
        queryKey: ["taxonomy", "current"],
      });
    });
    expect(c.picker).toHaveAttribute("title", "Food / Dairy");
    expect(within(c.picker).getByText("Dairy")).toBeVisible();
    expect(within(c.picker).getByText("Food")).toBeVisible();
    await waitFor(() => expect(c.picker).toBeDisabled());
    expect(c.description).toHaveValue("Retained milk");
    expect(c.router.state.location.pathname).toBe("/");
    expect(writes).toEqual([]);
  });
  it(`${consumer} retries cached taxonomy failure without deferring a taxonomy write`, async () => {
    const c = await prepare(consumer);
    await select(c);
    failure = true;
    await act(async () => {
      await c.queryClient.invalidateQueries({
        queryKey: ["taxonomy", "current"],
      });
    });
    const alert = await screen.findByRole("alert");
    failure = false;
    hold = true;
    await c.user.click(within(alert).getByRole("button", { name: "Retry" }));
    expect(c.description).toHaveValue("Retained milk");
    await act(async () => {
      hold = false;
      gate.resolve();
    });
    await waitFor(() => expect(c.picker).toBeEnabled());
    expect(c.picker).toHaveAttribute("title", "Food / Dairy");
    expect(within(c.picker).getByText("Dairy")).toBeVisible();
    expect(within(c.picker).getByText("Food")).toBeVisible();
    await save(consumer, c);
    expect(writes).toEqual([]);
  });
  it(`${consumer} stores category identity and visible revision on a deliberate path selection`, async () => {
    const c = await prepare(consumer);
    await select(c, "Food");
    const value = await save(consumer, c);
    expect(
      consumer === "item-form" ? value.classification : value.categoryChoice,
    ).toMatchObject({
      categoryId: taxonomyFixture.nodes[0].id,
      taxonomyRevisionId: taxonomyFixture.id,
    });
    expect(writes).toEqual([]);
  });
  it(`${consumer} does not silently assign an empty taxonomy result`, async () => {
    server.use(
      http.get("*/api/taxonomy", () =>
        HttpResponse.json({ ...taxonomyFixture, nodes: [] }),
      ),
    );
    const c = await prepare(consumer);
    await waitFor(() => expect(c.picker).toBeEnabled());
    await c.user.click(c.picker);
    expect(screen.getAllByRole("option")).toHaveLength(1);
    expect(screen.getByRole("option", { name: "Unclassified" })).toBeVisible();
    await c.user.keyboard("{Escape}");
    expect(c.description).toHaveValue("Retained milk");
    expect(writes).toEqual([]);
  });
  it(`${consumer} excludes archived paths without changing its saved draft`, async () => {
    server.use(
      http.get("*/api/taxonomy", () =>
        HttpResponse.json({
          ...taxonomyFixture,
          nodes: taxonomyFixture.nodes.map((n) => ({
            ...n,
            archived: n.name === "Dairy",
          })),
        }),
      ),
    );
    const c = await prepare(consumer);
    await waitFor(() => expect(c.picker).toBeEnabled());
    await c.user.click(c.picker);
    await c.user.type(
      screen.getByRole("combobox", { name: "Search category paths" }),
      "Dairy",
    );
    expect(screen.queryByRole("option")).not.toBeInTheDocument();
    await c.user.keyboard("{Escape}");
    expect(c.description).toHaveValue("Retained milk");
    expect(writes).toEqual([]);
  });
  it(`${consumer} replaces a parent choice with a nested choice without automatic creation`, async () => {
    const c = await prepare(consumer);
    await select(c, "Food");
    await select(c);
    expect(c.picker).toHaveAttribute("title", "Food / Dairy");
    expect(within(c.picker).getByText("Dairy")).toBeVisible();
    expect(within(c.picker).getByText("Food")).toBeVisible();
    const value = await save(consumer, c);
    expect(value).toMatchObject({ category: "Food", subcategory: "Dairy" });
    expect(writes).toEqual([]);
  });
  it(`${consumer} explicitly clears classification through Unclassified`, async () => {
    const c = await prepare(consumer);
    await select(c, "Unclassified");
    const value = await save(consumer, c);
    expect(
      consumer === "item-form" ? value.classification : value.categoryChoice,
    ).toMatchObject({
      categoryId: null,
      taxonomyRevisionId: taxonomyFixture.id,
    });
    expect(writes).toEqual([]);
  });
}
it("keeps a newer row edit draft when an earlier held refresh fails", async () => {
  const { queryClient } = renderFeature(<Lines edit secondRow />);
  const user = userEvent.setup();
  await waitFor(() => expect(queryClient.isFetching()).toBe(0));
  await user.click(screen.getAllByRole("button", { name: "Edit" })[0]);
  failure = true;
  hold = true;
  let refresh!: Promise<void>;
  act(() => {
    refresh = queryClient.invalidateQueries({
      queryKey: ["taxonomy", "current"],
    });
  });
  await user.click(screen.getByRole("button", { name: "Cancel" }));
  await user.click(screen.getAllByRole("button", { name: "Edit" })[1]);
  await user.clear(screen.getByLabelText("Edit description"));
  await user.type(
    screen.getByLabelText("Edit description"),
    "New second-row draft",
  );
  await act(async () => {
    hold = false;
    gate.resolve();
    await refresh;
  });
  expect(screen.getByLabelText("Edit description")).toHaveValue(
    "New second-row draft",
  );
  await user.click(screen.getByRole("button", { name: "Save" }));
  expect(rows.find((r) => r.id === "second")).toMatchObject({
    description: "New second-row draft",
    category: "Food",
    subcategory: "Dairy",
  });
  expect(writes).toEqual([]);
});
