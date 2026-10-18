import { QueryClient, type QueryKey } from "@tanstack/react-query";
import { describe, expect, it } from "vitest";
import { type DomainChange, repairDomainChange } from "./query-invalidation";

const cacheKeys = {
  budget: ["ynab", "settings", "budget"],
  accounts: ["ynab", "accounts"],
  accountMappings: ["ynab", "account-mappings"],
  categoryMappings: ["ynab", "category-mappings"],
  unmappedCategories: ["ynab", "category-mappings", "unmapped"],
  staleMappings: ["ynab", "stale-mappings"],
  splitComparison: ["ynab", "split-comparison"],
  syncStatus: ["ynab", "sync-status"],
  receiptSyncStatuses: ["ynab", "receipt-sync-statuses"],
  connectionStatus: ["ynab", "connection-status"],
  events: ["ynab", "events"],
  status: ["ynab", "status"],
  similarItems: ["similarItems", "milk"],
  categoryRecommendations: ["categoryRecommendations", "milk"],
  unrelated: ["receipts"],
} satisfies Record<string, QueryKey>;

function seededClient() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  for (const key of Object.values(cacheKeys)) {
    client.setQueryData(key, { cached: true });
  }
  return client;
}

describe("YNAB committed-change invalidation", () => {
  it.each<{
    change: DomainChange;
    affected: QueryKey[];
  }>([
    {
      change: "ynab-budget",
      affected: Object.values(cacheKeys).filter((key) => key[0] === "ynab"),
    },
    {
      change: "ynab-mapping",
      affected: [
        cacheKeys.accountMappings,
        cacheKeys.categoryMappings,
        cacheKeys.unmappedCategories,
        cacheKeys.staleMappings,
        cacheKeys.splitComparison,
      ],
    },
    {
      change: "ynab-sync-record",
      affected: [
        cacheKeys.syncStatus,
        cacheKeys.receiptSyncStatuses,
        cacheKeys.splitComparison,
        cacheKeys.connectionStatus,
      ],
    },
    {
      change: "ynab-sync-event",
      affected: [cacheKeys.events, cacheKeys.status],
    },
    {
      change: "item-embedding",
      affected: [cacheKeys.similarItems, cacheKeys.categoryRecommendations],
    },
  ])(
    "$change repairs exactly its projection family",
    async ({ change, affected }) => {
      const client = seededClient();

      await repairDomainChange(client, change, () => true);

      const affectedIdentities = new Set(
        affected.map((key) => JSON.stringify(key)),
      );
      for (const key of Object.values(cacheKeys)) {
        expect(client.getQueryState(key)?.isInvalidated).toBe(
          affectedIdentities.has(JSON.stringify(key)),
        );
      }
    },
  );

  it("does nothing after its session guard becomes obsolete", async () => {
    const client = seededClient();

    await repairDomainChange(client, "ynab-sync-record", () => false);

    for (const key of Object.values(cacheKeys)) {
      expect(client.getQueryState(key)?.isInvalidated).toBe(false);
    }
  });
});

it("repairs the health badge after accepting or unaccepting duplicates", async () => {
  const client = new QueryClient();
  const health = ["reports", "health-summary"];
  client.setQueryData(health, { duplicateGroups: 2 });
  client.setQueryData(["reports", "spending"], { amount: 50 });
  await repairDomainChange(client, "duplicate-acceptance", () => true);
  expect(client.getQueryState(health)?.isInvalidated).toBe(true);
  expect(client.getQueryState(["reports", "spending"])?.isInvalidated).toBe(
    false,
  );
  client.clear();
});

it.each(["receipt", "transaction"] as const)(
  "repairs duplicate acceptance projections after %s facts change",
  async (change) => {
    const client = new QueryClient();
    const keys = [
      ["reports", "health-summary"],
      ["reports", "accepted-duplicates"],
      ["reports", "duplicates"],
    ];
    for (const key of keys) client.setQueryData(key, { cached: true });
    await repairDomainChange(client, change, () => true);
    for (const key of keys)
      expect(client.getQueryState(key)?.isInvalidated).toBe(true);
    client.clear();
  },
);
