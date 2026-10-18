import type { components } from "@/generated/api";
import { accounts } from "./accounts";

export const cards: components["schemas"]["CardResponse"][] = [
  {
    id: "aaaa1111-1111-1111-1111-111111111111",
    accountId: accounts[0].id,
    cardCode: "1000",
    name: "Cash",
    isActive: true,
  },
  {
    id: "aaaa2222-2222-2222-2222-222222222222",
    accountId: accounts[0].id,
    cardCode: "2000",
    name: "Credit Card",
    isActive: true,
  },
  {
    id: "aaaa3333-3333-3333-3333-333333333333",
    accountId: accounts[0].id,
    cardCode: "3000",
    name: "Closed Savings",
    isActive: false,
  },
];
