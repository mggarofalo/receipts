import type { components } from "@/generated/api";

export type CategoryNode = components["schemas"]["TaxonomyNodeResponse"];
export type CategoryRevision =
  components["schemas"]["TaxonomyRevisionResponse"];

/** A deliberate choice pinned to the revision visible at selection time. */
export interface CategoryChoice {
  categoryId: string | null;
  taxonomyRevisionId: string;
  path: CategoryNode[];
}

export interface CategoryPath {
  node: CategoryNode;
  path: CategoryNode[];
  label: string;
}

/** Iterative traversal has no artificial category-depth limit. */
export function categoryPaths(nodes: CategoryNode[]): CategoryPath[] {
  const byId = new Map(nodes.map((node) => [node.id, node]));
  const rows: CategoryPath[] = [];
  for (const node of nodes) {
    const path: CategoryNode[] = [];
    const seen = new Set<string>();
    let cursor: CategoryNode | undefined = node;
    while (cursor && !seen.has(cursor.id)) {
      seen.add(cursor.id);
      path.push(cursor);
      if (!cursor.parentId) break;
      cursor = byId.get(cursor.parentId);
    }
    // Incomplete or cyclic paths cannot establish a selectable identity.
    if (!cursor || cursor.parentId || path.some((part) => part.archived))
      continue;
    path.reverse();
    rows.push({ node, path, label: path.map((part) => part.name).join(" / ") });
  }
  return rows.sort(
    (a, b) =>
      a.label.localeCompare(b.label) || a.node.id.localeCompare(b.node.id),
  );
}

/** Compatibility snapshots are transport labels, never identity resolution. */
export function categoryLegacyLabels(choice: CategoryChoice) {
  return {
    category: choice.path[0]?.name ?? "",
    subcategory: choice.path[1]?.name ?? null,
  };
}

export type CurrentClassification =
  components["schemas"]["CurrentClassificationResponse"];
export type ClassificationRequest =
  components["schemas"]["ClassificationRequest"];

/** An untouched recorded choice must not create another assignment. */
export function classificationChange(
  choice: CategoryChoice | undefined,
  current: CurrentClassification | null | undefined,
  operationId: string,
): ClassificationRequest | undefined {
  if (
    !choice ||
    (current &&
      choice.categoryId === (current.categoryId ?? null) &&
      choice.taxonomyRevisionId === current.taxonomyRevisionId)
  )
    return undefined;
  return {
    categoryId: choice.categoryId,
    taxonomyRevisionId: choice.taxonomyRevisionId,
    expectedAssignmentId: current?.assignmentId ?? null,
    operationId,
    reason: "Category selected during receipt entry",
  };
}

// One path index per taxonomy revision, shared by every caller.
const pathIndexes = new WeakMap<
  CategoryRevision["nodes"],
  Map<string, CategoryPath>
>();

/** A choice of a category in the given tree, or undefined when it is not selectable there. */
export function currentCategoryChoice(
  revision: CategoryRevision | undefined,
  categoryId: string | null | undefined,
): CategoryChoice | undefined {
  if (!revision || !categoryId) return undefined;
  let index = pathIndexes.get(revision.nodes);
  if (!index) {
    // categoryPaths leaves out archived categories and their descendants.
    index = new Map(
      categoryPaths(revision.nodes).map((row) => [row.node.id, row]),
    );
    pathIndexes.set(revision.nodes, index);
  }
  const row = index.get(categoryId);
  return row
    ? { categoryId, taxonomyRevisionId: revision.id, path: row.path }
    : undefined;
}
