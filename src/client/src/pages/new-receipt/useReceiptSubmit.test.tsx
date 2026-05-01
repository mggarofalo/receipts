import { renderHook, act } from "@testing-library/react";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { createWrapper } from "@/test/test-utils";
import { mockMutationResult } from "@/test/mock-hooks";
import { headerSchema, type HeaderFormValues } from "./headerSchema";
import type { ReceiptTransaction } from "./TransactionsSection";
import type { ReceiptLineItem } from "./LineItemsSection";
import { useReceiptSubmit } from "./useReceiptSubmit";

const mockCreateCompleteReceiptAsync = vi.fn();

vi.mock("@/hooks/useReceipts", () => ({
  useCreateCompleteReceipt: vi.fn(() =>
    mockMutationResult({ mutateAsync: mockCreateCompleteReceiptAsync }),
  ),
}));

const mockAddLocation = vi.fn();
vi.mock("@/hooks/useLocationHistory", () => ({
  useLocationHistory: vi.fn(() => ({
    locations: [],
    options: [],
    add: mockAddLocation,
    clear: vi.fn(),
  })),
}));

const mockNavigate = vi.fn();
vi.mock("react-router", async (importOriginal) => {
  const actual = await importOriginal<typeof import("react-router")>();
  return {
    ...actual,
    useNavigate: vi.fn(() => mockNavigate),
  };
});

vi.mock("sonner", () => ({
  toast: { success: vi.fn(), error: vi.fn(), info: vi.fn() },
}));

interface RenderOptions {
  defaultValues?: Partial<HeaderFormValues>;
  transactions?: ReceiptTransaction[];
  items?: ReceiptLineItem[];
}

function renderHookWithForm({
  defaultValues,
  transactions = [],
  items = [],
}: RenderOptions = {}) {
  return renderHook(
    () => {
      const form = useForm<HeaderFormValues>({
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
        resolver: zodResolver(headerSchema) as any,
        defaultValues: {
          location: "Walmart",
          date: "2024-06-15",
          taxAmount: 0,
          storeAddress: "",
          storePhone: "",
          ...defaultValues,
        },
      });
      const submit = useReceiptSubmit({ form, transactions, items });
      return { form, ...submit };
    },
    { wrapper: createWrapper() },
  );
}

const sampleTxn: ReceiptTransaction = {
  id: "t1",
  cardId: "card-1",
  accountId: "acct-1",
  amount: 55,
  date: "2024-06-15",
};

const sampleItem: ReceiptLineItem = {
  id: "i1",
  receiptItemCode: "",
  description: "Milk",
  pricingMode: "quantity",
  quantity: 1,
  unitPrice: 50,
  totalPrice: 50,
  category: "Food",
  subcategory: "",
  taxCode: "",
};

const sampleFlatItem: ReceiptLineItem = {
  id: "i2",
  receiptItemCode: "",
  description: "Walmart-style flat",
  pricingMode: "flat",
  quantity: 1,
  unitPrice: 0,
  totalPrice: 12.34,
  category: "Food",
  subcategory: "",
  taxCode: "",
};

describe("useReceiptSubmit", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockCreateCompleteReceiptAsync.mockResolvedValue({
      receipt: { id: "receipt-123" },
      transactions: [],
      items: [],
    });
  });

  it("starts with isSubmitting=false", () => {
    const { result } = renderHookWithForm();
    expect(result.current.isSubmitting).toBe(false);
  });

  it("blocks submission when header validation fails", async () => {
    const { result } = renderHookWithForm({
      defaultValues: { location: "" },
      transactions: [sampleTxn],
      items: [sampleItem],
    });

    await act(async () => {
      await result.current.submit();
    });

    expect(mockCreateCompleteReceiptAsync).not.toHaveBeenCalled();
  });

  it("toasts when there are no transactions", async () => {
    const { toast } = await import("sonner");
    const { result } = renderHookWithForm({
      transactions: [],
      items: [sampleItem],
    });

    await act(async () => {
      await result.current.submit();
    });

    expect(toast.error).toHaveBeenCalledWith("Add at least one transaction.");
    expect(mockCreateCompleteReceiptAsync).not.toHaveBeenCalled();
  });

  it("toasts when there are no items", async () => {
    const { toast } = await import("sonner");
    const { result } = renderHookWithForm({
      transactions: [sampleTxn],
      items: [],
    });

    await act(async () => {
      await result.current.submit();
    });

    expect(toast.error).toHaveBeenCalledWith("Add at least one line item.");
    expect(mockCreateCompleteReceiptAsync).not.toHaveBeenCalled();
  });

  it("submits, persists location, toasts success, and navigates", async () => {
    const { toast } = await import("sonner");
    const { result } = renderHookWithForm({
      transactions: [sampleTxn],
      items: [sampleItem],
    });

    await act(async () => {
      await result.current.submit();
    });

    expect(mockAddLocation).toHaveBeenCalledWith("Walmart");
    expect(mockCreateCompleteReceiptAsync).toHaveBeenCalledWith(
      expect.objectContaining({
        receipt: expect.objectContaining({ location: "Walmart" }),
        transactions: expect.any(Array),
        items: expect.any(Array),
      }),
    );
    expect(toast.success).toHaveBeenCalledWith("Receipt created successfully!");
    expect(mockNavigate).toHaveBeenCalledWith("/receipts/receipt-123");
  });

  it("toasts error when the create-receipt mutation fails", async () => {
    const { toast } = await import("sonner");
    mockCreateCompleteReceiptAsync.mockRejectedValueOnce(new Error("boom"));

    const { result } = renderHookWithForm({
      transactions: [sampleTxn],
      items: [sampleItem],
    });

    await act(async () => {
      await result.current.submit();
    });

    expect(toast.error).toHaveBeenCalledWith("Failed to create receipt.");
  });

  // RECEIPTS-655: a Walmart-style scan produces flat-priced items where the
  // line total carries the dollar value (unitPrice = 0). The submit hook must
  // forward `totalPrice` and `pricingMode = "flat"` so the server can persist
  // the real line total instead of computing 1 × 0.
  it("forwards totalPrice and pricingMode='flat' for flat-priced items (RECEIPTS-655)", async () => {
    const flatTxn: ReceiptTransaction = {
      ...sampleTxn,
      // The receipt-level invariant requires total transactions = total items.
      amount: sampleFlatItem.totalPrice,
    };

    const { result } = renderHookWithForm({
      transactions: [flatTxn],
      items: [sampleFlatItem],
    });

    await act(async () => {
      await result.current.submit();
    });

    expect(mockCreateCompleteReceiptAsync).toHaveBeenCalledTimes(1);
    const body = mockCreateCompleteReceiptAsync.mock.calls[0][0];
    expect(body.items).toEqual([
      expect.objectContaining({
        description: "Walmart-style flat",
        pricingMode: "flat",
        quantity: 1,
        unitPrice: 0,
        totalPrice: 12.34,
      }),
    ]);
  });
});
