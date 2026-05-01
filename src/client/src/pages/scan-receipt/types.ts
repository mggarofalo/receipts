import type { components } from "@/generated/api";

export type ConfidenceLevel = components["schemas"]["ConfidenceLevel"];
export type ProposedReceiptResponse =
  components["schemas"]["ProposedReceiptResponse"];
export type ProposedReceiptItemResponse =
  components["schemas"]["ProposedReceiptItemResponse"];
export type ProposedTaxLineResponse =
  components["schemas"]["ProposedTaxLineResponse"];
export type ProposedPaymentResponse =
  components["schemas"]["ProposedPaymentResponse"];

export interface ScanPayment {
  method: string;
  amount: number;
  lastFour: string;
}

// Confidence map for new-receipt fields populated from a scan proposal.
// Items are keyed by index since they are identified by position, not id.
export interface ReceiptConfidenceMap {
  location?: ConfidenceLevel;
  date?: ConfidenceLevel;
  taxAmount?: ConfidenceLevel;
  storeAddress?: ConfidenceLevel;
  storePhone?: ConfidenceLevel;
  receiptId?: ConfidenceLevel;
  storeNumber?: ConfidenceLevel;
  terminalId?: ConfidenceLevel;
  payments?: Array<{
    method?: ConfidenceLevel;
    amount?: ConfidenceLevel;
    lastFour?: ConfidenceLevel;
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
  payments: ScanPayment[];
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
