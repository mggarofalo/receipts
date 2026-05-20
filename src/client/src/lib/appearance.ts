/**
 * Appearance preferences (RECEIPTS-592 / Phase 18).
 *
 * Four orthogonal settings drive `data-*` attributes on `<html>`:
 *   palette  → data-palette  (graphite | paper)
 *   density  → data-density  (compact | comfortable | spacious)
 *   paper    → data-paper    (none | soft)   — ticket/serif intensity
 *   motion   → data-motion   (subtle | none)
 *
 * The anti-FOUC script in index.html reads the same localStorage keys
 * before first paint; keep the two in sync.
 */

export type Palette = "graphite" | "paper";
export type Density = "compact" | "comfortable" | "spacious";
export type PaperIntensity = "none" | "soft";
export type Motion = "subtle" | "none";

export interface Appearance {
  palette: Palette;
  density: Density;
  paper: PaperIntensity;
  motion: Motion;
}

export const PALETTES: readonly Palette[] = ["graphite", "paper"];
export const DENSITIES: readonly Density[] = [
  "compact",
  "comfortable",
  "spacious",
];
export const PAPER_INTENSITIES: readonly PaperIntensity[] = ["none", "soft"];
export const MOTIONS: readonly Motion[] = ["subtle", "none"];

export const DEFAULT_APPEARANCE: Appearance = {
  palette: "graphite",
  density: "comfortable",
  paper: "soft",
  motion: "subtle",
};

type AppearanceKey = keyof Appearance;

const STORAGE_KEYS: Record<AppearanceKey, string> = {
  palette: "appearance.palette",
  density: "appearance.density",
  paper: "appearance.paper",
  motion: "appearance.motion",
};

const DATA_ATTRIBUTES: Record<AppearanceKey, string> = {
  palette: "data-palette",
  density: "data-density",
  paper: "data-paper",
  motion: "data-motion",
};

const ALLOWED: Record<AppearanceKey, readonly string[]> = {
  palette: PALETTES,
  density: DENSITIES,
  paper: PAPER_INTENSITIES,
  motion: MOTIONS,
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
    paper: readSetting("paper"),
    motion: readSetting("motion"),
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
