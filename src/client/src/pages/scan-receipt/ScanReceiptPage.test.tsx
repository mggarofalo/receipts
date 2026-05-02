import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithProviders } from "@/test/test-utils";
import { mockMutationResult } from "@/test/mock-hooks";
import ScanReceiptPage from "./ScanReceiptPage";
import type { components } from "@/generated/api";

type ProposedReceiptResponse = components["schemas"]["ProposedReceiptResponse"];

vi.mock("@/hooks/usePageTitle", () => ({
  usePageTitle: vi.fn(),
}));

const mockMutate = vi.fn();

vi.mock("@/hooks/useReceiptScan", () => ({
  useReceiptScan: vi.fn(() => mockMutationResult({ mutate: mockMutate })),
}));

// Mock NewReceiptPage to isolate ScanReceiptPage logic
vi.mock("@/pages/new-receipt/NewReceiptPage", () => ({
  default: ({
    initialValues,
    confidenceMap,
    droppedPageCount,
  }: {
    initialValues?: {
      header: { location: string; storeAddress: string; storePhone: string };
      metadata: { receiptId: string; storeNumber: string; terminalId: string };
      proposedTransactions: Array<{
        cardId: string;
        accountId: string;
        amount: number;
        date: string;
      }>;
    };
    confidenceMap?: Record<string, unknown>;
    droppedPageCount?: number;
  }) => (
    <div data-testid="new-receipt-page">
      {initialValues?.header.location && (
        <span data-testid="prepopulated-location">
          {initialValues.header.location}
        </span>
      )}
      {initialValues?.header.storeAddress && (
        <span data-testid="prepopulated-address">
          {initialValues.header.storeAddress}
        </span>
      )}
      {initialValues?.header.storePhone && (
        <span data-testid="prepopulated-phone">
          {initialValues.header.storePhone}
        </span>
      )}
      {initialValues?.metadata?.receiptId && (
        <span data-testid="prepopulated-receipt-id">
          {initialValues.metadata.receiptId}
        </span>
      )}
      {initialValues?.proposedTransactions &&
        initialValues.proposedTransactions.length > 0 && (
          <span data-testid="prepopulated-transactions">
            {JSON.stringify(initialValues.proposedTransactions)}
          </span>
        )}
      {confidenceMap && (
        <span data-testid="confidence-map">{JSON.stringify(confidenceMap)}</span>
      )}
      {droppedPageCount !== undefined && (
        <span data-testid="dropped-page-count">{droppedPageCount}</span>
      )}
    </div>
  ),
}));

function makeProposal(
  overrides: Partial<ProposedReceiptResponse> = {},
): ProposedReceiptResponse {
  return {
    storeName: "Test Store",
    storeNameConfidence: "high",
    storeAddress: null,
    storeAddressConfidence: "high",
    storePhone: null,
    storePhoneConfidence: "high",
    date: "2024-06-15",
    dateConfidence: "high",
    items: [],
    subtotal: 0,
    subtotalConfidence: "high",
    taxLines: [
      {
        label: "Tax",
        labelConfidence: "high",
        amount: 0,
        amountConfidence: "high",
      },
    ],
    total: 0,
    totalConfidence: "high",
    paymentMethod: null,
    paymentMethodConfidence: "high",
    payments: [],
    proposedTransactions: [],
    receiptId: null,
    receiptIdConfidence: "high",
    storeNumber: null,
    storeNumberConfidence: "high",
    terminalId: null,
    terminalIdConfidence: "high",
    droppedPageCount: 0,
    ...overrides,
  };
}

function createTestFile(
  name = "receipt.jpg",
  type = "image/jpeg",
  sizeBytes = 1024,
): File {
  const buffer = new ArrayBuffer(sizeBytes);
  return new File([buffer], name, { type });
}

