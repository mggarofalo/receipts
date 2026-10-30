import type { components } from "@/generated/api";

type ReceiptItemResponse = components["schemas"]["ReceiptItemResponse"];

export const receiptItems: ReceiptItemResponse[] = [
  {
    id: "bbbb1111-1111-1111-1111-111111111111",
    receiptId: "aaaa1111-1111-1111-1111-111111111111",
    receiptItemCode: "SKU-001",
    description: "Milk",
    quantity: 2,
    unitPrice: 3.99,
    category: "Groceries",
    subcategory: "Dairy",
    envelopeId: null,
    envelopeSource: "historical",
  },
  {
    id: "bbbb2222-2222-2222-2222-222222222222",
    receiptId: "aaaa1111-1111-1111-1111-111111111111",
    receiptItemCode: null,
    description: "Bread",
    quantity: 1,
    unitPrice: 4.5,
    category: "Groceries",
    subcategory: "Bakery",
    envelopeId: null,
    envelopeSource: "historical",
  },
  {
    id: "bbbb3333-3333-3333-3333-333333333333",
    receiptId: "aaaa2222-2222-2222-2222-222222222222",
    receiptItemCode: "HW-100",
    description: "Screwdriver Set",
    quantity: 1,
    unitPrice: 24.99,
    category: "Tools",
    subcategory: null,
    envelopeId: null,
    envelopeSource: "historical",
  },
];
