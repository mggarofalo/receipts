/**
 * Single source of truth for an item's line total. Both the LineItemsSection
 * footer and the NewReceiptPage BalanceSidebar consume this so the two
 * subtotals can never disagree (RECEIPTS-655 / RECEIPTS-661): for "flat" rows
 * the receipt-printed `totalPrice` is authoritative; for "quantity" rows the
 * total is `q × p` rounded to cents to dodge IEEE-754 noise.
 */
export function computeLineTotal(item: {
  pricingMode: "quantity" | "flat";
  quantity: number;
  unitPrice: number;
  totalPrice: number;
}): number {
  if (item.pricingMode === "flat") {
    return item.totalPrice;
  }
  return Math.round(item.quantity * item.unitPrice * 100) / 100;
}
