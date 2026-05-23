import {
  parseChanges,
  actionBadgeVariant,
  relativeTime,
  truncateId,
} from "./audit-utils";

describe("parseChanges", () => {
  it("parses valid JSON array of FieldChange objects", () => {
    const json = JSON.stringify([
      { field: "name", oldValue: "A", newValue: "B" },
      { field: "code", oldValue: null, newValue: "123" },
    ]);
    const result = parseChanges(json);
    expect(result).toHaveLength(2);
    expect(result[0]).toEqual({ field: "name", oldValue: "A", newValue: "B" });
  });

  it("parses PascalCase FieldChange objects from the backend serializer", () => {
    const json = JSON.stringify([
      { FieldName: "Location", OldValue: "Target", NewValue: "Costco" },
      { FieldName: "TaxAmount", OldValue: null, NewValue: "1.50" },
    ]);
    const result = parseChanges(json);
    expect(result).toEqual([
      { field: "Location", oldValue: "Target", newValue: "Costco" },
      { field: "TaxAmount", oldValue: null, newValue: "1.50" },
    ]);
  });

  it("filters out invalid entries", () => {
    const json = JSON.stringify([
      { field: "name", oldValue: "A", newValue: "B" },
      { notAField: true },
      "string-entry",
      null,
    ]);
    const result = parseChanges(json);
    expect(result).toHaveLength(1);
  });

  it("returns empty array for non-array JSON", () => {
    expect(parseChanges('{"field":"name"}')).toEqual([]);
  });

  it("returns empty array for invalid JSON", () => {
    expect(parseChanges("not json")).toEqual([]);
  });

  it("returns empty array for empty array", () => {
    expect(parseChanges("[]")).toEqual([]);
  });
});

describe("actionBadgeVariant", () => {
  it("returns default for Created", () => {
    expect(actionBadgeVariant("Created")).toBe("default");
  });

  it("returns secondary for Updated", () => {
    expect(actionBadgeVariant("Updated")).toBe("secondary");
  });

  it("returns destructive for Deleted", () => {
    expect(actionBadgeVariant("Deleted")).toBe("destructive");
  });

  it("returns outline for Restored", () => {
    expect(actionBadgeVariant("Restored")).toBe("outline");
  });

  it("returns secondary for unknown action", () => {
    expect(actionBadgeVariant("Unknown")).toBe("secondary");
  });
});

describe("relativeTime", () => {
  it("returns 'just now' for < 60 seconds", () => {
    const now = new Date().toISOString();
    expect(relativeTime(now)).toBe("just now");
  });

  it("returns minutes ago", () => {
    const fiveMinAgo = new Date(Date.now() - 5 * 60 * 1000).toISOString();
    expect(relativeTime(fiveMinAgo)).toBe("5m ago");
  });

  it("returns hours ago", () => {
    const threeHoursAgo = new Date(
      Date.now() - 3 * 60 * 60 * 1000,
    ).toISOString();
    expect(relativeTime(threeHoursAgo)).toBe("3h ago");
  });

  it("returns days ago", () => {
    const twoDaysAgo = new Date(
      Date.now() - 2 * 24 * 60 * 60 * 1000,
    ).toISOString();
    expect(relativeTime(twoDaysAgo)).toBe("2d ago");
  });

  it("falls back to formatted timestamp for > 30 days", () => {
    const sixtyDaysAgo = new Date(
      Date.now() - 60 * 24 * 60 * 60 * 1000,
    ).toISOString();
    const result = relativeTime(sixtyDaysAgo);
    // Should be a locale string, not a relative format
    expect(result).not.toContain("ago");
  });
});

describe("truncateId", () => {
  it("truncates long IDs", () => {
    expect(truncateId("abcdefghijklmnop")).toBe("abcdefgh...");
  });

  it("does not truncate short IDs", () => {
    expect(truncateId("abcd")).toBe("abcd");
  });

  it("does not truncate IDs at exactly the limit", () => {
    expect(truncateId("abcdefgh")).toBe("abcdefgh");
  });

  it("respects custom length", () => {
    expect(truncateId("abcdefghijklmnop", 4)).toBe("abcd...");
  });
});

