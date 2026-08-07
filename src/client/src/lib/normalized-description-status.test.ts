import { isActive, isPendingReview } from "./normalized-description-status";

describe("normalized-description-status", () => {
  // The spec documents these lowercase; the API sends PascalCase (RECEIPTS-884). Both have to
  // work, or the predicates flip meaning the day RECEIPTS-884 lands.
  describe("isPendingReview", () => {
    it.each(["pendingReview", "PendingReview", "PENDINGREVIEW"])(
      "recognises %s",
      (value) => {
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
        expect(isPendingReview(value as any)).toBe(true);
      },
    );

    it.each(["active", "Active"])("rejects %s", (value) => {
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      expect(isPendingReview(value as any)).toBe(false);
    });

    // The report's "(Not Normalized)" bucket has no backing row. Treating absence as pending
    // would send a reviewer hunting for a queue entry that does not exist.
    it.each([null, undefined])("treats %s as not pending", (value) => {
      expect(isPendingReview(value)).toBe(false);
    });
  });

  describe("isActive", () => {
    it.each(["active", "Active"])("recognises %s", (value) => {
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      expect(isActive(value as any)).toBe(true);
    });

    it("rejects pendingReview", () => {
      expect(isActive("pendingReview")).toBe(false);
    });

    it.each([null, undefined])("treats %s as not active", (value) => {
      expect(isActive(value)).toBe(false);
    });
  });
});
