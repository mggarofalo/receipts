import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithProviders } from "@/test/test-utils";
import { PaymentsSection, type ReceiptPayment } from "./PaymentsSection";

describe("PaymentsSection", () => {
  it("renders an empty state when there are no payments", () => {
    renderWithProviders(<PaymentsSection payments={[]} onChange={vi.fn()} />);
    expect(
      screen.getByText("No payments detected on the receipt."),
    ).toBeInTheDocument();
  });

  it("displays a row per payment with method, amount, and last four", () => {
    const payments: ReceiptPayment[] = [
      { id: "1", method: "MASTERCARD", amount: 54.32, lastFour: "4538" },
      { id: "2", method: "Cash", amount: 5, lastFour: "" },
    ];

    renderWithProviders(
      <PaymentsSection payments={payments} onChange={vi.fn()} />,
    );

    expect(screen.getByDisplayValue("MASTERCARD")).toBeInTheDocument();
    expect(screen.getByDisplayValue("4538")).toBeInTheDocument();
    expect(screen.getByDisplayValue("Cash")).toBeInTheDocument();
  });

  it("calls onChange when adding a payment", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    renderWithProviders(<PaymentsSection payments={[]} onChange={onChange} />);

    await user.click(screen.getByRole("button", { name: /add payment/i }));

    expect(onChange).toHaveBeenCalledWith([
      expect.objectContaining({
        method: "",
        amount: 0,
        lastFour: "",
      }),
    ]);
  });

  it("calls onChange when removing a payment", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    const payments: ReceiptPayment[] = [
      { id: "1", method: "MASTERCARD", amount: 54.32, lastFour: "4538" },
    ];

    renderWithProviders(
      <PaymentsSection payments={payments} onChange={onChange} />,
    );

    await user.click(screen.getByRole("button", { name: /remove payment/i }));

    expect(onChange).toHaveBeenCalledWith([]);
  });

  it("updates a payment method when typed", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    const payments: ReceiptPayment[] = [
      { id: "1", method: "VIS", amount: 0, lastFour: "" },
    ];

    renderWithProviders(
      <PaymentsSection payments={payments} onChange={onChange} />,
    );

    const methodInput = screen.getByDisplayValue("VIS");
    await user.type(methodInput, "A");

    // Latest call should include the appended character
    const lastCall = onChange.mock.calls.at(-1)![0];
    expect(lastCall[0]).toMatchObject({ method: "VISA" });
  });

  it("rejects more than four digits in the last-four field", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    const payments: ReceiptPayment[] = [
      { id: "1", method: "VISA", amount: 0, lastFour: "1234" },
    ];

    renderWithProviders(
      <PaymentsSection payments={payments} onChange={onChange} />,
    );

    const lastFourInput = screen.getByDisplayValue("1234");
    await user.type(lastFourInput, "5");

    // The handler should refuse the change because the result would be 5 digits.
    expect(onChange).not.toHaveBeenCalled();
  });

  it("rejects non-numeric characters in the last-four field", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    const payments: ReceiptPayment[] = [
      { id: "1", method: "VISA", amount: 0, lastFour: "" },
    ];

    renderWithProviders(
      <PaymentsSection payments={payments} onChange={onChange} />,
    );

    const lastFourInputs = screen.getAllByLabelText("Last four digits");
    await user.type(lastFourInputs[0], "abc");

    expect(onChange).not.toHaveBeenCalled();
  });

  it("renders a low-confidence indicator on the matching field", () => {
    const payments: ReceiptPayment[] = [
      { id: "p-1", method: "MASTERCARD", amount: 54.32, lastFour: "4538" },
    ];
    const confidenceById = new Map([["p-1", { method: "low" as const }]]);

    renderWithProviders(
      <PaymentsSection
        payments={payments}
        onChange={vi.fn()}
        confidenceById={confidenceById}
      />,
    );

    // ConfidenceIndicator renders a chip with an aria-label exposing the rating.
    expect(
      screen.getByLabelText("AI confidence rating: low"),
    ).toBeInTheDocument();
  });

  it("keeps the confidence indicator paired with the surviving payment after a removal", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    const payments: ReceiptPayment[] = [
      { id: "p-1", method: "MASTERCARD", amount: 10, lastFour: "1234" },
      { id: "p-2", method: "VISA", amount: 5, lastFour: "5678" },
    ];
    // Only p-2 has low confidence.
    const confidenceById = new Map([["p-2", { method: "low" as const }]]);

    const { rerender } = renderWithProviders(
      <PaymentsSection
        payments={payments}
        onChange={onChange}
        confidenceById={confidenceById}
      />,
    );

    // Remove p-1.
    const removeButtons = screen.getAllByRole("button", {
      name: /remove payment/i,
    });
    await user.click(removeButtons[0]);
    expect(onChange).toHaveBeenCalledWith([payments[1]]);

    // Simulate parent updating the array.
    rerender(
      <PaymentsSection
        payments={[payments[1]]}
        onChange={onChange}
        confidenceById={confidenceById}
      />,
    );

    expect(
      screen.getByLabelText("AI confidence rating: low"),
    ).toBeInTheDocument();
    expect(screen.getByDisplayValue("VISA")).toBeInTheDocument();
  });

  it("displays the running total of payment amounts", () => {
    const payments: ReceiptPayment[] = [
      { id: "1", method: "VISA", amount: 10, lastFour: "" },
      { id: "2", method: "Cash", amount: 5.5, lastFour: "" },
    ];

    renderWithProviders(
      <PaymentsSection payments={payments} onChange={vi.fn()} />,
    );

    expect(screen.getByText(/Total: \$15\.50/)).toBeInTheDocument();
  });
});
