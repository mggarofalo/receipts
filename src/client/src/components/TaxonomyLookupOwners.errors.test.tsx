vi.hoisted(() => vi.stubEnv("VITE_API_URL", "http://taxonomy-owners.test"));
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
import { ItemTemplateForm } from "./ItemTemplateForm";
import { SubcategoryForm } from "./SubcategoryForm";
import Subcategories from "@/pages/Subcategories";
import UncategorizedItems from "./reports/UncategorizedItems";
import { createAppQueryClient } from "@/lib/query-client";
import { clearTokens, setTokens } from "@/lib/auth";
import { clearServerErrorPageFlag } from "@/lib/server-error-bus";
import "@/test/setup-combobox-polyfills";
let categoryFailure: boolean;
let scopedFailure: boolean;
let categoryReads: number;
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
    categoryReads++;
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
  categoryReads = 0;
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
const template = {
  name: "Manual template",
  description: "Original",
  defaultCategory: "Food",
  defaultSubcategory: "Historic dairy",
  defaultUnitPrice: 3.459,
  defaultItemCode: "MILK",
};
const subcategory = {
  name: "Dairy",
  categoryId: "food",
  description: "Original",
  isActive: true,
};
function form(
  kind: "template" | "subcategory",
  submit: (value: unknown) => void,
) {
  return kind === "template" ? (
    <ItemTemplateForm
      mode="edit"
      defaultValues={template}
      onSubmit={submit}
      onCancel={() => {}}
    />
  ) : (
    <SubcategoryForm
      mode="edit"
      defaultValues={subcategory}
      onSubmit={submit}
      onCancel={() => {}}
    />
  );
}
function noGlobalFailure() {
  expect(
    screen.queryByRole("heading", { name: "Global server error route" }),
  ).not.toBeInTheDocument();
  expect(
    document.querySelectorAll('[data-sonner-toast][data-type="error"]'),
  ).toHaveLength(0);
}
it.each(["template", "subcategory"] as const)(
  "retains the %s form draft and valid manual submission through category failure and retry",
  async (kind) => {
    const submitted = vi.fn();
    const client = renderFeature(form(kind, submitted));
    await waitFor(() =>
      expect(
        client
          .getQueryCache()
          .findAll({ queryKey: ["categories", "all"] })
          .every((query) => query.state.status === "success"),
      ).toBe(true),
    );
    const user = userEvent.setup();
    await user.clear(screen.getByLabelText("Description (optional)"));
    await user.type(
      screen.getByLabelText("Description (optional)"),
      "Retained draft",
    );
    categoryFailure = true;
    await act(async () => {
      await client.invalidateQueries({ queryKey: ["categories", "all"] });
    });
    const failure = await screen.findByRole("alert");
    expect(failure).toHaveTextContent(/categor/i);
    noGlobalFailure();
    expect(screen.getByLabelText("Description (optional)")).toHaveValue(
      "Retained draft",
    );
    expect(
      screen
        .getAllByRole("combobox")
        .some((select) => select.textContent?.includes("Food")),
    ).toBe(true);
    await user.click(
      screen.getByRole("button", {
        name: kind === "template" ? "Update Template" : "Update Subcategory",
      }),
    );
    await waitFor(() =>
      expect(submitted.mock.calls.map(([value]) => value)).toEqual([
        {
          ...(kind === "template" ? template : subcategory),
          description: "Retained draft",
        },
      ]),
    );
    categoryFailure = false;
    await user.click(within(failure).getByRole("button", { name: "Retry" }));
    await waitFor(() =>
      expect(screen.queryByRole("alert")).not.toBeInTheDocument(),
    );
    expect(screen.getByLabelText("Description (optional)")).toHaveValue(
      "Retained draft",
    );
    expect(categoryReads).toBe(3);
    expect(writes).toEqual([]);
  },
);
it.each(["template", "subcategory"] as const)(
  "keeps successful %s lookup and submit behavior",
  async (kind) => {
    const submitted = vi.fn();
    const client = renderFeature(form(kind, submitted));
    await waitFor(() => expect(client.isFetching()).toBe(0));
    await userEvent.setup().click(
      screen.getByRole("button", {
        name: kind === "template" ? "Update Template" : "Update Subcategory",
      }),
    );
    await waitFor(() =>
      expect(submitted.mock.calls.map(([value]) => value)).toEqual([
        kind === "template" ? template : subcategory,
      ]),
    );
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
    noGlobalFailure();
  },
);
it("keeps historical template subcategory text on a scoped failure and restores choices without resetting it", async () => {
  const client = renderFeature(form("template", vi.fn()));
  await waitFor(() =>
    expect(
      client.getQueryState(["subcategories", "byCategory", "all", "food", true])
        ?.status,
    ).toBe("success"),
  );
  scopedFailure = true;
  await act(async () => {
    await client.invalidateQueries({
      queryKey: ["subcategories", "byCategory", "all", "food"],
    });
  });
  const failure = await screen.findByRole("alert");
  expect(failure).toHaveTextContent(/subcategor/i);
  noGlobalFailure();
  expect(
    screen.getByRole("combobox", { name: "Default Subcategory (optional)" }),
  ).toHaveTextContent("Historic dairy");
  scopedFailure = false;
  await userEvent
    .setup()
    .click(within(failure).getByRole("button", { name: "Retry" }));
  await waitFor(() =>
    expect(screen.queryByRole("alert")).not.toBeInTheDocument(),
  );
  expect(
    screen.getByRole("combobox", { name: "Default Subcategory (optional)" }),
  ).toHaveTextContent("Historic dairy");
  expect(writes).toEqual([]);
});
it("keeps the Subcategories table, search and open edit dialog after a category lookup fails", async () => {
  const client = renderFeature(<Subcategories />);
  const user = userEvent.setup();
  await user.click(await screen.findByRole("button", { name: "Expand Food" }));
  await user.type(
    screen.getByRole("textbox", { name: "Search subcategories" }),
    "Dairy",
  );
  const row = screen.getByText("Dairy").closest("tr")!;
  await user.click(within(row).getByRole("button", { name: "Edit" }));
  const dialog = screen.getByRole("dialog", { name: "Edit Subcategory" });
  await user.clear(within(dialog).getByLabelText("Description (optional)"));
  await user.type(
    within(dialog).getByLabelText("Description (optional)"),
    "Dialog draft",
  );
  categoryFailure = true;
  await act(async () => {
    await client.invalidateQueries({ queryKey: ["categories", "all"] });
  });
  const failure = await within(dialog).findByRole("alert");
  noGlobalFailure();
  expect(within(dialog).getByLabelText("Description (optional)")).toHaveValue(
    "Dialog draft",
  );
  categoryFailure = false;
  await user.click(within(failure).getByRole("button", { name: "Retry" }));
  await waitFor(() =>
    expect(within(dialog).queryByRole("alert")).not.toBeInTheDocument(),
  );
  expect(within(dialog).getByLabelText("Description (optional)")).toHaveValue(
    "Dialog draft",
  );
  await user.click(within(dialog).getByRole("button", { name: "Cancel" }));
  expect(
    screen.getByRole("textbox", { name: "Search subcategories" }),
  ).toHaveValue("Dairy");
  expect(screen.getByText("Dairy")).toBeVisible();
  expect(writes).toEqual([]);
});
it.each(["category", "subcategory"] as const)(
  "keeps report rows, selection and taxonomy values through %s failure and retry",
  async (kind) => {
    const client = renderFeature(<UncategorizedItems />);
    const user = userEvent.setup();
    await screen.findByText("Unsorted milk");
    await user.click(
      screen.getByRole("checkbox", { name: "Select all items on this page" }),
    );
    await user.click(screen.getAllByRole("combobox")[0]);
    await user.click(await screen.findByRole("option", { name: "Food" }));
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
    await user.click(screen.getAllByRole("combobox")[1]);
    await user.click(await screen.findByRole("option", { name: "Dairy" }));
    categoryFailure = kind === "category";
    scopedFailure = kind === "subcategory";
    await act(async () => {
      await client.invalidateQueries({
        queryKey:
          kind === "category"
            ? ["categories", "all"]
            : ["subcategories", "byCategory", "all", "food"],
      });
    });
    const failure = await screen.findByRole("alert");
    noGlobalFailure();
    expect(screen.getByText("Unsorted milk")).toBeVisible();
    expect(screen.getByText("1 selected")).toBeVisible();
    expect(screen.getAllByRole("combobox")[0]).toHaveTextContent("Food");
    expect(screen.getAllByRole("combobox")[1]).toHaveTextContent("Dairy");
    categoryFailure = false;
    scopedFailure = false;
    await user.click(within(failure).getByRole("button", { name: "Retry" }));
    await waitFor(() =>
      expect(screen.queryByRole("alert")).not.toBeInTheDocument(),
    );
    expect(screen.getByText("1 selected")).toBeVisible();
    expect(screen.getAllByRole("combobox")[1]).toHaveTextContent("Dairy");
    expect(writes).toEqual([]);
  },
);

