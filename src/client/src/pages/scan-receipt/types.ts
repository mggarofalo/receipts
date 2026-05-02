import type { components } from "@/generated/api";

export type ConfidenceLevel = components["schemas"]["ConfidenceLevel"];
export type ProposedReceiptResponse =
  components["schemas"]["ProposedReceiptResponse"];
export type ProposedReceiptItemResponse =
  components["schemas"]["ProposedReceiptItemResponse"];
export type ProposedTaxLineResponse =
  components["schemas"]["ProposedTaxLineResponse"];
export type ProposedTransactionResponse =
  components["schemas"]["ProposedTransactionResponse"];

export interface ScanProposedTransaction {
  cardId: string;
  accountId: string;
  amount: number;
  date: string;
}

// Confidence map for new-receipt fields populated from a scan proposal.
// Items and transactions are keyed by index in the source proposal; the
// new-receipt page re-keys them by stable row id at mount time so confidence
// stays correctly attached after additions or deletions.
export interface ReceiptConfidenceMap {
  location?: ConfidenceLevel;
  date?: ConfidenceLevel;
  taxAmount?: ConfidenceLevel;
  storeAddress?: ConfidenceLevel;
  storePhone?: ConfidenceLevel;
  receiptId?: ConfidenceLevel;
  storeNumber?: ConfidenceLevel;
  terminalId?: ConfidenceLevel;
  transactions?: Array<{
    cardId?: ConfidenceLevel;
    amount?: ConfidenceLevel;
    date?: ConfidenceLevel;
  }>;
  items?: Array<{
    taxCode?: ConfidenceLevel;
  }>;
}

export interface ScanInitialValues {
  header: {
    location: string;
    date: string;
    taxAmount: number;
    storeAddress: string;
    storePhone: string;
  };
  metadata: {
    receiptId: string;
    storeNumber: string;
    terminalId: string;
  };
  proposedTransactions: ScanProposedTransaction[];
  items: Array<{
    receiptItemCode: string;
    description: string;
    pricingMode: "quantity" | "flat";
    quantity: number;
    unitPrice: number;
    totalPrice: number;
    category: string;
    subcategory: string;
    taxCode: string;
  }>;
}
