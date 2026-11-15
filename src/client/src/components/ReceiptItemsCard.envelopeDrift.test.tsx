import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ComponentProps } from "react";
import { ReceiptItemsCard } from "./ReceiptItemsCard";
import { renderWithQueryClient } from "@/test/test-utils";
import { mockMutationResult, mockQueryResult } from "@/test/mock-hooks";
import type { components } from "@/generated/api";

type YnabStatus = components["schemas"]["ReceiptYnabSyncStatusValue"];

// The form is replaced by one button that saves a chosen item type, so these
// tests drive only the card's envelope rules.
const form = vi.hoisted(() => ({ categoryId: "household" as string | null }));
vi.mock("@/components/ReceiptItemForm", () => ({
  ReceiptItemForm: ({
    onSubmit,
  }: {
    onSubmit: (values: Record<string, unknown>) => void;
  }) => (
    <button
      type="button"
      onClick={() =>
        onSubmit({
          receiptItemCode: "",
          description: "Steak",
          quantity: 1,
          unitPrice: 10,
          classification:
            form.categoryId === null
              ? undefined
              : {
                  categoryId: form.categoryId,
                  taxonomyRevisionId: "revision-1",
                  expectedAssignmentId: "assignment-1",
                  operationId: "operation-1",
                  reason: "test",
                },
        })
      }
    >
      Save type
    </button>
  ),
}));

const hooks = vi.hoisted(() => ({
  update: vi.fn(),
  setReceipt: vi.fn(),
  setItem: vi.fn(),
  setAutomatic: vi.fn(),
}));
vi.mock("@/hooks/useReceiptItems", () => ({
  useCreateReceiptItem: vi.fn(() => ({ mutate: vi.fn(), isPending: false })),
  // The save succeeds at once; the refetch is modelled by rerendering.
  useUpdateReceiptItem: vi.fn(() => ({
    mutate: (
      variables: unknown,
      options?: { onSuccess?: () => void },
    ) => {
      hooks.update(variables);
      options?.onSuccess?.();
    },
    isPending: false,
  })),
  useDeleteReceiptItems: vi.fn(() => ({ mutate: vi.fn(), isPending: false })),
}));
vi.mock("@/hooks/useBudgetChoices", () => ({
  useSetReceiptBudgetChoices: () =>
    mockMutationResult({ mutate: hooks.setReceipt }),
  useSetReceiptItemBudgetChoices: () =>
    mockMutationResult({ mutate: hooks.setItem }),
  useSetReceiptItemEnvelopeAutomatic: () =>
    mockMutationResult({ mutate: hooks.setAutomatic }),
}));

const envelopes = [
  { id: "env-party", name: "Party", isActive: true },
  { id: "env-groceries", name: "Groceries", isActive: true },
  { id: "env-cleaning", name: "Cleaning", isActive: true },
];
vi.mock("@/hooks/useEnvelopes", () => ({
  useEnvelopes: () => ({
    ...mockQueryResult({ data: { data: envelopes }, isSuccess: true }),
    envelopes,
  }),
  useOccasions: () => ({
    ...mockQueryResult({ data: { data: [] }, isSuccess: true }),
    occasions: [],
  }),
  // Food defaults to Groceries, Household to Cleaning; Party Supplies has none.
  useItemTypeEnvelopeDefaults: () => ({
    ...mockQueryResult({ data: { data: [] }, isSuccess: true }),
    defaults: [
      { categoryId: "food", envelopeId: "env-groceries", updatedAt: "" },
      { categoryId: "household", envelopeId: "env-cleaning", updatedAt: "" },
    ],
  }),
}));

beforeAll(() => {
  Element.prototype.hasPointerCapture ??= () => false;
  Element.prototype.setPointerCapture ??= () => {};
  Element.prototype.releasePointerCapture ??= () => {};
});

beforeEach(() => {
  vi.clearAllMocks();
  form.categoryId = "food";
});

const types = {
  party: { id: "party", name: "Party Supplies", archived: false },
  food: { id: "food", name: "Food", archived: false },
  household: { id: "household", name: "Household", archived: false },
  noDefault: { id: "no-default", name: "Misc", archived: false },
};

