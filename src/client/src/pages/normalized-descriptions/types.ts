import type { components } from "@/generated/api";
export type NormalizedDescription =
  components["schemas"]["NormalizedDescriptionResponse"];
export type ItemTemplate = components["schemas"]["ItemTemplateResponse"];
export type ReceiptItem = Pick<
  components["schemas"]["ReceiptItemResponse"],
  "id" | "description"
>;
