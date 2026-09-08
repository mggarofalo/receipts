vi.hoisted(() => vi.stubEnv("VITE_API_URL", "http://receipt-picker.test"));
import type { ComponentProps } from "react";
import {
  act,
  cleanup,
  fireEvent,
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
import { createAppQueryClient } from "@/lib/query-client";
import { clearTokens, setTokens } from "@/lib/auth";
import { clearServerErrorPageFlag } from "@/lib/server-error-bus";
import "@/test/setup-combobox-polyfills";

function deferred() {
  let resolve!: () => void;
  const promise = new Promise<void>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}
const defaults = {
  receiptId: "receipt-1",
  receiptItemCode: "MILK",
  description: "Manual milk",
  quantity: 1,
  unitPrice: 3.45,
  category: "Food",
  subcategory: "Dairy",
};
let receiptRequests: { offset: number; limit: number; q: string }[];
let receiptCount: number;
let receiptFailure: boolean;
let holdReceipts: boolean;
let receiptGate: ReturnType<typeof deferred>;
let receiptStarted: ReturnType<typeof deferred>;
let writes: string[];
const server = setupServer(
  http.get("*/api/receipts", async ({ request }) => {
    const params = new URL(request.url).searchParams;
    const offset = Number(params.get("offset") ?? 0);
    const limit = Number(params.get("limit") ?? 50);
    receiptRequests.push({ offset, limit, q: params.get("q") ?? "" });
    receiptStarted.resolve();
    if (holdReceipts) await receiptGate.promise;
    if (receiptFailure)
      return HttpResponse.json(
        { status: 503, detail: "Receipt history unavailable" },
        { status: 503 },
      );
    const data = Array.from(
      { length: Math.max(0, Math.min(limit, receiptCount - offset)) },
      (_, i) => ({
        id: `receipt-${offset + i + 1}`,
        location: `Market ${offset + i + 1}`,
        date: "2026-01-15",
        taxAmount: 0,
      }),
    );
    return HttpResponse.json({ data, total: receiptCount, offset, limit });
  }),
  http.get("*/api/receipts/:id", ({ params }) =>
    HttpResponse.json({
      id: params.id,
      location: String(params.id).startsWith("receipt-")
        ? `Market ${String(params.id).slice(8)}`
        : "Market 1",
      date: "2026-01-15",
      taxAmount: 0,
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
  http.get("*/api/item-templates", () =>
    HttpResponse.json({ data: [], total: 0, offset: 0, limit: 50 }),
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
  http.post("*/api/:entity", ({ params }) => {
    writes.push(String(params.entity));
    return HttpResponse.json({ id: "unexpected" });
  }),
);
const clients: ReturnType<typeof createAppQueryClient>[] = [];
const routers: ReturnType<typeof createMemoryRouter>[] = [];
function renderForm(
  props: Partial<ComponentProps<typeof ReceiptItemForm>> = {},
) {
  const queryClient = createAppQueryClient();
  clients.push(queryClient);
  const submitted = vi.fn();
  const router = createMemoryRouter([
    {
      element: <RootLayout />,
      children: [
        {
          path: "/",
          element: (
            <ReceiptItemForm
              mode="create"
              defaultValues={defaults}
              onSubmit={submitted}
              onCancel={() => {}}
              {...props}
            />
          ),
        },
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
  return { queryClient, router, submitted };
}
async function formReady(queryClient: ReturnType<typeof createAppQueryClient>) {
  await waitFor(() =>
    expect(
      queryClient
        .getQueryCache()
        .findAll({ queryKey: ["categories"] })
        .some((query) => query.state.status === "success"),
    ).toBe(true),
  );
  expect(screen.getByLabelText(/^Description/)).toBeInTheDocument();
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
  receiptRequests = [];
  receiptCount = 1;
  receiptFailure = false;
  holdReceipts = false;
  receiptGate = deferred();
  receiptStarted = deferred();
  writes = [];
});
afterEach(() => {
  receiptGate.resolve();
  cleanup();
  routers.splice(0).forEach((router) => router.dispose());
  clients.splice(0).forEach((client) => client.clear());
  toast.dismiss();
  clearTokens();
  server.resetHandlers();
});

it("does not enumerate receipt history before the receipt picker is opened", async () => {
  const { queryClient } = renderForm({
    defaultValues: { ...defaults, receiptId: "" },
  });
  await formReady(queryClient);
  await userEvent.setup().type(screen.getByLabelText(/^Description/), " kept");
  expect(receiptRequests).toEqual([]);
});

it("shows the first bounded page without draining remaining receipt history", async () => {
  receiptCount = 501;
  holdReceipts = true;
  const { queryClient } = renderForm({
    defaultValues: { ...defaults, receiptId: "" },
  });
  await formReady(queryClient);
  await userEvent
    .setup()
    .click(screen.getByRole("combobox", { name: /^Receipt/ }));
  await receiptStarted.promise;
  receiptGate.resolve();
  await waitFor(() =>
    expect(
      queryClient
        .getQueryCache()
        .findAll({ queryKey: ["receipts"] })
        .some(
          (query) =>
            query.state.status === "success" &&
            query.state.fetchStatus === "idle",
        ),
    ).toBe(true),
  );
  expect(receiptRequests).toHaveLength(1);
  expect(receiptRequests[0].limit).toBeLessThanOrEqual(100);
});

it("keeps the actual item draft mounted when an opened receipt lookup returns503", async () => {
  holdReceipts = true;
  receiptFailure = true;
  const { queryClient, router } = renderForm();
  await formReady(queryClient);
  const user = userEvent.setup();
  const description = screen.getByLabelText(/^Description/);
  await user.clear(description);
  await user.type(description, "Unsubmitted manual draft");
  await user.click(screen.getByRole("combobox", { name: /^Receipt/ }));
  await receiptStarted.promise;
  receiptGate.resolve();
  await waitFor(() =>
    expect(
      queryClient
        .getQueryCache()
        .findAll({ queryKey: ["receipts"] })
        .some((query) => query.state.status === "error"),
    ).toBe(true),
  );
  await waitFor(() =>
    expect(
      screen.queryByRole("heading", { name: "Global server error route" }) ||
        screen.queryAllByRole("alert").length,
    ).toBeTruthy(),
  );
  expect(router.state.location.pathname).toBe("/");
  expect(description).toBeInTheDocument();
  expect(description).toHaveValue("Unsubmitted manual draft");
  expect(writes).toEqual([]);
  receiptFailure = false;
  await user.click(
    within(screen.getByRole("alert")).getByRole("button", { name: "Retry" }),
  );
  await waitFor(() => expect(screen.getAllByRole("option")).toHaveLength(1));
  expect(description).toHaveValue("Unsubmitted manual draft");
  expect(router.state.location.pathname).toBe("/");
  expect(receiptRequests).toHaveLength(2);
});

it("does not enumerate history for a hidden receipt field and still submits manual item values", async () => {
  const { queryClient, submitted } = renderForm({
    hideReceiptField: true,
    location: "Known market",
  });
  await formReady(queryClient);
  const user = userEvent.setup();
  await user.clear(screen.getByLabelText(/^Description/));
  await user.type(
    screen.getByLabelText(/^Description/),
    "Manual hidden-parent item",
  );
  await user.click(screen.getByRole("button", { name: /Create Item/i }));
  await waitFor(() => expect(submitted).toHaveBeenCalled());
  expect(submitted.mock.calls[0][0]).toMatchObject({
    ...defaults,
    description: "Manual hidden-parent item",
  });
  expect(receiptRequests).toEqual([]);
  expect(writes).toEqual([]);
});

it("distinguishes successful empty history from failure and preserves required-receipt validation", async () => {
  receiptCount = 0;
  const { queryClient, submitted, router } = renderForm({
    defaultValues: { ...defaults, receiptId: "" },
  });
  await formReady(queryClient);
  const user = userEvent.setup();
  await user.click(screen.getByRole("combobox", { name: /^Receipt/ }));
  await receiptStarted.promise;
  expect(
    await screen.findByText(
      /No (receipts found|matching receipts in the loaded history)/,
    ),
  ).toBeVisible();
  await user.keyboard("{Escape}");
  await user.click(screen.getByRole("button", { name: /Create Item/i }));
  expect(await screen.findByText("Receipt is required")).toBeVisible();
  expect(screen.getByLabelText(/^Description/)).toHaveValue(
    defaults.description,
  );
  expect(submitted).not.toHaveBeenCalled();
  expect(router.state.location.pathname).toBe("/");
  expect(writes).toEqual([]);
});

it("retains the first page when loading more fails and retries only the failed next page", async () => {
  receiptCount = 55;
  let secondPageFailed = true;
  const pages: number[] = [];
  server.use(
    http.get("*/api/receipts", ({ request }) => {
      const offset = Number(
        new URL(request.url).searchParams.get("offset") ?? 0,
      );
      pages.push(offset);
      if (offset === 50 && secondPageFailed)
        return HttpResponse.json(
          { status: 503, detail: "Page unavailable" },
          { status: 503 },
        );
      return HttpResponse.json({
        data: Array.from({ length: offset === 0 ? 50 : 5 }, (_, i) => ({
          id: `receipt-${offset + i + 1}`,
          location: `Market ${offset + i + 1}`,
          date: "2026-01-15",
          taxAmount: 0,
        })),
        total: 55,
        offset,
        limit: 50,
      });
    }),
  );
  const { queryClient, router } = renderForm({
    defaultValues: { ...defaults, receiptId: "" },
  });
  await formReady(queryClient);
  const user = userEvent.setup();
  await user.click(screen.getByRole("combobox", { name: /^Receipt/ }));
  expect(
    await screen.findByRole("button", { name: "Load more receipts" }),
  ).toBeEnabled();
  expect(screen.getAllByRole("option")).toHaveLength(50);
  const loadMore = screen.getByRole("button", { name: "Load more receipts" });
  act(() => loadMore.focus());
  expect(loadMore).toHaveFocus();
  await user.keyboard("{Enter}");
  const alert = await screen.findByRole("alert");
  expect(alert).toHaveTextContent("More receipts could not be loaded");
  expect(screen.getAllByRole("option")).toHaveLength(50);
  expect(router.state.location.pathname).toBe("/");
  expect(pages).toEqual([0, 50]);
  secondPageFailed = false;
  await user.click(within(alert).getByRole("button", { name: "Retry" }));
  await waitFor(() => expect(screen.getAllByRole("option")).toHaveLength(55));
  expect(pages).toEqual([0, 50, 50]);
  expect(
    screen.queryByRole("button", { name: "Load more receipts" }),
  ).not.toBeInTheDocument();
  expect(screen.getByLabelText(/^Description/)).toHaveValue(
    defaults.description,
  );
});

it("loads one near-bottom page and keeps the keyboard selection and selected detail", async () => {
  receiptCount = 55;
  const { queryClient, submitted } = renderForm({
    defaultValues: { ...defaults, receiptId: "" },
  });
  await formReady(queryClient);
  const user = userEvent.setup();
  await user.click(screen.getByRole("combobox", { name: /^Receipt/ }));
  await screen.findByRole("button", { name: "Load more receipts" });
  const list = screen.getByRole("listbox");
  Object.defineProperties(list, {
    scrollHeight: { configurable: true, value: 800 },
    clientHeight: { configurable: true, value: 300 },
    scrollTop: { configurable: true, value: 490 },
  });
  fireEvent.scroll(list);
  await waitFor(() => expect(screen.getAllByRole("option")).toHaveLength(55));
  expect(receiptRequests.map((request) => request.offset)).toEqual([0, 50]);
  await user.click(screen.getByPlaceholderText("Search receipts..."));
  await user.keyboard("{End}{Enter}");
  const trigger = screen.getByRole("combobox", { name: /^Receipt/ });
  expect(trigger).toHaveTextContent("Market 55");
  expect(trigger).toHaveFocus();
  expect(queryClient.getQueryData(["receipts", "receipt-55"])).toEqual({
    id: "receipt-55",
    location: "Market 55",
    date: "2026-01-15",
    taxAmount: 0,
  });
  await user.click(screen.getByRole("button", { name: /Create Item/i }));
  await waitFor(() => expect(submitted).toHaveBeenCalled());
  expect(submitted.mock.calls[0][0]).toMatchObject({
    ...defaults,
    receiptId: "receipt-55",
  });
});

it("keeps loaded ISO-date matches when remote location search returns no rows", async () => {
  const searches: string[] = [];
  server.use(
    http.get("*/api/receipts", ({ request }) => {
      const q = new URL(request.url).searchParams.get("q") ?? "";
      searches.push(q);
      return HttpResponse.json({
        data: q
          ? []
          : [
              {
                id: "dated",
                location: "Dated market",
                date: "2024-03-15",
                taxAmount: 0,
              },
            ],
        total: q ? 0 : 101,
        offset: 0,
        limit: 50,
      });
    }),
  );
  const { queryClient } = renderForm({
    defaultValues: { ...defaults, receiptId: "" },
  });
  await formReady(queryClient);
  const user = userEvent.setup();
  await user.click(screen.getByRole("combobox", { name: /^Receipt/ }));
  await screen.findByRole("option");
  await user.type(
    screen.getByPlaceholderText("Search receipts..."),
    "2024-03-15",
  );
  expect(screen.getByRole("option")).toHaveTextContent("Dated market");
  await waitFor(() => expect(searches).toEqual(["", "2024-03-15"]));
  await waitFor(() =>
    expect(
      queryClient.getQueryState(["receipts", "picker", "search", "2024-03-15"])
        ?.status,
    ).toBe("success"),
  );
  expect(screen.getByRole("option")).toHaveTextContent("Dated market");
  expect(screen.getByRole("status")).toHaveTextContent(
    "Searching loaded receipt dates and all receipt locations",
  );
});

it("finds a remote location beyond the browse page and uses its location for suggestions", async () => {
  const locations: (string | null)[] = [];
  server.use(
    http.get("*/api/receipts", ({ request }) => {
      const q = new URL(request.url).searchParams.get("q") ?? "";
      return HttpResponse.json({
        data: q
          ? [
              {
                id: "far",
                location: "Far market",
                date: "2023-04-01",
                taxAmount: 1.25,
                expectedTotal: 99,
              },
            ]
          : [
              {
                id: "near",
                location: "Near market",
                date: "2026-01-15",
                taxAmount: 0,
              },
            ],
        total: q ? 1 : 100,
        offset: 0,
        limit: 50,
      });
    }),
    http.get("*/api/receipts/far", () =>
      HttpResponse.json({
        id: "far",
        location: "Far market",
        date: "2023-04-01",
        taxAmount: 1.25,
      }),
    ),
    http.get("*/api/receipt-items/suggestions", ({ request }) => {
      locations.push(new URL(request.url).searchParams.get("location"));
      return HttpResponse.json([]);
    }),
  );
  const { queryClient } = renderForm({
    defaultValues: { ...defaults, receiptId: "" },
  });
  await formReady(queryClient);
  const user = userEvent.setup();
  await user.click(screen.getByRole("combobox", { name: /^Receipt/ }));
  await screen.findByRole("option");
  await user.type(screen.getByPlaceholderText("Search receipts..."), "Far");
  const choice = await screen.findByRole("option", { name: /Far market/ });
  await user.click(choice);
  expect(screen.getByRole("combobox", { name: /^Receipt/ })).toHaveTextContent(
    "Far market",
  );
  await user.click(screen.getByPlaceholderText("Enter item code..."));
  await waitFor(() => expect(locations).toContain("Far market"));
  expect(queryClient.getQueryData(["receipts", "far"])).toEqual({
    id: "far",
    location: "Far market",
    date: "2023-04-01",
    taxAmount: 1.25,
  });
});

it.each([404, 503])(
  "hydrates an immutable historical selection after detail%d without enumerating history or discarding the draft",
  async (status) => {
    let detailFailed = true;
    let detailReads = 0;
    server.use(
      http.get("*/api/receipts/:id", ({ params }) => {
        detailReads++;
        return detailFailed
          ? HttpResponse.json(
              { status, detail: "Selected receipt unavailable" },
              { status },
            )
          : HttpResponse.json({
              id: params.id,
              location: "Historical market",
              date: "2020-02-03",
              taxAmount: 0,
            });
      }),
    );
    const { queryClient, submitted, router } = renderForm({
      mode: "edit",
      defaultValues: { ...defaults, receiptId: "historical" },
    });
    await formReady(queryClient);
    const alert = await screen.findByRole("alert");
    const trigger = screen.getByRole("combobox", { name: /^Receipt/ });
    expect(trigger).toBeDisabled();
    expect(trigger).toHaveTextContent("historical");
    expect(screen.getByLabelText(/^Description/)).toHaveValue(
      defaults.description,
    );
    expect(router.state.location.pathname).toBe("/");
    detailFailed = false;
    await userEvent
      .setup()
      .click(within(alert).getByRole("button", { name: "Retry" }));
    await waitFor(() => expect(trigger).toHaveTextContent("Historical market"));
    expect(detailReads).toBe(2);
    expect(receiptRequests).toEqual([]);
    await userEvent
      .setup()
      .click(screen.getByRole("button", { name: /Update Item/i }));
    await waitFor(() => expect(submitted).toHaveBeenCalled());
    expect(submitted.mock.calls[0][0]).toMatchObject({
      ...defaults,
      receiptId: "historical",
    });
  },
);

it("does not present an obsolete next-page failure as the retry for a newly typed search", async () => {
  const searches: string[] = [];
  server.use(
    http.get("*/api/receipts", ({ request }) => {
      const params = new URL(request.url).searchParams;
      const q = params.get("q") ?? "";
      const offset = Number(params.get("offset") ?? 0);
      searches.push(q);
      if (q === "Alpha" && offset > 0)
        return HttpResponse.json(
          { status: 503, detail: "Alpha page failed" },
          { status: 503 },
        );
      return HttpResponse.json({
        data: [
          {
            id: q || "browse",
            location: q ? `${q} market` : "Browse market",
            date: "2026-01-15",
            taxAmount: 0,
          },
        ],
        total: q === "Beta" ? 1 : 100,
        offset,
        limit: 50,
      });
    }),
  );
  const { queryClient } = renderForm({
    defaultValues: { ...defaults, receiptId: "" },
  });
  await formReady(queryClient);
  const user = userEvent.setup();
  await user.click(screen.getByRole("combobox", { name: /^Receipt/ }));
  await screen.findByRole("option");
  const search = screen.getByPlaceholderText("Search receipts...");
  await user.type(search, "Alpha");
  await screen.findByRole("option", { name: /Alpha market/ });
  await user.click(screen.getByRole("button", { name: "Load more receipts" }));
  expect(await screen.findByRole("alert")).toHaveTextContent(
    "More receipts could not be loaded",
  );
  // Synchronous input change asserts the debounce state before yielding to timers.
  fireEvent.change(search, { target: { value: "Beta" } });
  expect(searches).not.toContain("Beta");
  expect(
    screen.queryByText(
      "More receipts could not be loaded. Loaded receipts remain available.",
    ),
  ).not.toBeInTheDocument();
  expect(
    screen.queryByRole("button", { name: "Retry" }),
  ).not.toBeInTheDocument();
  expect(
    await screen.findByRole("option", { name: /Beta market/ }),
  ).toBeVisible();
});

it("reopens a held search into the current query without publishing the old result", async () => {
  const oldStarted = deferred();
  const oldGate = deferred();
  server.use(
    http.get("*/api/receipts", async ({ request }) => {
      const q = new URL(request.url).searchParams.get("q") ?? "";
      if (q === "Alpha") {
        oldStarted.resolve();
        await oldGate.promise;
      }
      return HttpResponse.json({
        data: [
          {
            id: q || "browse",
            location: q ? `${q} market` : "Browse market",
            date: "2026-01-15",
            taxAmount: 0,
          },
        ],
        total: q ? 1 : 100,
        offset: 0,
        limit: 50,
      });
    }),
  );
  const { queryClient } = renderForm({
    defaultValues: { ...defaults, receiptId: "" },
  });
  await formReady(queryClient);
  const user = userEvent.setup();
  try {
    await user.click(screen.getByRole("combobox", { name: /^Receipt/ }));
    await screen.findByRole("option");
    await user.type(screen.getByPlaceholderText("Search receipts..."), "Alpha");
    await oldStarted.promise;
    await user.keyboard("{Escape}");
    expect(
      screen.queryByPlaceholderText("Search receipts..."),
    ).not.toBeInTheDocument();
    await user.click(screen.getByRole("combobox", { name: /^Receipt/ }));
    const currentSearch = screen.getByPlaceholderText("Search receipts...");
    expect(currentSearch).toHaveValue("");
    await user.type(currentSearch, "Beta");
    expect(
      await screen.findByRole("option", { name: /Beta market/ }),
    ).toBeVisible();
    await act(async () => {
      oldGate.resolve();
      await oldGate.promise;
    });
    expect(
      screen.queryByRole("option", { name: /Alpha market/ }),
    ).not.toBeInTheDocument();
    expect(screen.getByRole("option", { name: /Beta market/ })).toBeVisible();
    expect(currentSearch).toHaveValue("Beta");
  } finally {
    oldGate.resolve();
  }
});

it("preserves newer independently cached detail when choosing an older browse row", async () => {
  let detailReads = 0;
  const newer = {
    id: "receipt-1",
    location: "Updated market",
    date: "2026-02-01",
    taxAmount: 2.25,
  };
  server.use(
    http.get("*/api/receipts/:id", () => {
      detailReads++;
      return HttpResponse.json(newer);
    }),
  );
  const { queryClient } = renderForm({
    defaultValues: { ...defaults, receiptId: "" },
  });
  await formReady(queryClient);
  const user = userEvent.setup();
  await user.click(screen.getByRole("combobox", { name: /^Receipt/ }));
  const oldChoice = await screen.findByRole("option", { name: /Market 1/ });
  act(() => {
    queryClient.setQueryData(["receipts", "receipt-1"], newer);
  });
  await user.click(oldChoice);
  expect(screen.getByRole("combobox", { name: /^Receipt/ })).toHaveTextContent(
    "Updated market",
  );
  expect(queryClient.getQueryData(["receipts", "receipt-1"])).toEqual(newer);
  expect(detailReads).toBe(0);
});

it("seeds an absent detail only as stale immediate context and verifies it with an actual held GET", async () => {
  const detailStarted = deferred();
  const detailGate = deferred();
  const verified = {
    id: "receipt-1",
    location: "Verified market",
    date: "2026-02-03",
    taxAmount: 4.5,
  };
  const locations: (string | null)[] = [];
  server.use(
    http.get("*/api/receipts/:id", async () => {
      detailStarted.resolve();
      await detailGate.promise;
      return HttpResponse.json(verified);
    }),
    http.get("*/api/receipt-items/suggestions", ({ request }) => {
      locations.push(new URL(request.url).searchParams.get("location"));
      return HttpResponse.json([]);
    }),
  );
  const { queryClient } = renderForm({
    defaultValues: { ...defaults, receiptId: "" },
  });
  await formReady(queryClient);
  const user = userEvent.setup();
  try {
    await user.click(screen.getByRole("combobox", { name: /^Receipt/ }));
    await user.click(await screen.findByRole("option", { name: /Market 1/ }));
    await detailStarted.promise;
    expect(
      queryClient.getQueryState(["receipts", "receipt-1"])?.dataUpdatedAt,
    ).toBe(0);
    expect(queryClient.getQueryData(["receipts", "receipt-1"])).toEqual({
      id: "receipt-1",
      location: "Market 1",
      date: "2026-01-15",
      taxAmount: 0,
    });
    expect(
      screen.getByRole("combobox", { name: /^Receipt/ }),
    ).toHaveTextContent("Market 1");
    await user.click(screen.getByPlaceholderText("Enter item code..."));
    await waitFor(() => expect(locations).toContain("Market 1"));
    detailGate.resolve();
    await waitFor(() =>
      expect(
        screen.getByRole("combobox", { name: /^Receipt/ }),
      ).toHaveTextContent("Verified market"),
    );
    await waitFor(() => expect(locations).toContain("Verified market"));
    expect(queryClient.getQueryData(["receipts", "receipt-1"])).toEqual(
      verified,
    );
    expect(screen.getByLabelText(/^Description/)).toHaveValue(
      defaults.description,
    );
  } finally {
    detailGate.resolve();
  }
});
