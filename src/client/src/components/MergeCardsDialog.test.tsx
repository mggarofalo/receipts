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

import client from "@/lib/api-client";

function renderDialog(overrides: Partial<React.ComponentProps<typeof MergeCardsDialog>> = {}) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  });
  const selectedCards = overrides.selectedCards ?? [
    { id: "c1", name: "Primary Visa", cardCode: "1234" },
    { id: "c2", name: "Reissued Visa", cardCode: "5678" },
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

beforeEach(() => {
  vi.clearAllMocks();
  // Accounts query used by the dialog
  (client.GET as Mock).mockResolvedValue({
    data: { data: [{ id: "a1", name: "Account One", isActive: true }], total: 1, offset: 0, limit: 500 },
    error: undefined,
  });
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
      data: { success: true },
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
      data: { success: true },
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
});
