import {
  currentNormalizationChoice,
  inlineProductSuggestions,
  normalizationSourceKey,
  type ReceiptNormalizationChoice,
} from "./receipt-normalization";

const source = {
  location: "Market A",
  description: "BF FRANKS",
  itemCode: "0123",
};
const choice: ReceiptNormalizationChoice = {
  sourceKey: normalizationSourceKey(source),
  label: "Beef franks",
  intent: {
    kind: "confirmExisting",
    rememberSource: false,
    clientRowId: "11111111-1111-4111-8111-111111111111",
    sourceFingerprint: "server-observed-fingerprint",
    targetId: "22222222-2222-4222-8222-222222222222",
    expectedTargetRevision: "33333333-3333-4333-8333-333333333333",
  },
};

describe("source-bound receipt choices", () => {
  it("retains an explicit choice for the unchanged source", () => {
    expect(currentNormalizationChoice(choice, { ...source })).toEqual(choice);
  });

  it.each([
    { ...source, location: "Market B" },
    { ...source, description: "TURKEY FRANKS" },
    { ...source, itemCode: "0124" },
    { ...source, itemCode: null },
  ])("discards an explicit choice after its source changes: %o", (changed) => {
    expect(currentNormalizationChoice(choice, changed)).toBeUndefined();
  });

  it("matches the server's outer ASCII-space normalization without broadening identity", () => {
    expect(
      normalizationSourceKey({
        location: " Market A ",
        description: " BF FRANKS ",
        itemCode: " 0123 ",
      }),
    ).toBe(normalizationSourceKey(source));
    expect(
      normalizationSourceKey({ ...source, description: "bf franks" }),
    ).not.toBe(normalizationSourceKey(source));
    expect(
      normalizationSourceKey({ ...source, description: "\tBF FRANKS" }),
    ).not.toBe(normalizationSourceKey(source));
    expect(
      normalizationSourceKey({ ...source, description: "\u00a0BF FRANKS" }),
    ).not.toBe(normalizationSourceKey(source));
  });

  it("treats absent and space-only codes equally without treating absent code as a wildcard", () => {
    expect(normalizationSourceKey({ ...source, itemCode: null })).toBe(
      normalizationSourceKey({ ...source, itemCode: "   " }),
    );
    expect(normalizationSourceKey({ ...source, itemCode: null })).not.toBe(
      normalizationSourceKey(source),
    );
  });
});

describe("inline product suggestions", () => {
  const row = (name: string, channels: string[]) => ({ name, channels });

  it("keeps the server order and at most two semantic-only rows within five", () => {
    const actual = inlineProductSuggestions([
      row("exact", ["exact"]),
      row("lexical", ["lexical", "semantic"]),
      row("near 1", ["semantic"]),
      row("near 2", ["semantic"]),
      row("near 3", ["semantic"]),
      row("approved", ["approved_example"]),
      row("lexical 2", ["lexical"]),
      row("lexical 3", ["lexical"]),
    ]).map((item) => item.name);

    expect(actual).toEqual(["exact", "lexical", "near 1", "near 2", "approved"]);
  });

  it("returns an empty list unchanged", () => {
    expect(inlineProductSuggestions([])).toEqual([]);
  });
});
