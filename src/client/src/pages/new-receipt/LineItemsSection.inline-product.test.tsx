vi.hoisted(() => vi.stubEnv("VITE_API_URL", "http://inline-product.test"));
import { act, fireEvent, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import { renderWithQueryClient } from "@/test/test-utils";
import { mockQueryResult } from "@/test/mock-hooks";
import { taxonomyFixture } from "@/test/taxonomy-fixture";
import { useSimilarItems } from "@/hooks/useSimilarItems";
import { useReceiptItemSuggestions } from "@/hooks/useReceiptItemSuggestions";
import { LineItemsSection } from "./LineItemsSection";
import "@/test/setup-combobox-polyfills";

vi.mock("@/hooks/useCategoryTaxonomy", () => ({
  useCategoryTaxonomy: () => mockQueryResult({ data: taxonomyFixture }),
}));
vi.mock("@/hooks/useSimilarItems", () => ({
  useSimilarItems: vi.fn(() => mockQueryResult({ data: [], isFetching: false })),
}));
vi.mock("@/hooks/useReceiptItemSuggestions", () => ({
  useReceiptItemSuggestions: vi.fn(() => mockQueryResult({ data: [], isFetching: false })),
}));

const product = {
  id: "11111111-1111-4111-8111-111111111111",
  revision: "22222222-2222-4222-8222-222222222222",
  canonicalName: "Whole milk 1 gallon",
  displayLabel: null,
  channels: ["lexical"],
};
const rawDescription = "MILK  1gal #42";
const requests: string[] = [];
const server = setupServer(
  http.get("*/api/receipt-items/normalization-candidates", ({ request }) => {
    requests.push(request.url);
    return HttpResponse.json([product]);
  }),
  http.post("*/api/receipt-items/normalization-preview", () =>
    HttpResponse.json({
      sourceFingerprint: "server-observed-milk-source",
      sourceAmbiguous: false,
      excluded: false,
      candidates: [], total: 0, offset: 0, limit: 10,
    }),
  ),
);
beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
afterAll(() => server.close());
beforeEach(() => {
  requests.length = 0;
  localStorage.clear();
  vi.mocked(useSimilarItems).mockReturnValue(mockQueryResult({ data: [], isFetching: false }));
  vi.mocked(useReceiptItemSuggestions).mockReturnValue(mockQueryResult({ data: [], isFetching: false }));
});
afterEach(() => server.resetHandlers());

async function prepare() {
  const onChange = vi.fn();
  const view = renderWithQueryClient(
    <LineItemsSection items={[]} onChange={onChange} location="Fixture market" />,
  );
  const user = userEvent.setup();
  const description = screen.getByPlaceholderText("Item description");
  await user.type(description, rawDescription);
  await waitFor(() => expect(screen.getByRole("option", { name: /Whole milk 1 gallon/ })).toHaveTextContent("Product"));
  const option = screen.getByRole("option", { name: /Whole milk 1 gallon/ });
  return { user, description, option, onChange, ...view };
}

const history = {
  name: "Milk from receipt history", similarity: 0.9, semanticSimilarity: null,
  combinedScore: 0.9, source: "history", defaultCategory: null,
  defaultSubcategory: null, defaultUnitPrice: null, defaultItemCode: null,
};

function expectActiveOption(input: HTMLElement, option: HTMLElement) {
  const listId = input.getAttribute("aria-controls");
  expect(listId).toBeTruthy();
  expect(document.getElementById(listId!)).toBe(screen.getByRole("listbox"));
  const id = input.getAttribute("aria-activedescendant");
  expect(id).toBeTruthy();
  expect(document.getElementById(id!)).toBe(option);
  expect(option).toHaveAttribute("aria-selected", "true");
  expect(screen.getAllByRole("option").filter((row) => row.getAttribute("aria-selected") === "true")).toEqual([option]);
}

it("announces actual product and history options while arrows move highlight and Escape preserves the draft", async () => {
  vi.mocked(useSimilarItems).mockReturnValue(mockQueryResult({ data: [history], isFetching: false }));
  const { user, description, option, onChange } = await prepare();
  const historyOption = screen.getByRole("option", { name: /Milk from receipt history/ });
  expectActiveOption(description, option);
  await user.keyboard("{ArrowDown}");
  expectActiveOption(description, historyOption);
  await user.keyboard("{ArrowUp}");
  expectActiveOption(description, option);
  await user.keyboard("{Escape}");
  expect(description).not.toHaveAttribute("aria-activedescendant");
  expect(description).toHaveAttribute("aria-expanded", "false");
  expect(description).toHaveValue(rawDescription);
  expect(description).toHaveFocus();
  expect(onChange).not.toHaveBeenCalled();
});

it("announces actual item-code options while arrows move highlight and Enter selects without submitting", async () => {
  vi.mocked(useReceiptItemSuggestions).mockReturnValue(mockQueryResult({ data: [
    { itemCode: "MILK-A", description: "First milk", category: null, unitPrice: null, matchType: "location" },
    { itemCode: "MILK-B", description: "Second milk", category: null, unitPrice: null, matchType: "global" },
  ], isFetching: false }));
  const onChange = vi.fn();
  const user = userEvent.setup();
  renderWithQueryClient(<LineItemsSection items={[]} onChange={onChange} location="Fixture market" />);
  const input = screen.getByPlaceholderText("e.g. MILK-GAL");
  await user.type(input, "MI");
  const first = await screen.findByRole("option", { name: /MILK-A/ });
  const second = screen.getByRole("option", { name: /MILK-B/ });
  await waitFor(() => expectActiveOption(input, first));
  await user.keyboard("{ArrowDown}");
  expectActiveOption(input, second);
  await user.keyboard("{ArrowUp}");
  expectActiveOption(input, first);
  await user.keyboard("{ArrowDown}{Enter}");
  expect(input).not.toHaveAttribute("aria-activedescendant");
  expect(input).toHaveValue("MILK-B");
  expect(screen.getByPlaceholderText("Item description")).toHaveValue("Second milk");
  expect(input).toHaveFocus();
  expect(onChange).not.toHaveBeenCalled();
});

it("deduplicates history matching the canonical name when the product uses a different display label", async () => {
  server.use(http.get("*/api/receipt-items/normalization-candidates", () => HttpResponse.json([
    { ...product, displayLabel: "Organic dairy" },
  ])));
  vi.mocked(useSimilarItems).mockReturnValue(mockQueryResult({ data: [{ ...history, name: product.canonicalName }], isFetching: false }));
  const user = userEvent.setup();
  renderWithQueryClient(<LineItemsSection items={[]} onChange={vi.fn()} location="Fixture market" />);
  await user.type(screen.getByPlaceholderText("Item description"), rawDescription);
  expect(await screen.findByRole("option", { name: /Organic dairy/ })).toHaveTextContent("Product");
  expect(screen.queryByRole("option", { name: /Whole milk 1 gallon/ })).not.toBeInTheDocument();
});

it.each(["click", "keyboard"] as const)(
  "selects a product by %s in the description lookup without changing raw text or opening a dialog",
  async (method) => {
    const { user, description, option, onChange } = await prepare();
    expect(option).toHaveTextContent("Product");
    expect(screen.queryByText(/Selected for this purchase/)).not.toBeInTheDocument();
    expect(onChange).not.toHaveBeenCalled();
    if (method === "click") await user.click(option);
    else await user.keyboard("{ArrowDown}{ArrowUp}{Enter}");
    expect(description).toHaveValue(rawDescription);
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(await screen.findByText(/Selected for this purchase:/)).toHaveTextContent(product.canonicalName);
    await user.click(screen.getByRole("button", { name: "Add Item" }));
    await waitFor(() => expect(onChange).toHaveBeenCalledWith([
      expect.objectContaining({
        description: rawDescription,
        normalizationChoice: expect.objectContaining({
          label: product.canonicalName,
          intent: expect.objectContaining({
            kind: "confirmExisting",
            targetId: product.id,
            expectedTargetRevision: product.revision,
            sourceFingerprint: "server-observed-milk-source",
            rememberSource: false,
            clientRowId: expect.any(String),
          }),
        }),
      }),
    ]));
    expect(description).toHaveValue("");
    expect(screen.queryByText(/Selected for this purchase/)).not.toBeInTheDocument();
  },
);

it("offers one Product option when receipt history has the same name", async () => {
  vi.mocked(useSimilarItems).mockReturnValue(mockQueryResult({
    data: [{ name: product.canonicalName, similarity: 0.9, semanticSimilarity: null,
      combinedScore: 0.9, source: "history", defaultCategory: null,
      defaultSubcategory: null, defaultUnitPrice: null, defaultItemCode: null }],
    isFetching: false,
  }));
  await prepare();
  expect(screen.getAllByRole("option", { name: /Whole milk 1 gallon/ })).toHaveLength(1);
  expect(screen.getByRole("option", { name: /Whole milk 1 gallon/ })).toHaveTextContent("Product");
});

it.each(["description", "item code", "store"] as const)(
  "invalidates the product and its intent immediately when %s changes",
  async (field) => {
    const { user, description, option, onChange, rerender } = await prepare();
    await user.click(option);
    expect(await screen.findByText(/Selected for this purchase:/)).toBeInTheDocument();
    await user.click(description);
    // Keep all following checks inside the real debounce window.
    if (field === "description") fireEvent.change(description, { target: { value: "BREAD #7" } });
    if (field === "item code") fireEvent.change(screen.getByPlaceholderText("e.g. MILK-GAL"), { target: { value: "NEW-CODE" } });
    if (field === "store") rerender(<LineItemsSection items={[]} onChange={onChange} location="Different market" />);
    expect(screen.queryByRole("option", { name: /Whole milk 1 gallon/ })).not.toBeInTheDocument();
    expect(screen.queryByText(/Selected for this purchase:/)).not.toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Add Item" }));
    await waitFor(() => expect(onChange).toHaveBeenCalled());
    expect(onChange.mock.calls[0][0][0].normalizationChoice).toBeUndefined();
  },
);

it("does not expose or bind candidates before server fingerprint evidence is ready", async () => {
  let release!: () => void;
  let started = false;
  const gate = new Promise<void>((resolve) => { release = resolve; });
  server.use(http.post("*/api/receipt-items/normalization-preview", async () => {
    started = true;
    await gate;
    return HttpResponse.json({ sourceFingerprint: "obsolete-fingerprint", sourceAmbiguous: false,
      excluded: false, candidates: [], total: 0, offset: 0, limit: 10 });
  }));
  const onChange = vi.fn();
  const user = userEvent.setup();
  renderWithQueryClient(<LineItemsSection items={[]} onChange={onChange} location="Fixture market" />);
  const description = screen.getByPlaceholderText("Item description");
  await user.type(description, rawDescription);
  await waitFor(() => expect(started).toBe(true));
  await waitFor(() => expect(requests.length).toBeGreaterThan(0));
  expect(screen.queryByRole("option", { name: /Whole milk 1 gallon/ })).not.toBeInTheDocument();
  fireEvent.change(description, { target: { value: "BREAD #7" } });
  await act(async () => release());
  expect(screen.queryByRole("option", { name: /Whole milk 1 gallon/ })).not.toBeInTheDocument();
  await user.click(screen.getByRole("button", { name: "Add Item" }));
  await waitFor(() => expect(onChange).toHaveBeenCalled());
  expect(onChange.mock.calls[0][0][0]).toMatchObject({ description: "BREAD #7" });
  expect(onChange.mock.calls[0][0][0].normalizationChoice).toBeUndefined();
});

it("keeps manual entry available when product preview fails", async () => {
  server.use(http.post("*/api/receipt-items/normalization-preview", () => HttpResponse.json({ detail: "Unavailable" }, { status: 503 })));
  const onChange = vi.fn();
  const user = userEvent.setup();
  renderWithQueryClient(<LineItemsSection items={[]} onChange={onChange} location="Fixture market" />);
  await user.type(screen.getByPlaceholderText("Item description"), rawDescription);
  await waitFor(() => expect(requests.length).toBeGreaterThan(0));
  expect(screen.queryByRole("option", { name: /Whole milk 1 gallon/ })).not.toBeInTheDocument();
  await user.click(screen.getByRole("button", { name: "Add Item" }));
  await waitFor(() => expect(onChange).toHaveBeenCalledWith([
    expect.objectContaining({ description: rawDescription, normalizationChoice: undefined }),
  ]));
});

it("keeps the optional advanced chooser available and changes a one-purchase selection only when requested", async () => {
  const { user, description, option, onChange } = await prepare();
  await user.click(option);
  await user.click(await screen.findByRole("button", { name: "Change product" }));
  expect(await screen.findByRole("dialog", { name: "Choose a product" })).toBeVisible();
  expect(screen.getByRole("checkbox", { name: /Remember this exact description/ })).not.toBeChecked();
  await user.click(screen.getByRole("button", { name: "Review later" }));
  expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  expect(description).toHaveValue(rawDescription);
  await user.click(screen.getByRole("button", { name: "Add Item" }));
  await waitFor(() => expect(onChange).toHaveBeenCalledWith([
    expect.objectContaining({ normalizationChoice: expect.objectContaining({ intent: expect.objectContaining({ kind: "defer", rememberSource: false }) }) }),
  ]));
});



it("offers at most two semantic-only products inline, after products with specific evidence", async () => {
  const near = (index: number) => ({
    ...product,
    id: `33333333-3333-4333-8333-33333333333${index}`,
    canonicalName: `Similar product ${index}`,
    channels: ["semantic"],
  });
  server.use(http.get("*/api/receipt-items/normalization-candidates", () =>
    HttpResponse.json([product, near(1), near(2), near(3)])));
  const user = userEvent.setup();
  renderWithQueryClient(<LineItemsSection items={[]} onChange={vi.fn()} location="Fixture market" />);
  await user.type(screen.getByPlaceholderText("Item description"), rawDescription);

  await screen.findByRole("option", { name: /Whole milk 1 gallon/ });
  expect(screen.getAllByRole("option").map((option) => option.textContent)).toEqual([
    expect.stringContaining("Whole milk 1 gallon"),
    expect.stringContaining("Similar product 1"),
    expect.stringContaining("Similar product 2"),
  ]);
  expect(screen.queryByRole("option", { name: /Similar product 3/ })).not.toBeInTheDocument();
});
