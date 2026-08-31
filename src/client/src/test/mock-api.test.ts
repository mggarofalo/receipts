import {
  mockReceiptListItemResponse,
  mockReceiptListResponse,
  mockReceiptResponse,
  resetMockIds,
} from "./mock-api";

describe("receipt list mock factories", () => {
  beforeEach(() => resetMockIds());

  it("enriches legacy receipt rows with deterministic list metadata", () => {
    const receipt = mockReceiptResponse({ taxAmount: 1.25 });

    const response = mockReceiptListResponse([receipt]);

    expect(response.data).toEqual([
      expect.objectContaining({
        ...receipt,
        itemSubtotal: 0,
        adjustmentTotal: 0,
        expectedTotal: 1.25,
        transactionTotal: 0,
        balanceState: "noTransactions",
        itemCount: 0,
        categorySummary: "",
        paymentSummary: "",
      }),
    ]);
  });

  it("preserves explicit aggregate overrides", () => {
    const row = mockReceiptListItemResponse({
      itemSubtotal: 10,
      adjustmentTotal: 2,
      expectedTotal: 13,
      transactionTotal: 13,
      balanceState: "balanced",
      itemCount: 2,
      categorySummary: "Food",
      paymentSummary: "Checking · Visa",
    });

    const response = mockReceiptListResponse([row], { total: 8, offset: 5 });

    expect(response.data[0]).toMatchObject(row);
    expect(response).toMatchObject({ total: 8, offset: 5, limit: 50 });
  });
});
