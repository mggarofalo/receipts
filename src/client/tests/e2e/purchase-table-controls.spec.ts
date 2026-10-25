import { expect, test, type Page } from "@playwright/test";
import type { components } from "../../src/generated/api";
import { installApiMocks, signInAsFixtureUser } from "../fixtures/api-mocks";

// Browser-level control/layout evidence with synthetic HTTP data. The real page,
// forms, router, query client, Radix controls and CSS run unchanged in Chromium.
const PURCHASES: components["schemas"]["PurchaseSearchItem"][] = Array.from(
  { length: 36 },
  (_, index) => {
    const number = index + 1;
    return {
      id: `10000000-0000-0000-0000-${String(number).padStart(12, "0")}`,
      receiptId: `20000000-0000-0000-0000-${String(number).padStart(12, "0")}`,
      date: new Date(Date.UTC(2026, 4, number)).toISOString().slice(0, 10),
      description: `Coffee sample ${String(number).padStart(2, "0")}`,
      location: number % 2 === 0 ? "Harbor Market" : "Hill Market",
      unitPrice: number / 4,
      unitPriceCurrency: "USD",
      quantity: 1,
      totalAmount: number / 4,
      totalAmountCurrency: "USD",
      receiptItemCode: `COFFEE-${number}`,
      normalizedDescription: "Coffee",
    };
  },
);

async function expectNoPageOverflow(page: Page) {
  const geometry = await page.evaluate(() => ({
    viewport: document.documentElement.clientWidth,
    content: document.documentElement.scrollWidth,
  }));
  expect(
    geometry.content,
    `page content ${geometry.content}px exceeds viewport ${geometry.viewport}px`,
  ).toBeLessThanOrEqual(geometry.viewport + 1);
}

