import { describe, it, expect, vi, beforeEach, beforeAll, type Mock } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MergeCardsDialog } from "./MergeCardsDialog";

// jsdom polyfills required by Radix UI Select/Dialog
beforeAll(() => {
  if (!(Element.prototype as unknown as { hasPointerCapture?: unknown }).hasPointerCapture) {
    Element.prototype.hasPointerCapture = () => false;
    Element.prototype.releasePointerCapture = () => {};
    Element.prototype.setPointerCapture = () => {};
  }
  if (!(Element.prototype as unknown as { scrollIntoView?: unknown }).scrollIntoView) {
    Element.prototype.scrollIntoView = () => {};
  }
});

vi.mock("@/lib/api-client", () => ({
  default: {
    GET: vi.fn(),
    POST: vi.fn(),
    PUT: vi.fn(),
    DELETE: vi.fn(),
  },
}));

vi.mock("sonner", () => ({
  toast: { success: vi.fn(), error: vi.fn(), info: vi.fn() },
}));

// The impact preview (RECEIPTS-889) is its own POST, and letting it share the
// client.POST mock with the merge call would make every test's call ordering depend
// on when the preview happened to fire. The hook's own behaviour is covered in
// useCards.test.ts; here it is stubbed, and the tests that care set it explicitly.
vi.mock("@/hooks/useCards", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/hooks/useCards")>();
  return { ...actual, useMergeCardsPreview: vi.fn(() => ({ data: undefined, isFetching: false })) };
});

import client from "@/lib/api-client";
import { toast } from "sonner";
import { useMergeCardsPreview } from "@/hooks/useCards";

function renderDialog(overrides: Partial<React.ComponentProps<typeof MergeCardsDialog>> = {}) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  });
  // Both cards sit on a source account, not on "a1" — the target the tests pick.
  // A merge from here genuinely moves something, which is what most of these
  // tests are about; the no-op guard is exercised explicitly further down.
  const selectedCards = overrides.selectedCards ?? [
    { id: "c1", name: "Primary Visa", cardCode: "1234", accountId: "a-source" },
    { id: "c2", name: "Reissued Visa", cardCode: "5678", accountId: "a-source" },
  ];
  const onOpenChange = vi.fn();
  const props = {
    open: true,
    onOpenChange,
    selectedCards,
    ...overrides,
  };
  const result = render(
    <QueryClientProvider client={queryClient}>
      <MergeCardsDialog {...props} />
    </QueryClientProvider>,
  );
  return { ...result, onOpenChange };
}

const okEmpty = { data: undefined, error: undefined, response: { status: 204, ok: true } };

/**
 * Cards belonging to each account, as `GET /api/accounts/{id}/cards` returns them.
 *
 * "a-source" holds exactly the two cards the default selection covers, so the
 * completeness check (RECEIPTS-888) passes and the pre-existing tests are unaffected.
 * Tests about a partial merge override this.
 */
let cardsByAccount: Record<string, { id: string; name: string; cardCode: string }[]> = {};

beforeEach(() => {
  vi.clearAllMocks();
  cardsByAccount = {
    "a-source": [
      { id: "c1", name: "Primary Visa", cardCode: "1234" },
      { id: "c2", name: "Reissued Visa", cardCode: "5678" },
    ],
    a1: [],
  };
  // The dialog issues two different GETs: the accounts list for the target picker,
  // and one card list per source account in the selection.
  (client.GET as Mock).mockImplementation((path: string, opts?: { params?: { path?: { id?: string } } }) => {
    if (path === "/api/accounts/{id}/cards") {
      const id = opts?.params?.path?.id ?? "";
      return Promise.resolve({ data: cardsByAccount[id] ?? [], error: undefined });
    }
    return Promise.resolve({
      data: {
        data: [
          { id: "a1", name: "Account One", isActive: true },
          // Named so the dialog can show where each selected card lives; the tests
          // that pick a target always name "Account One" explicitly.
          { id: "a-source", name: "Source Account", isActive: true },
        ],
        total: 2,
        offset: 0,
        limit: 500,
      },
      error: undefined,
    });
  });
  (client.DELETE as Mock).mockResolvedValue(okEmpty);
  (client.PUT as Mock).mockResolvedValue(okEmpty);
});

