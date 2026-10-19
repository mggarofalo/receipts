import {
  EMPTY_PURCHASE_FILTERS,
  purchaseFilterOptions,
  purchaseFiltersSchema,
  readPurchaseFilters,
} from "./purchase-table";
it("keeps URL filter facts for validation rather than silently discarding invalid links", () => {
  const filters = readPurchaseFilters(
    new URLSearchParams(
      "location=Market&startDate=not-a-date&minUnitPrice=-1&unrelated=keep",
    ),
  );
  expect(filters).toEqual({
    ...EMPTY_PURCHASE_FILTERS,
    location: "Market",
    startDate: "not-a-date",
    minUnitPrice: "-1",
  });
  expect(purchaseFiltersSchema.safeParse(filters).success).toBe(false);
});
it("accepts equal inclusive bounds including zero and preserves four-decimal prices", () => {
  const filters = purchaseFiltersSchema.parse({
    ...EMPTY_PURCHASE_FILTERS,
    location: "  Market  ",
    startDate: "2024-02-29",
    endDate: "2024-02-29",
    minUnitPrice: "0",
    maxUnitPrice: "1.2345",
  });
  expect(purchaseFilterOptions(filters)).toEqual({
    location: "Market",
    startDate: "2024-02-29",
    endDate: "2024-02-29",
    minUnitPrice: 0,
    maxUnitPrice: 1.2345,
  });
});
it.each([
  { startDate: "2025-02-29" },
  { startDate: "0000-01-01" },
  { endDate: "2025-13-01" },
  { startDate: "2025-01-02", endDate: "2025-01-01" },
  { minUnitPrice: "-0.0001" },
  { maxUnitPrice: "Infinity" },
  { minUnitPrice: "NaN" },
  { minUnitPrice: "1.0001", maxUnitPrice: "1" },
  { location: "x".repeat(201) },
])("rejects invalid date/price/store constraints %j", (patch) => {
  expect(
    purchaseFiltersSchema.safeParse({ ...EMPTY_PURCHASE_FILTERS, ...patch })
      .success,
  ).toBe(false);
});
