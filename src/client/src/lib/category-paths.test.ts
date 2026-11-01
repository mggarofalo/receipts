import { describe, expect, it } from "vitest";
import {
  categoryLegacyLabels,
  classificationChange,
  categoryPaths,
  currentCategoryChoice,
  type CategoryNode,
  type CategoryRevision,
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

describe("currentCategoryChoice", () => {
  const revision = (
    id: string,
    nodes: CategoryNode[],
  ): CategoryRevision => ({
    id,
    sequence: 1,
    recordedAt: "2026-10-23T00:00:00Z",
    actor: "fixture",
    reason: "fixture",
    operationId: "op",
    affectedCount: 0,
    nodes,
  });
  const tree = (): CategoryNode[] => [
    { id: "food", name: "Food", parentId: null, archived: false },
    { id: "fruit", name: "Fruit", parentId: "food", archived: false },
    { id: "old", name: "Old", parentId: null, archived: true },
    { id: "old-child", name: "Old child", parentId: "old", archived: false },
  ];

  it("pins a live category to the given revision with its full path", () => {
    const current = revision("revision-2", tree());
    expect(currentCategoryChoice(current, "fruit")).toEqual({
      categoryId: "fruit",
      taxonomyRevisionId: "revision-2",
      path: [current.nodes[0], current.nodes[1]],
    });
  });

  it.each([
    ["an archived category", "old"],
    ["a category under an archived parent", "old-child"],
    ["an id the tree does not hold", "missing"],
    ["a null id", null],
    ["an undefined id", undefined],
  ])("returns undefined for %s", (_, categoryId) => {
    expect(
      currentCategoryChoice(revision("revision-1", tree()), categoryId),
    ).toBeUndefined();
  });

  it("returns undefined while no revision is loaded", () => {
    expect(currentCategoryChoice(undefined, "fruit")).toBeUndefined();
  });

  it("indexes each nodes array once and re-indexes a new one", () => {
    const nodes = tree();
    const first = revision("revision-1", nodes);
    expect(currentCategoryChoice(first, "fruit")).toBeDefined();
    // The index is keyed by the nodes array, so a node added to the same
    // array afterwards is not seen: revisions are immutable snapshots.
    nodes.push({ id: "late", name: "Late", parentId: null, archived: false });
    expect(currentCategoryChoice(first, "late")).toBeUndefined();
    expect(
      currentCategoryChoice(revision("revision-2", [...nodes]), "late"),
    ).toMatchObject({ categoryId: "late", taxonomyRevisionId: "revision-2" });
  });
});
