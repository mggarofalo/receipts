import type { CSSProperties } from "react";
import {
  categoryPaths,
  type CategoryNode,
  type CategoryPath,
} from "@/lib/category-paths";

/** A half-open `[start, end)` span of characters to emphasise. */
export type MatchRange = readonly [number, number];

export interface CategoryHit {
  row: CategoryPath;
  score: number;
  nameRanges: MatchRange[];
  /** Present only when the definition itself explains why the row matched. */
  definition?: { text: string; ranges: MatchRange[] };
}

interface SearchEntry {
  row: CategoryPath;
  name: string;
  nameWords: string[];
  ancestors: string[];
  definition: string;
  /** Folding changed the string length, so ranges would not line up. */
  rangesUnsafe: boolean;
}

/** Selectable paths plus the parent→children shape needed to browse them. */
export interface CategoryIndex {
  rows: CategoryPath[];
  byId: ReadonlyMap<string, CategoryPath>;
  childrenOf: (parentId: string | null) => CategoryPath[];
  entries: SearchEntry[];
}

const NO_CHILDREN: CategoryPath[] = [];

function fold(text: string) {
  return text.normalize("NFD").replace(/\p{M}/gu, "").toLowerCase();
}

export function indexCategories(nodes: CategoryNode[]): CategoryIndex {
  const rows = categoryPaths(nodes);
  const byId = new Map(rows.map((row) => [row.node.id, row]));
  const children = new Map<string | null, CategoryPath[]>();
  // categoryPaths sorts by full label, so siblings arrive in name order.
  for (const row of rows) {
    const parentId = row.node.parentId ?? null;
    const siblings = children.get(parentId);
    if (siblings) siblings.push(row);
    else children.set(parentId, [row]);
  }
  const entries = rows.map((row) => {
    const name = fold(row.node.name);
    const definition = fold(row.node.definition ?? "");
    return {
      row,
      name,
      nameWords: name.split(/[^\p{L}\p{N}]+/u).filter(Boolean),
      ancestors: row.path.slice(0, -1).map((node) => fold(node.name)),
      definition,
      rangesUnsafe:
        name.length !== row.node.name.length ||
        definition.length !== (row.node.definition ?? "").length,
    };
  });
  return {
    rows,
    byId,
    childrenOf: (parentId) => children.get(parentId) ?? NO_CHILDREN,
    entries,
  };
}

/** Splits a query into the words every result must account for. */
export function searchTokens(query: string) {
  return fold(query)
    .split(/[\s/>›,]+/)
    .filter(Boolean);
}

function wordStart(text: string, token: string, from = 0) {
  let index = text.indexOf(token, from);
  while (index > 0 && /[\p{L}\p{N}]/u.test(text[index - 1])) {
    index = text.indexOf(token, index + 1);
  }
  return index;
}

/** Letters in order, starting at a word boundary: "frt" finds "fruit". */
function subsequence(text: string, token: string): MatchRange[] | undefined {
  for (
    let start = wordStart(text, token[0]);
    start >= 0;
    start = wordStart(text, token[0], start + 1)
  ) {
    const ranges: MatchRange[] = [[start, start + 1]];
    let cursor = start + 1;
    for (const char of token.slice(1)) {
      const found = text.indexOf(char, cursor);
      if (found < 0) break;
      ranges.push([found, found + 1]);
      cursor = found + 1;
    }
    if (ranges.length === token.length) return ranges;
  }
  return undefined;
}

/** One insertion, deletion, substitution or adjacent swap ("appel"). */
function withinOneEdit(a: string, b: string) {
  if (Math.abs(a.length - b.length) > 1) return false;
  let i = 0;
  while (i < a.length && i < b.length && a[i] === b[i]) i++;
  if (i === a.length && i === b.length) return true;
  return (
    a.slice(i + 1) === b.slice(i + 1) ||
    a.slice(i + 1) === b.slice(i) ||
    a.slice(i) === b.slice(i + 1) ||
    (a[i] === b[i + 1] &&
      a[i + 1] === b[i] &&
      a.slice(i + 2) === b.slice(i + 2))
  );
}

