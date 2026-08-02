import type { components } from "@/generated/api";

type ItemTemplateHistoryCandidateResponse =
  components["schemas"]["ItemTemplateHistoryCandidateResponse"];

export const itemTemplateHistoryCandidates: ItemTemplateHistoryCandidateResponse[] =
  [
    {
      name: "Orange Juice",
      occurrenceCount: 6,
      lastPurchasedAt: "2026-05-14",
      suggestedCategory: "Groceries",
      suggestedSubcategory: "Beverages",
      suggestedUnitPrice: 5.29,
      suggestedItemCode: "OJ-100",
    },
    {
      name: "Paper Towels",
      occurrenceCount: 4,
      lastPurchasedAt: "2026-04-02",
      suggestedCategory: "Household",
      suggestedSubcategory: "Cleaning",
      suggestedUnitPrice: 12.99,
      suggestedItemCode: null,
    },
    {
      name: "Trail Mix",
      occurrenceCount: 2,
      lastPurchasedAt: "2026-03-11",
      suggestedCategory: null,
      suggestedSubcategory: null,
      suggestedUnitPrice: null,
      suggestedItemCode: null,
    },
  ];
