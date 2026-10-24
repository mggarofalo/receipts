import { useState } from "react";
import { act, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, useLocation } from "react-router";
import { CatalogTab } from "./CatalogTab";
import { useNormalizationInboxPreferences } from "@/hooks/useNormalizationInbox";
vi.mock("@/hooks/useNormalizationInbox", () => ({
  useNormalizationInboxPreferences: vi.fn(),
}));
vi.mock("./CatalogInbox", () => ({
  CatalogInbox: ({ onRun }: { onRun: (id: string) => void }) => (
    <button onClick={() => onRun("durable-run")}>Open saved suggestions</button>
  ),
}));
import {
  useCatalogCoverage,
  useCatalogOperation,
  useCatalogOperations,
  type CatalogOperation,
  type CatalogSource,
} from "@/hooks/useNormalizationCatalog";
import {
  useAiConnection,
  useAiRun,
  useAiRuns,
} from "@/hooks/useNormalizationAi";
vi.mock("@/hooks/useNormalizationCatalog", () => ({
  useCatalogCoverage: vi.fn(),
  useCatalogOperation: vi.fn(),
  useCatalogOperations: vi.fn(),
}));
vi.mock("@/hooks/useNormalizationAi", () => ({
  useAiConnection: vi.fn(),
  useAiRun: vi.fn(),
  useAiRuns: vi.fn(),
}));
const callbacks: Array<(operation: CatalogOperation) => void> = [];
const sourceA = { location: "Store A", description: "Milk", itemCode: null };
const sourceB = { location: "Store B", description: "Bread", itemCode: null };
vi.mock("./CatalogSourceList", () => ({
  CatalogSourceList: ({
    onReview,
  }: {
    onReview: (source: CatalogSource) => void;
  }) => (
    <>
      <button onClick={() => onReview(sourceA)}>Review milk</button>
      <button onClick={() => onReview(sourceB)}>Review bread</button>
    </>
  ),
}));
vi.mock("./CatalogSourceReview", () => ({
  CatalogSourceReview: ({
    source,
    onPreview,
  }: {
    source: CatalogSource;
    onPreview: (operation: CatalogOperation) => void;
  }) => (
    <div aria-label="Active source review">
      {source.description}
      <button onClick={() => callbacks.push(onPreview)}>
        Start delayed preview
      </button>
    </div>
  ),
}));
vi.mock("./CatalogOperationReview", () => ({
  CatalogOperationReview: ({ operation }: { operation: CatalogOperation }) => (
    <OperationState operation={operation} />
  ),
}));
vi.mock("./CatalogAiReview", () => ({
  CatalogAiReview: () => <div>Catalog AI review</div>,
}));
const Location = () => <output aria-label="URL">{useLocation().search}</output>;
const ready = (data: unknown) => ({
  data,
  isPending: false,
  isError: false,
  isFetching: false,
  refetch: vi.fn(),
});
const coverage = {
  totalItems: 10,
  unassigned: 4,
  legacyUnknown: 3,
  automatic: 1,
  reviewed: 1,
  mapped: 1,
  protected: 0,
  excluded: 0,
  revisable: 0,
};
beforeEach(() => {
  vi.clearAllMocks();
  callbacks.length = 0;
  vi.mocked(useNormalizationInboxPreferences).mockReturnValue(
    ready({ isOwner: false }) as never,
  );
  vi.mocked(useCatalogCoverage).mockReturnValue(ready(coverage) as never);
  vi.mocked(useCatalogOperations).mockReturnValue(ready([]) as never);
  vi.mocked(useCatalogOperation).mockReturnValue(ready(undefined) as never);
  vi.mocked(useAiConnection).mockReturnValue(
    ready({
      configured: true,
      isOwner: true,
      generation: "generation1",
    }) as never,
  );
  vi.mocked(useAiRuns).mockReturnValue(ready([]) as never);
  vi.mocked(useAiRun).mockReturnValue(ready(undefined) as never);
});
const show = (url = "/normalized-descriptions?tab=catalog") =>
  render(
    <MemoryRouter initialEntries={[url]}>
      <CatalogTab />
      <Location />
    </MemoryRouter>,
  );

it("opens the durable suggestion run for the locally configured owner while provider discovery is offline", async () => {
  vi.mocked(useAiConnection).mockReturnValue({
    ...ready(undefined),
    isError: true,
  } as never);
  vi.mocked(useNormalizationInboxPreferences).mockReturnValue(
    ready({ isOwner: true }) as never,
  );
  vi.mocked(useAiRun).mockReturnValue(
    ready({ id: "durable-run", state: "ready", proposals: [] }) as never,
  );
  show();
  await userEvent.click(
    screen.getByRole("button", { name: "Open saved suggestions" }),
  );
  expect(screen.getByLabelText("URL")).toHaveTextContent("aiRun=durable-run");
  expect(screen.getByText("Catalog AI review")).toBeVisible();
});

