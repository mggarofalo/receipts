const RECENT_KEY = "receipts:palette-recent";
const PINNED_KEY = "receipts:palette-pinned";
const MAX_RECENT = 5;

function readStringArray(key: string): string[] {
  try {
    const raw = localStorage.getItem(key);
    if (!raw) return [];
    const parsed: unknown = JSON.parse(raw);
    return Array.isArray(parsed)
      ? parsed.filter((x): x is string => typeof x === "string")
      : [];
  } catch {
    return [];
  }
}

function writeStringArray(key: string, value: string[]): void {
  try {
    localStorage.setItem(key, JSON.stringify(value));
  } catch {
    // Ignore storage errors (quota, disabled, etc.)
  }
}

export function getRecent(): string[] {
  return readStringArray(RECENT_KEY);
}

/**
 * Prepend `id` to the recent list and return the new array. Exact-match dedupe
 * (command IDs are opaque identifiers like "create:account" — no case-folding).
 * Cap at 5 entries. Returning the new list lets callers sync React state in a
 * single read, mirroring `togglePinned`.
 */
export function addRecent(id: string): string[] {
  if (!id) return getRecent();
  const recent = getRecent().filter((r) => r !== id);
  recent.unshift(id);
  if (recent.length > MAX_RECENT) recent.length = MAX_RECENT;
  writeStringArray(RECENT_KEY, recent);
  return recent;
}

export function getPinned(): string[] {
  return readStringArray(PINNED_KEY);
}

/**
 * Toggle `id` in the pinned list and return the new array.
 * Returned value lets callers update React state without a second read.
 */
export function togglePinned(id: string): string[] {
  if (!id) return getPinned();
  const pinned = getPinned();
  const next = pinned.includes(id)
    ? pinned.filter((p) => p !== id)
    : [...pinned, id];
  writeStringArray(PINNED_KEY, next);
  return next;
}