describe("ScanReceiptPage", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.stubGlobal(
      "URL",
      Object.assign({}, globalThis.URL, {
        createObjectURL: vi.fn(() => "blob:preview-url"),
        revokeObjectURL: vi.fn(),
      }),
    );
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("renders the upload phase initially", () => {
    renderWithProviders(<ScanReceiptPage />);
    expect(
      screen.getByRole("heading", { name: /scan receipt/i }),
    ).toBeInTheDocument();
    expect(
      screen.getByText("Drop a receipt image or PDF here"),
    ).toBeInTheDocument();
  });

  it("shows loading state during scan", async () => {
    // Make mutate do nothing (simulate pending state)
    mockMutate.mockImplementation(() => {});

    const user = userEvent.setup();
    renderWithProviders(<ScanReceiptPage />);

    // Select a file
    const fileInput = screen.getByTestId("file-input");
    const file = createTestFile();
    await user.upload(fileInput, file);

    // Click scan
    await user.click(screen.getByRole("button", { name: /scan receipt/i }));

    // Should show processing state
    expect(screen.getByText("Processing receipt...")).toBeInTheDocument();
  });

  it("transitions to review phase on success", async () => {
    mockMutate.mockImplementation(
      (
        _file: File,
        options: { onSuccess: (data: unknown) => void },
      ) => {
        options.onSuccess(makeProposal({ storeName: "Test Store" }));
      },
    );

    const user = userEvent.setup();
    renderWithProviders(<ScanReceiptPage />);

    // Select a file
    const fileInput = screen.getByTestId("file-input");
    const file = createTestFile();
    await user.upload(fileInput, file);

    // Click scan
    await user.click(screen.getByRole("button", { name: /scan receipt/i }));

    await waitFor(() => {
      expect(screen.getByTestId("new-receipt-page")).toBeInTheDocument();
    });

    expect(screen.getByTestId("prepopulated-location")).toHaveTextContent(
      "Test Store",
    );
  });

  it("shows error on failure", async () => {
    mockMutate.mockImplementation(
      (
        _file: File,
        options: { onError: (error: unknown) => void },
      ) => {
        options.onError({ status: 400 });
      },
    );

    const user = userEvent.setup();
    renderWithProviders(<ScanReceiptPage />);

    // Select a file
    const fileInput = screen.getByTestId("file-input");
    const file = createTestFile();
    await user.upload(fileInput, file);

    // Click scan
    await user.click(screen.getByRole("button", { name: /scan receipt/i }));

    await waitFor(() => {
      expect(
        screen.getByText(/could not read the file/i),
      ).toBeInTheDocument();
    });
  });

  it("passes correct confidence map when store name confidence is low", async () => {
    mockMutate.mockImplementation(
      (
        _file: File,
        options: { onSuccess: (data: unknown) => void },
      ) => {
        options.onSuccess(
          makeProposal({
            storeName: "Low Confidence Store",
            storeNameConfidence: "low",
          }),
        );
      },
    );

    const user = userEvent.setup();
    renderWithProviders(<ScanReceiptPage />);

    const fileInput = screen.getByTestId("file-input");
    const file = createTestFile();
    await user.upload(fileInput, file);
    await user.click(screen.getByRole("button", { name: /scan receipt/i }));

    await waitFor(() => {
      expect(screen.getByTestId("confidence-map")).toBeInTheDocument();
    });

    const confidenceMap = JSON.parse(
      screen.getByTestId("confidence-map").textContent!,
    );
    expect(confidenceMap).toEqual({ location: "low" });
  });

  it("forwards droppedPageCount to the wizard so the warning banner can render", async () => {
    mockMutate.mockImplementation(
      (
        _file: File,
        options: { onSuccess: (data: unknown) => void },
      ) => {
        options.onSuccess(makeProposal({ droppedPageCount: 2 }));
      },
    );

    const user = userEvent.setup();
    renderWithProviders(<ScanReceiptPage />);

    const fileInput = screen.getByTestId("file-input");
    const file = createTestFile("multi-page.pdf", "application/pdf", 4096);
    await user.upload(fileInput, file);
    await user.click(screen.getByRole("button", { name: /scan receipt/i }));

    await waitFor(() => {
      expect(screen.getByTestId("dropped-page-count")).toBeInTheDocument();
    });

    expect(screen.getByTestId("dropped-page-count")).toHaveTextContent("2");
  });

  it("forwards droppedPageCount of 0 for single-page sources", async () => {
    mockMutate.mockImplementation(
      (
        _file: File,
        options: { onSuccess: (data: unknown) => void },
      ) => {
        options.onSuccess(makeProposal({ droppedPageCount: 0 }));
      },
    );

    const user = userEvent.setup();
    renderWithProviders(<ScanReceiptPage />);

    const fileInput = screen.getByTestId("file-input");
    const file = createTestFile();
    await user.upload(fileInput, file);
    await user.click(screen.getByRole("button", { name: /scan receipt/i }));

    await waitFor(() => {
      expect(screen.getByTestId("dropped-page-count")).toBeInTheDocument();
    });

    expect(screen.getByTestId("dropped-page-count")).toHaveTextContent("0");
  });

  it("forwards new fields (address, phone, metadata, proposedTransactions) to the wizard", async () => {
    mockMutate.mockImplementation(
      (
        _file: File,
        options: { onSuccess: (data: unknown) => void },
      ) => {
        options.onSuccess(
          makeProposal({
            storeAddress: "123 Main St",
            storePhone: "(555) 123-4567",
            receiptId: "TX-987654",
            storeNumber: "0042",
            terminalId: "T01",
            proposedTransactions: [
              {
                cardId: "card-99",
                cardIdConfidence: "high",
                accountId: "acct-99",
                accountIdConfidence: "high",
                amount: 54.32,
                amountConfidence: "high",
                date: "2024-06-15",
                dateConfidence: "high",
                methodSnapshot: "MASTERCARD",
              },
            ],
          }),
        );
      },
    );

    const user = userEvent.setup();
    renderWithProviders(<ScanReceiptPage />);

    const fileInput = screen.getByTestId("file-input");
    const file = createTestFile();
    await user.upload(fileInput, file);
    await user.click(screen.getByRole("button", { name: /scan receipt/i }));

    await waitFor(() => {
      expect(screen.getByTestId("new-receipt-page")).toBeInTheDocument();
    });

    expect(screen.getByTestId("prepopulated-address")).toHaveTextContent(
      "123 Main St",
    );
    expect(screen.getByTestId("prepopulated-phone")).toHaveTextContent(
      "(555) 123-4567",
    );
    expect(screen.getByTestId("prepopulated-receipt-id")).toHaveTextContent(
      "TX-987654",
    );
    const proposedTransactions = JSON.parse(
      screen.getByTestId("prepopulated-transactions").textContent!,
    );
    expect(proposedTransactions).toEqual([
      {
        cardId: "card-99",
        accountId: "acct-99",
        amount: 54.32,
        date: "2024-06-15",
      },
    ]);
  });
});
