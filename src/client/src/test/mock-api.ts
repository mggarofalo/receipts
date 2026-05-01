/**
 * Shared mock factories for API response shapes.
 *
 * These factories produce correctly-shaped objects matching the OpenAPI-generated
 * DTOs, making it easy to write tests with the right shapes and hard to write
 * them with wrong shapes.
 *
 * Usage:
 *   import { mockCardResponse, mockCardListResponse } from "@/test/mock-api";
 *
 *   const card = mockCardResponse({ name: "My Card" });
 *   const list = mockCardListResponse([card]);
 */
import type { components } from "@/generated/api";

// ---------------------------------------------------------------------------
// Type aliases for readability
// ---------------------------------------------------------------------------
type AccountResponse = components["schemas"]["AccountResponse"];
type CardResponse = components["schemas"]["CardResponse"];
type CategoryResponse = components["schemas"]["CategoryResponse"];
type SubcategoryResponse = components["schemas"]["SubcategoryResponse"];
type ReceiptResponse = components["schemas"]["ReceiptResponse"];
type ReceiptItemResponse = components["schemas"]["ReceiptItemResponse"];
type TransactionResponse = components["schemas"]["TransactionResponse"];
type AdjustmentResponse = components["schemas"]["AdjustmentResponse"];
type ItemTemplateResponse = components["schemas"]["ItemTemplateResponse"];

type CardListResponse = components["schemas"]["CardListResponse"];
type CategoryListResponse = components["schemas"]["CategoryListResponse"];
type SubcategoryListResponse = components["schemas"]["SubcategoryListResponse"];
type ReceiptListResponse = components["schemas"]["ReceiptListResponse"];
type ReceiptItemListResponse = components["schemas"]["ReceiptItemListResponse"];
type TransactionListResponse = components["schemas"]["TransactionListResponse"];
type AdjustmentListResponse = components["schemas"]["AdjustmentListResponse"];
type ItemTemplateListResponse =
  components["schemas"]["ItemTemplateListResponse"];

// ---------------------------------------------------------------------------
// Internal counter for deterministic IDs in tests
// ---------------------------------------------------------------------------
let counter = 0;

function nextId(): string {
  counter += 1;
  const hex = counter.toString(16).padStart(12, "0");
  return `00000000-0000-4000-a000-${hex}`;
}

/**
 * Reset the internal ID counter. Call in `beforeEach` if tests depend on
 * deterministic IDs.
 */
export function resetMockIds(): void {
  counter = 0;
}

// ---------------------------------------------------------------------------
// Generic paginated response factory
// ---------------------------------------------------------------------------

interface PaginatedEnvelope<T> {
  data: T[];
  total: number;
  offset: number;
  limit: number;
}

/**
 * Wraps an array of items in the standard paginated response envelope
 * (`{ data, total, offset, limit }`).
 *
 * By default, `total` equals the length of `items`, `offset` is 0, and
 * `limit` is 50. Use `overrides` to customise any field.
 */
export function mockPaginatedResponse<T>(
  items: T[],
  overrides?: Partial<PaginatedEnvelope<T>>,
): PaginatedEnvelope<T> {
  return {
    data: items,
    total: items.length,
    offset: 0,
    limit: 50,
    ...overrides,
  };
}

// ---------------------------------------------------------------------------
// Per-entity item factories
// ---------------------------------------------------------------------------

/** Creates a single `CardResponse` with sensible defaults. */
export function mockCardResponse(
  overrides?: Partial<CardResponse>,
): CardResponse {
  return {
    id: overrides?.id ?? nextId(),
    cardCode: "CARD-001",
    name: "Test Card",
    isActive: true,
    accountId: overrides?.accountId ?? nextId(),
    ...overrides,
  };
}

/** Creates a single `AccountResponse` with sensible defaults. */
export function mockAccountResponse(
  overrides?: Partial<AccountResponse>,
): AccountResponse {
  return {
    id: nextId(),
    name: "Test Account",
    isActive: true,
    ...overrides,
  };
}

/** Creates a single `CategoryResponse` with sensible defaults. */
export function mockCategoryResponse(
  overrides?: Partial<CategoryResponse>,
): CategoryResponse {
  return {
    id: nextId(),
    name: "Test Category",
    description: null,
    isActive: true,
    ...overrides,
  };
}

/** Creates a single `SubcategoryResponse` with sensible defaults. */
export function mockSubcategoryResponse(
  overrides?: Partial<SubcategoryResponse>,
): SubcategoryResponse {
  return {
    id: overrides?.id ?? nextId(),
    name: "Test Subcategory",
    categoryId: overrides?.categoryId ?? nextId(),
    description: null,
    isActive: true,
    ...overrides,
  };
}

/** Creates a single `ReceiptResponse` with sensible defaults. */
export function mockReceiptResponse(
  overrides?: Partial<ReceiptResponse>,
): ReceiptResponse {
  return {
    id: nextId(),
    location: "Test Store",
    date: "2025-01-15",
    taxAmount: 5.0,
    ...overrides,
  };
}

