import type {
  CategoryRevision,
  CurrentClassification,
} from "@/lib/category-paths";

export const taxonomyFixture: CategoryRevision = {
  id: "95000000-0000-4000-8000-000000000001",
  sequence: 1,
  recordedAt: "2026-10-23T00:00:00Z",
  actor: "fixture",
  reason: "Receipt entry test taxonomy",
  operationId: "95000000-0000-4000-8000-000000000002",
  affectedCount: 2,
  nodes: [
    {
      id: "95000000-0000-4000-8000-000000000010",
      name: "Food",
      parentId: null,
      archived: false,
    },
    {
      id: "95000000-0000-4000-8000-000000000011",
      name: "Dairy",
      parentId: "95000000-0000-4000-8000-000000000010",
      archived: false,
    },
  ],
};
export const dairyClassification: CurrentClassification = {
  assignmentId: "95000000-0000-4000-8000-000000000003",
  categoryId: taxonomyFixture.nodes[1].id,
  taxonomyRevisionId: taxonomyFixture.id,
  recordedPath: taxonomyFixture.nodes,
};
