import { test, expect } from "@playwright/test";
import { installApiMocks, signInAs } from "../fixtures/api-mocks";
import type { components } from "../../src/generated/api";

const artifacts = process.env.RECEIPTS_DASHBOARD_REVIEW_DIR ?? "test-results/dashboard-category-views";
type Item = components["schemas"]["SpendingCategoryItem"];
const historical: Item[] = [
  ...[1, 2].map((revision) => ({
    currency: "USD", state: "classified", pathRevisionId: `00000000-0000-0000-0000-00000000000${revision}`,
    categoryId: "10000000-0000-0000-0000-000000000001", purchaseCount: 2,
    path: [{ categoryId: "20000000-0000-0000-0000-000000000001", name: "Household essentials", archived: false },
      { categoryId: "10000000-0000-0000-0000-000000000001", name: "Food", archived: false }],
    categoryName: "Food", amount: revision === 1 ? 100 : 20, percentage: revision === 1 ? 25 : 5,
  })),
  { currency: "EUR", state: "classified", pathRevisionId: "00000000-0000-0000-0000-000000000001", categoryId: "30000000-0000-0000-0000-000000000001", purchaseCount: 1,
    path: [{ categoryId: "30000000-0000-0000-0000-000000000001", name: "Archived groceries", archived: true }], categoryName: "Archived groceries", amount: 50, percentage: 100 },
  { currency: "USD", state: "unresolvedLegacy", pathRevisionId: null, categoryId: null, purchaseCount: 1, path: [], categoryName: "unresolvedLegacy", amount: 10, percentage: 2.5 },
];

for (const width of [1280, 375]) {
  test(`dashboard category identity and URL history at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: width === 375 ? 812 : 900 });
    const requests: string[] = [];
    const consoleErrors: string[] = [];
    page.on("pageerror", (error) => consoleErrors.push(error.message));
    await installApiMocks(page, { freezeClock: false, overrides: {
      "**/api/dashboard/spending-by-category**": async (route) => {
        const query = new URL(route.request().url()).searchParams;
        requests.push(query.toString());
        const current = query.get("categoryView") === "currentHierarchy";
        await route.fulfill({ json: {
          view: current ? "currentHierarchy" : "asClassified", rollup: "assignedCategory", assignmentSelection: "currentPointer",
          currentRevisionId: current ? "00000000-0000-0000-0000-000000000003" : null,
          knowledgeCutoff: null,
          items: current ? [{ ...historical[0], amount: 120, percentage: 30, path: [{ categoryId: historical[0].categoryId!, name: "Current groceries", archived: false }] }] : historical,
        } });
      },
    } });
    await signInAs(page);
    await page.goto("/?startDate=2026-01-01&endDate=2026-05-24&page=3&offset=50&capturedCurrentRevisionId=old&keep=yes");
    await expect(page.getByRole("combobox", { name: "Category view" })).toHaveValue("asClassified");
    const usd = page.getByRole("region", { name: "USD category spending" });
    await expect(usd.getByText("$100.00 · 25.0%", { exact: true })).toBeVisible();
    await expect(usd.locator(":scope > ul > li")).toHaveCount(3);
    await expect(page.getByText("Partial category history · unresolved amounts included")).toBeVisible();
    await expect(page.getByRole("region", { name: "EUR category spending" }).getByText("Archived groceries (archived)", { exact: true }).last()).toBeVisible();
    await page.waitForTimeout(1600); // Recharts entrance animation must finish for evidence.
    await page.screenshot({ path: `${artifacts}/${width}-as-recorded.png`, fullPage: true });
    await page.getByRole("combobox", { name: "Category view" }).selectOption("currentHierarchy");
    await expect(page.getByText("Current groceries", { exact: true }).last()).toBeVisible();
    await expect(page).toHaveURL(/categoryView=currentHierarchy/);
    const url = new URL(page.url());
    expect(url.searchParams.get("keep")).toBe("yes");
    expect(url.searchParams.has("page")).toBe(false);
    expect(url.searchParams.has("offset")).toBe(false);
    expect(url.searchParams.has("capturedCurrentRevisionId")).toBe(false);
    await expect(page.getByRole("main").getByRole("link", { name: "Reports", exact: true })).toHaveAttribute("href", "/reports?categoryView=currentHierarchy&startDate=2026-01-01&endDate=2026-05-24");
    await page.waitForTimeout(1600);
    await page.screenshot({ path: `${artifacts}/${width}-current.png`, fullPage: true });
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
    await page.getByRole("main").getByRole("link", { name: "Reports", exact: true }).click();
    await expect(page).toHaveURL(/\/reports\?/);
    await expect(page.getByRole("heading", { name: "Reports", exact: true })).toBeVisible();
    await page.goBack();
    await expect(page).toHaveURL(url.href);
    await expect(page.getByRole("heading", { name: "Dashboard", exact: true })).toBeVisible();
    await expect(page.getByRole("combobox", { name: "Category view" })).toHaveValue("currentHierarchy");
    await expect(page.getByText("Current groceries", { exact: true }).last()).toBeVisible();
    await page.reload();
    await expect(page.getByText("Current groceries", { exact: true }).last()).toBeVisible();
    expect(requests.some((q) => q.includes("categoryView=asClassified") && q.includes("limit=100"))).toBe(true);
    expect(requests.some((q) => q.includes("categoryView=currentHierarchy") && q.includes("startDate=2026-01-01"))).toBe(true);
    expect(consoleErrors).toEqual([]);
    expect(overflow, "dashboard document overflow").toBeLessThanOrEqual(1);
    await page.goto("/?startDate=all&categoryView=currentHierarchy");
    await expect(page.getByText("All time", { exact: true })).toBeVisible();
    await expect(page.getByText(/undefined/)).toHaveCount(0);
  });
}
