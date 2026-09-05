// Transaction accounts follow the card's current parent. Keep local updates,
// merges and remote card notifications consistent across every derived read.
export const CARD_CHANGE_QUERY_KEYS = [
  ["cards"],
  ["transactions"],
  ["transaction-accounts"],
  ["trips"],
  ["dashboard", "summary"],
  ["dashboard", "spending-by-account"],
  ["ynab", "split-comparison"],
  ["receipts", "list"],
] as const;
