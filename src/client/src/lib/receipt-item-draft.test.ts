import { describe, expect, it } from "vitest";
import { receiptItemDraftSchema } from "./receipt-item-draft";

const valid = {
  receiptItemCode: " X ",
  description: " Milk ",
  quantity: 1,
  unitPrice: 0,
  category: " Food ",
  subcategory: " Dairy ",
};

describe("receipt item draft", () => {
  it("trims text once and preserves free items and fractional quantities", () => {
    expect(receiptItemDraftSchema.parse({ ...valid, quantity: 0.5 })).toEqual({
      receiptItemCode: "X",
      description: "Milk",
      quantity: 0.5,
      unitPrice: 0,
      category: "Food",
      subcategory: "Dairy",
    });
  });
  it.each(["description"])("rejects whitespace-only %s", (field) => {
    expect(
      receiptItemDraftSchema.safeParse({ ...valid, [field]: " \t " }).success,
    ).toBe(false);
  });
  it.each([0, -1, NaN, Infinity, -Infinity])(
    "rejects quantity %s",
    (quantity) => {
      expect(
        receiptItemDraftSchema.safeParse({ ...valid, quantity }).success,
      ).toBe(false);
    },
  );
  it.each([-1, NaN, Infinity, -Infinity])("rejects price %s", (unitPrice) => {
    expect(
      receiptItemDraftSchema.safeParse({ ...valid, unitPrice }).success,
    ).toBe(false);
  });
  it("defaults optional wizard fields without weakening required text", () => {
    expect(
      receiptItemDraftSchema.parse({
        description: "Milk",
        category: "Food",
        quantity: 1,
        unitPrice: 0,
      }),
    ).toEqual({
      receiptItemCode: "",
      description: "Milk",
      category: "Food",
      subcategory: "",
      quantity: 1,
      unitPrice: 0,
    });
  });
});

it("accepts parent and Unclassified compatibility snapshots", () => {
  expect(
    receiptItemDraftSchema.parse({ ...valid, category: "", subcategory: "" }),
  ).toMatchObject({ category: "", subcategory: "" });
  expect(
    receiptItemDraftSchema.parse({ ...valid, subcategory: "" }),
  ).toMatchObject({ category: "Food", subcategory: "" });
});