describe("MergeCardsDialog", () => {
  it("renders the selected cards", () => {
    renderDialog();
    expect(screen.getByText("Primary Visa")).toBeInTheDocument();
    expect(screen.getByText("Reissued Visa")).toBeInTheDocument();
    expect(screen.getByText(/merging 2 cards/i)).toBeInTheDocument();
  });

  it("submit button is disabled until target account is selected", () => {
    renderDialog();
    const submit = screen.getByRole("button", { name: /^merge$/i });
    expect(submit).toBeDisabled();
  });

  it("submits merge request with selected target and closes on success", async () => {
    const user = userEvent.setup();
    (client.POST as Mock).mockResolvedValue({
      data: { accountsRemoved: 1, cardsMoved: 2, transactionsRepointed: 3 },
      error: undefined,
      response: { status: 200, ok: true },
    });

    const { onOpenChange } = renderDialog();

    // Open the Select dropdown and pick the account
    const trigger = screen.getByLabelText("Target account");
    await user.click(trigger);
    const option = await screen.findByRole("option", { name: "Account One" });
    await user.click(option);

    const submit = screen.getByRole("button", { name: /^merge$/i });
    expect(submit).not.toBeDisabled();
    await user.click(submit);

    await vi.waitFor(() => {
      expect(client.POST).toHaveBeenCalledWith("/api/cards/merge", {
        body: {
          targetAccountId: "a1",
          sourceCardIds: ["c1", "c2"],
          ynabMappingWinnerAccountId: null,
        },
      });
    });
    await vi.waitFor(() => {
      expect(onOpenChange).toHaveBeenCalledWith(false);
    });
  });

  it("creates new account, hits conflict, cancels — deletes the newly-created account", async () => {
    const user = userEvent.setup();

    // First POST: create account (for target). Second POST: merge → 409 conflict.
    (client.POST as Mock)
      .mockResolvedValueOnce({
        data: { id: "new-acc-1", name: "Fresh Account", isActive: true },
        error: undefined,
        response: { status: 200, ok: true },
      })
      .mockResolvedValueOnce({
        error: {
          message: "conflict",
          conflicts: [
            { accountId: "srcA", accountName: "Src A", ynabBudgetId: "b", ynabAccountId: "y1", ynabAccountName: "YA" },
            { accountId: "srcB", accountName: "Src B", ynabBudgetId: "b", ynabAccountId: "y2", ynabAccountName: "YB" },
          ],
        },
        response: { status: 409, ok: false },
      });

    const { onOpenChange } = renderDialog();

    await user.click(screen.getByLabelText("New account"));
    await user.type(screen.getByLabelText(/new account name/i), "Fresh Account");
    await user.click(screen.getByRole("button", { name: /^merge$/i }));

    expect(await screen.findByText(/ynab mapping conflict/i)).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: /cancel/i }));

    await vi.waitFor(() => {
      expect(client.DELETE).toHaveBeenCalledWith("/api/accounts/{id}", {
        params: { path: { id: "new-acc-1" } },
      });
    });
    expect(onOpenChange).toHaveBeenCalledWith(false);
  });

  it("shows conflict alert on 409 and resubmits with winner", async () => {
    const user = userEvent.setup();
    (client.POST as Mock).mockResolvedValueOnce({
      error: {
        message: "conflict",
        conflicts: [
          { accountId: "srcA", accountName: "Src A", ynabBudgetId: "b", ynabAccountId: "y1", ynabAccountName: "YA" },
          { accountId: "srcB", accountName: "Src B", ynabBudgetId: "b", ynabAccountId: "y2", ynabAccountName: "YB" },
        ],
      },
      response: { status: 409, ok: false },
    });

    renderDialog();

    const trigger = screen.getByLabelText("Target account");
    await user.click(trigger);
    const option = await screen.findByRole("option", { name: "Account One" });
    await user.click(option);

    await user.click(screen.getByRole("button", { name: /^merge$/i }));

    expect(await screen.findByText(/ynab mapping conflict/i)).toBeInTheDocument();
    expect(screen.getByText(/Src A/)).toBeInTheDocument();
    expect(screen.getByText(/Src B/)).toBeInTheDocument();

    // Resubmit disabled until a winner is picked
    const resubmit = screen.getByRole("button", { name: /resubmit/i });
    expect(resubmit).toBeDisabled();

    await user.click(screen.getByLabelText(/Src A/));
    expect(resubmit).not.toBeDisabled();

    (client.POST as Mock).mockResolvedValueOnce({
      data: { accountsRemoved: 1, cardsMoved: 2, transactionsRepointed: 3 },
      error: undefined,
      response: { status: 200, ok: true },
    });
    await user.click(resubmit);

    await vi.waitFor(() => {
      expect((client.POST as Mock).mock.calls).toHaveLength(2);
    });
    const secondCall = (client.POST as Mock).mock.calls[1];
    expect(secondCall[1].body.ynabMappingWinnerAccountId).toBe("srcA");
  });

  // RECEIPTS-894. The "reuse the already-created account" branch was written for
  // the conflict-resolution retry but fired after any failure, so a corrected
  // name was silently dropped and the merge landed on the original one.
  describe("new-account target after a failed merge", () => {
    const created = {
      data: { id: "new-acc-1", name: "Fresh Account", isActive: true },
      error: undefined,
      response: { status: 200, ok: true },
    };
    // A bare-string 400 — a partial source-account merge, the most common
    // non-conflict rejection. Not a 409, so it is not the conflict path.
    const mergeRejected = {
      error: "Source account would be partially merged.",
      response: { status: 400, ok: false },
    };

    async function createThenFailMerge(user: ReturnType<typeof userEvent.setup>) {
      (client.POST as Mock)
        .mockResolvedValueOnce(created)
        .mockResolvedValueOnce(mergeRejected);

      await user.click(screen.getByLabelText("New account"));
      await user.type(screen.getByLabelText(/new account name/i), "Fresh Account");
      await user.click(screen.getByRole("button", { name: /^merge$/i }));

      // The dialog must stay open on a rejected merge so the user can correct it.
      await vi.waitFor(() => {
        expect((client.POST as Mock).mock.calls).toHaveLength(2);
      });
    }

    it("applies an edited name to the already-created account instead of ignoring it", async () => {
      const user = userEvent.setup();
      const { onOpenChange } = renderDialog();
      await createThenFailMerge(user);

      const nameInput = screen.getByLabelText(/new account name/i);
      await user.clear(nameInput);
      await user.type(nameInput, "Corrected Account");

      (client.POST as Mock).mockResolvedValueOnce({
        data: { accountsRemoved: 1, cardsMoved: 2, transactionsRepointed: 3 },
        error: undefined,
        response: { status: 200, ok: true },
      });
      await user.click(screen.getByRole("button", { name: /^merge$/i }));

      // The correction is applied to the account we already made...
      await vi.waitFor(() => {
        expect(client.PUT).toHaveBeenCalledWith("/api/accounts/{id}", {
          params: { path: { id: "new-acc-1" } },
          body: { id: "new-acc-1", name: "Corrected Account", isActive: true },
        });
      });

      // ...rather than leaking a second one.
      const createCalls = (client.POST as Mock).mock.calls.filter(
        ([url]) => url === "/api/accounts",
      );
      expect(createCalls).toHaveLength(1);

      const merges = (client.POST as Mock).mock.calls.filter(
        ([url]) => url === "/api/cards/merge",
      );
      expect(merges[merges.length - 1][1].body.targetAccountId).toBe("new-acc-1");
      await vi.waitFor(() => expect(onOpenChange).toHaveBeenCalledWith(false));
    });

    it("does not rename when the name is unchanged on retry", async () => {
      const user = userEvent.setup();
      renderDialog();
      await createThenFailMerge(user);

      (client.POST as Mock).mockResolvedValueOnce({
        data: { accountsRemoved: 1, cardsMoved: 2, transactionsRepointed: 3 },
        error: undefined,
        response: { status: 200, ok: true },
      });
      await user.click(screen.getByRole("button", { name: /^merge$/i }));

      await vi.waitFor(() => {
        const merges = (client.POST as Mock).mock.calls.filter(
          ([url]) => url === "/api/cards/merge",
        );
        expect(merges).toHaveLength(2);
      });
      expect(client.PUT).not.toHaveBeenCalled();
    });

    it("discards the created account when the merge lands on a different target", async () => {
      const user = userEvent.setup();
      renderDialog();
      await createThenFailMerge(user);

      // Change of mind: use an existing account instead of the one just created.
      await user.click(screen.getByLabelText("Existing account"));
      await user.click(screen.getByLabelText("Target account"));
      await user.click(await screen.findByRole("option", { name: "Account One" }));

      (client.POST as Mock).mockResolvedValueOnce({
        data: { accountsRemoved: 1, cardsMoved: 2, transactionsRepointed: 3 },
        error: undefined,
        response: { status: 200, ok: true },
      });
      await user.click(screen.getByRole("button", { name: /^merge$/i }));

      // A successful merge used to clear the ref unconditionally, stranding the
      // account the dialog had created but never used.
      await vi.waitFor(() => {
        expect(client.DELETE).toHaveBeenCalledWith("/api/accounts/{id}", {
          params: { path: { id: "new-acc-1" } },
        });
      });
    });

    it("reports a cleanup delete that fails rather than leaking silently", async () => {
      const user = userEvent.setup();
      renderDialog();
      await createThenFailMerge(user);

      (client.DELETE as Mock).mockResolvedValue({
        error: { status: 403 },
        response: { status: 403, ok: false },
      });

      await user.click(screen.getByRole("button", { name: /cancel/i }));

      await vi.waitFor(() => {
        expect(toast.error).toHaveBeenCalledWith(
          expect.stringMatching(/couldn't remove the empty account/i),
        );
      });
    });
  });

  // RECEIPTS-893. The server reports a no-op honestly now, but the better fix is
  // for the user never to reach one by accident: if every selected card already
  // sits on the chosen target, say so at the point of choosing.
  describe("target that would change nothing", () => {
    const cardsAlreadyOnA1 = [
      { id: "c1", name: "Primary Visa", cardCode: "1234", accountId: "a1" },
      { id: "c2", name: "Reissued Visa", cardCode: "5678", accountId: "a1" },
    ];

    it("blocks submit and explains why when every card already sits on the target", async () => {
      const user = userEvent.setup();
      renderDialog({ selectedCards: cardsAlreadyOnA1 });

      await user.click(screen.getByLabelText("Target account"));
      await user.click(await screen.findByRole("option", { name: "Account One" }));

      expect(
        await screen.findByText(/every selected card already belongs to this account/i),
      ).toBeInTheDocument();
      expect(screen.getByRole("button", { name: /^merge$/i })).toBeDisabled();
      expect(client.POST).not.toHaveBeenCalled();
    });

    it("still allows the merge when only some of the cards are already on the target", async () => {
      const user = userEvent.setup();
      // Keep the account fixtures consistent with where the cards say they live:
      // c1 sits on a1, so a-source holds only c2.
      cardsByAccount = {
        "a-source": [{ id: "c2", name: "Reissued Visa", cardCode: "5678" }],
        a1: [{ id: "c1", name: "Primary Visa", cardCode: "1234" }],
      };
      renderDialog({
        selectedCards: [
          cardsAlreadyOnA1[0],
          { id: "c2", name: "Reissued Visa", cardCode: "5678", accountId: "a-source" },
        ],
      });

      await user.click(screen.getByLabelText("Target account"));
      await user.click(await screen.findByRole("option", { name: "Account One" }));

      expect(
        screen.queryByText(/every selected card already belongs to this account/i),
      ).not.toBeInTheDocument();
      expect(screen.getByRole("button", { name: /^merge$/i })).not.toBeDisabled();
    });

    it("does not block 'New account' mode, which cannot already hold the cards", async () => {
      const user = userEvent.setup();
      renderDialog({ selectedCards: cardsAlreadyOnA1 });

      await user.click(screen.getByLabelText("New account"));
      await user.type(screen.getByLabelText(/new account name/i), "Fresh Account");

      expect(screen.getByRole("button", { name: /^merge$/i })).not.toBeDisabled();
    });
  });

  // RECEIPTS-888. The server refuses a merge that would leave cards behind on an
  // account it is about to delete. That rule is right, but it used to arrive only
  // after submitting, naming no cards — and the siblings might be on another page
  // of /cards, so the user could not have selected them even if they had known.
  describe("a source account that would only be partly merged", () => {
    const oneOfThree = [
      { id: "c1", name: "Primary Visa", cardCode: "1234", accountId: "a-source" },
    ];

    function withThreeCardSource() {
      cardsByAccount = {
        "a-source": [
          { id: "c1", name: "Primary Visa", cardCode: "1234" },
          { id: "c2", name: "Reissued Visa", cardCode: "5678" },
          { id: "c3", name: "Spare Visa", cardCode: "9012" },
        ],
        a1: [],
      };
    }

    async function chooseTarget(user: ReturnType<typeof userEvent.setup>) {
      await user.click(screen.getByLabelText("Target account"));
      await user.click(await screen.findByRole("option", { name: "Account One" }));
    }

    it("names the account and the cards left out, and blocks submit", async () => {
      const user = userEvent.setup();
      withThreeCardSource();
      renderDialog({ selectedCards: oneOfThree });

      await chooseTarget(user);

      expect(
        await screen.findByText(/one account would be left with cards behind/i),
      ).toBeInTheDocument();
      // The specific cards, not just a count — they may not be on screen to select.
      expect(screen.getByText(/5678 Reissued Visa, 9012 Spare Visa/)).toBeInTheDocument();
      expect(screen.getByRole("button", { name: /^merge$/i })).toBeDisabled();
      expect(client.POST).not.toHaveBeenCalled();
    });

    it("offers to include the remaining cards and hands their ids back to the caller", async () => {
      const user = userEvent.setup();
      withThreeCardSource();
      const onIncludeCards = vi.fn();
      renderDialog({ selectedCards: oneOfThree, onIncludeCards });

      await chooseTarget(user);

      await user.click(await screen.findByRole("button", { name: /include the other 2 cards/i }));

      // The dialog cannot widen the selection itself — it lives on the /cards page.
      expect(onIncludeCards).toHaveBeenCalledWith(["c2", "c3"]);
    });

    it("clears once every card of the account is selected", async () => {
      const user = userEvent.setup();
      withThreeCardSource();
      renderDialog({
        selectedCards: [
          ...oneOfThree,
          { id: "c2", name: "Reissued Visa", cardCode: "5678", accountId: "a-source" },
          { id: "c3", name: "Spare Visa", cardCode: "9012", accountId: "a-source" },
        ],
      });

      await chooseTarget(user);

      expect(
        screen.queryByText(/would be left with cards behind/i),
      ).not.toBeInTheDocument();
      expect(screen.getByRole("button", { name: /^merge$/i })).not.toBeDisabled();
    });

    it("shows which account each selected card belongs to", async () => {
      // Without this the all-or-nothing rule is unintelligible: the dialog listed
      // card codes and names but never said where any of them lived.
      withThreeCardSource();
      renderDialog({ selectedCards: oneOfThree });

      // The card list renders immediately; the account names arrive with the
      // accounts query, so the name appears a tick later.
      const cardRow = await screen.findByText("Primary Visa");
      await vi.waitFor(() => {
        expect(cardRow.closest("li")).toHaveTextContent("Source Account");
      });
    });
  });

  // RECEIPTS-889. Merging deletes the emptied source accounts and repoints all their
  // transactions, trashed ones included, with no undo. The dialog's only warning was
  // one line of prose, so the user committed without knowing the blast radius.
  describe("impact preview", () => {
    const FULL_PREVIEW = {
      accountsToRemove: [{ id: "a-source", name: "Source Account" }],
      cardsToMove: 2,
      transactionsToRepoint: 37,
      trashedTransactionsToRepoint: 4,
      survivingYnabMapping: {
        fromAccountId: "a-source",
        fromAccountName: "Source Account",
        ynabAccountName: "YNAB Source",
      },
      conflicts: null,
    };

    function stubPreview(data: unknown, isFetching = false) {
      vi.mocked(useMergeCardsPreview).mockReturnValue({ data, isFetching } as ReturnType<
        typeof useMergeCardsPreview
      >);
    }

    async function openWithTarget() {
      const user = userEvent.setup();
      renderDialog();
      await user.click(screen.getByLabelText("Target account"));
      await user.click(await screen.findByRole("option", { name: "Account One" }));
      return user;
    }

    it("spells out what would move and what would be destroyed", async () => {
      stubPreview(FULL_PREVIEW);
      await openWithTarget();

      expect(await screen.findByText(/this merge cannot be undone/i)).toBeInTheDocument();
      expect(screen.getByText(/2 cards moved/)).toBeInTheDocument();
      expect(screen.getByText(/37 transactions repointed/)).toBeInTheDocument();
      // Named, not just counted — the account is about to stop existing.
      expect(screen.getByText(/Deleted permanently:/)).toBeInTheDocument();
      expect(screen.getByText(/YNAB Source/)).toBeInTheDocument();
    });

    it("calls out trashed transactions, which are invisible everywhere else", async () => {
      stubPreview(FULL_PREVIEW);
      await openWithTarget();

      expect(
        await screen.findByText(/4 trashed transactions repointed/),
      ).toBeInTheDocument();
    });

    it("says nothing about trashed transactions when there are none", async () => {
      stubPreview({ ...FULL_PREVIEW, trashedTransactionsToRepoint: 0 });
      await openWithTarget();

      await screen.findByText(/this merge cannot be undone/i);
      expect(screen.queryByText(/trashed transaction/)).not.toBeInTheDocument();
    });

    it("holds submit while the impact is still being worked out", async () => {
      stubPreview(undefined, true);
      await openWithTarget();

      expect(
        await screen.findByText(/working out what this merge would change/i),
      ).toBeInTheDocument();
      // Confirming an impact the user has not been shown is the thing to avoid.
      expect(screen.getByRole("button", { name: /^merge$/i })).toBeDisabled();
    });
  });
});
