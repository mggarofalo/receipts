const RECENT_CATEGORIES_KEY = "category-picker:recent";
const RECENT_LIMIT = 8;

/** Most recent first. A per-browser convenience; never an identity source. */
export function getRecentCategoryIds(): string[] {
  try {
    const parsed: unknown = JSON.parse(
      localStorage.getItem(RECENT_CATEGORIES_KEY) ?? "[]",
    );
    return Array.isArray(parsed)
      ? parsed.filter((id): id is string => typeof id === "string")
      : [];
  } catch {
    return [];
  }
}

export function rememberRecentCategory(categoryId: string): void {
  try {
    const next = [
      categoryId,
      ...getRecentCategoryIds().filter((id) => id !== categoryId),
    ].slice(0, RECENT_LIMIT);
    localStorage.setItem(RECENT_CATEGORIES_KEY, JSON.stringify(next));
  } catch {
    // Storage may be full or disabled; recents are optional.
  }
}
