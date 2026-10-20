import type { CatalogSource } from "@/hooks/useNormalizationCatalog";

export function pendingReviewHref(id: string, source?: CatalogSource) {
  const params = new URLSearchParams({ tab: "review", reviewTarget: id });
  if (source) params.set("catalogSource", JSON.stringify(source));
  return `/admin/normalized-descriptions?${params}`;
}

export function readCatalogSource(value: string | null): CatalogSource | null {
  if (!value) return null;
  try {
    const source: unknown = JSON.parse(value);
    if (!source || typeof source !== "object") return null;
    const row = source as Record<string, unknown>;
    if (
      typeof row.location !== "string" ||
      typeof row.description !== "string" ||
      (row.itemCode != null && typeof row.itemCode !== "string")
    )
      return null;
    return {
      location: row.location,
      description: row.description,
      itemCode: row.itemCode as string | null | undefined,
    };
  } catch {
    return null;
  }
}
