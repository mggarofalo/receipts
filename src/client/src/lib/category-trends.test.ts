import {
  categoryTrendGroups,
  categoryTrendsExport,
  type CategoryTrendsResult,
} from "./category-trends";
function fixture(): CategoryTrendsResult {
  return {
    categories: ["Same", "Same", "Other", "Other"],
    view: "currentHierarchy",
    rollup: "root",
    assignmentSelection: "recordedAtCutoff",
    currentRevisionId: "head",
    knowledgeCutoff: "2026-01-01T00:00:00Z",
    series: [
      {
        currency: "USD",
        state: "classified",
        isOther: false,
        purchaseCount: 1,
        pathRevisionId: "r1",
        categoryId: "a",
        path: [
          { categoryId: "root", name: "Food", archived: true },
          { categoryId: "a", name: "Same", archived: false },
        ],
      },
      {
        currency: "USD",
        state: "classified",
        isOther: false,
        purchaseCount: 1,
        pathRevisionId: "r2",
        categoryId: "b",
        path: [
          { categoryId: "root", name: "Food", archived: true },
          { categoryId: "b", name: "Same", archived: false },
        ],
      },
      {
        currency: "USD",
        state: "other",
        isOther: true,
        purchaseCount: 2,
        path: [],
      },
      {
        currency: "EUR",
        state: "other",
        isOther: true,
        purchaseCount: 1,
        path: [],
      },
    ],
    buckets: [{ period: "2026-01", amounts: [10.5, 20, 30, 40] }],
    currencyTotals: [
      { currency: "USD", amount: 60.5, purchaseCount: 4 },
      { currency: "EUR", amount: 40, purchaseCount: 1 },
    ],
  };
}
it("keeps archived full paths, distinct identities and per-currency Other in server order", () => {
  const groups = categoryTrendGroups(fixture());
  expect(groups).toHaveLength(2);
  expect(groups[0].categories).toEqual([
    "Food (archived) / Same [r1/a]",
    "Food (archived) / Same [r2/b]",
    "Other",
  ]);
  expect(groups[0].buckets[0].amounts).toEqual([10.5, 20, 30]);
  expect(groups[1].buckets[0].amounts).toEqual([40]);
  expect(groups[0].total).toBe(60.5);
});
it("freezes response and metadata before asynchronous export completion", async () => {
  const data = fixture();
  const exported = categoryTrendsExport(
    data,
    {
      categoryView: "currentHierarchy",
      categoryId: "filter",
      envelopeId: "envelope-1",
      occasionId: "occasion-1",
      granularity: "monthly",
      topN: 3,
    },
    "generated",
  );
  const pending = Promise.resolve().then(() => exported);
  data.view = "asClassified";
  data.buckets[0].amounts[0] = 999;
  data.series![0].path[0].name = "Changed";
  const resolved = await pending;
  const row = Object.fromEntries(
    resolved.headers.map((header, index) => [header, resolved.rows[0][index]]),
  );
  expect(
    resolved.headers.slice(
      resolved.headers.indexOf("Category filter"),
      resolved.headers.indexOf("Period"),
    ),
  ).toEqual(["Category filter", "Envelope filter", "Occasion filter"]);
  expect(row).toMatchObject({
    View: "currentHierarchy",
    "Generated at": "generated",
    Amount: 10.5,
    "Category filter": "filter",
    "Envelope filter": "envelope-1",
    "Occasion filter": "occasion-1",
    Path: "Food (archived) / Same",
    "Captured current revision": "head",
    "Path revision": "r1",
    "Currency total": 60.5,
  });
});
