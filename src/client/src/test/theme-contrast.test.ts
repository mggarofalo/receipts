/// <reference types="node" />
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";
import { describe, it, expect } from "vitest";

// Regression guard for RECEIPTS-567: `text-muted-foreground` must meet WCAG AA
// (4.5:1 for normal text) against every surface it can plausibly render small
// text on, in both palettes. Updated for the Phase 18 design system
// (RECEIPTS-592): palettes are hex-based and selected via `data-palette`.
//
// Reads index.css from disk (rather than `?raw` import) because the vite
// tailwindcss plugin transforms CSS imports through the vitest pipeline,
// yielding an empty string for `?raw` queries.

const CSS_PATH = resolve(
  dirname(fileURLToPath(import.meta.url)),
  "../index.css",
);
const CSS = readFileSync(CSS_PATH, "utf8");

const WCAG_AA_NORMAL = 4.5;

// Surfaces a small-text caller using `text-muted-foreground` may sit on.
// `--muted` (a structural elevated background) is intentionally excluded:
// content on it uses `--ink-2`, not `--mute`.
const BACKGROUND_TOKENS = [
  "background",
  "card",
  "popover",
  "secondary",
  "accent-surface",
  "sidebar-accent",
] as const;

/** Collect every `--name: value;` declaration inside a selector's block. */
function parseBlock(css: string, selector: string): Map<string, string> {
  const start = css.indexOf(`${selector} {`);
  if (start === -1) throw new Error(`Missing "${selector}" block in index.css`);
  const end = css.indexOf("}", start);
  const body = css.slice(start, end);
  const decls = new Map<string, string>();
  const re = /--([\w-]+):\s*([^;]+);/g;
  let match: RegExpExecArray | null;
  while ((match = re.exec(body)) !== null) {
    decls.set(match[1], match[2].trim());
  }
  return decls;
}

/** Resolve a token through any `var(--x)` indirection to a literal value. */
function resolveToken(decls: Map<string, string>, name: string): string {
  const seen = new Set<string>();
  let value = decls.get(name);
  while (value !== undefined) {
    const varMatch = value.match(/^var\(--([\w-]+)\)$/);
    if (!varMatch) return value;
    const next = varMatch[1];
    if (seen.has(next)) throw new Error(`Cyclic var reference at --${next}`);
    seen.add(next);
    value = decls.get(next);
  }
  throw new Error(`Token --${name} could not be resolved`);
}

function hexToRgb(hex: string): [number, number, number] {
  const m = hex.trim().match(/^#([0-9a-f]{6})$/i);
  if (!m) throw new Error(`Expected a 6-digit hex color, got "${hex}"`);
  const n = Number.parseInt(m[1], 16);
  return [(n >> 16) & 0xff, (n >> 8) & 0xff, n & 0xff];
}

/** WCAG relative luminance for an sRGB hex color. */
function luminance(hex: string): number {
  const channel = (c: number) => {
    const s = c / 255;
    return s <= 0.04045 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4;
  };
  const [r, g, b] = hexToRgb(hex);
  return 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b);
}

function contrast(a: string, b: string): number {
  const la = luminance(a);
  const lb = luminance(b);
  const [lighter, darker] = la > lb ? [la, lb] : [lb, la];
  return (lighter + 0.05) / (darker + 0.05);
}

// The Graphite palette block also carries the shadcn semantic mappings; the
// Paper block only overrides raw palette tokens, so it inherits the mappings.
const graphite = parseBlock(CSS, 'html[data-palette="graphite"]');
const paper = new Map(graphite);
for (const [k, v] of parseBlock(CSS, 'html[data-palette="paper"]')) {
  paper.set(k, v);
}

describe("muted-foreground contrast meets WCAG AA", () => {
  for (const [name, decls] of [
    ["graphite", graphite],
    ["paper", paper],
  ] as const) {
    describe(name, () => {
      const fg = resolveToken(decls, "muted-foreground");

      for (const bgName of BACKGROUND_TOKENS) {
        it(`on --${bgName}`, () => {
          const bg = resolveToken(decls, bgName);
          const ratio = contrast(fg, bg);
          expect(
            ratio,
            `muted-foreground on --${bgName} in ${name}: ${ratio.toFixed(2)}:1`,
          ).toBeGreaterThanOrEqual(WCAG_AA_NORMAL);
        });
      }
    });
  }
});
