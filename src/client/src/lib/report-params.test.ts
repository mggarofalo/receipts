import { describe, it, expect } from "vitest";
import { format, subMonths } from "date-fns";
import {
  getDefaultRange,
  parseCategoryViewParam,
  parseCategoryReportParams,
  parseDateRangeParam,
  serializeDateRangeParam,
  parseEnumParam,
  parseNumberEnumParam,
  parsePositiveIntParam,
  parseBoolParam,
  parseSelectedItemParam,
  serializeSelectedItemParam,
  parseNormalizedDescriptionParam,
  NOT_NORMALIZED_LABEL,
  NO_LOCATION_LABEL,
  buildLocationDrillDownHref,
  buildItemCostDrillDownHref,
} from "./report-params";

function paramsFrom(query: string): URLSearchParams {
  return new URLSearchParams(query);
}

it("defaults absent or malformed category views to as recorded", () => {
  expect(parseCategoryViewParam(paramsFrom(""))).toBe("asClassified");
  expect(parseCategoryViewParam(paramsFrom("categoryView=unknown"))).toBe(
    "asClassified",
  );
  expect(
    parseCategoryViewParam(paramsFrom("categoryView=currentHierarchy")),
  ).toBe("currentHierarchy");
});

describe("getDefaultRange", () => {
  it("returns a 12-month trailing range through today", () => {
    const range = getDefaultRange();
    expect(range.startDate).toBe(
      format(subMonths(new Date(), 12), "yyyy-MM-dd"),
    );
    expect(range.endDate).toBe(format(new Date(), "yyyy-MM-dd"));
  });
});

describe("parseDateRangeParam", () => {
  it("returns the default range when no params are present", () => {
    const range = parseDateRangeParam(paramsFrom(""));
    expect(range).toEqual(getDefaultRange());
  });

  it("reads a valid explicit range", () => {
    const range = parseDateRangeParam(
      paramsFrom("startDate=2023-01-01&endDate=2023-06-30"),
    );
    expect(range).toEqual({ startDate: "2023-01-01", endDate: "2023-06-30" });
  });

  it("treats the 'all' sentinel as an open-ended range", () => {
    const range = parseDateRangeParam(paramsFrom("startDate=all"));
    expect(range).toEqual({ startDate: undefined, endDate: undefined });
  });

  it("falls back to the default for garbage date strings", () => {
    const range = parseDateRangeParam(
      paramsFrom("startDate=not-a-date&endDate=also-not-a-date"),
    );
    expect(range).toEqual(getDefaultRange());
  });

  it("falls back to the default for a reversed range", () => {
    const range = parseDateRangeParam(
      paramsFrom("startDate=2023-06-30&endDate=2023-01-01"),
    );
    expect(range).toEqual(getDefaultRange());
  });

  it("falls back to the default when only one side is present", () => {
    const range = parseDateRangeParam(paramsFrom("startDate=2023-01-01"));
    expect(range).toEqual(getDefaultRange());
  });

  it("falls back to the default for an impossible calendar date", () => {
    const range = parseDateRangeParam(
      paramsFrom("startDate=2023-02-30&endDate=2023-06-30"),
    );
    expect(range).toEqual(getDefaultRange());
  });

  it("supports custom param keys", () => {
    const range = parseDateRangeParam(
      paramsFrom("from=2023-01-01&to=2023-06-30"),
      { startDate: "from", endDate: "to" },
    );
    expect(range).toEqual({ startDate: "2023-01-01", endDate: "2023-06-30" });
  });
});

describe("serializeDateRangeParam", () => {
  it("writes the 'all' sentinel for an open-ended range", () => {
    expect(
      serializeDateRangeParam({ startDate: undefined, endDate: undefined }),
    ).toEqual({ startDate: "all", endDate: undefined });
  });

  it("writes both dates for a complete range", () => {
    expect(
      serializeDateRangeParam({
        startDate: "2023-01-01",
        endDate: "2023-06-30",
      }),
    ).toEqual({ startDate: "2023-01-01", endDate: "2023-06-30" });
  });

  it("falls back to the default range when only one side is set", () => {
    expect(
      serializeDateRangeParam({ startDate: "2023-01-01", endDate: undefined }),
    ).toEqual(getDefaultRange());
  });

  it("round-trips through parseDateRangeParam", () => {
    const range = { startDate: "2023-01-01", endDate: "2023-06-30" };
    const serialized = serializeDateRangeParam(range);
    const params = new URLSearchParams();
    for (const [key, value] of Object.entries(serialized)) {
      if (value !== undefined) params.set(key, value);
    }
    expect(parseDateRangeParam(params)).toEqual(range);
  });

  it("round-trips the 'all' sentinel through parseDateRangeParam", () => {
    const serialized = serializeDateRangeParam({
      startDate: undefined,
      endDate: undefined,
    });
    const params = new URLSearchParams();
    for (const [key, value] of Object.entries(serialized)) {
      if (value !== undefined) params.set(key, value);
    }
    expect(parseDateRangeParam(params)).toEqual({
      startDate: undefined,
      endDate: undefined,
    });
  });
});

