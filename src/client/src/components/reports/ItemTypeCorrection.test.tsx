import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ItemTypeCorrection } from "./ItemTypeCorrection";
import type { CategoryRevision } from "@/lib/category-paths";
import type { components } from "@/generated/api";

type Suggestion = components["schemas"]["UncategorizedItemSuggestion"];

const FOOD = "96000000-0000-4000-8000-000000000010";
const DAIRY = "96000000-0000-4000-8000-000000000011";
const FRUIT = "96000000-0000-4000-8000-000000000012";
const RETIRED = "96000000-0000-4000-8000-000000000013";
const RETIRED_CHILD = "96000000-0000-4000-8000-000000000014";

const revision: CategoryRevision = {
  id: "96000000-0000-4000-8000-000000000001",
  sequence: 3,
  recordedAt: "2026-10-30T00:00:00Z",
  actor: "fixture",
  reason: "Item type correction test taxonomy",
  operationId: "96000000-0000-4000-8000-000000000002",
  affectedCount: 0,
  nodes: [
    { id: FOOD, name: "Food", parentId: null, archived: false },
    { id: DAIRY, name: "Dairy", parentId: FOOD, archived: false },
    { id: FRUIT, name: "Fruit", parentId: FOOD, archived: false },
    { id: RETIRED, name: "Retired", parentId: null, archived: true },
    {
      id: RETIRED_CHILD,
      name: "Leftover",
      parentId: RETIRED,
      archived: false,
    },
  ],
};

function suggestion(overrides: Partial<Suggestion> = {}): Suggestion {
  return {
    attemptId: "96000000-0000-4000-8000-000000000100",
    state: "suggested",
    categoryId: DAIRY,
    confidence: 0.874,
    abstentionReason: null,
    rootCandidates: [{ categoryId: FOOD, probability: 0.95 }],
    leafCandidates: [
      { categoryId: DAIRY, probability: 0.874 },
      { categoryId: FRUIT, probability: 0.08 },
    ],
    ...overrides,
  };
}

function renderCorrection(
  props: Partial<Parameters<typeof ItemTypeCorrection>[0]> = {},
) {
  const onChoose = vi.fn();
  render(
    <ItemTypeCorrection
      description="Milk"
      revision={revision}
      onChoose={onChoose}
      {...props}
    />,
  );
  return { onChoose };
}

describe("ItemTypeCorrection", () => {
  it("shows Jev's live choice with its confidence and writes it on Use", async () => {
    const user = userEvent.setup();
    const { onChoose } = renderCorrection({ suggestion: suggestion() });

    expect(screen.getByText("Food / Dairy")).toBeInTheDocument();
    expect(screen.getByText(/87%/)).toBeInTheDocument();

    await user.click(
      screen.getByRole("button", { name: "Use Jev's choice for Milk" }),
    );

    expect(onChoose).toHaveBeenCalledTimes(1);
    expect(onChoose).toHaveBeenCalledWith(
      {
        categoryId: DAIRY,
        taxonomyRevisionId: revision.id,
        path: [revision.nodes[0], revision.nodes[1]],
      },
      true,
    );
    expect(screen.queryByText(/earlier answer/)).not.toBeInTheDocument();
  });

  it("marks a stale answer as an earlier answer and still offers it", async () => {
    const user = userEvent.setup();
    const { onChoose } = renderCorrection({
      suggestion: suggestion({ state: "stale" }),
    });

    expect(screen.getByText(/earlier answer/)).toBeInTheDocument();
    await user.click(
      screen.getByRole("button", { name: "Use Jev's choice for Milk" }),
    );
    expect(onChoose).toHaveBeenCalledWith(
      expect.objectContaining({ categoryId: DAIRY }),
      true,
    );
  });

  it("disables Use while a write is in flight", () => {
    renderCorrection({ suggestion: suggestion(), busy: true });
    expect(
      screen.getByRole("button", { name: "Use Jev's choice for Milk" }),
    ).toBeDisabled();
  });

  it.each([
    ["sits under an archived ancestor", RETIRED_CHILD],
    ["is itself archived", RETIRED],
    ["is not in the current taxonomy", "96000000-0000-4000-8000-0000000000ff"],
  ])("offers no Use when the proposed category %s", (_, categoryId) => {
    renderCorrection({ suggestion: suggestion({ categoryId }) });
    expect(
      screen.queryByRole("button", { name: /Use Jev's choice/ }),
    ).not.toBeInTheDocument();
    expect(screen.getByText("Jev wasn't sure")).toBeInTheDocument();
  });

  it("offers no Use until the taxonomy has loaded", () => {
    renderCorrection({
      suggestion: suggestion(),
      revision: undefined,
      loading: true,
    });
    expect(
      screen.queryByRole("button", { name: /Use Jev's choice/ }),
    ).not.toBeInTheDocument();
  });

  it("says Jev wasn't sure when it abstained", () => {
    renderCorrection({
      suggestion: suggestion({
        state: "abstained",
        categoryId: null,
        confidence: null,
        abstentionReason: "low_confidence",
      }),
    });
    expect(screen.getByText("Jev wasn't sure")).toBeInTheDocument();
    expect(
      screen.queryByRole("button", { name: /Use Jev's choice/ }),
    ).not.toBeInTheDocument();
    expect(
      screen.getByRole("combobox", { name: "Category for Milk" }),
    ).toBeInTheDocument();
  });

  it("lists Jev's candidates under Suggested in the picker, without Unclassified", async () => {
    const user = userEvent.setup();
    renderCorrection({
      suggestion: suggestion({ state: "abstained", categoryId: null }),
    });

    await user.click(
      screen.getByRole("combobox", { name: "Category for Milk" }),
    );

    expect(
      within(screen.getByRole("group", { name: "Suggested" }))
        .getAllByRole("option")
        .map((option) => option.getAttribute("aria-label")),
    ).toEqual([
      "Food / Dairy (suggested, 87% confidence)",
      "Food / Fruit (suggested, 8% confidence)",
      "Food (suggested, 95% confidence)",
    ]);
    expect(
      screen.queryByRole("option", { name: "Unclassified" }),
    ).not.toBeInTheDocument();
  });

  it("writes a category picked from the list", async () => {
    const user = userEvent.setup();
    const { onChoose } = renderCorrection({ suggestion: suggestion() });

    await user.click(
      screen.getByRole("combobox", { name: "Category for Milk" }),
    );
    await user.click(
      within(screen.getByRole("group", { name: "Suggested" })).getByRole(
        "option",
        { name: "Food / Fruit (suggested, 8% confidence)" },
      ),
    );

    expect(onChoose).toHaveBeenCalledWith(
      expect.objectContaining({
        categoryId: FRUIT,
        taxonomyRevisionId: revision.id,
      }),
      false,
    );
  });

  it("shows only the picker when Jev has not been asked", async () => {
    const user = userEvent.setup();
    renderCorrection({ suggestion: null });

    expect(screen.queryByText(/Jev/)).not.toBeInTheDocument();
    expect(
      screen.queryByRole("button", { name: /Use Jev's choice/ }),
    ).not.toBeInTheDocument();

    await user.click(
      screen.getByRole("combobox", { name: "Category for Milk" }),
    );
    expect(
      screen.queryByRole("group", { name: "Suggested" }),
    ).not.toBeInTheDocument();
  });
});