function mergeRanges(ranges: MatchRange[]) {
  const sorted = [...ranges].sort((a, b) => a[0] - b[0]);
  const merged: [number, number][] = [];
  for (const [start, end] of sorted) {
    const last = merged.at(-1);
    if (last && start <= last[1]) last[1] = Math.max(last[1], end);
    else merged.push([start, end]);
  }
  return merged;
}

function scoreEntry(entry: SearchEntry, tokens: string[]) {
  let score = 0;
  const nameRanges: MatchRange[] = [];
  const definitionRanges: MatchRange[] = [];
  let definitionOnly = false;
  for (const token of tokens) {
    const nameAt = wordStart(entry.name, token);
    if (nameAt >= 0) {
      score += nameAt === 0 ? 120 : 100;
      nameRanges.push([nameAt, nameAt + token.length]);
      continue;
    }
    const nameInside = entry.name.indexOf(token);
    if (nameInside >= 0) {
      score += 60;
      nameRanges.push([nameInside, nameInside + token.length]);
      continue;
    }
    if (entry.ancestors.some((name) => wordStart(name, token) >= 0)) {
      score += 45;
      continue;
    }
    if (entry.ancestors.some((name) => name.includes(token))) {
      score += 30;
      continue;
    }
    const definitionAt = wordStart(entry.definition, token);
    if (
      definitionAt >= 0 ||
      (token.length >= 3 && entry.definition.includes(token))
    ) {
      const at =
        definitionAt >= 0 ? definitionAt : entry.definition.indexOf(token);
      score += definitionAt >= 0 ? 25 : 12;
      definitionRanges.push([at, at + token.length]);
      definitionOnly = true;
      continue;
    }
    if (
      token.length >= 4 &&
      entry.nameWords.some((word) => withinOneEdit(word, token))
    ) {
      score += 20;
      continue;
    }
    const loose =
      token.length >= 3 ? subsequence(entry.name, token) : undefined;
    if (loose) {
      score += 8;
      nameRanges.push(...loose);
      continue;
    }
    return undefined;
  }
  if (entry.name === tokens.join(" ")) score += 50;
  // Among equals, prefer the broader, shallower path.
  score -= entry.row.path.length;
  const definitionText = entry.row.node.definition ?? "";
  return {
    row: entry.row,
    score,
    nameRanges: entry.rangesUnsafe ? [] : mergeRanges(nameRanges),
    definition: definitionOnly
      ? {
          text: definitionText,
          ranges: entry.rangesUnsafe ? [] : mergeRanges(definitionRanges),
        }
      : undefined,
  } satisfies CategoryHit;
}

/**
 * Forgiving search across names, ancestry and definitions. Every word of the
 * query must land somewhere in the path; closer, earlier matches rank higher.
 */
export function searchCategories(
  index: CategoryIndex,
  query: string,
  limit = 50,
) {
  const tokens = searchTokens(query);
  if (tokens.length === 0) return { hits: [] as CategoryHit[], total: 0 };
  const hits: CategoryHit[] = [];
  for (const entry of index.entries) {
    const hit = scoreEntry(entry, tokens);
    if (hit) hits.push(hit);
  }
  hits.sort(
    (a, b) => b.score - a.score || a.row.label.localeCompare(b.row.label),
  );
  return { hits: hits.slice(0, limit), total: hits.length };
}

/** A stable hue per root so a path's family reads at a glance. */
export function rootHue(rootId: string) {
  let hash = 2166136261;
  for (let i = 0; i < rootId.length; i++) {
    hash ^= rootId.charCodeAt(i);
    hash = Math.imul(hash, 16777619);
  }
  return (hash >>> 0) % 360;
}

/** Inline style that tints an element with its root's hue (see `.cat-hue`). */
export function rootHueStyle(root: CategoryNode | undefined) {
  return root ? ({ "--cat-h": rootHue(root.id) } as CSSProperties) : undefined;
}

/** The first letter or digit of a name, for the root monogram. */
export function monogram(name: string) {
  return name.match(/[\p{L}\p{N}]/u)?.[0].toLocaleUpperCase() ?? "•";
}
