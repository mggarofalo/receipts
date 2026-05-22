import { describe, it, expect, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import "@/test/setup-combobox-polyfills";
import { ReconcileSheet } from "./ReconcileSheet";

vi.mock("@/hooks/useEnumMetadata", () => ({
  useEnumMetadata: vi.fn(() => ({
    adjustmentTypes: [
      { value: "coupon", label: "Coupon" },
      { value: "discount", label: "Discount" },
      { value: "other", label: "Other" },
    ],
  })),
}));

function renderSheet(
  overrides: Partial<React.ComponentProps<typeof ReconcileSheet>> = {},
) {
  const onClose = vi.fn();
  const onCreateAdjustment = vi.fn();
  render(
    <ReconcileSheet
      open
      onClose={onClose}
      onCreateAdjustment={onCreateAdjustment}
      receiptId="abcdef1234"
      receiptLabel="Whole Foods"
      receiptDate="2024-01-15"
      receiptTotal={100}
      transactionsTotal={94.5}
      {...overrides}
    />,
  );
  return { onClose, onCreateAdjustment };
}

describe("ReconcileSheet", () => {
  it("does not render when closed", () => {
    const { container } = render(
      <ReconcileSheet
        open={false}
        onClose={() => {}}
        onCreateAdjustment={() => {}}
        receiptId="abc"
        receiptLabel="X"
        receiptDate="2024-01-01"
        receiptTotal={0}
        transactionsTotal={0}
      />,
    );
    expect(container.firstChild).toBeNull();
  });

  it("renders the dialog with title and delta totals", () => {
    renderSheet();
    expect(
      screen.getByRole("dialog", { name: /reconcile receipt/i }),
    ).toBeInTheDocument();
    expect(screen.getByText(/whole foods/i)).toBeInTheDocument();
    expect(screen.getByText("$100.00")).toBeInTheDocument();
    expect(screen.getByText("$94.50")).toBeInTheDocument();
    expect(screen.getByText(/REC-ABCDEF12/)).toBeInTheDocument();
  });

  it("shows the balanced empty state and no Create button when totals match", () => {
    renderSheet({ receiptTotal: 50, transactionsTotal: 50 });
    expect(screen.getByText(/receipt is balanced/i)).toBeInTheDocument();
    expect(
      screen.queryByRole("button", { name: /create adjustment/i }),
    ).not.toBeInTheDocument();
  });

  it("disables Create adjustment until a type is selected", () => {
    renderSheet();
    expect(
      screen.getByRole("button", { name: /create adjustment/i }),
    ).toBeDisabled();
  });

  it("creates an adjustment for the balancing amount once a type is picked", async () => {
    const user = userEvent.setup();
    const { onCreateAdjustment, onClose } = renderSheet();

    await user.click(
      screen.getByRole("combobox", { name: /adjustment type/i }),
    );
    await user.click(await screen.findByRole("option", { name: "Discount" }));

    const create = screen.getByRole("button", {
      name: /create adjustment/i,
    });
    expect(create).toBeEnabled();
    await user.click(create);

    // delta = transactionsTotal - receiptTotal = 94.5 - 100
    expect(onCreateAdjustment).toHaveBeenCalledWith({
      type: "discount",
      amount: -5.5,
      description: null,
    });
    // The sheet does not self-close; the caller closes on success.
    expect(onClose).not.toHaveBeenCalled();
  });

  it("requires a description for the 'other' adjustment type", async () => {
    const user = userEvent.setup();
    renderSheet();

    await user.click(
      screen.getByRole("combobox", { name: /adjustment type/i }),
    );
    await user.click(await screen.findByRole("option", { name: "Other" }));

    expect(
      screen.getByRole("button", { name: /create adjustment/i }),
    ).toBeDisabled();
    expect(
      screen.getByLabelText(/adjustment description/i),
    ).toBeInTheDocument();
  });

  it("calls onClose when Escape is pressed", async () => {
    const user = userEvent.setup();
    const { onClose } = renderSheet();
    await user.keyboard("{Escape}");
    expect(onClose).toHaveBeenCalled();
  });

  it("calls onClose when Cancel is clicked", async () => {
    const user = userEvent.setup();
    const { onClose } = renderSheet();
    await user.click(screen.getByRole("button", { name: /^cancel$/i }));
    expect(onClose).toHaveBeenCalled();
  });
});
