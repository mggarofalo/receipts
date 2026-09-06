vi.hoisted(() => vi.stubEnv("VITE_API_URL", "http://receipt-suggestions.test"));
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
import { ReceiptHeaderForm } from "./ReceiptHeaderForm";
import { ReceiptItemForm } from "./ReceiptItemForm";
import { TemplateHistorySuggestions } from "./TemplateHistorySuggestions";
import { ReceiptForm } from "./ReceiptForm";
import {
  LineItemsSection,
  type ReceiptLineItem,
} from "@/pages/new-receipt/LineItemsSection";
import { createAppQueryClient } from "@/lib/query-client";
import { clearTokens, setTokens } from "@/lib/auth";
import { clearServerErrorPageFlag } from "@/lib/server-error-bus";
import { locationHistory } from "@/lib/location-history";
import "@/test/setup-combobox-polyfills";

let locationFailure: boolean;
let locationReads: number;
let locationOptions: string[];
let similarFailure: boolean;
let similarReads: string[];
let emptySimilar: boolean;
let writes: string[];
let itemFailure: boolean;
let itemReads: { code: string; location: string | null }[];
let categoryFailure: boolean;
let categoryReads: string[];
let historyFailure: boolean;
let historyReads: number;
const server = setupServer(
  http.get("*/api/receipts/locations", () => {
    locationReads++;
    return locationFailure
      ? HttpResponse.json(
          { status: 503, detail: "Locations unavailable" },
          { status: 503 },
        )
      : HttpResponse.json({ locations: locationOptions });
  }),
  http.get("*/api/item-templates/similar", ({ request }) => {
    similarReads.push(new URL(request.url).searchParams.get("q")!);
    return similarFailure
      ? HttpResponse.json(
          { status: 503, detail: "Similarity unavailable" },
          { status: 503 },
        )
      : HttpResponse.json(
          emptySimilar
            ? []
            : [
                {
                  name: "Milk from history",
                  source: "history",
                  combinedScore: 0.95,
                  defaultCategory: "Food",
                  defaultUnitPrice: 2.01,
                },
              ],
        );
  }),
  http.get("*/api/item-templates/category-suggestions", ({ request }) => {
    categoryReads.push(new URL(request.url).searchParams.get("q")!);
    return categoryFailure
      ? HttpResponse.json(
          { status: 503, detail: "Category hints unavailable" },
          { status: 503 },
        )
      : HttpResponse.json([]);
  }),
  http.get("*/api/receipt-items/suggestions", ({ request }) => {
    const q = new URL(request.url).searchParams;
    itemReads.push({ code: q.get("itemCode")!, location: q.get("location") });
    return itemFailure
      ? HttpResponse.json(
          { status: 503, detail: "Item hints unavailable" },
          { status: 503 },
        )
      : HttpResponse.json([]);
  }),
  http.get("*/api/item-templates/history-candidates", () => {
    historyReads++;
    return historyFailure
      ? HttpResponse.json(
          { status: 503, detail: "History unavailable" },
          { status: 503 },
        )
      : HttpResponse.json({
          data: [
            {
              name: "History milk",
              occurrenceCount: 3,
              lastPurchasedAt: "2024-01-15",
              suggestedCategory: "Food",
              suggestedSubcategory: "Dairy",
              suggestedUnitPrice: 2.01,
              suggestedItemCode: "MILK",
            },
          ],
          total: 1,
          offset: 0,
          limit: 10,
        });
  }),
  http.get("*/api/item-templates", () =>
    HttpResponse.json({ data: [], total: 0, offset: 0, limit: 50 }),
  ),
  http.get("*/api/accounts", () =>
    HttpResponse.json({ data: [], total: 0, offset: 0, limit: 50 }),
  ),
  http.get("*/api/metadata/enums", () =>
    HttpResponse.json({
      adjustmentTypes: [],
      authEventTypes: [],
      auditActions: [],
      entityTypes: [],
    }),
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
  http.post("*/api/:entity", ({ params }) => {
    writes.push(String(params.entity));
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
  return queryClient;
}
beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
afterAll(() => server.close());
beforeEach(() => {
  locationFailure = false;
  locationReads = 0;
  locationOptions = ["Old API market"];
  similarFailure = false;
  similarReads = [];
  emptySimilar = false;
  writes = [];
  itemFailure = false;
  itemReads = [];
  categoryFailure = false;
  categoryReads = [];
  historyFailure = false;
  historyReads = 0;
  clearServerErrorPageFlag();
  localStorage.clear();
  setTokens(
    `header.${btoa(JSON.stringify({ sub: "alice", email: "alice@example.test", exp: 4102444800 }))}.signature`,
    "alice-refresh",
  );
  locationHistory.addEntry("Local MRU market");
});
afterEach(() => {
  cleanup();
  routers.splice(0).forEach((router) => router.dispose());
  clients.splice(0).forEach((queryClient) => queryClient.clear());
  toast.dismiss();
  clearTokens();
  server.resetHandlers();
});
const defaults = {
  location: "Manually entered market",
  date: "2024-01-15",
  taxAmount: 0,
};
function receiptForm(
  kind: "header" | "receipt",
  submit: (data: unknown) => void,
) {
  return kind === "header" ? (
    <ReceiptHeaderForm
      defaultValues={defaults}
      onSubmit={submit}
      onCancel={() => {}}
    />
  ) : (
    <ReceiptForm
      mode="edit"
      defaultValues={defaults}
      onSubmit={submit}
      onCancel={() => {}}
    />
  );
}

it.each(["header", "receipt"] as const)(
  "keeps the %s draft, manual submit and MRU while failed location hints recover through inline Retry",
  async (kind) => {
    const submitted = vi.fn();
    const queryClient = renderFeature(receiptForm(kind, submitted));
    await waitFor(() =>
      expect(
        queryClient.getQueryState(["receipts", "locations", ""])?.status,
      ).toBe("success"),
    );
    const user = userEvent.setup();
    await user.clear(screen.getByLabelText("Tax Amount"));
    await user.type(screen.getByLabelText("Tax Amount"), "17.42");
    locationFailure = true;
    await act(async () => {
      await queryClient.invalidateQueries({
        queryKey: ["receipts", "locations"],
      });
    });
    expect(
      screen.queryByRole("heading", { name: "Global server error route" }),
    ).not.toBeInTheDocument();
    expect(screen.getByLabelText("Tax Amount")).toHaveValue("17.42");
    const failure = await screen.findByRole("alert");
    expect(failure).toHaveTextContent(/location/i);
    expect(
      within(failure).getByRole("button", { name: "Retry" }),
    ).toBeEnabled();
    expect(
      document.querySelectorAll('[data-sonner-toast][data-type="error"]'),
    ).toHaveLength(0);
    await user.click(screen.getByRole("combobox"));
    expect(
      await screen.findByRole("option", { name: "Local MRU market" }),
    ).toBeVisible();
    expect(
      screen.getByRole("option", { name: "Old API market" }),
    ).toBeVisible();
    await user.keyboard("{Escape}");
    await user.click(screen.getByRole("button", { name: "Update Receipt" }));
    await waitFor(() =>
      expect(submitted.mock.calls.map(([value]) => value)).toEqual([
        { ...defaults, taxAmount: 17.42 },
      ]),
    );
    locationFailure = false;
    locationOptions = ["Recovered API market"];
    await user.click(within(failure).getByRole("button", { name: "Retry" }));
    await waitFor(() =>
      expect(screen.queryByRole("alert")).not.toBeInTheDocument(),
    );
    expect(screen.getByLabelText("Tax Amount")).toHaveValue("17.42");
    await user.click(screen.getByRole("combobox"));
    expect(
      await screen.findByRole("option", { name: "Recovered API market" }),
    ).toBeVisible();
    expect(
      screen.getByRole("option", { name: "Local MRU market" }),
    ).toBeVisible();
    expect(locationReads).toBe(3);
    expect(writes).toEqual([]);
  },
);

it("treats successful empty location hints as optional and keeps manual submission usable", async () => {
  locationOptions = [];
  const submitted = vi.fn();
  const queryClient = renderFeature(receiptForm("header", submitted));
  await waitFor(() =>
    expect(
      queryClient.getQueryState(["receipts", "locations", ""])?.status,
    ).toBe("success"),
  );
  expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  await userEvent
    .setup()
    .click(screen.getByRole("button", { name: "Update Receipt" }));
  await waitFor(() =>
    expect(submitted.mock.calls.map(([value]) => value)).toEqual([defaults]),
  );
  expect(locationReads).toBe(1);
});
function Lines() {
  const [items, setItems] = useState<ReceiptLineItem[]>([]);
  return (
    <LineItemsSection
      items={items}
      onChange={setItems}
      location="Manual market"
    />
  );
}
async function enterLine() {
  const user = userEvent.setup();
  await user.click(screen.getByText("Select category..."));
  await user.click(await screen.findByRole("option", { name: "Food" }));
  await user.clear(screen.getByLabelText(/^Unit Price/));
  await user.type(screen.getByLabelText(/^Unit Price/), "2.01");
  await user.type(screen.getByPlaceholderText("Item description"), "Milk");
  return user;
}
it("keeps a line draft during similarity503 and restores hints through a usable retry outside the popup", async () => {
  similarFailure = true;
  const queryClient = renderFeature(<Lines />);
  const user = await enterLine();
  await waitFor(() =>
    expect(
      queryClient
        .getQueryCache()
        .findAll({ queryKey: ["similarItems"] })
        .some((query) => query.state.status === "error"),
    ).toBe(true),
  );
  expect(
    screen.queryByRole("heading", { name: "Global server error route" }),
  ).not.toBeInTheDocument();
  expect(screen.getByPlaceholderText("Item description")).toHaveValue("Milk");
  expect(screen.getByLabelText(/^Unit Price/)).toHaveValue("2.0100");
  const failure = await screen.findByRole("alert");
  expect(failure).toHaveTextContent(/similar|suggest/i);
  expect(screen.queryByText("No similar items found")).not.toBeInTheDocument();
  similarFailure = false;
  await user.click(within(failure).getByRole("button", { name: "Retry" }));
  await waitFor(() =>
    expect(screen.queryByRole("alert")).not.toBeInTheDocument(),
  );
  expect(screen.getByPlaceholderText("Item description")).toHaveValue("Milk");
  expect(screen.getByLabelText(/^Unit Price/)).toHaveValue("2.0100");
  await user.click(screen.getByPlaceholderText("Item description"));
  expect(await screen.findByText("Milk from history")).toBeVisible();
  expect(similarReads).toEqual(["Milk", "Milk"]);
  expect(writes).toEqual([]);
  expect(
    document.querySelectorAll('[data-sonner-toast][data-type="error"]'),
  ).toHaveLength(0);
});
it("allows adding a manually entered line while similarity hints remain unavailable", async () => {
  similarFailure = true;
  const queryClient = renderFeature(<Lines />);
  const user = await enterLine();
  await waitFor(() =>
    expect(
      queryClient
        .getQueryCache()
        .findAll({ queryKey: ["similarItems"] })
        .some((query) => query.state.status === "error"),
    ).toBe(true),
  );
  expect(
    screen.queryByRole("heading", { name: "Global server error route" }),
  ).not.toBeInTheDocument();
  await user.click(screen.getByRole("button", { name: "Add Item" }));
  expect(await screen.findByRole("cell", { name: "Milk" })).toBeVisible();
  expect(screen.getAllByRole("cell", { name: "$2.01" })).toHaveLength(2);
  expect(writes).toEqual([]);
});
it("shows a successful empty similarity result and still adds a manual line", async () => {
  emptySimilar = true;
  renderFeature(<Lines />);
  const user = await enterLine();
  expect(await screen.findByText("No similar items found")).toBeVisible();
  expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  await user.click(screen.getByRole("button", { name: "Add Item" }));
  expect(await screen.findByRole("cell", { name: "Milk" })).toBeVisible();
  expect(writes).toEqual([]);
});

it.each([true, false])(
  "shares local location ownership regardless of observer order (header first:%s)",
  async (headerFirst) => {
    locationFailure = true;
    const header = (
      <div data-testid="header">{receiptForm("header", vi.fn())}</div>
    );
    const receipt = (
      <div data-testid="receipt">{receiptForm("receipt", vi.fn())}</div>
    );
    renderFeature(
      <>
        {headerFirst ? header : receipt}
        {headerFirst ? receipt : header}
      </>,
    );
    await waitFor(() => expect(screen.getAllByRole("alert")).toHaveLength(2));
    expect(locationReads).toBe(1);
    expect(
      screen.queryByRole("heading", { name: "Global server error route" }),
    ).not.toBeInTheDocument();
    locationFailure = false;
    await userEvent.setup().click(
      within(screen.getByTestId("header")).getByRole("button", {
        name: "Retry",
      }),
    );
    await waitFor(() =>
      expect(screen.queryByRole("alert")).not.toBeInTheDocument(),
    );
    expect(locationReads).toBe(2);
  },
);

it("hides obsolete similarity Retry while input changes, becomes short or clears", async () => {
  similarFailure = true;
  renderFeature(<Lines />);
  const user = await enterLine();
  await screen.findByRole("alert");
  const description = screen.getByPlaceholderText("Item description");
  await user.type(description, "shake");
  expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  expect(similarReads).toEqual(["Milk"]);
  await screen.findByRole("alert");
  expect(similarReads).toEqual(["Milk", "Milkshake"]);
  await user.clear(description);
  await user.type(description, "M");
  expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  await waitFor(() =>
    expect(
      clients[0].getQueryCache().findAll({ queryKey: ["similarItems", "M"] }),
    ).toHaveLength(1),
  );
  expect(similarReads).toEqual(["Milk", "Milkshake"]);
  await user.clear(description);
  expect(
    screen.queryByRole("button", { name: "Retry" }),
  ).not.toBeInTheDocument();
});

it("does not turn a failed background refresh of empty similarity data into successful no-results", async () => {
  emptySimilar = true;
  const queryClient = renderFeature(<Lines />);
  await enterLine();
  await screen.findByText("No similar items found");
  similarFailure = true;
  await act(async () => {
    await queryClient.invalidateQueries({ queryKey: ["similarItems"] });
  });
  expect(await screen.findByRole("alert")).toHaveTextContent(
    /description suggestions/i,
  );
  expect(screen.queryByText("No similar items found")).not.toBeInTheDocument();
  expect(screen.getByPlaceholderText("Item description")).toHaveValue("Milk");
});

it("owns category failures only while a valid description needs a category, allowing manual selection", async () => {
  categoryFailure = true;
  emptySimilar = true;
  renderFeature(<Lines />);
  const user = userEvent.setup();
  const description = screen.getByPlaceholderText("Item description");
  await user.type(description, "Milk");
  expect(await screen.findByRole("alert")).toHaveTextContent(
    /category suggestions/i,
  );
  await user.type(description, "shake");
  expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  await screen.findByRole("alert");
  expect(categoryReads).toEqual(["Milk", "Milkshake"]);
  await user.click(screen.getByText("Select category..."));
  await user.click(await screen.findByRole("option", { name: "Food" }));
  expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  expect(
    screen.queryByRole("button", { name: "Retry" }),
  ).not.toBeInTheDocument();
  expect(description).toHaveValue("Milkshake");
  expect(writes).toEqual([]);
});

it("retries category hints without assigning a category or creating taxonomy", async () => {
  categoryFailure = true;
  emptySimilar = true;
  renderFeature(<Lines />);
  const user = userEvent.setup();
  await user.type(screen.getByPlaceholderText("Item description"), "Milk");
  const failure = await screen.findByRole("alert");
  categoryFailure = false;
  await user.click(within(failure).getByRole("button", { name: "Retry" }));
  await waitFor(() =>
    expect(screen.queryByRole("alert")).not.toBeInTheDocument(),
  );
  expect(screen.getByText("Select category...")).toBeVisible();
  expect(categoryReads).toEqual(["Milk", "Milk"]);
  expect(writes).toEqual([]);
});

const itemDefaults = {
  receiptId: "receipt",
  receiptItemCode: "",
  description: "Manual milk",
  quantity: 1,
  unitPrice: 2.01,
  category: "Food",
  subcategory: "Dairy",
};
it.each(["edit", "new"] as const)(
  "retains %s item values and location-scoped item-code Retry outside its popup",
  async (kind) => {
    itemFailure = true;
    const submitted = vi.fn();
    renderFeature(
      kind === "edit" ? (
        <ReceiptItemForm
          mode="edit"
          hideReceiptField
          location="Manual market"
          defaultValues={itemDefaults}
          onSubmit={submitted}
          onCancel={() => {}}
        />
      ) : (
        <Lines />
      ),
    );
    const user = kind === "new" ? await enterLine() : userEvent.setup();
    const code = screen.getByPlaceholderText(
      kind === "edit" ? "Enter item code..." : "e.g. MILK-GAL",
    );
    await user.type(code, "MILK");
    expect(await screen.findByRole("alert")).toHaveTextContent(
      /item code suggestions/i,
    );
    expect(code).toHaveValue("MILK");
    expect(itemReads).toEqual([{ code: "MILK", location: "Manual market" }]);
    await user.type(code, "-X");
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
    await screen.findByRole("alert");
    expect(itemReads.at(-1)).toEqual({
      code: "MILK-X",
      location: "Manual market",
    });
    itemFailure = false;
    await user.click(
      within(screen.getByRole("alert")).getByRole("button", { name: "Retry" }),
    );
    await waitFor(() =>
      expect(screen.queryByRole("alert")).not.toBeInTheDocument(),
    );
    expect(itemReads).toHaveLength(3);
    expect(code).toHaveValue("MILK-X");
    expect(writes).toEqual([]);
  },
);

it("rejects stale empty item-code no-results after503 and clears obsolete Retry", async () => {
  const queryClient = renderFeature(<Lines />);
  const user = await enterLine();
  const code = screen.getByPlaceholderText("e.g. MILK-GAL");
  await user.type(code, "MILK");
  await screen.findByText("No suggestions found");
  itemFailure = true;
  await act(async () => {
    await queryClient.invalidateQueries({
      queryKey: ["receiptItemSuggestions"],
    });
  });
  expect(await screen.findByRole("alert")).toHaveTextContent(/item code/i);
  expect(screen.queryByText("No suggestions found")).not.toBeInTheDocument();
  await user.clear(code);
  expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  expect(
    screen.queryByRole("button", { name: "Retry" }),
  ).not.toBeInTheDocument();
});

it("keeps the existing template-history failure owner local and recovers without creating a template", async () => {
  historyFailure = true;
  renderFeature(<TemplateHistorySuggestions />);
  expect(
    await screen.findByText(/Couldn't load suggestions from your history/),
  ).toBeVisible();
  expect(
    screen.queryByRole("heading", { name: "Global server error route" }),
  ).not.toBeInTheDocument();
  expect(
    document.querySelectorAll('[data-sonner-toast][data-type="error"]'),
  ).toHaveLength(0);
  historyFailure = false;
  await userEvent
    .setup()
    .click(screen.getByRole("button", { name: "Try again" }));
  expect(await screen.findByText("History milk")).toBeVisible();
  expect(historyReads).toBe(2);
  expect(writes).toEqual([]);
});

it("submits valid manual item values while its item-code suggestions remain unavailable", async () => {
  itemFailure = true;
  const submitted = vi.fn();
  renderFeature(
    <ReceiptItemForm
      mode="edit"
      hideReceiptField
      location="Manual market"
      defaultValues={itemDefaults}
      onSubmit={submitted}
      onCancel={() => {}}
    />,
  );
  const user = userEvent.setup();
  await user.type(screen.getByPlaceholderText("Enter item code..."), "MILK");
  expect(await screen.findByRole("alert")).toHaveTextContent(/item code/i);
  await user.click(screen.getByRole("button", { name: "Update Item" }));
  await waitFor(() =>
    expect(submitted.mock.calls.map(([value]) => value)).toEqual([
      { ...itemDefaults, receiptItemCode: "MILK" },
    ]),
  );
  expect(itemReads).toHaveLength(1);
  expect(writes).toEqual([]);
});

it("disables the live location Retry while its request is held, retaining values and avoiding duplicate requests", async () => {
  const queryClient = renderFeature(receiptForm("header", vi.fn()));
  await waitFor(() =>
    expect(
      queryClient.getQueryState(["receipts", "locations", ""])?.status,
    ).toBe("success"),
  );
  locationFailure = true;
  await act(async () => {
    await queryClient.invalidateQueries({
      queryKey: ["receipts", "locations"],
    });
  });
  const failure = await screen.findByRole("alert");
  let release!: () => void;
  const held = new Promise<void>((resolve) => {
    release = resolve;
  });
  server.use(
    http.get("*/api/receipts/locations", async () => {
      locationReads++;
      await held;
      return HttpResponse.json({ locations: ["Recovered API market"] });
    }),
  );
  try {
    const user = userEvent.setup();
    await user.click(within(failure).getByRole("button", { name: "Retry" }));
    const pending = await screen.findByRole("button", { name: /Retrying/ });
    expect(pending).toBeDisabled();
    await user.click(pending);
    expect(locationReads).toBe(3);
    expect(screen.getByRole("combobox")).toHaveTextContent(defaults.location);
    await act(async () => {
      release();
      await held;
    });
    await waitFor(() =>
      expect(screen.queryByRole("alert")).not.toBeInTheDocument(),
    );
    expect(locationReads).toBe(3);
  } finally {
    release();
  }
});
