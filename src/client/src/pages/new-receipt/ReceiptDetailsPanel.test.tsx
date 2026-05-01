import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithProviders } from "@/test/test-utils";
import { ReceiptDetailsPanel } from "./ReceiptDetailsPanel";

describe("ReceiptDetailsPanel", () => {
  const fullMetadata = {
    receiptId: "TX-987654",
    storeNumber: "0042",
    terminalId: "T01",
  };

  it("renders the heading even when collapsed", () => {
    renderWithProviders(<ReceiptDetailsPanel metadata={fullMetadata} />);
    expect(screen.getByText(/receipt details/i)).toBeInTheDocument();
  });

  it("starts collapsed (expand button label says 'Expand')", () => {
    renderWithProviders(<ReceiptDetailsPanel metadata={fullMetadata} />);
    expect(
      screen.getByRole("button", { name: /expand receipt details/i }),
    ).toBeInTheDocument();
  });

  it("reveals all populated values after expansion", async () => {
    const user = userEvent.setup();
    renderWithProviders(<ReceiptDetailsPanel metadata={fullMetadata} />);
    await user.click(
      screen.getByRole("button", { name: /expand receipt details/i }),
    );
    expect(screen.getByText("TX-987654")).toBeInTheDocument();
    expect(screen.getByText("0042")).toBeInTheDocument();
    expect(screen.getByText("T01")).toBeInTheDocument();
  });

  it("toggles the aria-expanded attribute when clicked", async () => {
    const user = userEvent.setup();
    renderWithProviders(<ReceiptDetailsPanel metadata={fullMetadata} />);
    const button = screen.getByRole("button", {
      name: /expand receipt details/i,
    });
    expect(button).toHaveAttribute("aria-expanded", "false");
    await user.click(button);
    expect(
      screen.getByRole("button", { name: /collapse receipt details/i }),
    ).toHaveAttribute("aria-expanded", "true");
  });

  it("omits a row when its metadata field is empty", async () => {
    const user = userEvent.setup();
    renderWithProviders(
      <ReceiptDetailsPanel
        metadata={{
          receiptId: "TX-1",
          storeNumber: "",
          terminalId: "",
        }}
      />,
    );
    await user.click(
      screen.getByRole("button", { name: /expand receipt details/i }),
    );
    expect(screen.getByText("TX-1")).toBeInTheDocument();
    expect(screen.queryByText(/store number/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/terminal id/i)).not.toBeInTheDocument();
  });

  it("renders confidence indicators for low/medium fields", async () => {
    const user = userEvent.setup();
    renderWithProviders(
      <ReceiptDetailsPanel
        metadata={fullMetadata}
        confidenceMap={{ receiptId: "low" }}
      />,
    );
    await user.click(
      screen.getByRole("button", { name: /expand receipt details/i }),
    );
    expect(
      screen.getByLabelText("AI confidence rating: low"),
    ).toBeInTheDocument();
  });

  it("renders nothing in the value list when no metadata fields are populated", async () => {
    const user = userEvent.setup();
    renderWithProviders(
      <ReceiptDetailsPanel
        metadata={{ receiptId: "", storeNumber: "", terminalId: "" }}
      />,
    );
    // The card itself still renders, but nothing inside the dl.
    await user.click(
      screen.getByRole("button", { name: /expand receipt details/i }),
    );
    expect(screen.queryByText(/receipt id/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/store number/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/terminal id/i)).not.toBeInTheDocument();
  });
});
