import {
  QueryClient,
  QueryObserver,
  type QueryKey,
} from "@tanstack/react-query";
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

it("refreshes server receipt counts and identity projections after a review change", async () => {
  const client = new QueryClient();
  const keys = [
    ["receipts", "list"],
    ["receipts", "detail", "receipt-1"],
    ["receipts-with-items"],
    ["receipt-items"],
    ["reports", "purchase-search"],
    ["reports", "spending-by-normalized-description"],
  ];
  for (const key of keys) client.setQueryData(key, { cached: true });
  client.setQueryData(["accounts"], { cached: true });
  await repairDomainChange(client, "normalized-description", () => true);
  for (const key of keys)
    expect(client.getQueryState(key)?.isInvalidated).toBe(true);
  expect(client.getQueryState(["accounts"])?.isInvalidated).toBe(false);
  client.clear();
});

describe("taxonomy cross-session repair", () => {
  const current = [
    "dashboard",
    "spending-by-category",
    "currentHierarchy",
    "2026",
  ];
  const historical = [
    "dashboard",
    "spending-by-category",
    "asClassified",
    "2026",
  ];
  const tree = ["taxonomy", "current"];
  const snapshot = ["taxonomy", "old-revision"];
  // Match the trends hook's existing object-valued params, including filters.
  const currentTrends = [
    "reports",
    "category-trends",
    {
      categoryView: "currentHierarchy",
      rollup: "root",
      startDate: "2026-01-01",
      endDate: "2026-12-31",
      categoryId: "category-1",
    },
  ];
  const historicalTrends = [
    "reports",
    "category-trends",
    { categoryView: "asClassified", rollup: "root" },
  ];

  it("refreshes current-hierarchy trends with filtered params while preserving historical trends", async () => {
    const client = new QueryClient();
    for (const key of [currentTrends, historicalTrends])
      client.setQueryData(key, "old");
    await repairDomainChange(client, "taxonomy", () => true);
    expect(client.getQueryState(currentTrends)?.isInvalidated).toBe(true);
    expect(client.getQueryState(historicalTrends)?.isInvalidated).toBe(false);
    client.clear();
  });

  it("refreshes the current hierarchy and choices, preserving historical views", async () => {
    const client = new QueryClient();
    for (const key of [current, historical, tree, snapshot])
      client.setQueryData(key, "old");
    await repairDomainChange(client, "taxonomy", () => true);
    for (const key of [current, tree])
      expect(client.getQueryState(key)?.isInvalidated).toBe(true);
    for (const key of [historical, snapshot])
      expect(client.getQueryState(key)?.isInvalidated).toBe(false);
    client.clear();
  });

  it.each([
    { queryKey: current },
    { queryKey: tree },
    { queryKey: currentTrends },
  ])(
    "cancels a stale first read before fetching the committed projection: $queryKey",
    async ({ queryKey }) => {
      const client = new QueryClient({
        defaultOptions: { queries: { retry: false, staleTime: Infinity } },
      });
      let finishOld!: (value: string) => void;
      let oldSignal!: AbortSignal;
      let reads = 0;
      const observer = new QueryObserver(client, {
        queryKey,
        queryFn: ({ signal }) => {
          if (++reads === 1) {
            oldSignal = signal;
            return new Promise<string>((resolve) => {
              finishOld = resolve;
            });
          }
          return Promise.resolve("committed");
        },
      });
      const unsubscribe = observer.subscribe(() => {});
      await repairDomainChange(client, "taxonomy", () => true);
      expect(oldSignal.aborted).toBe(true);
      expect(reads).toBe(2);
      expect(client.getQueryData(queryKey)).toBe("committed");
      finishOld("stale");
      await Promise.resolve();
      expect(client.getQueryData(queryKey)).toBe("committed");
      unsubscribe();
      client.clear();
    },
  );

  it("assignment changes refresh both report views and receipt/search projections", async () => {
    const client = new QueryClient();
    const keys = [
      current,
      historical,
      ["receipts"],
      ["receipt-items"],
      ["reports", "purchase-search"],
      currentTrends,
      historicalTrends,
    ];
    for (const key of keys) client.setQueryData(key, "old");
    await repairDomainChange(client, "receipt-item", () => true);
    for (const key of keys)
      expect(client.getQueryState(key)?.isInvalidated).toBe(true);
    client.clear();
  });
});
