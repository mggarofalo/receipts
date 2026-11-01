import { z } from "zod/v4";

/** Interaction validation shared by wizard add/edit and persisted-item forms. */
export const receiptItemDraftSchema = z.object({
  receiptItemCode: z.string().trim().optional().default(""),
  description: z.string().trim().min(1, "Description is required"),
  quantity: z.number().positive("Quantity must be positive"),
  unitPrice: z.number().min(0, "Unit price must be non-negative"),
});
