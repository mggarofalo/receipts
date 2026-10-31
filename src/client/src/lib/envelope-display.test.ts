import { describe, it, expect } from "vitest";
import { previewItemEnvelope, receiptEnvelope } from "./envelope-display";

const food = { id: "food", name: "Food", archived: false };
const produce = {
  id: "produce",
  name: "Produce",
  parentId: "food",
  archived: false,
};
const path = [food, produce];

const envelopes = [
  { id: "env-groceries", isActive: true },
  { id: "env-produce", isActive: true },
  { id: "env-gifts", isActive: true },
  { id: "env-old", isActive: false },
];

describe("previewItemEnvelope", () => {
  const defaults = [
    { categoryId: "food", envelopeId: "env-groceries" },
    { categoryId: "produce", envelopeId: "env-produce" },
  ];

  it("prefers the item's own override over everything else", () => {
    expect(
      previewItemEnvelope(
        { envelopeId: "env-gifts", typePath: path },
        "env-groceries",
        defaults,
        envelopes,
      ),
    ).toBe("env-gifts");
  });

  it("uses the receipt's paid-from envelope when the item has no override", () => {
    expect(
      previewItemEnvelope({ typePath: path }, "env-gifts", defaults, envelopes),
    ).toBe("env-gifts");
  });

  it("uses the paid-from envelope even before an item type is chosen", () => {
    expect(previewItemEnvelope({}, "env-gifts", defaults, envelopes)).toBe(
      "env-gifts",
    );
  });

  it("is unknown when neither choice is made and no item type is chosen", () => {
    expect(previewItemEnvelope({}, null, defaults, envelopes)).toBeUndefined();
    expect(
      previewItemEnvelope({ typePath: [] }, null, defaults, envelopes),
    ).toBeUndefined();
  });

  it("uses the nearest mapped item type on the path", () => {
    expect(
      previewItemEnvelope({ typePath: path }, null, defaults, envelopes),
    ).toBe("env-produce");
  });

  it("falls back to an ancestor's default when the leaf has none", () => {
    expect(
      previewItemEnvelope(
        { typePath: path },
        null,
        [{ categoryId: "food", envelopeId: "env-groceries" }],
        envelopes,
      ),
    ).toBe("env-groceries");
  });

  it("skips a default whose envelope is archived", () => {
    expect(
      previewItemEnvelope(
        { typePath: path },
        null,
        [
          { categoryId: "food", envelopeId: "env-groceries" },
          { categoryId: "produce", envelopeId: "env-old" },
        ],
        envelopes,
      ),
    ).toBe("env-groceries");
  });

  it("is null when a typed item has no usable default", () => {
    expect(previewItemEnvelope({ typePath: path }, null, [], envelopes)).toBe(
      null,
    );
    expect(
      previewItemEnvelope(
        { typePath: path },
        null,
        [{ categoryId: "produce", envelopeId: "env-old" }],
        envelopes,
      ),
    ).toBe(null);
  });

  it("treats a null override as automatic", () => {
    expect(
      previewItemEnvelope(
        { envelopeId: null, typePath: path },
        null,
        defaults,
        envelopes,
      ),
    ).toBe("env-produce");
  });
});

describe("receiptEnvelope", () => {
  it("returns the explicit paid-from envelope regardless of items", () => {
    expect(
      receiptEnvelope("env-gifts", ["env-groceries", "env-groceries"]),
    ).toBe("env-gifts");
  });

  it("returns the envelope most items share", () => {
    expect(
      receiptEnvelope(null, ["env-gifts", "env-groceries", "env-groceries"]),
    ).toBe("env-groceries");
  });

  it("lets the first seen envelope win a tie", () => {
    expect(receiptEnvelope(null, ["env-gifts", "env-groceries"])).toBe(
      "env-gifts",
    );
    expect(
      receiptEnvelope(null, [
        "env-gifts",
        "env-groceries",
        "env-groceries",
        "env-gifts",
      ]),
    ).toBe("env-gifts");
  });

  it("ignores items with no or unknown envelopes", () => {
    expect(receiptEnvelope(null, [null, undefined, "env-gifts", null])).toBe(
      "env-gifts",
    );
  });

  it("is null when no item has an envelope", () => {
    expect(receiptEnvelope(null, [])).toBeNull();
    expect(receiptEnvelope(null, [null, undefined])).toBeNull();
  });
});
