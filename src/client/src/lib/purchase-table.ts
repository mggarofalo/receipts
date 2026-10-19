import { z } from "zod";
import type { PurchaseSearchOptions } from "@/hooks/usePurchaseSearch";

export const PURCHASE_SORT_COLUMNS = [
  "date",
  "description",
  "location",
  "unitPrice",
  "quantity",
  "totalAmount",
] as const;
export type PurchaseSortColumn = (typeof PURCHASE_SORT_COLUMNS)[number];

const date = z.string().refine((value) => {
  if (!value) return true;
  if (!/^\d{4}-\d{2}-\d{2}$/.test(value) || Number(value.slice(0, 4)) < 1)
    return false;
  const parsed = new Date(`${value}T00:00:00Z`);
  return (
    Number.isFinite(parsed.getTime()) &&
    parsed.toISOString().slice(0, 10) === value
  );
}, "Use a valid date.");
const price = z
  .string()
  .refine(
    (value) =>
      !value ||
      (value.trim() !== "" &&
        Number.isFinite(Number(value)) &&
        Number(value) >= 0),
    "Use a non-negative price.",
  );

export const purchaseFiltersSchema = z
  .object({
    location: z
      .string()
      .trim()
      .max(200, "Use at most 200 characters for the store."),
    startDate: date,
    endDate: date,
    minUnitPrice: price,
    maxUnitPrice: price,
  })
  .refine(
    (value) =>
      !value.startDate || !value.endDate || value.startDate <= value.endDate,
    {
      message: "End date must not be earlier than start date.",
      path: ["endDate"],
    },
  )
  .refine(
    (value) =>
      !value.minUnitPrice ||
      !value.maxUnitPrice ||
      Number(value.minUnitPrice) <= Number(value.maxUnitPrice),
    {
      message: "Maximum price must not be less than minimum price.",
      path: ["maxUnitPrice"],
    },
  );
export type PurchaseFilters = z.infer<typeof purchaseFiltersSchema>;
export const EMPTY_PURCHASE_FILTERS: PurchaseFilters = {
  location: "",
  startDate: "",
  endDate: "",
  minUnitPrice: "",
  maxUnitPrice: "",
};

export function readPurchaseFilters(params: URLSearchParams): PurchaseFilters {
  return Object.fromEntries(
    Object.keys(EMPTY_PURCHASE_FILTERS).map((key) => [
      key,
      params.get(key) ?? "",
    ]),
  ) as PurchaseFilters;
}

export function purchaseFilterOptions(
  filters: PurchaseFilters,
): PurchaseSearchOptions {
  return {
    location: filters.location.trim() || undefined,
    startDate: filters.startDate || undefined,
    endDate: filters.endDate || undefined,
    minUnitPrice: filters.minUnitPrice
      ? Number(filters.minUnitPrice)
      : undefined,
    maxUnitPrice: filters.maxUnitPrice
      ? Number(filters.maxUnitPrice)
      : undefined,
  };
}
