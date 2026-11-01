import { getAutoGranularity } from "./trend-granularity";

describe("getAutoGranularity", () => {
  it("uses yearly when either end of the range is open", () => {
    expect(getAutoGranularity({})).toBe("yearly");
    expect(getAutoGranularity({ startDate: "2025-01-01" })).toBe("yearly");
    expect(getAutoGranularity({ endDate: "2025-01-01" })).toBe("yearly");
  });

  it.each([
    ["2025-01-01", "2025-04-04", "daily"], // 93 days
    ["2025-01-01", "2025-04-05", "monthly"], // 94 days
    ["2023-01-01", "2024-12-31", "monthly"], // 730 days
    ["2023-01-01", "2025-01-01", "yearly"], // 731 days
  ] as const)("%s to %s is %s", (startDate, endDate, expected) => {
    expect(getAutoGranularity({ startDate, endDate })).toBe(expected);
  });
});
