import { toApiError } from "@/lib/problem-details";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { CatalogSourceReview } from "./CatalogSourceReview";
import {
  useCatalogActions,
  useCatalogCandidates,
  useCatalogMappings,
} from "@/hooks/useNormalizationCatalog";
vi.mock("@/hooks/useNormalizationCatalog", () => ({
  useCatalogActions: vi.fn(),
  useCatalogCandidates: vi.fn(),
  useCatalogMappings: vi.fn(),
}));
const source = {
  location: "Store A",
  description: "BF FRANKS",
  itemCode: "001",
};
const candidate = {
  id: "10000000-0000-0000-0000-000000000001",
  revision: "20000000-0000-0000-0000-000000000001",
  canonicalName: "Beef Franks",
  displayLabel: null,
  channels: ["lexical"],
  approvedExamples: ["BEEF FRANKS"],
};
const mutate = vi.fn();
const revoke = vi.fn();
const refetch = vi.fn();
let candidateState: Record<string, unknown>;
let mappingState: Record<string, unknown>;
let error: ReturnType<typeof toApiError> | null;
let pending: boolean;
beforeEach(() => {
  vi.clearAllMocks();
  error = null;
  pending = false;
  candidateState = {
    data: [candidate],
    isPending: false,
    isError: false,
    isFetching: false,
    refetch,
  };
  mappingState = {
    data: [],
    isPending: false,
    isError: false,
    isFetching: false,
    refetch,
  };
  vi.mocked(useCatalogCandidates).mockImplementation(
    () => candidateState as never,
  );
  vi.mocked(useCatalogMappings).mockImplementation(() => mappingState as never);
  vi.mocked(useCatalogActions).mockImplementation(
    () =>
      ({
        preview: { mutate, error, isPending: pending },
        revoke: { mutate: revoke, error: null, isPending: false },
      }) as never,
  );
});

it("renders loading and recoverable candidate errors without inventing a catalog match", async () => {
  candidateState = { ...candidateState, data: undefined, isPending: true };
  const { rerender } = render(
    <CatalogSourceReview source={source} onPreview={vi.fn()} />,
  );
  expect(screen.getByRole("status")).toHaveTextContent(
    "Finding existing names",
  );
  expect(
    screen.getByRole("button", { name: "Preview historical changes" }),
  ).toBeDisabled();
  candidateState = { ...candidateState, isPending: false, isError: true };
  rerender(<CatalogSourceReview source={source} onPreview={vi.fn()} />);
  expect(
    screen.getByText("Existing names could not be loaded."),
  ).toBeInTheDocument();
  await userEvent.setup().click(screen.getByRole("button", { name: /retry/i }));
  expect(refetch).toHaveBeenCalledOnce();
});

it("previews exact scoped candidate identity with explicit legacy consent and retries the same intent id", async () => {
  const user = userEvent.setup();
  const { rerender } = render(
    <CatalogSourceReview source={source} onPreview={vi.fn()} />,
  );
  await user.click(screen.getByRole("radio", { name: /Beef Franks/ }));
  await user.click(
    screen.getByRole("checkbox", { name: /Include older assignments/ }),
  );
  await user.click(
    screen.getByRole("button", { name: "Preview historical changes" }),
  );
  const first = mutate.mock.calls[0][0];
  expect(first).toMatchObject({
    source,
    targetId: candidate.id,
    targetRevision: candidate.revision,
    newCanonicalName: null,
    includeLegacyUnknown: true,
    exclude: false,
  });
  expect(first.clientRequestId).toMatch(/^[0-9a-f-]{36}$/);
  error = toApiError(503, { detail: "Temporary failure; retry this request." });
  rerender(<CatalogSourceReview source={source} onPreview={vi.fn()} />);
  expect(screen.getByRole("alert")).toHaveTextContent("Temporary failure");
  await user.click(
    screen.getByRole("button", { name: "Preview historical changes" }),
  );
  expect(mutate.mock.calls[1][0]).toEqual(first);
  await user.type(
    screen.getByRole("textbox", { name: "Or propose a new canonical name" }),
    "  Turkey Franks  ",
  );
  await user.click(
    screen.getByRole("button", { name: "Preview historical changes" }),
  );
  expect(mutate.mock.calls[2][0]).toMatchObject({
    targetId: null,
    targetRevision: null,
    newCanonicalName: "Turkey Franks",
  });
  expect(mutate.mock.calls[2][0].clientRequestId).not.toBe(
    first.clientRequestId,
  );
});

it("binds a model suggestion to the captured target revision and proposal provenance", async () => {
  const suggestion = {
    targetId: candidate.id,
    targetRevision: "30000000-0000-0000-0000-000000000001",
    suggestionRunId: candidate.id,
    suggestionProposalId: candidate.id,
    suggestionRunRevision: candidate.revision,
  };
  render(
    <CatalogSourceReview
      source={source}
      suggestion={suggestion}
      onPreview={vi.fn()}
    />,
  );
  await userEvent
    .setup()
    .click(screen.getByRole("button", { name: "Preview historical changes" }));
  expect(mutate).toHaveBeenCalledWith(expect.objectContaining(suggestion));
  expect(mutate.mock.calls[0][0].targetRevision).not.toBe(candidate.revision);
});

