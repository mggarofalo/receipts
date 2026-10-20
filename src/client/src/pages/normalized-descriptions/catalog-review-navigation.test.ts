import {
  pendingReviewHref,
  readCatalogSource,
} from "./catalog-review-navigation";
it("round trips exact source scope and target despite reserved URL characters", () => {
  const source = {
    location: "A & B",
    description: "Milk + / ?",
    itemCode: "001",
  };
  const url = new URL(pendingReviewHref("target-b", source), "http://test");
  expect(url.searchParams.get("tab")).toBe("review");
  expect(url.searchParams.get("reviewTarget")).toBe("target-b");
  expect(readCatalogSource(url.searchParams.get("catalogSource"))).toEqual(
    source,
  );
});
it.each([
  null,
  "bad json",
  "null",
  "[]",
  '{"location":3,"description":"x"}',
  '{"location":"a","description":"b","itemCode":4}',
])("rejects invalid source context %s", (value) =>
  expect(readCatalogSource(value)).toBeNull(),
);
