import { describe, expect, expectTypeOf, it } from "vitest";
import type { components } from "@/generated/api";
import client from "@/lib/api-client";
import type {
  useBulkPushYnabTransactions,
  usePushYnabTransactions,
} from "./useYnab";
import useYnabSource from "./useYnab.ts?raw";
import useYnabEventsSource from "./useYnabEvents.ts?raw";
import useYnabStatusSource from "./useYnabStatus.ts?raw";

const generatedRequests = {
  connectionStatus: () => client.GET("/api/ynab/connection-status"),
  status: () => client.GET("/api/ynab/status"),
  events: () =>
    client.GET("/api/ynab/events", {
      params: { query: { offset: 0, limit: 25 } },
    }),
  staleMappings: () => client.GET("/api/ynab/stale-mappings"),
  clearStaleMappings: () => client.DELETE("/api/ynab/stale-mappings"),
  splitComparison: () =>
    client.GET("/api/ynab/receipts/{receiptId}/split-comparison", {
      params: { path: { receiptId: "receipt-id" } },
    }),
  rateLimitStatus: () => client.GET("/api/ynab/rate-limit-status"),
  pushTransactions: () =>
    client.POST("/api/ynab/push-transactions", {
      body: { receiptId: "receipt-id" },
    }),
  bulkPushTransactions: () =>
    client.POST("/api/ynab/push-transactions/bulk", {
      body: { receiptIds: ["receipt-id"] },
    }),
};

type ResponseData<TRequest extends () => Promise<{ data?: unknown }>> =
  NonNullable<Awaited<ReturnType<TRequest>>["data"]>;

type PushMutationResult = Awaited<
  ReturnType<ReturnType<typeof usePushYnabTransactions>["mutateAsync"]>
>;
type BulkPushMutationResult = Awaited<
  ReturnType<ReturnType<typeof useBulkPushYnabTransactions>["mutateAsync"]>
>;

describe("YNAB generated contract ownership", () => {
  it("types the YNAB endpoint responses from the generated client", () => {
    expect(Object.keys(generatedRequests)).toHaveLength(9);
    expectTypeOf<
      ResponseData<typeof generatedRequests.connectionStatus>
    >().toEqualTypeOf<components["schemas"]["YnabConnectionStatusResponse"]>();
    expectTypeOf<ResponseData<typeof generatedRequests.status>>().toEqualTypeOf<
      components["schemas"]["YnabStatusResponse"]
    >();
    expectTypeOf<ResponseData<typeof generatedRequests.events>>().toEqualTypeOf<
      components["schemas"]["YnabSyncEventListResponse"]
    >();
    expectTypeOf<
      ResponseData<typeof generatedRequests.staleMappings>
    >().toEqualTypeOf<components["schemas"]["StaleMappingsResponse"]>();
    expectTypeOf<
      ResponseData<typeof generatedRequests.clearStaleMappings>
    >().toEqualTypeOf<components["schemas"]["ClearStaleMappingsResponse"]>();
    expectTypeOf<
      ResponseData<typeof generatedRequests.splitComparison>
    >().toEqualTypeOf<
      components["schemas"]["ReceiptYnabSplitComparisonResponse"]
    >();
    expectTypeOf<
      ResponseData<typeof generatedRequests.rateLimitStatus>
    >().toEqualTypeOf<components["schemas"]["YnabRateLimitStatusResponse"]>();
    expectTypeOf<
      Awaited<ReturnType<typeof generatedRequests.pushTransactions>>["data"]
    >().toEqualTypeOf<
      components["schemas"]["PushYnabTransactionsResponse"] | undefined
    >();
    expectTypeOf<
      Awaited<ReturnType<typeof generatedRequests.bulkPushTransactions>>["data"]
    >().toEqualTypeOf<
      components["schemas"]["BulkPushYnabTransactionsResponse"] | undefined
    >();
  });

  it("keeps push mutation results non-optional after errors are rejected", () => {
    expectTypeOf<PushMutationResult>().toEqualTypeOf<
      components["schemas"]["PushYnabTransactionsResponse"]
    >();
    expectTypeOf<BulkPushMutationResult>().toEqualTypeOf<
      components["schemas"]["BulkPushYnabTransactionsResponse"]
    >();
  });

  it("keeps the owning hooks free of generated-contract escape hatches", () => {
    const hookSources = [
      useYnabSource,
      useYnabEventsSource,
      useYnabStatusSource,
    ];

    for (const source of hookSources) {
      expect(source).not.toMatch(/\bas never\b/);
      expect(source).not.toMatch(/\bas unknown as\b/);
    }
  });
});
