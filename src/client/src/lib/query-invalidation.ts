import { matchQuery, type Query, type QueryClient, type QueryKey } from "@tanstack/react-query";

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
  normalizedDescriptionSettings: ["normalized-descriptions", "settings"],
  reportDuplicates: ["reports", "duplicates"],
  acceptedDuplicates: ["reports", "accepted-duplicates"],
  ynab: ["ynab"],
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

// Curation changes are visible anywhere a canonical label or link is projected.
// Keep this separate from the monetary ledger union: renaming or reviewing a
// description does not change receipt totals or dashboard ranges.
const NORMALIZED_DESCRIPTION_CHANGE_QUERY_KEYS = [
  queryKeys.normalizedDescriptions,
  queryKeys.receiptItems,
  queryKeys.receiptsWithItems,
  queryKeys.trips,
  queryKeys.reports,
  // Link/reject/merge operations can also repoint the server-side template
  // association. The current template wire shape omits that field, but retaining
  // this existing repair keeps dependent template evidence ready for contract growth.
  queryKeys.itemTemplates,
] as const;

const DUPLICATE_ACCEPTANCE_CHANGE_QUERY_KEYS = [
  queryKeys.reportDuplicates,
  queryKeys.acceptedDuplicates,
] as const;

// Portable restore can change destination settings as well as ledger rows.
const BACKUP_IMPORT_QUERY_KEYS = [
  ...LEDGER_CHANGE_QUERY_KEYS,
  ...CARD_CHANGE_QUERY_KEYS,
  queryKeys.categories,
  queryKeys.subcategories,
  ...TEMPLATE_CHANGE_QUERY_KEYS,
  queryKeys.ynab,
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
  "normalized-description": NORMALIZED_DESCRIPTION_CHANGE_QUERY_KEYS,
  "normalized-description-settings": [queryKeys.normalizedDescriptionSettings],
  "duplicate-acceptance": DUPLICATE_ACCEPTANCE_CHANGE_QUERY_KEYS,
  "backup-import": BACKUP_IMPORT_QUERY_KEYS,
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

function projectionKeysForChange(
  change: DomainChange,
  operation: "created" | "changed",
): readonly QueryKey[] {
  if (change !== "item-template" || operation !== "created") {
    return DOMAIN_CHANGE_QUERY_KEYS[change];
  }

  // A new template may not have its embedding yet (RECEIPTS-866). Keep mounted
  // similarity results until the embedding-completion producer repairs them.
  return DOMAIN_CHANGE_QUERY_KEYS[change].filter(
    (queryKey) =>
      queryKey !== queryKeys.similarItems &&
      queryKey !== queryKeys.categoryRecommendations,
  );
}

/** Refresh mounted projections and mark inactive entries stale in this session. */
export function invalidateDomainChange(
  queryClient: QueryClient,
  change: DomainChange,
  operation: "created" | "changed" = "changed",
) {
  const seen = new Set<string>();
  for (const queryKey of projectionKeysForChange(change, operation)) {
    const identity = JSON.stringify(queryKey);
    if (seen.has(identity)) continue;
    seen.add(identity);
    void queryClient.invalidateQueries({ queryKey, refetchType: "active" });
  }
}

/** Cancel stale reads before repairing a known domain projection family. */
export function repairDomainChange(
  queryClient: QueryClient,
  change: DomainChange,
  isCurrent: () => boolean,
  operation: "created" | "changed" = "changed",
) {
  return repairDomainQueries(
    queryClient,
    projectionKeysForChange(change, operation),
    isCurrent,
  );
}

// Reconnection repairs unknown missed changes, independent of individual event
// exceptions (such as a known template creation retaining similarity results).
const RECONNECT_QUERY_KEYS = Array.from(
  new Map(Object.values(DOMAIN_CHANGE_QUERY_KEYS).flat().map(
    (queryKey) => [JSON.stringify(queryKey), queryKey],
  )).values(),
);

/** Cancel potentially stale reads, then repair this domain union once. */
async function repairDomainQueries(
  queryClient: QueryClient,
  prefixes: readonly QueryKey[],
  isCurrent: () => boolean,
) {
  const matches = (query: Query) => prefixes.some((queryKey) => matchQuery({ queryKey }, query));
  // First reads can be reused by invalidation, and inactive reads can continue
  // after navigation. Either old result would clear staleness (including Infinity
  // freshness). Cancel all matching in-flight reads before asking for current data.
  if (!isCurrent()) return;
  await queryClient.cancelQueries({
    predicate: (query) => matches(query) && query.state.fetchStatus !== "idle",
  });
  if (!isCurrent()) return;
  // A union predicate prevents ancestor/child prefixes (especially YNAB) from
  // repeatedly cancelling and restarting the same active refetch.
  await queryClient.invalidateQueries({ predicate: matches, refetchType: "active" });
}

export function invalidateAfterReconnect(
  queryClient: QueryClient,
  isCurrentConnection: () => boolean,
) {
  return repairDomainQueries(queryClient, RECONNECT_QUERY_KEYS, isCurrentConnection);
}

export function invalidateAfterBackupImport(
  queryClient: QueryClient,
  isCurrentSession: () => boolean,
) {
  return repairDomainQueries(queryClient, BACKUP_IMPORT_QUERY_KEYS, isCurrentSession);
}
