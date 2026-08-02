import { authHandlers } from "./auth";
import { cardHandlers } from "./cards";
import { categoryHandlers } from "./categories";
import { subcategoryHandlers } from "./subcategories";
import { receiptHandlers } from "./receipts";
import { receiptItemHandlers } from "./receipt-items";
import { transactionHandlers } from "./transactions";
import { tripHandlers } from "./trips";
import { metadataHandlers } from "./metadata";
import { itemTemplateHistoryCandidateHandlers } from "./item-template-history-candidates";

export const handlers = [
  ...itemTemplateHistoryCandidateHandlers,
  ...authHandlers,
  ...cardHandlers,
  ...categoryHandlers,
  ...subcategoryHandlers,
  ...receiptHandlers,
  ...receiptItemHandlers,
  ...transactionHandlers,
  ...tripHandlers,
  ...metadataHandlers,
];
