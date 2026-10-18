export function purchaseSearchHref(query: string): string {
  return `/search?${new URLSearchParams({ q: query.trim() })}`;
}

/** Currency codes remain explicit: a dollar symbol alone is ambiguous. */
export function formatPurchasePrice(
  amount: number,
  currency: string,
  unitPrice = false,
): string {
  return new Intl.NumberFormat("en-US", {
    style: "currency",
    currency,
    currencyDisplay: "code",
    minimumFractionDigits: 2,
    maximumFractionDigits: unitPrice ? 4 : 2,
  }).format(amount);
}
