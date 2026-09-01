import { expect, test, type Page } from "@playwright/test";
import { installApiMocks, signInAsFixtureUser } from "../fixtures/api-mocks";

const RECEIPTS = [
  {
    id: "10000000-0000-0000-0000-000000000001",
    location: "Wegmans Food Markets",
    date: "2026-08-30",
    taxAmount: 4.27,
    itemSubtotal: 82.14,
    adjustmentTotal: -2,
    expectedTotal: 84.41,
    transactionTotal: 84.41,
    balanceState: "balanced",
    itemCount: 12,
    categorySummary: "Groceries, Household",
    paymentSummary: "Checking · Visa 4242",
  },
  {
    id: "10000000-0000-0000-0000-000000000002",
    location: "Target",
    date: "2026-08-29",
    taxAmount: 1.32,
    itemSubtotal: 26.48,
    adjustmentTotal: 0,
    expectedTotal: 27.8,
    transactionTotal: 0,
    balanceState: "noTransactions",
    itemCount: 3,
    categorySummary: "Household",
    paymentSummary: "",
  },
] as const;

type CellGeometry = {
  index: number;
  left: number;
  right: number;
  top: number;
  bottom: number;
  width: number;
};
type TableGeometry = {
  card: { left: number; right: number; width: number };
  table: { left: number; right: number; width: number };
  header: CellGeometry[];
  rows: CellGeometry[][];
};

async function gotoReceipts(
  page: Page,
  viewportWidth: number,
  cardWidth?: number,
) {
  await page.setViewportSize({ width: viewportWidth, height: 900 });
  await installApiMocks(page, {
    freezeClock: false,
    overrides: {
      "**/api/receipts?*": (route) =>
        route.fulfill({
          status: 200,
          contentType: "application/json",
          body: JSON.stringify({ data: RECEIPTS, total: RECEIPTS.length }),
        }),
      "**/api/trips?*": (route) =>
        route.fulfill({
          status: 200,
          contentType: "application/json",
          body: JSON.stringify({
            receipt: {
              ...RECEIPTS[0],
              subtotal: RECEIPTS[0].itemSubtotal,
              items: [],
            },
            transactions: [],
          }),
        }),
    },
  });
  await signInAsFixtureUser(page);
  await page.goto("/receipts");
  if (cardWidth) {
    // Isolate container-query branches independently of the surrounding sidebar.
    await page.addStyleTag({
      content: `.receipts-table-card { width: ${cardWidth}px !important; max-width: ${cardWidth}px !important; }`,
    });
  }
  await expect(
    page.getByRole("row", { name: /Wegmans Food Markets/ }),
  ).toBeVisible();
}

async function geometry(page: Page): Promise<TableGeometry> {
  return page.locator("table.receipts-table").evaluate((table) => {
    const cellRect = (cell: HTMLTableCellElement): CellGeometry => {
      const box = cell.getBoundingClientRect();
      return {
        index: cell.cellIndex,
        left: box.left,
        right: box.right,
        top: box.top,
        bottom: box.bottom,
        width: box.width,
      };
    };
    const visibleCells = (row: HTMLTableRowElement) =>
      Array.from(row.cells)
        .map(cellRect)
        .filter((cell) => cell.width > 0.5);
    const tableBox = table.getBoundingClientRect();
    const cardBox = table
      .closest(".receipts-table-card")!
      .getBoundingClientRect();
    const normalRows = Array.from(table.tBodies[0]?.rows ?? []).filter(
      (row) => !row.classList.contains("receipt-detail-row"),
    );
    return {
      card: { left: cardBox.left, right: cardBox.right, width: cardBox.width },
      table: {
        left: tableBox.left,
        right: tableBox.right,
        width: tableBox.width,
      },
      header:
        getComputedStyle(table.tHead!).display === "none"
          ? []
          : visibleCells(table.tHead!.rows[0]),
      rows: normalRows.map(visibleCells),
    };
  });
}

function expectTableFillsCard(value: TableGeometry, label: string) {
  // The card's one-pixel border may put its content edge one pixel inside its
  // outer bounding box; no responsive branch may leave a larger empty gutter.
  expect(
    Math.abs(value.table.left - value.card.left),
    `${label}: table left edge does not fill card`,
  ).toBeLessThanOrEqual(1.5);
  expect(
    Math.abs(value.table.right - value.card.right),
    `${label}: table right edge does not fill card`,
  ).toBeLessThanOrEqual(1.5);
  expect(
    Math.abs(value.table.width - value.card.width),
    `${label}: table width does not fill card`,
  ).toBeLessThanOrEqual(2);
}

function expectCellsDoNotOverlap(cells: CellGeometry[], label: string) {
  for (let first = 0; first < cells.length; first += 1) {
    for (let second = first + 1; second < cells.length; second += 1) {
      const a = cells[first];
      const b = cells[second];
      const shareLine = a.top < b.bottom - 0.5 && b.top < a.bottom - 0.5;
      if (shareLine) {
        expect(
          Math.min(a.right, b.right) - Math.max(a.left, b.left),
          `${label} cells ${a.index} and ${b.index} overlap`,
        ).toBeLessThanOrEqual(0.5);
      }
    }
  }
}

