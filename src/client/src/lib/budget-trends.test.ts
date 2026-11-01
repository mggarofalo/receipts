import {
  budgetSeriesLabel,
  budgetTrendGroups,
  budgetTrendsExport,
  type BudgetTrendsResult,
} from "./budget-trends";

const GROCERIES = "11111111-1111-1111-1111-111111111111";
const HOLIDAY = "22222222-2222-2222-2222-222222222222";

function fixture(): BudgetTrendsResult {
  return {
    groupBy: "envelope",
    series: [
      {
        id: GROCERIES,
        name: "Groceries",
        isActive: true,
        currency: "USD",
        isOther: false,
        total: 150,
        purchaseCount: 3,
      },
      {
        id: HOLIDAY,
        name: "Holiday",
        isActive: false,
        currency: "EUR",
        isOther: false,
        total: 40,
        purchaseCount: 1,
      },
      {
        id: null,
        name: "No envelope",
        isActive: true,
        currency: "USD",
        isOther: false,
        total: 25.5,
        purchaseCount: 2,
      },
      {
        id: null,
        name: "Other",
        isActive: true,
        currency: "USD",
        isOther: true,
        total: 10,
        purchaseCount: 1,
      },
    ],
    buckets: [
      { period: "2025-01", amounts: [100, 40, 25.5, 0] },
      { period: "2025-02", amounts: [50, 0, 0, 10] },
    ],
  };
}

describe("budgetSeriesLabel", () => {
  it("marks archived envelopes and leaves active ones alone", () => {
    const [groceries, holiday] = fixture().series;
    expect(budgetSeriesLabel(groceries)).toBe("Groceries");
    expect(budgetSeriesLabel(holiday)).toBe("Holiday (archived)");
  });
});

describe("budgetTrendGroups", () => {
  it("returns nothing without data", () => {
    expect(budgetTrendGroups(undefined)).toEqual([]);
    expect(
      budgetTrendGroups({ groupBy: "envelope", series: [], buckets: [] }),
    ).toEqual([]);
  });

  it("splits series by currency, in first-seen order, with parallel amounts and totals", () => {
    const groups = budgetTrendGroups(fixture());
    expect(groups.map((group) => group.currency)).toEqual(["USD", "EUR"]);

    const [usd, eur] = groups;
    expect(usd.categories).toEqual(["Groceries", "No envelope", "Other"]);
    expect(usd.buckets).toEqual([
      { period: "2025-01", amounts: [100, 25.5, 0] },
      { period: "2025-02", amounts: [50, 0, 10] },
    ]);
    expect(usd.total).toBe(185.5);
    expect(usd.series.map((series) => series.name)).toEqual([
      "Groceries",
      "No envelope",
      "Other",
    ]);

    expect(eur.categories).toEqual(["Holiday (archived)"]);
    expect(eur.buckets).toEqual([
      { period: "2025-01", amounts: [40] },
      { period: "2025-02", amounts: [0] },
    ]);
    expect(eur.total).toBe(40);
  });

  it("keys each chart by its series so a changed series set remounts it", () => {
    const [usd, eur] = budgetTrendGroups(fixture());
    expect(usd.key).toBe(`USD:${GROCERIES},none,other`);
    expect(eur.key).toBe(`EUR:${HOLIDAY}`);
  });

  it("treats a missing amount as zero", () => {
    const data = fixture();
    data.buckets = [{ period: "2025-03", amounts: [7] }];
    expect(budgetTrendGroups(data)[0].buckets).toEqual([
      { period: "2025-03", amounts: [7, 0, 0] },
    ]);
  });
});

describe("budgetTrendsExport", () => {
  it("writes one long-format row per period and series with the selection", () => {
    const { headers, rows } = budgetTrendsExport(fixture(), {
      startDate: "2025-01-01",
      endDate: "2025-02-28",
      granularity: "monthly",
      topN: 7,
      occasionId: HOLIDAY,
    });
    expect(headers).toHaveLength(15);
    expect(rows).toHaveLength(8);
    expect(rows[0]).toEqual([
      "envelope",
      "2025-01-01",
      "2025-02-28",
      "monthly",
      7,
      "",
      HOLIDAY,
      "2025-01",
      "USD",
      GROCERIES,
      "Groceries",
      false,
      false,
      3,
      100,
    ]);
    const archivedRow = rows[1];
    expect(archivedRow[headers.indexOf("Series")]).toBe("Holiday");
    expect(archivedRow[headers.indexOf("Archived")]).toBe(true);
    const otherRow = rows[7];
    expect(otherRow[headers.indexOf("Period")]).toBe("2025-02");
    expect(otherRow[headers.indexOf("Series ID")]).toBe("");
    expect(otherRow[headers.indexOf("Other")]).toBe(true);
    expect(otherRow[headers.indexOf("Amount")]).toBe(10);
  });

  it("leaves unset dates and filters blank", () => {
    const { headers, rows } = budgetTrendsExport(fixture(), {
      granularity: "yearly",
      topN: 3,
    });
    for (const column of [
      "Start date",
      "End date",
      "Envelope filter",
      "Occasion filter",
    ]) {
      expect(rows[0][headers.indexOf(column)]).toBe("");
    }
  });
});