for (const width of [1280, 375]) {
  for (const palette of ["Graphite", "Paper"]) {
    test(`purchase controls work at ${width}px in ${palette}`, async ({
      page,
    }, testInfo) => {
      test.setTimeout(60_000);
      await page.setViewportSize({ width, height: 900 });
      const requests: Record<string, string>[] = [];
      await installApiMocks(page, {
        freezeClock: false,
        overrides: {
          "**/api/search/purchases?*": async (route) => {
            const params = new URL(route.request().url()).searchParams;
            requests.push(Object.fromEntries(params));
            const query = (params.get("q") ?? "").toLowerCase();
            const store = (params.get("location") ?? "").toLowerCase();
            const minimum = Number(params.get("minUnitPrice") ?? 0);
            const maximum = Number(params.get("maxUnitPrice") ?? Infinity);
            const start = params.get("startDate") ?? "";
            const end = params.get("endDate") ?? "9999-12-31";
            const rows = PURCHASES.filter(
              (purchase) =>
                purchase.description.toLowerCase().includes(query) &&
                purchase.location.toLowerCase().includes(store) &&
                purchase.unitPrice >= minimum &&
                purchase.unitPrice <= maximum &&
                purchase.date >= start &&
                purchase.date <= end,
            );
            const direction = params.get("sortDirection") === "asc" ? 1 : -1;
            rows.sort(
              (left, right) =>
                direction *
                (params.get("sortBy") === "unitPrice"
                  ? left.unitPrice - right.unitPrice
                  : left.date.localeCompare(right.date)),
            );
            const offset = Number(params.get("offset") ?? 0);
            const limit = Number(params.get("limit") ?? 25);
            await route.fulfill({
              status: 200,
              contentType: "application/json",
              body: JSON.stringify({
                items: rows.slice(offset, offset + limit),
                total: rows.length,
                offset,
                limit,
              }),
            });
          },
        },
      });
      await signInAsFixtureUser(page);
      await page.goto("/search?q=coffee");
      await expect(
        page.getByRole("heading", { name: "Purchase history" }),
      ).toBeVisible();
      const rows = page
        .getByRole("table", { name: "Purchase results" })
        .locator("tbody tr");
      await expect(rows).toHaveCount(25);
      await expect(rows.first()).toContainText("Coffee sample 36");

      await page.getByRole("button", { name: "Appearance settings" }).click();
      await page
        .getByRole("menuitemradio", { name: palette, exact: true })
        .click();
      await expect(page.locator("html")).toHaveAttribute(
        "data-palette",
        palette.toLowerCase(),
      );
      await expectNoPageOverflow(page);

      await page
        .getByRole("button", { name: "Next page", exact: true })
        .click();
      await expect(rows).toHaveCount(11);
      await expect(rows.first()).toContainText("Coffee sample 11");

      // Sorting must reset the server offset, rather than reordering that page.
      await page
        .getByRole("button", { name: "Unit price", exact: true })
        .click();
      await expect(rows).toHaveCount(25);
      await expect(rows.first()).toContainText("Coffee sample 01");
      await expect(
        page.getByRole("columnheader", { name: "Unit price" }),
      ).toHaveAttribute("aria-sort", "ascending");
      await expect(
        page.getByRole("button", { name: "Previous page", exact: true }),
      ).toBeDisabled();
      await page
        .getByRole("button", { name: "Unit price", exact: true })
        .click();
      await expect(rows.first()).toContainText("Coffee sample 36");
      await expect(
        page.getByRole("columnheader", { name: "Unit price" }),
      ).toHaveAttribute("aria-sort", "descending");

      await page.getByRole("combobox", { name: "Rows per page" }).click();
      await page.getByRole("option", { name: "10", exact: true }).click();
      await expect(rows).toHaveCount(10);
      await expect(
        page.getByText("Showing 1-10 of 36", { exact: true }),
      ).toBeVisible();
      await page
        .getByRole("button", { name: "Next page", exact: true })
        .click();
      await expect(rows.first()).toContainText("Coffee sample 26");
      await expectNoPageOverflow(page);

      await page.getByLabel("Store contains").fill("Harbor");
      await page.getByLabel("From date", { exact: true }).fill("05/01/2026");
      await page.getByLabel("To date", { exact: true }).fill("05/31/2026");
      await page.getByLabel("Minimum unit price").fill("2");
      await page.getByLabel("Maximum unit price").fill("7");
      // Draft edits cannot refetch before Apply.
      await expect(rows.first()).toContainText("Coffee sample 26");
      expect(requests.some((request) => request.location === "Harbor")).toBe(
        false,
      );
      await page
        .getByRole("button", { name: "Apply filters", exact: true })
        .click();
      await expect(
        page.getByText("Showing 1-10 of 11", { exact: true }),
      ).toBeVisible();
      await expect(rows).toHaveCount(10);
      await expect(rows.first()).toContainText("Coffee sample 28");
      await expect(rows.first()).toContainText("Harbor Market");
      expect(requests).toContainEqual(
        expect.objectContaining({
          q: "coffee",
          location: "Harbor",
          startDate: "2026-05-01",
          endDate: "2026-05-31",
          minUnitPrice: "2",
          maxUnitPrice: "7",
          sortBy: "unitPrice",
          sortDirection: "desc",
          offset: "0",
          limit: "10",
        }),
      );
      await expectNoPageOverflow(page);
      await testInfo.attach("purchase-controls", {
        body: await page.screenshot({ fullPage: true }),
        contentType: "image/png",
      });
      await page
        .getByRole("button", { name: "Next page", exact: true })
        .click();
      await expect(rows).toHaveCount(1);
      await expect(rows.first()).toContainText("Coffee sample 08");

      await page
        .getByRole("button", { name: "Clear filters", exact: true })
        .click();
      await expect(
        page.getByText("Showing 1-10 of 36", { exact: true }),
      ).toBeVisible();
      await expect(rows.first()).toContainText("Coffee sample 36");
      await expect(page.getByLabel("Store contains")).toHaveValue("");

      await page
        .getByRole("textbox", { name: "Search purchases", exact: true })
        .fill("nonexistent");
      await page.getByRole("button", { name: "Search", exact: true }).click();
      await expect(
        page.getByText(/No purchases match this phrase/),
      ).toBeVisible();
      await expect(
        page.getByText("Showing 0-0 of 0", { exact: true }),
      ).toHaveCount(0);
      await expect(
        page.getByRole("combobox", { name: "Rows per page" }),
      ).toHaveCount(0);
      await expect(
        page.getByRole("button", { name: "Previous page", exact: true }),
      ).toHaveCount(0);
      await expect(
        page.getByRole("button", { name: "Next page", exact: true }),
      ).toHaveCount(0);
      await expectNoPageOverflow(page);
      await page
        .getByRole("textbox", { name: "Search purchases", exact: true })
        .fill("coffee");
      await page.getByRole("button", { name: "Search", exact: true }).click();
      await expect(rows).toHaveCount(10);

      // A fresh URL without limit proves persistence, not just URL retention.
      await page.goto("/search?q=coffee");
      await expect(rows).toHaveCount(10);
      await expect(
        page.getByRole("combobox", { name: "Rows per page" }),
      ).toContainText("10");
      await expectNoPageOverflow(page);
    });
  }
}