describe("parseEnumParam", () => {
  const allowed = ["asc", "desc"] as const;

  it("returns the fallback when the param is absent", () => {
    expect(parseEnumParam(null, allowed, "asc")).toBe("asc");
  });

  it("returns the value when it is in the allow-list", () => {
    expect(parseEnumParam("desc", allowed, "asc")).toBe("desc");
  });

  it("returns the fallback for an unknown value", () => {
    expect(parseEnumParam("sideways", allowed, "asc")).toBe("asc");
  });
});

describe("parseNumberEnumParam", () => {
  const allowed = [0, 0.01, 0.05, 0.1, 0.5, 1] as const;

  it("returns the fallback when the param is absent", () => {
    expect(parseNumberEnumParam(null, allowed, 0)).toBe(0);
  });

  it("returns the fallback for an empty string", () => {
    expect(parseNumberEnumParam("", allowed, 0)).toBe(0);
  });

  it("returns the value when it is in the allow-list", () => {
    expect(parseNumberEnumParam("0.5", allowed, 0)).toBe(0.5);
  });

  it("returns the fallback for a value outside the allow-list", () => {
    expect(parseNumberEnumParam("2.5", allowed, 0)).toBe(0);
  });

  it("returns the fallback for a non-numeric value", () => {
    expect(parseNumberEnumParam("garbage", allowed, 0)).toBe(0);
  });
});

describe("parsePositiveIntParam", () => {
  it("returns the fallback when the param is absent", () => {
    expect(parsePositiveIntParam(null, 1)).toBe(1);
  });

  it("returns the fallback for an empty string", () => {
    expect(parsePositiveIntParam("", 1)).toBe(1);
  });

  it("parses a valid positive integer", () => {
    expect(parsePositiveIntParam("3", 1)).toBe(3);
  });

  it("returns the fallback for zero", () => {
    expect(parsePositiveIntParam("0", 1)).toBe(1);
  });

  it("returns the fallback for a negative number", () => {
    expect(parsePositiveIntParam("-5", 1)).toBe(1);
  });

  it("returns the fallback for a non-integer", () => {
    expect(parsePositiveIntParam("2.5", 1)).toBe(1);
  });

  it("returns the fallback for a non-numeric string", () => {
    expect(parsePositiveIntParam("abc", 1)).toBe(1);
  });

  it("returns the fallback for a value above the max bound", () => {
    expect(parsePositiveIntParam("999999", 1, 100_000)).toBe(1);
  });

  it("accepts a value at the max bound", () => {
    expect(parsePositiveIntParam("100000", 1, 100_000)).toBe(100_000);
  });
});

describe("parseBoolParam", () => {
  it("returns true for the literal string 'true'", () => {
    expect(parseBoolParam("true", false)).toBe(true);
  });

  it("returns false for the literal string 'false'", () => {
    expect(parseBoolParam("false", true)).toBe(false);
  });

  it("returns the fallback when the param is absent", () => {
    expect(parseBoolParam(null, true)).toBe(true);
    expect(parseBoolParam(null, false)).toBe(false);
  });

  it("returns the fallback for a garbage value", () => {
    expect(parseBoolParam("yes", false)).toBe(false);
    expect(parseBoolParam("1", true)).toBe(true);
  });
});

describe("parseSelectedItemParam", () => {
  it("returns no selection when item is absent", () => {
    expect(parseSelectedItemParam(paramsFrom(""))).toBeNull();
  });

  it("returns no selection for an empty item", () => {
    expect(parseSelectedItemParam(paramsFrom("item="))).toBeNull();
  });

  it("selects the description from an item-only link", () => {
    expect(parseSelectedItemParam(paramsFrom("item=Milk"))).toBe("Milk");
  });

  it("ignores a legacy category carried beside the item", () => {
    expect(
      parseSelectedItemParam(paramsFrom("item=Milk&category=Dairy")),
    ).toBe("Milk");
  });

  it("does not treat a category alone as a selection", () => {
    expect(parseSelectedItemParam(paramsFrom("category=Dairy"))).toBeNull();
  });

  it("opens an old category-only link with nothing selected", () => {
    // In the retired category-text mode `item` named a legacy category, not
    // an item description, so it must not be read as one.
    expect(
      parseSelectedItemParam(
        paramsFrom("item=Dairy&category=Dairy&categoryOnly=true"),
      ),
    ).toBeNull();
    expect(parseSelectedItemParam(paramsFrom("categoryOnly=true"))).toBeNull();
  });

  it("keeps the item when categoryOnly is explicitly false", () => {
    expect(
      parseSelectedItemParam(paramsFrom("item=Milk&categoryOnly=false")),
    ).toBe("Milk");
  });
});

