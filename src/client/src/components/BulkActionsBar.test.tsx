import { describe, it, expect, vi } from "vitest";
import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithProviders } from "@/test/test-utils";
import { BulkActionsBar } from "./BulkActionsBar";

const defaultProps = {
  selectedCount: 2,
  totalCount: 10,
  itemLabel: "receipt",
  onClearSelection: () => {},
  onDelete: () => {},
};

describe("BulkActionsBar", () => {
  it("renders nothing when selectedCount is 0", () => {
    const { container } = renderWithProviders(
      <BulkActionsBar {...defaultProps} selectedCount={0} />,
    );
    expect(container.firstChild).toBeNull();
  });

  it("renders the visible count with a plural noun", () => {
    renderWithProviders(<BulkActionsBar {...defaultProps} selectedCount={3} />);
    expect(screen.getByText("3")).toBeInTheDocument();
    expect(screen.getByText(/receipts selected/)).toBeInTheDocument();
  });

  it("uses the singular noun when exactly one item is selected", () => {
    renderWithProviders(<BulkActionsBar {...defaultProps} selectedCount={1} />);
    expect(screen.getByText("1")).toBeInTheDocument();
    expect(screen.getByText(/receipt selected/)).toBeInTheDocument();
  });

  it("invokes onClearSelection when Clear is clicked", async () => {
    const onClearSelection = vi.fn();
    const user = userEvent.setup();
    renderWithProviders(
      <BulkActionsBar {...defaultProps} onClearSelection={onClearSelection} />,
    );
    await user.click(screen.getByRole("button", { name: "Clear" }));
    expect(onClearSelection).toHaveBeenCalledOnce();
  });

  it("invokes onDelete when Delete is clicked", async () => {
    const onDelete = vi.fn();
    const user = userEvent.setup();
    renderWithProviders(
      <BulkActionsBar {...defaultProps} onDelete={onDelete} />,
    );
    await user.click(screen.getByRole("button", { name: /delete/i }));
    expect(onDelete).toHaveBeenCalledOnce();
  });

  it("renders the Push to YNAB button when onPushToYnab is provided", async () => {
    const onPushToYnab = vi.fn();
    const user = userEvent.setup();
    renderWithProviders(
      <BulkActionsBar {...defaultProps} onPushToYnab={onPushToYnab} />,
    );
    await user.click(screen.getByRole("button", { name: /push to ynab/i }));
    expect(onPushToYnab).toHaveBeenCalledOnce();
  });

  it("omits the Push to YNAB button when onPushToYnab is not provided", () => {
    renderWithProviders(<BulkActionsBar {...defaultProps} />);
    expect(
      screen.queryByRole("button", { name: /push to ynab/i }),
    ).not.toBeInTheDocument();
  });

  it("disables the YNAB button and shows a spinner while pushing", () => {
    renderWithProviders(
      <BulkActionsBar
        {...defaultProps}
        onPushToYnab={() => {}}
        isPushingToYnab
      />,
    );
    const btn = screen.getByRole("button", { name: /pushing/i });
    expect(btn).toBeDisabled();
  });

  it("disables the Delete button and swaps to 'Deleting…' while deleting", () => {
    renderWithProviders(
      <BulkActionsBar {...defaultProps} isDeleting />,
    );
    const btn = screen.getByRole("button", { name: /deleting/i });
    expect(btn).toBeDisabled();
  });

  it("wraps the bar in an aria-live region for SR announcement", () => {
    renderWithProviders(<BulkActionsBar {...defaultProps} />);
    const region = screen.getByRole("region", { name: "Bulk actions" });
    expect(region).toHaveAttribute("aria-live", "polite");
  });
});
