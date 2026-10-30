import type { CategoryRevision } from "../../src/lib/category-paths";

/**
 * A realistic item-type taxonomy (the RECEIPTS-1084 shape: ~15 roots with a
 * handful of leaves each, some with definitions) for picker screenshots.
 */
const tree: Record<string, [string, string?][]> = {
  Groceries: [
    [
      "Produce",
      "Includes fresh fruit, vegetables and herbs. Not frozen or canned.",
    ],
    ["Dairy & eggs", "Includes milk, cheese, yogurt, butter and eggs."],
    ["Meat & seafood"],
    ["Bakery", "Includes bread, tortillas and pastries baked in store."],
    ["Pantry", "Includes rice, pasta, canned goods, oils, spices and flour."],
    ["Frozen"],
    ["Snacks", "Includes chips, crackers, nuts and candy."],
    ["Beverages", "Includes coffee, tea, juice and soda. Not alcohol."],
  ],
  Household: [
    ["Cleaning", "Includes detergent, dish soap, sponges and wipes."],
    ["Paper goods", "Includes paper towels, toilet paper and tissues."],
    ["Kitchenware"],
    ["Storage & organization"],
  ],
  "Personal care": [
    ["Hair care"],
    ["Oral care", "Includes toothpaste, floss and toothbrushes."],
    ["Skin care"],
    ["Cosmetics"],
  ],
  Health: [
    ["Medicine", "Includes over-the-counter and prescription medicine."],
    ["Vitamins"],
    ["First aid"],
  ],
  Baby: [["Diapers & wipes"], ["Formula & baby food"], ["Clothing"]],
  Pets: [["Pet food"], ["Litter"], ["Toys & supplies"]],
  "Dining out": [["Restaurants"], ["Coffee shops"], ["Delivery"]],
  Alcohol: [["Beer"], ["Wine"], ["Spirits"]],
  Clothing: [["Shoes"], ["Outerwear"], ["Basics"]],
  Electronics: [
    ["Accessories", "Includes cables, chargers and cases."],
    ["Devices"],
  ],
  "Home improvement": [
    ["Tools"],
    ["Garden", "Includes soil, seeds, plants and planters."],
    ["Hardware"],
  ],
  Transportation: [["Fuel"], ["Parking & tolls"], ["Car care"]],
  Entertainment: [["Books"], ["Games"], ["Streaming"]],
  Gifts: [],
  Fees: [
    ["Bag fee", "Includes checkout bag charges."],
    ["Deposit", "Includes bottle and can deposits."],
  ],
};

const id = (n: number) =>
  `00000000-0000-4000-8000-${String(n).padStart(12, "0")}`;
let next = 1;
const nodes: CategoryRevision["nodes"] = [];
for (const [rootName, leaves] of Object.entries(tree)) {
  const rootId = id(next++);
  nodes.push({ id: rootId, name: rootName, archived: false });
  for (const [name, definition] of leaves)
    nodes.push({
      id: id(next++),
      parentId: rootId,
      name,
      definition,
      archived: false,
    });
}

export const itemTypeTaxonomy: CategoryRevision = {
  id: "00000000-0000-4000-9000-000000000001",
  sequence: 7,
  recordedAt: "2026-05-20T12:00:00Z",
  actor: "admin",
  reason: "Item-type restructure",
  operationId: "00000000-0000-4000-9000-000000000002",
  affectedCount: nodes.length,
  nodes,
};

export function nodeId(name: string) {
  return nodes.find((node) => node.name === name)!.id;
}