function item(
  type: keyof typeof types,
  envelopeId: string | null,
  envelopeSource: components["schemas"]["EnvelopeSource"],
) {
  return {
    id: "item-1",
    receiptItemCode: "",
    description: "Steak",
    quantity: 1,
    unitPrice: 10,
    currentClassification: {
      assignmentId: `assignment-${type}`,
      categoryId: types[type].id,
      taxonomyRevisionId: "revision-1",
      recordedPath: [types[type]],
    },
    envelopeId,
    envelopeSource,
    occasionId: null,
  };
}

type CardProps = ComponentProps<typeof ReceiptItemsCard>;

function renderCard(props: Partial<CardProps> & Pick<CardProps, "items">) {
  const all: CardProps = { receiptId: "receipt-1", subtotal: 10, ...props };
  const view = renderWithQueryClient(<ReceiptItemsCard {...all} />);
  return {
    ...view,
    rerenderWith: (next: Partial<CardProps>) =>
      view.rerender(<ReceiptItemsCard {...all} {...next} />),
  };
}

/** Saves a new type for the item, then lands the refetched item. */
async function retype(
  user: ReturnType<typeof userEvent.setup>,
  view: ReturnType<typeof renderCard>,
  refetched: ReturnType<typeof item>,
) {
  await user.click(screen.getByRole("button", { name: "Edit" }));
  await user.click(screen.getByRole("button", { name: "Save type" }));
  view.rerenderWith({ items: [refetched] });
}

describe("the offer to move an envelope after a type change", () => {
  it("offers the new type's default on a never-pushed receipt and runs Automatic on accept", async () => {
    const user = userEvent.setup();
    const view = renderCard({
      items: [item("party", "env-party", "default")],
      persistedYnabStatus: "notSynced",
    });

    await retype(user, view, item("food", "env-party", "default"));

    expect(
      await screen.findByRole("alertdialog", {
        name: "Also move to Groceries?",
      }),
    ).toBeVisible();
    await user.click(screen.getByRole("button", { name: "Move" }));
    expect(hooks.setAutomatic).toHaveBeenCalledWith({ itemId: "item-1" });
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
  });

  it("leaves the envelope as it was on decline", async () => {
    const user = userEvent.setup();
    const view = renderCard({
      items: [item("party", "env-party", "historical")],
      persistedYnabStatus: "notSynced",
    });

    await retype(user, view, item("food", "env-party", "historical"));
    await user.click(
      await screen.findByRole("button", { name: "Keep envelope" }),
    );

    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    expect(hooks.setAutomatic).not.toHaveBeenCalled();
    expect(hooks.setItem).not.toHaveBeenCalled();
  });

  it("waits for the refetched item to carry the new type", async () => {
    const user = userEvent.setup();
    renderCard({
      items: [item("household", "env-party", "historical")],
      persistedYnabStatus: "notSynced",
    });

    // Saved as Food, but the stale Household item (default Cleaning) is
    // still shown: no offer may name Cleaning.
    await user.click(screen.getByRole("button", { name: "Edit" }));
    await user.click(screen.getByRole("button", { name: "Save type" }));

    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
  });

  it.each<[string, YnabStatus | undefined, ReturnType<typeof item>]>([
    ["the receipt was pushed", "synced", item("food", "env-party", "default")],
    ["a push is pending", "pending", item("food", "env-party", "default")],
    ["a push failed", "failed", item("food", "env-party", "default")],
    ["the status is unknown", "unknown", item("food", "env-party", "default")],
    ["the status is missing", undefined, item("food", "env-party", "default")],
    ["the item has an override", "notSynced", item("food", "env-party", "item")],
    [
      "the envelope came from the paid-from",
      "notSynced",
      item("food", "env-party", "receipt"),
    ],
    [
      "the new type has no default",
      "notSynced",
      item("noDefault", "env-party", "default"),
    ],
    [
      "the default equals the recorded envelope",
      "notSynced",
      item("food", "env-groceries", "default"),
    ],
  ])("is not shown when %s", async (_case, status, refetched) => {
    form.categoryId = refetched.currentClassification.categoryId;
    const user = userEvent.setup();
    const view = renderCard({
      items: [item("party", refetched.envelopeId, refetched.envelopeSource)],
      persistedYnabStatus: status,
    });

    await retype(user, view, refetched);

    expect(
      screen.queryByRole("alertdialog", { name: /Also move/ }),
    ).not.toBeInTheDocument();
    expect(hooks.setAutomatic).not.toHaveBeenCalled();
  });

  it("is not shown when the same type is chosen again under a newer revision", async () => {
    const user = userEvent.setup();
    const view = renderCard({
      items: [item("food", "env-party", "default")],
      persistedYnabStatus: "notSynced",
    });

    await retype(user, view, item("food", "env-party", "default"));

    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
  });

  it("is dropped by a later envelope action", async () => {
    const user = userEvent.setup();
    const view = renderCard({
      items: [item("party", "env-party", "default")],
      persistedYnabStatus: "notSynced",
    });
    // Saved as Food; before the refetch lands, the person overrides the envelope.
    await user.click(screen.getByRole("button", { name: "Edit" }));
    await user.click(screen.getByRole("button", { name: "Save type" }));
    await user.click(
      screen.getByRole("button", { name: "More actions for Steak" }),
    );
    await user.click(screen.getByRole("menuitemradio", { name: "Cleaning" }));

    view.rerenderWith({ items: [item("food", "env-party", "default")] });

    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    expect(hooks.setItem).toHaveBeenCalledTimes(1);
  });

  it("is not shown when the save did not change the type", async () => {
    form.categoryId = null;
    const user = userEvent.setup();
    const view = renderCard({
      items: [item("food", "env-party", "default")],
      persistedYnabStatus: "notSynced",
    });

    await retype(user, view, item("food", "env-party", "default"));

    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
  });
});

