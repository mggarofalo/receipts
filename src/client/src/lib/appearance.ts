/**
 * Appearance preferences (RECEIPTS-592 / Phase 18).
 *
 * Two orthogonal settings drive `data-*` attributes on `<html>`:
 *   palette  → data-palette  (graphite | paper)
 *   density  → data-density  (compact | comfortable | spacious)
 *
 * Paper intensity ("soft") and motion ("subtle") are no longer
 * user-configurable — they are the design's only correct values and
 * the toggles for them were retired.
 *
 * The anti-FOUC script in index.html reads the same localStorage keys
 * before first paint; keep the two in sync.
 */

export type Palette = "graphite" | "paper";
export type Density = "compact" | "comfortable" | "spacious";

export interface Appearance {
  palette: Palette;
  density: Density;
}

export const PALETTES: readonly Palette[] = ["graphite", "paper"];
export const DENSITIES: readonly Density[] = [
  "compact",
  "comfortable",
  "spacious",
];

export const DEFAULT_APPEARANCE: Appearance = {
  palette: "graphite",
  density: "comfortable",
};

type AppearanceKey = keyof Appearance;

const STORAGE_KEYS: Record<AppearanceKey, string> = {
  palette: "appearance.palette",
  density: "appearance.density",
};

const DATA_ATTRIBUTES: Record<AppearanceKey, string> = {
  palette: "data-palette",
  density: "data-density",
};

const ALLOWED: Record<AppearanceKey, readonly string[]> = {
  palette: PALETTES,
  density: DENSITIES,
};

/** Read a persisted appearance setting, falling back to the default. */
function readSetting<K extends AppearanceKey>(key: K): Appearance[K] {
  try {
    const stored = localStorage.getItem(STORAGE_KEYS[key]);
    if (stored && ALLOWED[key].includes(stored)) {
      return stored as Appearance[K];
    }
  } catch {
    // localStorage unavailable (private mode, SSR) — use the default.
  }
  return DEFAULT_APPEARANCE[key];
}

/** Read the full persisted appearance, falling back to defaults per key. */
export function readAppearance(): Appearance {
  return {
    palette: readSetting("palette"),
    density: readSetting("density"),
  };
}

/** Persist a single setting and apply it to `<html>`. */
export function applySetting<K extends AppearanceKey>(
  key: K,
  value: Appearance[K],
): void {
  document.documentElement.setAttribute(DATA_ATTRIBUTES[key], value);
  try {
    localStorage.setItem(STORAGE_KEYS[key], value);
  } catch {
    // localStorage unavailable — the attribute is still applied for this session.
  }
}