function expectStable(actual: TableGeometry, expected: TableGeometry) {
  const close = (value: number, expectedValue: number, label: string) =>
    expect(value, label).toBeCloseTo(expectedValue, 1);
  close(actual.table.left, expected.table.left, "table left edge moved");
  close(actual.table.right, expected.table.right, "table right edge moved");
  close(actual.table.width, expected.table.width, "table width changed");
  expect(actual.rows).toHaveLength(expected.rows.length);

  for (const [rowLabel, cells, expectedCells] of [
    ["header", actual.header, expected.header],
    ...actual.rows.map(
      (row, index) =>
        [`normal row ${index + 1}`, row, expected.rows[index]] as const,
    ),
  ] as const) {
    expect(
      cells.map((cell) => cell.index),
      `${rowLabel} visible columns`,
    ).toEqual(expectedCells.map((cell) => cell.index));
    for (const [index, cell] of cells.entries()) {
      const prior = expectedCells[index];
      close(cell.left, prior.left, `${rowLabel} cell ${cell.index} left moved`);
      close(
        cell.right,
        prior.right,
        `${rowLabel} cell ${cell.index} right moved`,
      );
      close(
        cell.width,
        prior.width,
        `${rowLabel} cell ${cell.index} width changed`,
      );
    }
    expectCellsDoNotOverlap(cells, rowLabel);
    // Mobile rows are CSS grids with intentional inset padding. In table mode,
    // the last visible column must consume the table through its right edge.
    if (cells.length > 0 && actual.header.length > 0)
      close(
        cells.at(-1)!.right,
        actual.table.right,
        `${rowLabel} last visible cell does not meet table right edge`,
      );
  }
}

const responsiveCases = [
  {
    name: "wide container with all columns",
    viewportWidth: 1920,
    cardWidth: 1400,
    visibleHeaders: 8,
  },
  {
    name: "container at 1200px collapses Contents",
    viewportWidth: 1600,
    cardWidth: 1100,
    visibleHeaders: 7,
  },
  {
    name: "container at 900px collapses all secondary columns",
    viewportWidth: 1400,
    cardWidth: 800,
    visibleHeaders: 6,
  },
  {
    name: "mobile CSS-grid rows",
    viewportWidth: 600,
    cardWidth: 560,
    visibleHeaders: 0,
  },
] as const;

for (const responsiveCase of responsiveCases) {
  test(`expansion preserves every visible column boundary: ${responsiveCase.name}`, async ({
    page,
  }) => {
    await gotoReceipts(
      page,
      responsiveCase.viewportWidth,
      responsiveCase.cardWidth,
    );
    const before = await geometry(page);
    expectTableFillsCard(before, "before expansion");
    expect(before.header).toHaveLength(responsiveCase.visibleHeaders);
    if (before.header.length > 0) {
      const corners = await page
        .locator("table.receipts-table")
        .evaluate((table) => {
          const header = table.tHead!.rows[0];
          const last = header.cells[header.cells.length - 1];
          return {
            left: getComputedStyle(header.cells[0]).borderTopLeftRadius,
            right: getComputedStyle(last).borderTopRightRadius,
            tableLeft: table.getBoundingClientRect().left,
            tableRight: table.getBoundingClientRect().right,
            firstLeft: header.cells[0].getBoundingClientRect().left,
            lastRight: last.getBoundingClientRect().right,
          };
        });
      expect(parseFloat(corners.left)).toBeGreaterThan(0);
      expect(parseFloat(corners.right)).toBeGreaterThan(0);
      expect(corners.firstLeft).toBeCloseTo(corners.tableLeft, 1);
      expect(corners.lastRight).toBeCloseTo(corners.tableRight, 1);
    }
    for (const [index, row] of before.rows.entries())
      expectCellsDoNotOverlap(row, `normal row ${index + 1} before expansion`);

    await page.getByRole("row", { name: /Wegmans Food Markets/ }).click();
    await expect(page.getByText("Receipt breakdown")).toBeVisible();
    const after = await geometry(page);
    expectTableFillsCard(after, "after expansion");
    expectStable(after, before);
  });
}

for (const mode of [
  { name: "desktop table", viewportWidth: 1600, cardWidth: 1100 },
  { name: "mobile grid", viewportWidth: 600, cardWidth: 560 },
] as const) {
  test(`the final receipt row has no dashed bottom divider in ${mode.name} mode`, async ({
    page,
  }) => {
    await gotoReceipts(page, mode.viewportWidth, mode.cardWidth);
    const borders = await page
      .locator("table.receipts-table")
      .evaluate((table) => {
        const rows = Array.from(table.tBodies[0].rows).filter(
          (row) => !row.classList.contains("receipt-detail-row"),
        );
        const last = rows.at(-1)!;
        return {
          row: getComputedStyle(last).borderBottomWidth,
          cells: Array.from(last.cells).map(
            (cell) => getComputedStyle(cell).borderBottomWidth,
          ),
        };
      });

    expect(borders.row).toBe("0px");
    expect(borders.cells).toEqual(borders.cells.map(() => "0px"));
  });
}

test("the boundary regression detects the old mixed column-and-cell hiding", async ({
  page,
}) => {
  await gotoReceipts(page, 1400, 800);
  await page.addStyleTag({
    content: `.receipts-table .receipt-col-secondary { display: none !important; }`,
  });
  const before = await geometry(page);
  await page.getByRole("row", { name: /Wegmans Food Markets/ }).click();
  await expect(page.getByText("Receipt breakdown")).toBeVisible();
  const after = await geometry(page);

  expect(
    after.rows.some((row, rowIndex) =>
      row.some((cell, cellIndex) => {
        const prior = before.rows[rowIndex]?.[cellIndex];
        return prior && Math.abs(cell.width - prior.width) > 0.5;
      }),
    ),
    "positive control should reproduce a changed data-cell boundary",
  ).toBe(true);
});