describe("serializeSelectedItemParam", () => {
  it("clears all keys when there is no selection", () => {
    expect(serializeSelectedItemParam(null)).toEqual({
      item: undefined,
      category: undefined,
      categoryOnly: undefined,
    });
  });

  it("writes item and clears the legacy category and categoryOnly keys", () => {
    expect(serializeSelectedItemParam("Milk")).toEqual({
      item: "Milk",
      category: undefined,
      categoryOnly: undefined,
    });
  });
});

describe("parseNormalizedDescriptionParam", () => {
  it("returns the trimmed value when present", () => {
    expect(
      parseNormalizedDescriptionParam(paramsFrom("normalized=Organic+Milk")),
    ).toBe("Organic Milk");
  });

  it("returns null when the param is missing", () => {
    expect(parseNormalizedDescriptionParam(paramsFrom(""))).toBeNull();
  });

  it("returns null when the param is blank", () => {
    expect(
      parseNormalizedDescriptionParam(paramsFrom("normalized=")),
    ).toBeNull();
  });

  it("returns null when the param is whitespace-only", () => {
    expect(
      parseNormalizedDescriptionParam(
        new URLSearchParams({ normalized: "   " }),
      ),
    ).toBeNull();
  });

  it("trims surrounding whitespace from an otherwise valid value", () => {
    expect(
      parseNormalizedDescriptionParam(
        new URLSearchParams({ normalized: "  Milk  " }),
      ),
    ).toBe("Milk");
  });
});

describe("buildLocationDrillDownHref", () => {
  it("builds a receipts URL with the location URL-encoded", () => {
    expect(buildLocationDrillDownHref("Walmart")).toBe(
      "/receipts?location=Walmart",
    );
  });

  it("URL-encodes spaces", () => {
    expect(buildLocationDrillDownHref("Costco Wholesale")).toBe(
      "/receipts?location=Costco%20Wholesale",
    );
  });

  it("URL-encodes &, ?, and # in the location", () => {
    const location = "Ben & Jerry's? #1";
    expect(buildLocationDrillDownHref(location)).toBe(
      `/receipts?location=${encodeURIComponent(location)}`,
    );
  });

  it("returns null for the synthetic (No Location) bucket", () => {
    expect(buildLocationDrillDownHref(NO_LOCATION_LABEL)).toBeNull();
  });

  it("returns null for an empty location", () => {
    expect(buildLocationDrillDownHref("")).toBeNull();
  });
});

describe("buildItemCostDrillDownHref", () => {
  const range = { startDate: "2023-01-01", endDate: "2023-06-30" };

  it("builds the item-cost-over-time URL with the report slug, canonical name, and date range", () => {
    expect(buildItemCostDrillDownHref("Organic Milk", range)).toBe(
      "/reports?report=item-cost-over-time&normalized=Organic+Milk&startDate=2023-01-01&endDate=2023-06-30",
    );
  });

  it("URL-encodes special characters in the canonical name", () => {
    const canonicalName = "Ben & Jerry's #1";
    const href = buildItemCostDrillDownHref(canonicalName, range);
    expect(href).not.toBeNull();
    const params = new URLSearchParams(href!.split("?")[1]);
    expect(params.get("report")).toBe("item-cost-over-time");
    expect(params.get("normalized")).toBe(canonicalName);
    expect(params.get("startDate")).toBe("2023-01-01");
    expect(params.get("endDate")).toBe("2023-06-30");
  });

  it("writes the 'all' sentinel for an open-ended range", () => {
    expect(
      buildItemCostDrillDownHref("Organic Milk", {
        startDate: undefined,
        endDate: undefined,
      }),
    ).toBe(
      "/reports?report=item-cost-over-time&normalized=Organic+Milk&startDate=all",
    );
  });

  it("returns null for the synthetic (Not Normalized) bucket", () => {
    expect(buildItemCostDrillDownHref(NOT_NORMALIZED_LABEL, range)).toBeNull();
  });

  it("returns null for an empty canonical name", () => {
    expect(buildItemCostDrillDownHref("", range)).toBeNull();
  });
});

it("parses category identity, cutoff and reused revision only for current hierarchy", () => {
  const query = new URLSearchParams(
    "categoryView=currentHierarchy&rollup=root&categoryId=11111111-1111-1111-1111-111111111111&knowledgeCutoff=2026-01-01T00:00:00Z&capturedCurrentRevisionId=22222222-2222-2222-2222-222222222222",
  );
  expect(parseCategoryReportParams(query)).toEqual({
    categoryView: "currentHierarchy",
    rollup: "root",
    categoryId: "11111111-1111-1111-1111-111111111111",
    knowledgeCutoff: "2026-01-01T00:00:00.000Z",
    capturedCurrentRevisionId: "22222222-2222-2222-2222-222222222222",
  });
  query.set("categoryView", "garbage");
  query.set("categoryId", "garbage");
  query.set("knowledgeCutoff", "garbage");
  query.set("rollup", "garbage");
  expect(parseCategoryReportParams(query)).toEqual({
    categoryView: "asClassified",
    rollup: "assignedCategory",
    categoryId: undefined,
    knowledgeCutoff: undefined,
    capturedCurrentRevisionId: undefined,
  });
});
