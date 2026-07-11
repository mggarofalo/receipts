import "@/test/setup-combobox-polyfills";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ReceiptForm } from "./ReceiptForm";

vi.mock("@/hooks/useFormShortcuts", () => ({
  useFormShortcuts: vi.fn(),
}));

vi.mock("@/hooks/useReceipts", () => ({
  useLocationSuggestions: vi.fn(() => ({ data: undefined })),
}));

describe("ReceiptForm", () => {
  const defaultProps = {
    mode: "create" as const,
    onSubmit: vi.fn(),
    onCancel: vi.fn(),
  };

  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
  });

  it("renders in create mode with empty fields and correct submit button text", () => {
    render(<ReceiptForm {...defaultProps} />);

    // Combobox renders as a button with role="combobox"; empty value shows placeholder
    expect(screen.getByRole("combobox")).toHaveTextContent(
      "Select or type a location...",
    );
    expect(
      screen.getByRole("button", { name: /create receipt/i }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: /cancel/i }),
    ).toBeInTheDocument();
  });

  it("renders in edit mode with pre-populated fields and correct submit button text", () => {
    render(
      <ReceiptForm
        {...defaultProps}
        mode="edit"
        defaultValues={{
          location: "Walmart",
          date: "2024-01-15",
          taxAmount: 5.25,
        }}
      />,
    );

    // Combobox shows raw value text when it doesn't match a saved option
    expect(screen.getByRole("combobox")).toHaveTextContent("Walmart");
    expect(
      screen.getByRole("button", { name: /update receipt/i }),
    ).toBeInTheDocument();
  });

  it("calls onSubmit with correct data when form is valid", async () => {
    const user = userEvent.setup();
    render(<ReceiptForm {...defaultProps} />);

    // Open the combobox and type a custom value
    await user.click(screen.getByRole("combobox"));
    const searchInput = screen.getByPlaceholderText("Search locations...");
    await user.type(searchInput, "Target");
    // Click the "Use ..." button to select the custom value
    await user.click(screen.getByText(/Use "Target"/));

    await user.type(screen.getByLabelText(/^Date/), "2024-03-01");
    await user.click(
      screen.getByRole("button", { name: /create receipt/i }),
    );

    await waitFor(() => {
      expect(defaultProps.onSubmit).toHaveBeenCalledWith(
        expect.objectContaining({
          location: "Target",
          date: "2024-03-01",
          taxAmount: 0,
        }),
      );
    });
  });

  it("shows validation errors when required fields are empty", async () => {
    const user = userEvent.setup();
    render(<ReceiptForm {...defaultProps} />);

    await user.click(
      screen.getByRole("button", { name: /create receipt/i }),
    );

    await waitFor(() => {
      expect(screen.getByText("Location is required")).toBeInTheDocument();
      expect(screen.getByText("Date is required")).toBeInTheDocument();
    });
    expect(defaultProps.onSubmit).not.toHaveBeenCalled();
  });

  // RECEIPTS-782: catch the server's "Date cannot be in the future" rejection
  // on the client so the user gets inline feedback before submitting.
  it("rejects a future date with an inline error and does not submit", async () => {
    const user = userEvent.setup();
    render(<ReceiptForm {...defaultProps} />);

    await user.click(screen.getByRole("combobox"));
    const searchInput = screen.getByPlaceholderText("Search locations...");
    await user.type(searchInput, "Target");
    await user.click(screen.getByText(/Use "Target"/));

    const futureYear = new Date().getFullYear() + 1;
    await user.type(screen.getByLabelText(/^Date/), `${futureYear}-01-15`);
    await user.click(
      screen.getByRole("button", { name: /create receipt/i }),
    );

    await waitFor(() => {
      expect(
        screen.getByText("Date cannot be in the future"),
      ).toBeInTheDocument();
    });
    expect(defaultProps.onSubmit).not.toHaveBeenCalled();
  });

  it("calls onCancel when cancel button is clicked", async () => {
    const user = userEvent.setup();
    render(<ReceiptForm {...defaultProps} />);

    await user.click(screen.getByRole("button", { name: /cancel/i }));

    expect(defaultProps.onCancel).toHaveBeenCalledTimes(1);
  });

  it("disables submit button and shows spinner when isSubmitting is true", () => {
    render(<ReceiptForm {...defaultProps} isSubmitting={true} />);

    const submitButton = screen.getByRole("button", { name: /saving/i });
    expect(submitButton).toBeDisabled();
  });

  it("renders tax amount field", () => {
    render(<ReceiptForm {...defaultProps} />);

    expect(screen.getByText("Tax Amount")).toBeInTheDocument();
  });

  it("renders date input field", () => {
    render(<ReceiptForm {...defaultProps} />);

    expect(screen.getByLabelText(/^Date/)).toBeInTheDocument();
  });

  it("associates Location label with the combobox trigger via id", () => {
    render(<ReceiptForm {...defaultProps} />);

    const combobox = screen.getByRole("combobox");
    const label = screen.getByText(/^Location/);

    // FormControl's Slot passes id to the Combobox trigger button;
    // FormLabel sets htmlFor to the same id, creating the association.
    expect(combobox).toHaveAttribute("id");
    expect(label).toHaveAttribute("for", combobox.getAttribute("id"));
  });

  it("shows saved locations in the dropdown and allows selection", async () => {
    const user = userEvent.setup();
    localStorage.setItem(
      "receipts:location-history",
      JSON.stringify(["Walmart", "Target", "Costco"]),
    );

    render(<ReceiptForm {...defaultProps} />);

    // Open the combobox
    await user.click(screen.getByRole("combobox"));

    // Verify saved locations appear as options
    expect(screen.getByText("Walmart")).toBeInTheDocument();
    expect(screen.getByText("Target")).toBeInTheDocument();
    expect(screen.getByText("Costco")).toBeInTheDocument();

    // Select an existing location
    await user.click(screen.getByText("Target"));

    // Combobox should show the selected value
    expect(screen.getByRole("combobox")).toHaveTextContent("Target");

    // Fill remaining fields and submit
    await user.type(screen.getByLabelText(/^Date/), "2024-05-10");
    await user.click(
      screen.getByRole("button", { name: /create receipt/i }),
    );

    await waitFor(() => {
      expect(defaultProps.onSubmit).toHaveBeenCalledWith(
        expect.objectContaining({
          location: "Target",
          date: "2024-05-10",
          taxAmount: 0,
        }),
      );
    });
  });

  it("persists location to history on submit", async () => {
    const user = userEvent.setup();
    render(<ReceiptForm {...defaultProps} />);

    // Open combobox and type custom value
    await user.click(screen.getByRole("combobox"));
    const searchInput = screen.getByPlaceholderText("Search locations...");
    await user.type(searchInput, "Costco");
    await user.click(screen.getByText(/Use "Costco"/));

    await user.type(screen.getByLabelText(/^Date/), "2024-06-01");
    await user.click(
      screen.getByRole("button", { name: /create receipt/i }),
    );

    await waitFor(() => {
      expect(defaultProps.onSubmit).toHaveBeenCalled();
    });

    const stored = JSON.parse(
      localStorage.getItem("receipts:location-history") ?? "[]",
    ) as string[];
    expect(stored).toContain("Costco");
  });
});