describe("the warning before an envelope change YNAB will not follow", () => {
  async function openMenu(user: ReturnType<typeof userEvent.setup>) {
    await user.click(
      screen.getByRole("button", { name: "More actions for Steak" }),
    );
  }

  const actions = {
    override: async (user: ReturnType<typeof userEvent.setup>) => {
      await openMenu(user);
      await user.click(screen.getByRole("menuitemradio", { name: "Groceries" }));
    },
    automatic: async (user: ReturnType<typeof userEvent.setup>) => {
      await openMenu(user);
      await user.click(screen.getByRole("menuitem", { name: /Automatic/ }));
    },
    "paid-from": async (user: ReturnType<typeof userEvent.setup>) => {
      await user.click(screen.getByRole("combobox", { name: "Paid from" }));
      await user.click(screen.getByRole("option", { name: "Party" }));
    },
  };
  const calls = {
    override: () => hooks.setItem,
    automatic: () => hooks.setAutomatic,
    "paid-from": () => hooks.setReceipt,
  };

  describe.each(Object.keys(actions) as (keyof typeof actions)[])(
    "for %s",
    (action) => {
      it.each<YnabStatus | undefined>([
        "synced",
        "pending",
        "failed",
        "unknown",
        undefined,
      ])("warns when the status is %s, and applies only after confirming", async (status) => {
        const user = userEvent.setup();
        renderCard({
          items: [item("food", "env-cleaning", "default")],
          persistedYnabStatus: status,
        });

        await actions[action](user);

        expect(
          await screen.findByRole("alertdialog", {
            name: "YNAB will not be updated",
          }),
        ).toBeVisible();
        expect(calls[action]()).not.toHaveBeenCalled();
        await user.click(screen.getByRole("button", { name: "Change locally" }));
        expect(calls[action]()).toHaveBeenCalledTimes(1);
      });

      it("changes nothing when cancelled", async () => {
        const user = userEvent.setup();
        renderCard({
          items: [item("food", "env-cleaning", "default")],
          persistedYnabStatus: "synced",
        });

        await actions[action](user);
        await user.click(
          await screen.findByRole("button", { name: "Cancel" }),
        );

        expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
        expect(calls[action]()).not.toHaveBeenCalled();
      });

      it("applies at once on a never-pushed receipt", async () => {
        const user = userEvent.setup();
        renderCard({
          items: [item("food", "env-cleaning", "default")],
          persistedYnabStatus: "notSynced",
        });

        await actions[action](user);

        expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
        expect(calls[action]()).toHaveBeenCalledTimes(1);
      });
    },
  );
});
