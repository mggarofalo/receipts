import type { components } from "@/generated/api";
import { generateId } from "@/lib/id";
import type { ReceiptLineItem } from "@/pages/new-receipt/LineItemsSection";
import type { ReceiptTransaction } from "@/pages/new-receipt/TransactionsSection";
import type {
  ConfidenceLevel,
  ScanInitialValues,
  ReceiptConfidenceMap,
} from "./types";

type ProposedReceiptResponse = components["schemas"]["ProposedReceiptResponse"];

type ItemConfidenceEntry = NonNullable<ReceiptConfidenceMap["items"]>[number];
type TransactionConfidenceEntry = NonNullable<
  ReceiptConfidenceMap["transactions"]
>[number];

/**
 * True when a confidence value indicates the user needs to review the field.
 * "high" — extracted with high confidence; no review needed.
 * "none" — the source receipt did not contain this field; there is nothing to review.
 * "low" / "medium" — extracted with reduced confidence; surface to the user.
 */
function needsReview(confidence: ConfidenceLevel): boolean {
  return confidence !== "high" && confidence !== "none";
}

/**
 * Map a {@link ProposedReceiptResponse} (returned by the VLM scan endpoint)
 * to the {@link ScanInitialValues} shape consumed by the new-receipt wizard.
 */
export function mapProposalToInitialValues(
  proposal: ProposedReceiptResponse,
): ScanInitialValues {
  // Defensive guards: the OpenAPI contract declares these arrays as required
  // (non-optional), but a stale fixture or partially-stubbed test handler can
  // omit them. Coalescing to [] prevents a hard `TypeError: Cannot read
  // properties of undefined` and keeps the wizard usable. See RECEIPTS-632.
  const taxLines = proposal.taxLines ?? [];
  const items = proposal.items ?? [];
  // proposedTransactions is the new (RECEIPTS-657) replacement for the legacy
  // payments[] field; it is declared optional in the OpenAPI contract because
  // it ships alongside the deprecated payments[] until the latter is removed.
  const proposedTransactions = proposal.proposedTransactions ?? [];

  const taxAmount = Number(taxLines[0]?.amount ?? 0);

  let date = "";
  if (proposal.date) {
    // The API returns a DateOnly (YYYY-MM-DD). If it comes as ISO datetime, extract the date part.
    date = proposal.date.split("T")[0];
  }

  return {
    header: {
      location: proposal.storeName ?? "",
      date,
      taxAmount,
      storeAddress: proposal.storeAddress ?? "",
      storePhone: proposal.storePhone ?? "",
    },
    metadata: {
      receiptId: proposal.receiptId ?? "",
      storeNumber: proposal.storeNumber ?? "",
      terminalId: proposal.terminalId ?? "",
    },
    // Pre-resolved Transaction rows (RECEIPTS-657). The server resolves cards
    // by lastFour and surfaces the matching cardId/accountId so the wizard can
    // pre-populate Transaction rows directly. An unmatched payment yields a
    // null cardId; we coerce to "" so the row falls back to "needs picker".
    proposedTransactions: proposedTransactions.map((t) => {
      const proposalDate = t.date
        ? t.date.split("T")[0]
        : (date ?? "");
      return {
        cardId: t.cardId ?? "",
        accountId: t.accountId ?? "",
        amount: Number(t.amount ?? 0),
        date: proposalDate,
      };
    }),
    items: items.map((item) => {
      // Decide pricingMode from confidence + presence: when only the line total
      // is reliable (Walmart-style: unit-priced items printed without quantity
      // or unit-price columns), the wizard must enter "flat" mode and treat the
      // total as the source of truth. Otherwise fall back to traditional
      // quantity x unit-price math. See RECEIPTS-655.
      const hasOnlyTotal =
        item.totalPriceConfidence === "high" &&
        (item.quantityConfidence === "none" || !item.quantity) &&
        (item.unitPriceConfidence === "none" || !item.unitPrice);

      if (hasOnlyTotal) {
        const totalPrice = Number(item.totalPrice ?? 0);
        return {
          receiptItemCode: item.code ?? "",
          description: item.description ?? "",
          pricingMode: "flat" as const,
          // Domain requires quantity == 1 for flat-priced items.
          quantity: 1,
          unitPrice: 0,
          totalPrice,
          category: "",
          subcategory: "",
          taxCode: item.taxCode ?? "",
        };
      }

      const quantity = Number(item.quantity ?? 1);
      const unitPrice = Number(item.unitPrice ?? 0);
      // Carry totalPrice through: prefer the VLM's value when present so the
      // saved total survives any later rounding ambiguity. Fall back to the
      // computed total for round-trip consistency on traditional receipts.
      const totalPrice =
        item.totalPrice != null
          ? Number(item.totalPrice)
          : Math.round(quantity * unitPrice * 100) / 100;
      return {
        receiptItemCode: item.code ?? "",
        description: item.description ?? "",
        pricingMode: "quantity" as const,
        quantity,
        unitPrice,
        totalPrice,
        category: "",
        subcategory: "",
        taxCode: item.taxCode ?? "",
      };
    }),
  };
}

/**
 * Build a confidence map from the proposal that highlights low/medium fields
 * (so the new-receipt wizard can render review badges).
 *
 * Fields whose confidence is "high" or "none" are intentionally omitted — the
 * absence of a key signals "no badge needed". "high" means we are confident in
 * the extracted value; "none" means the source receipt did not contain that
 * field at all (so there is nothing for the user to review).
 */
