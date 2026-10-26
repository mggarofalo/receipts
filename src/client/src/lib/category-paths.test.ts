import { describe, expect, it } from "vitest";
import {
  categoryLegacyLabels,
  classificationChange,
  categoryPaths,
  type CategoryNode,
} from "./category-paths";

describe("categoryPaths", () => {
  it("retains parent choices and arbitrary-depth paths with repeated leaf names", () => {
    const nodes: CategoryNode[] = Array.from({ length: 120 }, (_, index) => ({
      id: String(index),
      parentId: index ? String(index - 1) : null,
      name: `Level ${index}`,
      archived: false,
    }));
    nodes.push({ id: "other", name: "Level 119", archived: false });
    const paths = categoryPaths(nodes);
    expect(paths).toHaveLength(121);
    expect(paths.find((row) => row.node.id === "119")?.path).toHaveLength(120);
    expect(paths.find((row) => row.node.id === "0")?.label).toBe("Level 0");
    expect(paths.find((row) => row.node.id === "other")?.path).toHaveLength(1);
  });

  it("excludes archived, orphaned and cyclic paths", () => {
    expect(
      categoryPaths([
        { id: "archive", name: "Archive", archived: true },
        { id: "child", name: "Child", parentId: "archive", archived: false },
        { id: "orphan", name: "Orphan", parentId: "missing", archived: false },
        { id: "cycle", name: "Cycle", parentId: "cycle", archived: false },
      ]),
    ).toEqual([]);
  });

  it("only derives the first two compatibility labels from a chosen identity", () => {
    const path = ["Food", "Produce", "Fruit"].map((name, index) => ({
      id: String(index),
      name,
      archived: false,
    }));
    expect(
      categoryLegacyLabels({
        categoryId: "2",
        taxonomyRevisionId: "revision",
        path,
      }),
    ).toEqual({ category: "Food", subcategory: "Produce" });
    expect(
      categoryLegacyLabels({
        categoryId: "0",
        taxonomyRevisionId: "revision",
        path: path.slice(0, 1),
      }),
    ).toEqual({ category: "Food", subcategory: null });
    expect(
      categoryLegacyLabels({
        categoryId: null,
        taxonomyRevisionId: "revision",
        path: [],
      }),
    ).toEqual({ category: "", subcategory: null });
  });
});

it("preserves an existing explicit Unclassified assignment when nullable categoryId is omitted", () => {
  expect(
    classificationChange(
      { categoryId: null, taxonomyRevisionId: "r1", path: [] },
      { assignmentId: "a1", taxonomyRevisionId: "r1", recordedPath: [] },
      "op1",
    ),
  ).toBeUndefined();
});