it("renders coverage loading and retryable errors without claiming all linked items are reviewed", () => {
  vi.mocked(useCatalogCoverage).mockReturnValue({
    ...ready(undefined),
    isPending: true,
  } as never);
  const { rerender } = show();
  expect(screen.getByText("Loading coverage...")).toBeInTheDocument();
  vi.mocked(useCatalogCoverage).mockReturnValue({
    ...ready(undefined),
    isError: true,
  } as never);
  rerender(
    <MemoryRouter>
      <CatalogTab />
    </MemoryRouter>,
  );
  expect(
    screen.getByText("Catalog coverage could not be loaded."),
  ).toBeInTheDocument();
  vi.mocked(useCatalogCoverage).mockReturnValue(ready(coverage) as never);
  rerender(
    <MemoryRouter>
      <CatalogTab />
    </MemoryRouter>,
  );
  expect(screen.getByText("Unknown legacy")).toBeInTheDocument();
  expect(
    screen.getByText(/a linked item alone is not proof/),
  ).toBeInTheDocument();
});

it("ignores a late preview from a closed source after another review opens", async () => {
  const user = userEvent.setup();
  show();
  await user.click(screen.getByRole("button", { name: "Review milk" }));
  await user.click(
    screen.getByRole("button", { name: "Start delayed preview" }),
  );
  await user.click(screen.getByRole("button", { name: "Close source review" }));
  await user.click(screen.getByRole("button", { name: "Review bread" }));
  await act(async () => {
    callbacks[0]({ id: "old-operation" } as CatalogOperation);
  });
  expect(screen.getByLabelText("Active source review")).toHaveTextContent(
    "Bread",
  );
  expect(screen.getByLabelText("URL")).not.toHaveTextContent("old-operation");
  await user.click(
    screen.getByRole("button", { name: "Start delayed preview" }),
  );
  await act(async () => {
    callbacks[1]({ id: "current-operation" } as CatalogOperation);
  });
  await waitFor(() =>
    expect(screen.getByLabelText("URL")).toHaveTextContent(
      "catalogOperation=current-operation",
    ),
  );
  expect(
    screen.queryByLabelText("Active source review"),
  ).not.toBeInTheDocument();
});

it("resumes an operation from URL and closes only that selection", async () => {
  vi.mocked(useCatalogOperation).mockReturnValue(
    ready({ id: "saved-operation" }) as never,
  );
  show(
    "/normalized-descriptions?tab=catalog&catalogOperation=saved-operation&q=milk",
  );
  expect(useCatalogOperation).toHaveBeenCalledWith("saved-operation");
  expect(screen.getByText("Operation saved-operation")).toBeInTheDocument();
  await userEvent
    .setup()
    .click(screen.getByRole("button", { name: "Close operation" }));
  await waitFor(() =>
    expect(screen.getByLabelText("URL")).not.toHaveTextContent(
      "catalogOperation",
    ),
  );
  expect(screen.getByLabelText("URL")).toHaveTextContent("q=milk");
});

it("routes historical item-level suggestions to their original supported review", async () => {
  vi.mocked(useAiRun).mockReturnValue(
    ready({
      id: "old-run",
      proposals: [{ id: "item-proposal", source: null }],
    }) as never,
  );
  show("/normalized-descriptions?tab=catalog&aiRun=old-run");
  expect(screen.queryByText("Catalog AI review")).not.toBeInTheDocument();
  await userEvent
    .setup()
    .click(
      screen.getByRole("button", { name: "Open original purchase review" }),
    );
  await waitFor(() =>
    expect(screen.getByLabelText("URL")).toHaveTextContent("tab=chatgpt"),
  );
  expect(screen.getByLabelText("URL")).toHaveTextContent("aiRun=old-run");
});

function OperationState({ operation }: { operation: CatalogOperation }) {
  const [failed, setFailed] = useState(false);
  return (
    <div>
      Operation {operation.id}
      <button onClick={() => setFailed(true)}>Simulate failed operation</button>
      {failed && <p role="alert">The operation failed; review before retry.</p>}
    </div>
  );
}

it("retains mounted operation mutation feedback when background polling changes its revision", async () => {
  vi.mocked(useCatalogOperation).mockReturnValue(
    ready({ id: "saved-operation", revision: "revision-1" }) as never,
  );
  const tree = (
    <MemoryRouter
      initialEntries={[
        "/normalized-descriptions?tab=catalog&catalogOperation=saved-operation",
      ]}
    >
      <CatalogTab />
      <Location />
    </MemoryRouter>
  );
  const { rerender } = render(tree);
  await userEvent
    .setup()
    .click(screen.getByRole("button", { name: "Simulate failed operation" }));
  expect(screen.getByRole("alert")).toHaveTextContent("The operation failed");
  vi.mocked(useCatalogOperation).mockReturnValue(
    ready({ id: "saved-operation", revision: "revision-2" }) as never,
  );
  rerender(
    <MemoryRouter
      initialEntries={[
        "/normalized-descriptions?tab=catalog&catalogOperation=saved-operation",
      ]}
    >
      <CatalogTab />
      <Location />
    </MemoryRouter>,
  );
  expect(screen.getByRole("alert")).toHaveTextContent("The operation failed");
});
