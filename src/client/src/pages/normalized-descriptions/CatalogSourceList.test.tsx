import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { CatalogSourceList } from "./CatalogSourceList";
import {
  useCatalogSources,
  useCatalogSuggestions,
} from "@/hooks/useNormalizationCatalog";
import { useAiConnection } from "@/hooks/useNormalizationAi";
import { toApiError } from "@/lib/problem-details";
vi.mock("@/hooks/useNormalizationCatalog", () => ({
  useCatalogSources: vi.fn(),
  useCatalogSuggestions: vi.fn(),
}));
vi.mock("@/hooks/useNormalizationAi", () => ({ useAiConnection: vi.fn() }));
const source = {
  location: "Store A",
  description: "BF FRANKS",
  itemCode: "001",
};
const group = {
  source,
  itemCount: 5,
  unassignedCount: 3,
  automaticCount: 1,
  legacyUnknownCount: 1,
  protectedCount: 0,
  mappedCount: 0,
  excludedCount: 0,
  revisableCount: 0,
};
const mutate = vi.fn();
let sources: Record<string, unknown>;
let connection: Record<string, unknown>;
let failure: ReturnType<typeof toApiError> | null;
beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  failure = null;
  sources = {
    data: { items: [group], total: 1 },
    isPending: false,
    isFetching: false,
    isError: false,
    refetch: vi.fn(),
  };
  connection = {
    configured: true,
    isOwner: true,
    credentialsPresent: true,
    busy: false,
    generation: "generation1",
    models: ["explicit-model"],
  };
  vi.mocked(useCatalogSources).mockImplementation(() => sources as never);
  vi.mocked(useAiConnection).mockImplementation(
    () => ({ data: connection, isError: false }) as never,
  );
  vi.mocked(useCatalogSuggestions).mockImplementation(
    () => ({ mutate, isPending: false, error: failure }) as never,
  );
});

it("label-only selection includes already classified sources and does not reuse identity-review consent", async () => {
  const user = userEvent.setup();
  render(<CatalogSourceList onReview={vi.fn()} onStarted={vi.fn()} />);
  await user.click(
    screen.getByRole("checkbox", { name: "Select BF FRANKS at Store A" }),
  );
  await user.click(
    screen.getByRole("checkbox", { name: /Include unknown legacy/ }),
  );
  await user.selectOptions(
    screen.getByRole("combobox", { name: "Catalog suggestion model" }),
    "explicit-model",
  );
  await user.click(
    screen.getByRole("checkbox", { name: /Improve display names only/ }),
  );
  expect(
    screen.getByRole("combobox", { name: "Source disposition" }),
  ).toHaveValue("all");
  expect(
    screen.getByRole("checkbox", { name: "Select BF FRANKS at Store A" }),
  ).not.toBeChecked();
  await user.click(
    screen.getByRole("checkbox", { name: "Select BF FRANKS at Store A" }),
  );
  await user.click(
    screen.getByRole("button", { name: "Suggest readable display names" }),
  );
  expect(mutate).toHaveBeenCalledWith(
    expect.objectContaining({
      sources: [{ source, includeLegacyUnknown: false, labelOnly: true }],
    }),
  );
});

it("supports loading, errors, and manual review without a ChatGPT connection", async () => {
  sources = { ...sources, isPending: true, data: undefined };
  const onReview = vi.fn();
  const { rerender } = render(
    <CatalogSourceList onReview={onReview} onStarted={vi.fn()} />,
  );
  expect(
    screen.getByText("Loading source descriptions..."),
  ).toBeInTheDocument();
  sources = { ...sources, isPending: false, isError: true };
  rerender(<CatalogSourceList onReview={onReview} onStarted={vi.fn()} />);
  expect(
    screen.getByText("Source descriptions could not be loaded."),
  ).toBeInTheDocument();
  sources = { ...sources, data: { items: [group], total: 1 }, isError: false };
  connection = { ...connection, credentialsPresent: false, models: [] };
  rerender(<CatalogSourceList onReview={onReview} onStarted={vi.fn()} />);
  await userEvent.setup().click(screen.getByRole("button", { name: "Review" }));
  expect(onReview).toHaveBeenCalledWith(source);
  expect(
    screen.getByRole("button", { name: "Suggest normalized names" }),
  ).toBeDisabled();
});

it("requires explicit model and scoped selection, preserves failed request identity, and clears selection after filtering", async () => {
  const user = userEvent.setup();
  const { rerender } = render(
    <CatalogSourceList onReview={vi.fn()} onStarted={vi.fn()} />,
  );
  await user.click(
    screen.getByRole("checkbox", { name: "Select BF FRANKS at Store A" }),
  );
  expect(
    screen.getByRole("button", { name: "Suggest normalized names" }),
  ).toBeDisabled();
  await user.selectOptions(
    screen.getByRole("combobox", { name: "Catalog suggestion model" }),
    "explicit-model",
  );
  await user.click(
    screen.getByRole("checkbox", { name: /Include unknown legacy/ }),
  );
  await user.click(
    screen.getByRole("button", { name: "Suggest normalized names" }),
  );
  const first = mutate.mock.calls[0][0];
  expect(first).toMatchObject({
    model: "explicit-model",
    sources: [{ source, includeLegacyUnknown: true }],
  });
  failure = toApiError(503, { detail: "Request could not be started." });
  rerender(<CatalogSourceList onReview={vi.fn()} onStarted={vi.fn()} />);
  expect(screen.getByRole("alert")).toHaveTextContent(
    "Request could not be started.",
  );
  await user.click(
    screen.getByRole("button", { name: "Suggest normalized names" }),
  );
  expect(mutate.mock.calls[1][0]).toEqual(first);
  await user.selectOptions(
    screen.getByRole("combobox", { name: "Source disposition" }),
    "all",
  );
  expect(
    screen.getByRole("checkbox", { name: "Select BF FRANKS at Store A" }),
  ).not.toBeChecked();
  expect(
    screen.getByRole("button", { name: "Suggest normalized names" }),
  ).toBeDisabled();
});

it("keeps the selected model visible during busy refresh without submitting against an unavailable catalog", async () => {
  const user = userEvent.setup();
  const { rerender } = render(
    <CatalogSourceList onReview={vi.fn()} onStarted={vi.fn()} />,
  );
  await user.click(
    screen.getByRole("checkbox", { name: "Select BF FRANKS at Store A" }),
  );
  await user.selectOptions(
    screen.getByRole("combobox", { name: "Catalog suggestion model" }),
    "explicit-model",
  );
  connection = {
    ...connection,
    busy: true,
    models: [],
    credentialsPresent: false,
  };
  rerender(<CatalogSourceList onReview={vi.fn()} onStarted={vi.fn()} />);
  expect(
    screen.getByRole("combobox", { name: "Catalog suggestion model" }),
  ).toHaveValue("explicit-model");
  expect(
    screen.getByRole("button", { name: "Suggest normalized names" }),
  ).toBeDisabled();
  connection = {
    ...connection,
    busy: false,
    models: ["explicit-model"],
    credentialsPresent: true,
  };
  rerender(<CatalogSourceList onReview={vi.fn()} onStarted={vi.fn()} />);
  expect(
    screen.getByRole("button", { name: "Suggest normalized names" }),
  ).toBeEnabled();
  expect(
    screen.getByText(/Submitting sends the selected receipt descriptions/),
  ).toHaveTextContent("OpenAI");
  expect(screen.getByText(/Requests may consume/)).toBeInTheDocument();
});
