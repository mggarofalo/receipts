vi.hoisted(() => vi.stubEnv("VITE_API_URL", "http://suggestion-focus.test"));
import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createMemoryRouter, RouterProvider } from "react-router";
import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import { AuthProvider } from "@/contexts/AuthContext";
import { AppearanceProvider } from "@/contexts/AppearanceContext";
import { TooltipProvider } from "@/components/ui/tooltip";
import { RootLayout } from "@/components/RootLayout";
import { createAppQueryClient } from "@/lib/query-client";
import { clearTokens, setTokens } from "@/lib/auth";
import { LineItemsSection } from "./LineItemsSection";
import "@/test/setup-combobox-polyfills";
import { chooseCategoryOption } from "@/test/category-picker";

function deferred() {
  let resolve!: () => void;
  const promise = new Promise<void>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}
let responseGate: ReturnType<typeof deferred>;
let requestStarted: boolean;
let withMatch: boolean;
let queryClient: ReturnType<typeof createAppQueryClient>;
let router: ReturnType<typeof createMemoryRouter>;
const match = {
  name: "Milk suggestion",
  similarity: 0.9,
  combinedScore: 0.9,
  semanticSimilarity: null,
  source: "history",
  defaultCategory: "Food",
  defaultSubcategory: "Dairy",
  defaultUnitPrice: 2.01,
  defaultItemCode: "MILK",
};
const server = setupServer(
  http.post("*/api/receipt-items/normalization-preview", () =>
    HttpResponse.json({
      sourceFingerprint: "suggestions-draft",
      sourceAmbiguous: false,
      excluded: false,
      candidates: [],
      total: 0,
      offset: 0,
      limit: 10,
    }),
  ),
  http.get("*/api/taxonomy", () =>
    HttpResponse.json({
      id: "taxonomy-r1",
      sequence: 1,
      recordedAt: "2026-10-26T00:00:00Z",
      actor: "test",
      reason: "Fixture",
      operationId: "taxonomy-operation",
      affectedCount: 2,
      nodes: [
        { id: "food", name: "Food", archived: false },
        { id: "dairy", parentId: "food", name: "Dairy", archived: false },
      ],
    }),
  ),
  http.get("*/api/item-templates/similar", async () => {
    requestStarted = true;
    await responseGate.promise;
    return HttpResponse.json(withMatch ? [match] : []);
  }),
  http.get("*/api/receipt-items/suggestions", () => HttpResponse.json([])),
);
beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
afterAll(() => server.close());
beforeEach(() => {
  responseGate = deferred();
  requestStarted = false;
  withMatch = false;
  localStorage.clear();
  setTokens(
    `header.${btoa(JSON.stringify({ sub: "alice", exp: 4102444800 }))}.signature`,
    "refresh",
  );
});
afterEach(() => {
  responseGate.resolve();
  cleanup();
  router?.dispose();
  queryClient?.clear();
  clearTokens();
  server.resetHandlers();
});
async function prepare() {
  queryClient = createAppQueryClient();
  router = createMemoryRouter([
    {
      element: <RootLayout />,
      children: [
        {
          path: "/",
          element: (
            <LineItemsSection
              items={[]}
              onChange={vi.fn()}
              location="Manual market"
            />
          ),
        },
      ],
    },
  ]);
  render(
    <AppearanceProvider>
      <TooltipProvider>
        <AuthProvider queryClientFactory={() => queryClient}>
          <RouterProvider router={router} />
        </AuthProvider>
      </TooltipProvider>
    </AppearanceProvider>,
  );
  const user = userEvent.setup();
  await user.click(screen.getByRole("combobox", { name: /^Category/ }));
  await chooseCategoryOption(user, "Food");
  const description = screen.getByPlaceholderText("Item description");
  await user.type(description, "Manual milk");
  await waitFor(() => expect(requestStarted).toBe(true)); // The real debounced request is already pending.
  return { user, description };
}

