import type { QueryClient } from "@tanstack/react-query";

// Preserve cache identities while sharing their prefixes between reads and writes.
export const queryKeys = {
  receipts: ["receipts"],
  receiptItems: ["receipt-items"],
  transactions: ["transactions"],
  adjustments: ["adjustments"],
  receiptsWithItems: ["receipts-with-items"],
  trips: ["trips"],
  transactionAccounts: ["transaction-accounts"],
  reports: ["reports"],
  dashboard: ["dashboard"],
  accounts: ["accounts"],
  cards: ["cards"],
  categories: ["categories"],
  subcategories: ["subcategories"],
  itemTemplates: ["itemTemplates"],
  templateHistoryCandidates: ["itemTemplates", "historyCandidates"],
  receiptItemSuggestions: ["receiptItemSuggestions"],
  similarItems: ["similarItems"],
  categoryRecommendations: ["categoryRecommendations"],
  normalizedDescriptions: ["normalized-descriptions"],
  ynabSplitComparison: ["ynab", "split-comparison"],
  ynabReceiptSyncStatuses: ["ynab", "receipt-sync-statuses"],
  ynabSyncStatus: ["ynab", "sync-status"],
  ynabUnmappedCategories: ["ynab", "category-mappings", "unmapped"],
  ynabStaleMappings: ["ynab", "stale-mappings"],
  ynabAccountMappings: ["ynab", "account-mappings"],
} as const;

// Refresh receipt-derived projections without invalidating unrelated YNAB query
// roots. Split comparison itself may consult YNAB to refresh its derived result.
const LEDGER_CHANGE_QUERY_KEYS = [
  queryKeys.receipts,
  queryKeys.receiptItems,
  queryKeys.transactions,
  queryKeys.adjustments,
  queryKeys.receiptsWithItems,
  queryKeys.trips,
  queryKeys.transactionAccounts,
  queryKeys.reports,
  queryKeys.dashboard,
  queryKeys.receiptItemSuggestions,
  queryKeys.templateHistoryCandidates,
  queryKeys.similarItems,
  queryKeys.categoryRecommendations,
  queryKeys.normalizedDescriptions,
  queryKeys.ynabSplitComparison,
  queryKeys.ynabReceiptSyncStatuses,
  queryKeys.ynabSyncStatus,
  queryKeys.ynabUnmappedCategories,
  queryKeys.ynabStaleMappings,
] as const;

// Card merges can remove source accounts and change YNAB account mappings.
// Retain the complete RECEIPTS-852 dependency set for ordinary card edits too.
export const CARD_CHANGE_QUERY_KEYS = [
  queryKeys.cards,
  queryKeys.accounts,
  queryKeys.transactions,
  queryKeys.transactionAccounts,
  queryKeys.trips,
  queryKeys.dashboard,
  queryKeys.ynabSplitComparison,
  queryKeys.ynabAccountMappings,
  queryKeys.ynabStaleMappings,
  queryKeys.receipts,
] as const;

const TEMPLATE_CHANGE_QUERY_KEYS = [
  queryKeys.itemTemplates,
  queryKeys.normalizedDescriptions,
  queryKeys.similarItems,
  queryKeys.categoryRecommendations,
] as const;

const DOMAIN_CHANGE_QUERY_KEYS = {
  receipt: LEDGER_CHANGE_QUERY_KEYS,
  "receipt-item": LEDGER_CHANGE_QUERY_KEYS,
  transaction: LEDGER_CHANGE_QUERY_KEYS,
  adjustment: LEDGER_CHANGE_QUERY_KEYS,
  account: CARD_CHANGE_QUERY_KEYS,
  card: CARD_CHANGE_QUERY_KEYS,
  category: [queryKeys.categories, queryKeys.subcategories],
  subcategory: [queryKeys.subcategories],
  "item-template": TEMPLATE_CHANGE_QUERY_KEYS,
  "trash-purge": [
    ...LEDGER_CHANGE_QUERY_KEYS,
    ...CARD_CHANGE_QUERY_KEYS,
    queryKeys.categories,
    queryKeys.subcategories,
    ...TEMPLATE_CHANGE_QUERY_KEYS,
  ],
} as const;

export type DomainChange = keyof typeof DOMAIN_CHANGE_QUERY_KEYS;

export function isDomainChange(value: string): value is DomainChange {
  return Object.hasOwn(DOMAIN_CHANGE_QUERY_KEYS, value);
}

/** Refresh mounted projections and mark inactive entries stale in this session. */
export function invalidateDomainChange(
  queryClient: QueryClient,
  change: DomainChange,
  operation: "created" | "changed" = "changed",
) {
  const seen = new Set<string>();
  for (const queryKey of DOMAIN_CHANGE_QUERY_KEYS[change]) {
    // Promotion deliberately retains mounted similarity results until their normal
    // freshness expires: a new template may not have its embedding yet (RECEIPTS-866).
    if (change === "item-template" && operation === "created" &&
      (queryKey === queryKeys.similarItems || queryKey === queryKeys.categoryRecommendations)) {
      continue;
    }
    const identity = JSON.stringify(queryKey);
    if (seen.has(identity)) continue;
    seen.add(identity);
    void queryClient.invalidateQueries({ queryKey, refetchType: "active" });
  }
}
