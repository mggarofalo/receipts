import type { components } from "@/generated/api";

export type ReceiptNormalizationSource =
  components["schemas"]["NormalizationSourceKey"];
export type ReceiptNormalizationIntent =
  components["schemas"]["ReceiptNormalizationIntent"];
export type ReceiptNormalizationChoice = {
  sourceKey: string;
  intent: ReceiptNormalizationIntent;
  label: string;
};

// This key binds UI evidence to its input. The authoritative fingerprint is
// supplied by the server; this never manufactures verification evidence.
export function normalizationSourceKey(source: ReceiptNormalizationSource) {
  const trim = (text: string) => text.replace(/^ +| +$/g, "");
  return JSON.stringify([
    trim(source.location),
    trim(source.description),
    trim(source.itemCode ?? "") || null,
  ]);
}

export function currentNormalizationChoice(
  choice: ReceiptNormalizationChoice | undefined,
  source: ReceiptNormalizationSource,
) {
  return choice?.sourceKey === normalizationSourceKey(source)
    ? choice
    : undefined;
}

export function receiptNormalizationSource(
  location: string | null | undefined,
  item: { description: string; receiptItemCode?: string | null },
): ReceiptNormalizationSource {
  return {
    location: location ?? "",
    description: item.description,
    itemCode: item.receiptItemCode || null,
  };
}
