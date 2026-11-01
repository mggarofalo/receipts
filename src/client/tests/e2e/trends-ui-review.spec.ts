import { test, expect } from "@playwright/test";
import { installApiMocks, signInAs } from "../fixtures/api-mocks";
import type { components } from "../../src/generated/api";
import { readFile } from "node:fs/promises";
const output =
  "C:/Users/mggar/Source/receipts-017-artifacts/1050/trends-ui-review";
type Result = components["schemas"]["CategoryTrendsResponse"];
const id = "10000000-0000-0000-0000-000000000001";
const rev = "20000000-0000-0000-0000-000000000001";
const cutoff = "2026-05-01T00:00:00.000Z";
const historical: NonNullable<Result["series"]> = [
  ...[1, 2].map((n) => ({
    currency: "USD",
    state: "classified",
    isOther: false,
    purchaseCount: 2,
    categoryId: id,
    pathRevisionId: rev.slice(0, -1) + n,
    path: [
      {
        categoryId: "30000000-0000-0000-0000-000000000001",
        name: "Household essentials",
        archived: false,
      },
      {
        categoryId: id,
        name: "Organic groceries and pantry supplies",
        archived: true,
      },
    ],
  })),
  {
    currency: "USD",
    state: "unresolvedLegacy",
    isOther: false,
    purchaseCount: 1,
    categoryId: null,
    pathRevisionId: null,
    path: [],
  },
  {
    currency: "USD",
    state: "other",
    isOther: true,
    purchaseCount: 4,
    categoryId: null,
    pathRevisionId: null,
    path: [],
  },
  {
    currency: "EUR",
    state: "classified",
    isOther: false,
    purchaseCount: 1,
    categoryId: id,
    pathRevisionId: rev,
    path: [{ categoryId: id, name: "European groceries", archived: false }],
  },
  {
    currency: "EUR",
    state: "other",
    isOther: true,
    purchaseCount: 2,
    categoryId: null,
    pathRevisionId: null,
    path: [],
  },
];
for (const width of [1280, 375])
  test(`trends acceptance ${width}`, async ({ page }) => {
    await page.setViewportSize({ width, height: width === 375 ? 812 : 900 });
    const queries: string[] = [];
    const errors: string[] = [];
    let delayed = false;
    page.on("pageerror", (e) => errors.push(e.message));
    await installApiMocks(page, {
      freezeClock: false,
      overrides: {
        "**/api/dashboard/spending-by-category**": async (route) => {
          await route.fulfill({
            json: {
              view: "currentHierarchy",
              rollup: "assignedCategory",
              assignmentSelection: "currentPointer",
              currentRevisionId: rev,
              knowledgeCutoff: null,
              items: [],
            },
          });
        },
        "**/api/reports/category-trends**": async (route) => {
          const p = new URL(route.request().url()).searchParams;
          queries.push(p.toString());
          if (delayed) await new Promise((r) => setTimeout(r, 700));
          const current = p.get("categoryView") === "currentHierarchy";
          const series = current
            ? [
                {
                  ...historical[0],
                  path: [
                    {
                      categoryId: id,
                      name: "Current groceries",
                      archived: false,
                    },
                  ],
                },
              ]
            : historical;
          const result: Result = {
            view: current ? "currentHierarchy" : "asClassified",
            rollup: p.get("rollup") ?? "assignedCategory",
            knowledgeCutoff: p.get("knowledgeCutoff"),
            assignmentSelection: "latestAtCutoff",
            currentRevisionId: current ? rev : null,
            categories: series.map((s) => s.state),
            series,
            buckets: [
              {
                period: "2026-01",
                amounts: current ? [150] : [100, 20, 10, 30, 50, 5],
              },
              {
                period: "2026-02",
                amounts: current ? [160] : [110, 25, 15, 35, 60, 10],
              },
            ],
            currencyTotals: [
              { currency: "USD", amount: 345, purchaseCount: 9 },
              { currency: "EUR", amount: 125, purchaseCount: 3 },
            ],
          };
          await route.fulfill({ json: result });
        },
      },
    });
    await signInAs(page);
    const path = `/reports?report=category-trends&categoryView=currentHierarchy&rollup=root&categoryId=${id}&knowledgeCutoff=${cutoff}&capturedCurrentRevisionId=${rev}&startDate=2026-01-01&endDate=2026-02-28&granularity=monthly&topN=3&keep=yes&page=4&offset=90`;
    await page.goto(path);
    await expect(
      page.getByRole("region", { name: "USD category trends" }),
    ).toBeVisible();
    expect(
      queries.some(
        (q) =>
          q.includes("capturedCurrentRevisionId=") &&
          q.includes("categoryId=") &&
          q.includes("knowledgeCutoff=") &&
          q.includes("rollup=root") &&
          q.includes("granularity=monthly") &&
          q.includes("topN=3") &&
          q.includes("startDate=2026-01-01"),
      ),
    ).toBe(true);
    await expect(
      page.getByText(`Captured taxonomy revision: ${rev}`),
    ).toBeVisible();
    await page
      .getByRole("combobox", { name: "Category view", exact: true })
      .selectOption("asClassified");
    await expect(
      page.getByRole("region", { name: "EUR category trends" }),
    ).toBeVisible();
    expect(
      new URL(page.url()).searchParams.has("capturedCurrentRevisionId"),
    ).toBe(false);
    expect(new URL(page.url()).searchParams.has("page")).toBe(false);
    expect(new URL(page.url()).searchParams.has("offset")).toBe(false);
    expect(new URL(page.url()).searchParams.get("keep")).toBe("yes");
    expect(await page.getByRole("main").innerText()).not.toContain(
      String.fromCharCode(0xfffd),
    );
    await expect(
      page.getByRole("status").filter({ hasText: "Partial category history" }),
    ).toBeVisible();
    const usd = page.getByRole("region", { name: "USD category trends" });
    await expect(usd.locator("thead th")).toHaveCount(5);
    await expect(usd.locator("thead")).toContainText(
      "Organic groceries and pantry supplies (archived)",
    );
    await expect(usd.locator("thead")).toContainText(rev);
    await expect(usd.locator("tbody tr").first()).toHaveText(
      "2026-01$100.00$20.00$10.00$30.00",
    );
    await expect(
      page
        .getByRole("region", { name: "EUR category trends" })
        .locator("tbody tr")
        .first(),
    ).toHaveText("2026-01€50.00€5.00");
    await page.waitForTimeout(1500);
    await page.screenshot({
      path: `${output}/${width}-historical.png`,
      fullPage: true,
    });
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth - window.innerWidth,
      ),
      "overflow",
    ).toBeLessThanOrEqual(1);
    const downloadPromise = page.waitForEvent("download");
    await page.getByRole("button", { name: "Export CSV", exact: true }).click();
    await page
      .getByRole("combobox", { name: "Category view", exact: true })
      .selectOption("currentHierarchy");
    const download = await downloadPromise;
    const csvPath = `${output}/${width}-frozen.csv`;
    await download.saveAs(csvPath);
    const csv = await readFile(csvPath, "utf8");
    expect(csv).toContain("latestAtCutoff");
    expect(csv).toContain("asClassified");
    expect(csv).not.toContain("currentHierarchy");
    expect(csv).toContain("Organic groceries and pantry supplies (archived)");
    expect(csv).toContain("Currency total");
    expect(csv).toContain("2026-05-01T00:00:00.000Z");
    expect(csv).toContain(id);
    expect(csv).toContain("unresolvedLegacy");
    expect(csv).toContain(",345,");
    await expect(
      page
        .getByRole("region", { name: "USD category trends" })
        .locator("thead"),
    ).toContainText("Current groceries");
    delayed = true;
    await page
      .getByRole("combobox", { name: "Category rollup", exact: true })
      .selectOption("assignedCategory");
    await expect(
      page.getByRole("button", { name: "Export CSV", exact: true }),
    ).toBeDisabled();
    await expect(
      page.getByRole("region", { name: "USD category trends" }),
    ).toHaveCount(0);
    await expect(
      page.getByRole("button", { name: "Export CSV", exact: true }),
    ).toBeEnabled();
    delayed = false;
    await page.getByRole("button", { name: "Quarter", exact: true }).click();
    await expect(page).toHaveURL(/granularity=quarterly/);
    await page.getByRole("combobox", { name: "Top N categories" }).click();
    await page.getByRole("option", { name: "Top 7", exact: true }).click();
    await expect(page).toHaveURL(/topN=7/);
    await page
      .getByRole("button", { name: "Clear filter", exact: true })
      .click();
    await expect(page).not.toHaveURL(/categoryId=/);
    await page.waitForTimeout(1500);
    await page.screenshot({
      path: `${output}/${width}-current.png`,
      fullPage: true,
    });
    await page.reload();
    await expect(
      page.getByRole("combobox", { name: "Category view", exact: true }),
    ).toHaveValue("currentHierarchy");
    await page
      .getByRole("button", { name: "All reports", exact: true })
      .click();
    await page
      .getByRole("link", {
        name: "Category Trends What you buy, by item type. Smaller categories are grouped into Other.",
      })
      .click();
    await expect(
      page.getByRole("combobox", { name: "Category view", exact: true }),
    ).toHaveValue("currentHierarchy");
    await page.goBack();
    await expect(page).not.toHaveURL(/report=category-trends/);
    await page.goForward();
    await expect(
      page.getByRole("combobox", { name: "Category view", exact: true }),
    ).toHaveValue("currentHierarchy");
    await page.goto(
      "/?categoryView=currentHierarchy&startDate=2026-01-01&endDate=2026-02-28",
    );
    const reportsHref = await page
      .getByRole("main")
      .getByRole("link", { name: "Reports", exact: true })
      .getAttribute("href");
    expect(reportsHref).toContain("startDate=2026-01-01");
    await page.goto(reportsHref!);
    await expect(page).toHaveURL(/categoryView=currentHierarchy/);
    await page
      .getByRole("link", {
        name: "Category Trends What you buy, by item type. Smaller categories are grouped into Other.",
      })
      .click();
    await expect(
      page.getByRole("combobox", { name: "Category view", exact: true }),
    ).toHaveValue("currentHierarchy");
    expect(
      new URL(page.url()).searchParams.get("startDate"),
      "dashboard date carryover",
    ).toBe("2026-01-01");
    expect(
      new URL(page.url()).searchParams.get("endDate"),
      "dashboard end-date carryover",
    ).toBe("2026-02-28");
    expect(errors).toEqual([]);
  });