it("lets a human explicitly refresh the same suggested identity after a separate global label change", async () => {
  const user = userEvent.setup();
  const suggestion = {
    targetId: candidate.id,
    targetRevision: "30000000-0000-0000-0000-000000000001",
    suggestionRunId: candidate.id,
    suggestionProposalId: candidate.id,
    suggestionRunRevision: candidate.revision,
  };
  render(
    <CatalogSourceReview
      source={source}
      suggestion={suggestion}
      onPreview={vi.fn()}
    />,
  );
  await user.click(
    screen.getByRole("button", { name: "Use current candidate details" }),
  );
  await user.click(
    screen.getByRole("button", { name: "Preview historical changes" }),
  );
  expect(mutate).toHaveBeenCalledWith(
    expect.objectContaining({
      ...suggestion,
      source,
      targetRevision: candidate.revision,
    }),
  );
});

it("does not silently advance a manually reviewed target revision during refetch", async () => {
  const user = userEvent.setup();
  const { rerender } = render(
    <CatalogSourceReview source={source} onPreview={vi.fn()} />,
  );
  await user.click(screen.getByRole("radio", { name: /Beef Franks/ }));
  const newer = {
    ...candidate,
    revision: "40000000-0000-0000-0000-000000000001",
    displayLabel: "Beef franks, 8 count",
  };
  candidateState = { ...candidateState, data: [newer] };
  rerender(<CatalogSourceReview source={source} onPreview={vi.fn()} />);
  await user.click(
    screen.getByRole("button", { name: "Preview historical changes" }),
  );
  expect(mutate.mock.calls[0][0].targetRevision).toBe(candidate.revision);
  await user.click(
    screen.getByRole("button", { name: "Use current candidate details" }),
  );
  await user.click(
    screen.getByRole("button", { name: "Preview historical changes" }),
  );
  expect(mutate.mock.calls[1][0]).toMatchObject({
    source,
    targetId: candidate.id,
    targetRevision: newer.revision,
  });
});

it("exclusion clears target and name in the submitted decision and keeps financial facts out of the request", async () => {
  const user = userEvent.setup();
  render(<CatalogSourceReview source={source} onPreview={vi.fn()} />);
  await user.click(screen.getByRole("radio", { name: /Beef Franks/ }));
  await user.click(
    screen.getByRole("checkbox", {
      name: "Exclude this source from normalization",
    }),
  );
  await user.click(
    screen.getByRole("button", { name: "Preview historical changes" }),
  );
  expect(mutate.mock.calls[0][0]).toMatchObject({
    source,
    exclude: true,
    targetId: null,
    targetRevision: null,
    newCanonicalName: null,
  });
  expect(mutate.mock.calls[0][0]).not.toHaveProperty("unitPrice");
});

it("requires revocation confirmation and passes the actual saved revision", async () => {
  const mapping = {
    id: candidate.id,
    revision: candidate.revision,
    source,
    targetId: candidate.id,
    targetName: "Beef Franks",
    state: "active",
    approvedAt: "2026-01-01T00:00:00Z",
  };
  mappingState = { ...mappingState, data: [mapping] };
  const user = userEvent.setup();
  render(<CatalogSourceReview source={source} onPreview={vi.fn()} />);
  await user.click(screen.getByRole("button", { name: "Revoke future reuse" }));
  expect(revoke).not.toHaveBeenCalled();
  expect(screen.getByText(/Existing assignments remain/)).toBeInTheDocument();
  await user.click(screen.getByRole("button", { name: "Confirm revoke" }));
  expect(revoke).toHaveBeenCalledWith(
    mapping,
    expect.objectContaining({ onSuccess: expect.any(Function) }),
  );
});

it("finds an omitted canonical name without replacing the source being approved and invalidates an earlier target choice", async () => {
  const user = userEvent.setup();
  const omitted = {
    ...candidate,
    id: "90000000-0000-0000-0000-000000000001",
    canonicalName: "Correct omitted product",
  };
  vi.mocked(useCatalogCandidates).mockImplementation(
    (key) =>
      ({
        ...candidateState,
        data:
          key.description === "Correct omitted product"
            ? [omitted]
            : [candidate],
      }) as never,
  );
  render(<CatalogSourceReview source={source} onPreview={vi.fn()} />);
  await user.click(screen.getByRole("radio", { name: /Beef Franks/ }));
  await user.type(
    screen.getByRole("textbox", { name: "Find an existing canonical name" }),
    "Correct omitted product",
  );
  await user.click(screen.getByRole("button", { name: "Find existing name" }));
  expect(useCatalogCandidates).toHaveBeenLastCalledWith({
    ...source,
    description: "Correct omitted product",
  });
  expect(
    screen.getByRole("button", { name: "Preview historical changes" }),
  ).toBeDisabled();
  await user.click(
    screen.getByRole("radio", { name: /Correct omitted product/ }),
  );
  await user.click(
    screen.getByRole("button", { name: "Preview historical changes" }),
  );
  expect(mutate).toHaveBeenCalledWith(
    expect.objectContaining({
      source,
      targetId: omitted.id,
      targetRevision: omitted.revision,
    }),
  );
});
