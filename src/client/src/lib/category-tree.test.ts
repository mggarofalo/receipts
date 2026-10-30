import { describe, expect, it } from "vitest";
import type { CategoryNode } from "@/lib/category-paths";
import {
  indexCategories,
  monogram,
  rootHue,
  rootHueStyle,
  searchCategories,
  searchTokens,
} from "./category-tree";

const nodes: CategoryNode[] = [
  { id: "home", name: "Home", archived: false },
  {
    id: "food",
    name: "Food",
    definition: "Things you eat",
    archived: false,
  },
  { id: "fruit", parentId: "food", name: "Fruit", archived: false },
  { id: "apple", parentId: "fruit", name: "Apple", archived: false },
  { id: "banana", parentId: "fruit", name: "Banana", archived: false },
  {
    id: "dairy",
    parentId: "food",
    name: "Dairy",
    definition: "Milk, cheese and yogurt",
    archived: false,
  },
  {
    id: "home-apple",
    parentId: "home",
    name: "Apple",
    definition: "Devices and accessories",
    archived: false,
  },
  { id: "cafe", parentId: "food", name: "Café", archived: false },
  { id: "old", name: "Old", archived: true },
  { id: "old-child", parentId: "old", name: "Inside old", archived: false },
  { id: "orphan", parentId: "missing", name: "Orphan", archived: false },
];

const index = indexCategories(nodes);
const labels = (query: string, limit?: number) =>
  searchCategories(index, query, limit).hits.map((hit) => hit.row.label);

describe("indexCategories", () => {
  it("keeps only selectable paths and looks them up by ID", () => {
    expect(index.rows.map((row) => row.node.id)).not.toEqual(
      expect.arrayContaining(["old"]),
    );
    expect(index.byId.has("old-child")).toBe(false);
    expect(index.byId.has("orphan")).toBe(false);
    expect(index.byId.get("apple")?.label).toBe("Food / Fruit / Apple");
  });

  it("lists children by parent in name order", () => {
    const names = (parentId: string | null) =>
      index.childrenOf(parentId).map((row) => row.node.name);
    expect(names(null)).toEqual(["Food", "Home"]);
    expect(names("food")).toEqual(["Café", "Dairy", "Fruit"]);
    expect(names("fruit")).toEqual(["Apple", "Banana"]);
    expect(names("apple")).toEqual([]);
    expect(names("unknown")).toEqual([]);
  });
});

describe("searchTokens", () => {
  it("splits on spaces and path separators and folds case and accents", () => {
    expect(searchTokens("  Food / Fruit › APPLE, Café ")).toEqual([
      "food",
      "fruit",
      "apple",
      "cafe",
    ]);
    expect(searchTokens("   ")).toEqual([]);
  });
});

describe("searchCategories", () => {
  it("returns nothing for an empty query", () => {
    expect(searchCategories(index, "")).toEqual({ hits: [], total: 0 });
  });

  it("requires every word to match the name or an ancestor", () => {
    expect(labels("home apple")).toEqual(["Home / Apple"]);
    expect(labels("food apple")).toEqual(["Food / Fruit / Apple"]);
    expect(labels("apple")).toEqual(
      expect.arrayContaining(["Food / Fruit / Apple", "Home / Apple"]),
    );
  });

  it("ranks an exact name above a descendant matched through its ancestor", () => {
    expect(labels("fruit")).toEqual([
      "Food / Fruit",
      "Food / Fruit / Apple",
      "Food / Fruit / Banana",
    ]);
  });

  it("highlights where the name matched", () => {
    const [hit] = searchCategories(index, "nan").hits;
    expect(hit.row.label).toBe("Food / Fruit / Banana");
    expect(hit.nameRanges).toEqual([[2, 5]]);
    expect(hit.definition).toBeUndefined();
  });

  it("matches definition text and reports the definition ranges", () => {
    const { hits } = searchCategories(index, "cheese");
    expect(hits.map((hit) => hit.row.label)).toEqual(["Food / Dairy"]);
    expect(hits[0].definition).toEqual({
      text: "Milk, cheese and yogurt",
      ranges: [[6, 12]],
    });
  });

  it("forgives a single typo in a longer word", () => {
    expect(labels("appel")).toEqual(
      expect.arrayContaining(["Food / Fruit / Apple", "Home / Apple"]),
    );
    expect(labels("bnaana")).toEqual(["Food / Fruit / Banana"]);
    expect(labels("dairyy")).toEqual(["Food / Dairy"]);
  });

  it("accepts letters in order from the start of a word", () => {
    expect(labels("frt")).toContain("Food / Fruit");
    expect(labels("zzz")).toEqual([]);
  });

  it("folds accents when matching", () => {
    expect(labels("cafe")).toEqual(["Food / Café"]);
  });

  it("searches a 2,000-node tree well within a keystroke", () => {
    const large: CategoryNode[] = [];
    for (let root = 0; root < 40; root++) {
      large.push({ id: `r${root}`, name: `Root ${root}`, archived: false });
      for (let leaf = 0; leaf < 49; leaf++)
        large.push({
          id: `r${root}-${leaf}`,
          parentId: `r${root}`,
          name: `Item ${leaf}`,
          definition: "Includes things people buy every week",
          archived: false,
        });
    }
    const big = indexCategories(large);
    // Generous budget: this guards against accidental quadratic work, not
    // against a loaded CI machine.
    const started = performance.now();
    for (const query of ["item 4", "root 3 item", "weekly", "itme"])
      searchCategories(big, query);
    expect(performance.now() - started).toBeLessThan(500);
  });

  it("limits the hits but reports the full total", () => {
    const many: CategoryNode[] = Array.from({ length: 3000 }, (_, i) => ({
      id: `n${i}`,
      name: `Item ${i}`,
      archived: false,
    }));
    const big = indexCategories(many);
    const started = performance.now();
    const result = searchCategories(big, "item");
    expect(performance.now() - started).toBeLessThan(500);
    expect(result.total).toBe(3000);
    expect(result.hits).toHaveLength(50);
    expect(searchCategories(big, "item", 5).hits).toHaveLength(5);
  });
});

describe("root styling helpers", () => {
  it("gives each root a stable hue", () => {
    expect(rootHue("food")).toBe(rootHue("food"));
    expect(rootHue("food")).toBeGreaterThanOrEqual(0);
    expect(rootHue("food")).toBeLessThan(360);
    expect(rootHueStyle(undefined)).toBeUndefined();
    expect(rootHueStyle(nodes[1])).toEqual({ "--cat-h": rootHue("food") });
  });

  it("uses the first letter or digit for the monogram", () => {
    expect(monogram("food")).toBe("F");
    expect(monogram("  (2nd) hand")).toBe("2");
    expect(monogram("—")).toBe("•");
  });
});
