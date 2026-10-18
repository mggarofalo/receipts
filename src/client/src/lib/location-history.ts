import { createFieldHistory } from "./field-history";

export const locationHistory = createFieldHistory(
  "receipts:location-history",
  50,
);