it.each(["category", "subcategory"] as const)(
  "treats a failed second %s page as unavailable, then retries the entire scoped lookup",
  async (kind) => {
    let secondPageFails = true;
    const requests: {
      offset: number;
      active: string | null;
      parent: string | null;
    }[] = [];
    const pageOne = Array.from({ length: 500 }, (_, index) => ({
      id: `entry-${index}`,
      name: `Entry ${index}`,
      isActive: true,
      ...(kind === "subcategory" ? { categoryId: "food" } : {}),
    }));
    const finalEntry = {
      id: "last",
      name: "Final option",
      isActive: true,
      ...(kind === "subcategory" ? { categoryId: "food" } : {}),
    };
    server.use(
      http.get(
        kind === "category" ? "*/api/categories" : "*/api/subcategories",
        ({ request }) => {
          const query = new URL(request.url).searchParams;
          const offset = Number(query.get("offset"));
          requests.push({
            offset,
            active: query.get("isActive"),
            parent: query.get("categoryId"),
          });
          if (offset === 500 && secondPageFails)
            return HttpResponse.json(
              { status: 503, detail: "Second lookup page unavailable" },
              { status: 503 },
            );
          return HttpResponse.json({
            data: offset === 0 ? pageOne : [finalEntry],
            total: 501,
            offset,
            limit: 500,
          });
        },
      ),
    );
    const client = renderFeature(form("template", vi.fn()));
    const failure = await screen.findByRole("alert");
    noGlobalFailure();
    const key =
      kind === "category"
        ? ["categories", "all", true]
        : ["subcategories", "byCategory", "all", "food", true];
    expect(client.getQueryData(key)).toBeUndefined();
    expect(screen.getByLabelText(/^Name/)).toHaveValue(template.name);
    expect(requests.map((request) => request.offset)).toEqual([0, 500]);
    secondPageFails = false;
    await userEvent
      .setup()
      .click(within(failure).getByRole("button", { name: "Retry" }));
    await waitFor(() => expect(client.getQueryData(key)).toHaveLength(501));
    expect(requests.map((request) => request.offset)).toEqual([0, 500, 0, 500]);
    expect(
      requests.every(
        (request) =>
          request.active === "true" &&
          request.parent === (kind === "subcategory" ? "food" : null),
      ),
    ).toBe(true);
    expect(screen.getByLabelText(/^Name/)).toHaveValue(template.name);
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
    expect(writes).toEqual([]);
  },
);

