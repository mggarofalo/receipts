import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { CatalogAiReview } from "./CatalogAiReview";
import { useAiActions, type AiRun } from "@/hooks/useNormalizationAi";
vi.mock("@/hooks/useNormalizationAi", () => ({ useAiActions: vi.fn() }));
vi.mock("./CatalogLabelReview", () => ({
  CatalogLabelReview: (props: { suggestion?: unknown }) => (
    <div data-testid="label-review">
      {JSON.stringify(props.suggestion ?? null)}
    </div>
  ),
}));
const cancel = vi.fn();
const retry = vi.fn();
const source = {
  location: "Store A",
  description: "BF FRANKS",
  itemCode: "001",
};
const proposal = {
  id: "10000000-0000-0000-0000-000000000001",
  receiptItemId: "20000000-0000-0000-0000-000000000001",
  description: source.description,
  action: "reuse",
  state: "pending",
  targetId: "30000000-0000-0000-0000-000000000001",
  targetRevision: "40000000-0000-0000-0000-000000000001",
  displayLabel: null,
  canonicalName: "Beef Franks",
  reason: "Same product evidence.",
  source,
  includeLegacyUnknown: true,
};
const run = {
  id: "50000000-0000-0000-0000-000000000001",
  revision: "60000000-0000-0000-0000-000000000001",
  clientRequestId: "70000000-0000-0000-0000-000000000001",
  state: "ready",
  model: "explicit-model",
  createdAt: "2026-01-01T00:00:00Z",
  completedAt: null,
  errorCode: null,
  errorDetail: null,
  proposals: [proposal],
  usage: null,
} satisfies AiRun;
beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(useAiActions).mockReturnValue({
    cancel: { mutate: cancel, isPending: false, error: null },
    retry: { mutate: retry, isPending: false, error: null },
  } as never);
});

it.each([true, false])(
  "separates label approval provenance from source assignment when labelOnly=%s",
  async (labelOnly) => {
    const onReview = vi.fn();
    render(
      <CatalogAiReview
        run={{
          ...run,
          proposals: [
            {
              ...proposal,
              labelOnly,
              suggestedDisplayLabel: "Beef franks, 8 count",
            },
          ],
        }}
        onReview={onReview}
        onRetried={vi.fn()}
      />,
    );
    const mapping = screen.queryByRole("button", {
      name: "Review source and preview",
    });
    if (labelOnly) expect(mapping).not.toBeInTheDocument();
    else expect(mapping).toBeEnabled();
    await userEvent
      .setup()
      .click(
        screen.getByRole("button", { name: "Review global display name" }),
      );
    expect(screen.getByTestId("label-review")).toHaveTextContent(
      JSON.stringify(
        labelOnly
          ? {
              runId: run.id,
              proposalId: proposal.id,
              runRevision: run.revision,
            }
          : null,
      ),
    );
    expect(onReview).not.toHaveBeenCalled();
  },
);

it("reports edited label approval without claiming a historical source mapping", () => {
  render(
    <CatalogAiReview
      run={{
        ...run,
        state: "applied",
        proposals: [
          {
            ...proposal,
            state: "overridden",
            labelOnly: true,
            suggestedDisplayLabel: "Beef franks",
          },
        ],
      }}
      onReview={vi.fn()}
      onRetried={vi.fn()}
    />,
  );
  expect(
    screen.getByText(
      "Display name approved with edits; purchase assignments unchanged.",
    ),
  ).toBeInTheDocument();
  expect(
    screen.queryByText(/See historical operations/),
  ).not.toBeInTheDocument();
  expect(
    screen.queryByRole("button", { name: "Review global display name" }),
  ).not.toBeInTheDocument();
});

it("renders a completed label-only no-change outcome without an unusable approval action", () => {
  render(
    <CatalogAiReview
      run={{
        ...run,
        proposals: [
          {
            ...proposal,
            state: "no_change",
            labelOnly: true,
            suggestedDisplayLabel: null,
          },
        ],
      }}
      onReview={vi.fn()}
      onRetried={vi.fn()}
    />,
  );
  expect(
    screen.getByText("No display-name change proposed"),
  ).toBeInTheDocument();
  expect(
    screen.getByText("Completed without changing the display name."),
  ).toBeInTheDocument();
  expect(
    screen.queryByRole("button", { name: "Review global display name" }),
  ).not.toBeInTheDocument();
  expect(
    screen.queryByRole("button", { name: "Review source and preview" }),
  ).not.toBeInTheDocument();
});

it("requires a separate scoped historical review instead of directly applying the model answer", async () => {
  const review = vi.fn();
  render(<CatalogAiReview run={run} onReview={review} onRetried={vi.fn()} />);
  expect(
    screen.getByText(/Suggestions do not change your catalog/),
  ).toBeInTheDocument();
  await userEvent
    .setup()
    .click(screen.getByRole("button", { name: "Review source and preview" }));
  expect(review).toHaveBeenCalledWith(
    source,
    expect.objectContaining({
      targetId: proposal.targetId,
      targetRevision: proposal.targetRevision,
      suggestionRunId: run.id,
      suggestionProposalId: proposal.id,
      suggestionRunRevision: run.revision,
      includeLegacyUnknown: true,
    }),
  );
  expect(
    screen.queryByRole("button", { name: /apply/i }),
  ).not.toBeInTheDocument();
});