export function mapProposalToConfidenceMap(
  proposal: ProposedReceiptResponse,
): ReceiptConfidenceMap {
  // See note in mapProposalToInitialValues — same defensive guards.
  const taxLines = proposal.taxLines ?? [];
  const items = proposal.items ?? [];
  const proposedTransactions = proposal.proposedTransactions ?? [];

  const map: ReceiptConfidenceMap = {};

  if (needsReview(proposal.storeNameConfidence)) {
    map.location = proposal.storeNameConfidence;
  }
  if (needsReview(proposal.dateConfidence)) {
    map.date = proposal.dateConfidence;
  }

  // Use the first tax line's confidence, or the subtotal confidence as fallback.
  // `??` is not enough: under RECEIPTS-631, an absent tax-line amount comes back as
  // "none" — a non-nullish string — which would block the fallback and leave the
  // taxAmount badge silently unset even when the subtotal came in at low confidence.
  // Treat "none" the same way as `undefined` for the purpose of falling back.
  const firstTaxAmountConfidence = taxLines[0]?.amountConfidence;
  const taxConfidence =
    firstTaxAmountConfidence && firstTaxAmountConfidence !== "none"
      ? firstTaxAmountConfidence
      : proposal.subtotalConfidence;
  if (needsReview(taxConfidence)) {
    map.taxAmount = taxConfidence;
  }

  if (needsReview(proposal.storeAddressConfidence)) {
    map.storeAddress = proposal.storeAddressConfidence;
  }
  if (needsReview(proposal.storePhoneConfidence)) {
    map.storePhone = proposal.storePhoneConfidence;
  }
  if (needsReview(proposal.receiptIdConfidence)) {
    map.receiptId = proposal.receiptIdConfidence;
  }
  if (needsReview(proposal.storeNumberConfidence)) {
    map.storeNumber = proposal.storeNumberConfidence;
  }
  if (needsReview(proposal.terminalIdConfidence)) {
    map.terminalId = proposal.terminalIdConfidence;
  }

  // Per-transaction confidences. Always emit an entry per pre-resolved
  // transaction so indices align with the same array consumed by the wizard,
  // omitting fields whose confidence is "high" or "none".
  if (proposedTransactions.length > 0) {
    map.transactions = proposedTransactions.map((t) => {
      const entry: TransactionConfidenceEntry = {};
      if (needsReview(t.cardIdConfidence)) entry.cardId = t.cardIdConfidence;
      if (needsReview(t.amountConfidence)) entry.amount = t.amountConfidence;
      if (needsReview(t.dateConfidence)) entry.date = t.dateConfidence;
      return entry;
    });
  }

  // Per-item taxCode confidences.
  if (items.length > 0) {
    const itemEntries = items.map((item) => {
      const entry: ItemConfidenceEntry = {};
      if (needsReview(item.taxCodeConfidence)) {
        entry.taxCode = item.taxCodeConfidence;
      }
      return entry;
    });
    // Only include if any entry has at least one non-high/non-none confidence
    if (itemEntries.some((e) => Object.keys(e).length > 0)) {
      map.items = itemEntries;
    }
  }

  return map;
}

/**
 * Build the new-receipt wizard's initial line items along with a confidence
 * map keyed by the freshly-generated row id. Pairing confidence with id
 * (rather than index) keeps confidence correctly attached to a row after
 * additions or deletions. The map is write-once: stale entries for deleted
 * rows are harmless because the row will never be looked up again.
 */
export function initialItemsAndConfidence(
  initialValues: ScanInitialValues | undefined,
  confidenceMap: ReceiptConfidenceMap | undefined,
): {
  items: ReceiptLineItem[];
  itemConfidenceById: Map<string, ItemConfidenceEntry>;
} {
  const sourceItems = initialValues?.items ?? [];
  const sourceConfidence = confidenceMap?.items ?? [];

  const items: ReceiptLineItem[] = sourceItems.map((item) => ({
    id: generateId(),
    ...item,
  }));
  const itemConfidenceById = new Map<string, ItemConfidenceEntry>();
  for (let i = 0; i < items.length; i++) {
    const entry = sourceConfidence[i];
    if (entry) {
      itemConfidenceById.set(items[i].id, entry);
    }
  }
  return { items, itemConfidenceById };
}

/**
 * Build the new-receipt wizard's initial transactions along with a confidence
 * map keyed by the freshly-generated row id. See {@link initialItemsAndConfidence}
 * for the rationale. The transactions originate from the server-side
 * {@link ProposedTransactionResolver} (RECEIPTS-657), which resolves each
 * VLM-extracted payment to a Card by lastFour and propagates the Card's
 * accountId so the wizard can pre-populate a Transaction row directly.
 */
export function initialTransactionsAndConfidence(
  initialValues: ScanInitialValues | undefined,
  confidenceMap: ReceiptConfidenceMap | undefined,
): {
  transactions: ReceiptTransaction[];
  transactionConfidenceById: Map<string, TransactionConfidenceEntry>;
} {
  const sourceTransactions = initialValues?.proposedTransactions ?? [];
  const sourceConfidence = confidenceMap?.transactions ?? [];

  const transactions: ReceiptTransaction[] = sourceTransactions.map((t) => ({
    id: generateId(),
    cardId: t.cardId,
    accountId: t.accountId,
    amount: t.amount,
    date: t.date,
  }));
  const transactionConfidenceById = new Map<
    string,
    TransactionConfidenceEntry
  >();
  for (let i = 0; i < transactions.length; i++) {
    const entry = sourceConfidence[i];
    if (entry) {
      transactionConfidenceById.set(transactions[i].id, entry);
    }
  }
  return { transactions, transactionConfidenceById };
}