/** Creates a single `ReceiptItemResponse` with sensible defaults. */
export function mockReceiptItemResponse(
  overrides?: Partial<ReceiptItemResponse>,
): ReceiptItemResponse {
  return {
    id: overrides?.id ?? nextId(),
    receiptId: overrides?.receiptId ?? nextId(),
    receiptItemCode: null,
    description: "Test Item",
    quantity: 1,
    unitPrice: 9.99,
    totalPrice: 9.99,
    category: "General",
    subcategory: null,
    pricingMode: "quantity",
    ...overrides,
  };
}

/** Creates a single `TransactionResponse` with sensible defaults. */
export function mockTransactionResponse(
  overrides?: Partial<TransactionResponse>,
): TransactionResponse {
  return {
    id: overrides?.id ?? nextId(),
    receiptId: overrides?.receiptId ?? nextId(),
    accountId: overrides?.accountId ?? nextId(),
    cardId: overrides?.cardId ?? nextId(),
    amount: 25.0,
    date: "2025-01-15",
    ...overrides,
  };
}

/** Creates a single `AdjustmentResponse` with sensible defaults. */
export function mockAdjustmentResponse(
  overrides?: Partial<AdjustmentResponse>,
): AdjustmentResponse {
  return {
    id: overrides?.id ?? nextId(),
    receiptId: overrides?.receiptId ?? nextId(),
    type: "tip",
    amount: 5.0,
    description: null,
    ...overrides,
  };
}

/** Creates a single `ItemTemplateResponse` with sensible defaults. */
export function mockItemTemplateResponse(
  overrides?: Partial<ItemTemplateResponse>,
): ItemTemplateResponse {
  return {
    id: nextId(),
    name: "Test Template",
    description: null,
    defaultCategory: null,
    defaultSubcategory: null,
    defaultUnitPrice: null,
    defaultUnitPriceCurrency: null,
    defaultPricingMode: null,
    defaultItemCode: null,
    ...overrides,
  };
}

// ---------------------------------------------------------------------------
// Per-entity list response factories
// ---------------------------------------------------------------------------

/** Creates a `CardListResponse` in the paginated envelope. */
export function mockCardListResponse(
  items?: CardResponse[],
  overrides?: Partial<PaginatedEnvelope<CardResponse>>,
): CardListResponse {
  return mockPaginatedResponse(items ?? [mockCardResponse()], overrides);
}

/** Creates a `CategoryListResponse` in the paginated envelope. */
export function mockCategoryListResponse(
  items?: CategoryResponse[],
  overrides?: Partial<PaginatedEnvelope<CategoryResponse>>,
): CategoryListResponse {
  return mockPaginatedResponse(items ?? [mockCategoryResponse()], overrides);
}

/** Creates a `SubcategoryListResponse` in the paginated envelope. */
export function mockSubcategoryListResponse(
  items?: SubcategoryResponse[],
  overrides?: Partial<PaginatedEnvelope<SubcategoryResponse>>,
): SubcategoryListResponse {
  return mockPaginatedResponse(items ?? [mockSubcategoryResponse()], overrides);
}

/** Creates a `ReceiptListResponse` in the paginated envelope. */
export function mockReceiptListResponse(
  items?: ReceiptResponse[],
  overrides?: Partial<PaginatedEnvelope<ReceiptResponse>>,
): ReceiptListResponse {
  return mockPaginatedResponse(items ?? [mockReceiptResponse()], overrides);
}

/** Creates a `ReceiptItemListResponse` in the paginated envelope. */
export function mockReceiptItemListResponse(
  items?: ReceiptItemResponse[],
  overrides?: Partial<PaginatedEnvelope<ReceiptItemResponse>>,
): ReceiptItemListResponse {
  return mockPaginatedResponse(items ?? [mockReceiptItemResponse()], overrides);
}

/** Creates a `TransactionListResponse` in the paginated envelope. */
export function mockTransactionListResponse(
  items?: TransactionResponse[],
  overrides?: Partial<PaginatedEnvelope<TransactionResponse>>,
): TransactionListResponse {
  return mockPaginatedResponse(items ?? [mockTransactionResponse()], overrides);
}

/** Creates an `AdjustmentListResponse` in the paginated envelope. */
export function mockAdjustmentListResponse(
  items?: AdjustmentResponse[],
  overrides?: Partial<PaginatedEnvelope<AdjustmentResponse>>,
): AdjustmentListResponse {
  return mockPaginatedResponse(items ?? [mockAdjustmentResponse()], overrides);
}

/** Creates an `ItemTemplateListResponse` in the paginated envelope. */
export function mockItemTemplateListResponse(
  items?: ItemTemplateResponse[],
  overrides?: Partial<PaginatedEnvelope<ItemTemplateResponse>>,
): ItemTemplateListResponse {
  return mockPaginatedResponse(
    items ?? [mockItemTemplateResponse()],
    overrides,
  );
}