it("offers manual review after abstention without pretending the human choice was an AI approval", async () => {
  const review = vi.fn();
  render(
    <CatalogAiReview
      run={{
        ...run,
        state: "applied",
        proposals: [
          {
            ...proposal,
            action: "abstain",
            state: "abstained",
            targetId: null,
            targetRevision: null,
            canonicalName: null,
          },
        ],
      }}
      onReview={review}
      onRetried={vi.fn()}
    />,
  );
  expect(screen.getByText("Needs manual review")).toBeInTheDocument();
  await userEvent
    .setup()
    .click(screen.getByRole("button", { name: "Review source and preview" }));
  expect(review).toHaveBeenCalledWith(
    source,
    expect.objectContaining({
      targetId: null,
      newCanonicalName: null,
      suggestionRunId: undefined,
      suggestionProposalId: undefined,
      suggestionRunRevision: undefined,
    }),
  );
});

it("retains unknown outcome guidance and retries only on explicit action using stable identity", async () => {
  const user = userEvent.setup();
  render(
    <CatalogAiReview
      run={{
        ...run,
        state: "unknown",
        errorDetail: "The provider may have processed the request.",
      }}
      onReview={vi.fn()}
      onRetried={vi.fn()}
    />,
  );
  expect(screen.getByRole("alert")).toHaveTextContent(
    "may have consumed allowance",
  );
  expect(retry).not.toHaveBeenCalled();
  expect(
    screen.queryByRole("button", { name: "Review source and preview" }),
  ).not.toBeInTheDocument();
  await user.click(
    screen.getByRole("button", { name: "Retry as a new request" }),
  );
  await user.click(
    screen.getByRole("button", { name: "Retry as a new request" }),
  );
  expect(retry.mock.calls[0][0]).toEqual(retry.mock.calls[1][0]);
  expect(retry.mock.calls[0][0]).toMatchObject({
    id: run.id,
    clientRequestId: expect.stringMatching(/^[0-9a-f-]{36}$/),
  });
  await user.click(screen.getByRole("button", { name: "Cancel request" }));
  expect(cancel).toHaveBeenCalledWith(run.id);
});

it("shows a human override honestly and prevents replaying the original model decision", () => {
  render(
    <CatalogAiReview
      run={{
        ...run,
        state: "applied",
        proposals: [{ ...proposal, state: "overridden" }],
      }}
      onReview={vi.fn()}
      onRetried={vi.fn()}
    />,
  );
  expect(
    screen.getByText(/Reviewed with a different decision/),
  ).toBeInTheDocument();
  expect(screen.queryByText("Approved as suggested.")).not.toBeInTheDocument();
  expect(
    screen.getByRole("button", { name: "Review source and preview" }),
  ).toBeDisabled();
});

it.each(["queued", "running", "failed", "unknown"])(
  "hides unvalidated catalog proposals for %s",
  (state) => {
    render(
      <CatalogAiReview
        run={{ ...run, state }}
        onReview={vi.fn()}
        onRetried={vi.fn()}
      />,
    );
    expect(screen.queryByText(source.description)).not.toBeInTheDocument();
    expect(
      screen.queryByRole("button", { name: "Review source and preview" }),
    ).not.toBeInTheDocument();
  },
);

it.each(["applied", "overridden", "no_change"])(
  "preserves %s evidence after discarding a partially reviewed run",
  async (state) => {
    const reviewed = { ...proposal, state };
    const pending = {
      ...proposal,
      id: "10000000-0000-0000-0000-000000000002",
      description: "Unreviewed sibling",
    };
    const onReview = vi.fn();
    const onRetried = vi.fn();
    const partialRun = { ...run, proposals: [reviewed, pending] };
    const { rerender } = render(
      <CatalogAiReview
        run={partialRun}
        onReview={onReview}
        onRetried={onRetried}
      />,
    );
    await userEvent
      .setup()
      .click(screen.getByRole("button", { name: "Discard suggestions" }));
    expect(cancel).toHaveBeenCalledWith(run.id);
    rerender(
      <CatalogAiReview
        run={{ ...partialRun, state: "cancelled" }}
        onReview={onReview}
        onRetried={onRetried}
      />,
    );
    expect(
      screen.getByRole("heading", { name: source.description }),
    ).toBeVisible();
    expect(
      screen.queryByRole("heading", { name: "Unreviewed sibling" }),
    ).not.toBeInTheDocument();
    expect(screen.getByRole("status")).toHaveTextContent("Cancelled");
    expect(
      screen.queryByRole("button", { name: "Discard suggestions" }),
    ).not.toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "Review source and preview" }),
    ).toBeDisabled();
  },
);
