import { formatPurchasePrice, purchaseSearchHref } from "./purchase-search";
it("preserves literal search characters in a shareable URL", () => {
  const url = new URL(
    purchaseSearchHref("  milk & 50%_off  "),
    "https://receipts.test",
  );
  expect(url.pathname).toBe("/search");
  expect(url.searchParams.get("q")).toBe("milk & 50%_off");
});
it("shows explicit currencies and preserves sub-cent unit prices", () => {
  expect(formatPurchasePrice(1.2345, "USD", true)).toMatch(/USD\s1\.2345/);
  expect(formatPurchasePrice(1.2, "CAD", true)).toMatch(/CAD\s1\.20/);
  expect(formatPurchasePrice(2.47, "USD")).toMatch(/USD\s2\.47/);
});
