import type { components } from "@/generated/api";

type ProposedReceiptResponse = components["schemas"]["ProposedReceiptResponse"];

/**
 * Fixture for the VLM scan endpoint (`POST /api/receipts/scan`).
 *
 * Typed as {@link ProposedReceiptResponse} so TypeScript enforces field
 * completeness against the OpenAPI contract — adding a new required field
 * upstream causes this file to fail to type-check, preventing the silent
 * runtime crash described in RECEIPTS-632.
 *
 * Confidence values are intentionally a mix of high/medium/low so the
 * new-receipt wizard's badge rendering is exercised under realistic data.
 *
 * `payments[]` is still emitted by the server (deprecated, retained for
 * VLM evaluation/scoring) but is no longer consumed by the client; the
 * wizard now reads `proposedTransactions[]` instead. See RECEIPTS-657
 * and RECEIPTS-658.
 */
export const scanProposal: ProposedReceiptResponse = {
  storeName: "Walmart Supercenter",
  storeNameConfidence: "high",
  storeAddress: "123 Main St, Springfield, IL 62701",
  storeAddressConfidence: "medium",
  storePhone: "(555) 123-4567",
  storePhoneConfidence: "low",
  date: "2024-06-15",
  dateConfidence: "high",
  items: [
    {
      code: "MILK-GAL",
      codeConfidence: "high",
      description: "Great Value Whole Milk",
      descriptionConfidence: "high",
      quantity: 2,
      quantityConfidence: "high",
      unitPrice: 3.99,
      unitPriceConfidence: "medium",
      totalPrice: 7.98,
      totalPriceConfidence: "high",
      taxCode: "F",
      taxCodeConfidence: "high",
    },
    {
      code: null,
      codeConfidence: "low",
      description: "Bananas",
      descriptionConfidence: "medium",
      quantity: 1,
      quantityConfidence: "high",
      unitPrice: 1.29,
      unitPriceConfidence: "low",
      totalPrice: 1.29,
      totalPriceConfidence: "medium",
      taxCode: null,
      taxCodeConfidence: "low",
    },
  ],
  subtotal: 9.27,
  subtotalConfidence: "high",
  taxLines: [
    {
      label: "Tax",
      labelConfidence: "high",
      amount: 0.74,
      amountConfidence: "medium",
    },
  ],
  total: 10.01,
  totalConfidence: "high",
  paymentMethod: "VISA",
  paymentMethodConfidence: "medium",
  // Server still emits the deprecated `payments[]` for back-compat (e.g. VLM
  // evaluation scoring); the client no longer reads it. Kept populated here
  // so the OpenAPI-typed fixture continues to satisfy the contract.
  payments: [
    {
      method: "VISA",
      methodConfidence: "high",
      amount: 10.01,
      amountConfidence: "high",
      lastFour: "4242",
      lastFourConfidence: "medium",
    },
  ],
  // The new path: the server resolved the VISA tender (last four 4242)
  // against an active card and produced a Transaction row pre-populated with
  // both cardId and accountId. cardIdConfidence is "high" because exactly
  // one matching card was found.
  proposedTransactions: [
    {
      cardId: "00000000-0000-0000-0000-000000004242",
      cardIdConfidence: "high",
      accountId: "00000000-0000-0000-0000-000000000099",
      accountIdConfidence: "high",
      amount: 10.01,
      amountConfidence: "high",
      date: "2024-06-15",
      dateConfidence: "high",
      methodSnapshot: "VISA",
    },
  ],
  receiptId: "TX-987654321",
  receiptIdConfidence: "high",
  storeNumber: "0042",
  storeNumberConfidence: "medium",
  terminalId: "T01",
  terminalIdConfidence: "low",
  droppedPageCount: 0,
};