it.each(["template", "report"] as const)(
  "removes the %s owner's obsolete scoped Retry after selecting another parent",
  async (kind) => {
    const client = renderFeature(
      kind === "template" ? form("template", vi.fn()) : <UncategorizedItems />,
    );
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

it("keeps loaded subcategory rows and an edited dialog mounted while the first successful category lookup is still pending", async () => {
  categoryFailure = true;
  const client = renderFeature(<Subcategories />);
  const user = userEvent.setup();
  await screen.findByRole("alert");
  await user.click(
    await screen.findByRole("button", { name: "Expand Unknown" }),
  );
  const table = screen.getByRole("table");
  const row = screen.getByText("Dairy").closest("tr")!;
  await user.click(within(row).getByRole("button", { name: "Edit" }));
  const dialog = screen.getByRole("dialog", { name: "Edit Subcategory" });
  const failure = await within(dialog).findByRole("alert");
  await user.clear(within(dialog).getByLabelText("Description (optional)"));
  await user.type(
    within(dialog).getByLabelText("Description (optional)"),
    "Pending retry draft",
  );
  let release!: () => void;
  let started = false;
  const held = new Promise<void>((resolve) => {
    release = resolve;
  });
  server.use(
    http.get("*/api/categories", async () => {
      started = true;
      await held;
      return HttpResponse.json({
        data: categories,
        total: categories.length,
        offset: 0,
        limit: 500,
      });
    }),
  );
  try {
    await user.click(within(failure).getByRole("button", { name: "Retry" }));
    await waitFor(() => expect(started).toBe(true));
    expect(
      client.getQueryData(["categories", "all", undefined]),
    ).toBeUndefined();
    expect(client.getQueryState(["categories", "all", undefined])?.status).toBe(
      "pending",
    );
    expect(table).toBeInTheDocument();
    expect(dialog).toBeInTheDocument();
    expect(within(dialog).getByLabelText("Description (optional)")).toHaveValue(
      "Pending retry draft",
    );
    await act(async () => {
      release();
      await held;
    });
    await waitFor(() =>
      expect(within(dialog).queryByRole("alert")).not.toBeInTheDocument(),
    );
    expect(within(dialog).getByLabelText("Description (optional)")).toHaveValue(
      "Pending retry draft",
    );
    expect(table).toBeInTheDocument();
    expect(writes).toEqual([]);
  } finally {
    release();
  }
});