it.each(["empty", "matching"] as const)(
  "does not reopen obsolete description suggestions after a late %s response while category owns focus",
  async (kind) => {
    withMatch = kind === "matching";
    const { user, description } = await prepare();
    await user.clear(screen.getByLabelText(/^Unit Price/));
    await user.type(screen.getByLabelText(/^Unit Price/), "2.01");
    const category = screen.getByRole("combobox", { name: /^Category/ });
    await user.click(category);
    const search = await screen.findByPlaceholderText("Search category paths…");
    await waitFor(() => expect(search).toHaveFocus());
    expect(category).toHaveAttribute("aria-expanded", "true");
    await act(async () => {
      responseGate.resolve();
    });
    await waitFor(() =>
      expect(
        queryClient.getQueryState([
          "similarItems",
          "Manual milk",
          undefined,
          undefined,
        ])?.fetchStatus,
      ).toBe("idle"),
    );
    const descriptionBeforeEscape = description.getAttribute("aria-expanded");
    expect(search).toHaveFocus();
    await user.keyboard("{Escape}");
    expect.soft(descriptionBeforeEscape).not.toBe("true");
    await waitFor(() =>
      expect(category).toHaveAttribute("aria-expanded", "false"),
    );
    expect(description).toHaveValue("Manual milk");
    expect(screen.getByLabelText(/^Unit Price/)).toHaveValue("2.0100");
  },
);

it("shows a completed description suggestion when its input still owns focus and lets Escape dismiss it", async () => {
  withMatch = true;
  const { user, description } = await prepare();
  expect(description).toHaveFocus();
  await act(async () => {
    responseGate.resolve();
  });
  expect(
    await screen.findByRole("option", { name: /Milk suggestion/ }),
  ).toBeVisible();
  expect(description).toHaveAttribute("aria-expanded", "true");
  await user.keyboard("{Escape}");
  await waitFor(() =>
    expect(description).toHaveAttribute("aria-expanded", "false"),
  );
  expect(description).toHaveValue("Manual milk");
  // Refocusing still owns the cached chooser; pointer selection must survive the
  // input's blur into its already-mounted suggestion portal.
  await user.type(screen.getByLabelText(/^Unit Price/), "2.01");
  await user.click(description);
  await user.click(
    await screen.findByRole("option", { name: /Milk suggestion/ }),
  );
  expect(description).toHaveValue("Milk suggestion");
  await waitFor(() =>
    expect(description).toHaveAttribute("aria-expanded", "false"),
  );
  expect(screen.getByLabelText(/^Unit Price/)).toHaveValue("2.0100");
});

it("hides cached suggestions immediately while the edited description is still debouncing", async () => {
  withMatch = true;
  const { description } = await prepare();
  await act(async () => {
    responseGate.resolve();
  });
  expect(await screen.findByRole("option", { name: /Milk suggestion/ })).toBeVisible();

  // One input event keeps this assertion inside the real hook's 300ms debounce.
  // This synthetic edit reproduces a stale result, not a retrieval-quality claim.
  fireEvent.change(description, { target: { value: "Baked beans 12 oz" } });

  expect(screen.queryByRole("option", { name: /Milk suggestion/ })).not.toBeInTheDocument();
  expect(description).toHaveAttribute("aria-expanded", "false");
  expect(description).toHaveValue("Baked beans 12 oz");
  expect(description).toHaveFocus();
});

it("hides cached item-code suggestions immediately while the edited code is still debouncing", async () => {
  server.use(http.get("*/api/receipt-items/suggestions", () => HttpResponse.json([{
    itemCode: "MILK-GAL", description: "Whole milk", category: "Food",
    subcategory: "Dairy", unitPrice: 2.01, matchType: "location",
  }])));
  const { user, description } = await prepare();
  const code = screen.getByPlaceholderText("e.g. MILK-GAL");
  await user.type(code, "MILK");
  expect(await screen.findByRole("option", { name: /MILK-GAL/ })).toBeVisible();
  fireEvent.change(code, { target: { value: "BEANS" } });
  expect(screen.queryByRole("option", { name: /MILK-GAL/ })).not.toBeInTheDocument();
  expect(code).toHaveAttribute("aria-expanded", "false");
  expect(code).toHaveValue("BEANS");
  expect(code).toHaveFocus();
  expect(description).toHaveValue("Manual milk");
});
