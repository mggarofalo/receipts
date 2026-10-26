import { expect, test } from "@playwright/test";
import {
  ADMIN_FIXTURE_USER,
  installApiMocks,
  signInAs,
} from "../fixtures/api-mocks";
import {
  dairyClassification,
  taxonomyFixture,
} from "../../src/test/taxonomy-fixture";

for (const width of [1280, 375]) {
  test(`saved item explicit mapping approval and keyboard access at ${width}px`, async ({
    page,
  }) => {
    await page.setViewportSize({ width, height: 900 });
    const source = { location: "Market", description: "MILK", itemCode: "123" };
    const writes: unknown[] = [];
    let mapping: unknown = null;
    const unexpected: string[] = [];
    page.on("request", (request) => {
      if (
        request.method() !== "GET" &&
        request.url().includes("/api/") &&
        !request.url().includes("/category-mappings/") &&
        !request.url().includes("/auth/")
      )
        unexpected.push(request.url());
    });
    await installApiMocks(page, {
      freezeClock: false,
      user: ADMIN_FIXTURE_USER,
      overrides: {
        "**/api/metadata/enums": (route) =>
          route.fulfill({
            json: {
              adjustmentTypes: [],
              authEventTypes: [],
              auditActions: [],
              entityTypes: [],
            },
          }),
        "**/api/trips?*": (route) =>
          route.fulfill({
            json: {
              receipt: {
                receipt: {
                  id: "saved",
                  location: source.location,
                  date: "2026-10-26",
                  taxAmount: 0,
                },
                subtotal: 5,
                adjustmentTotal: 0,
                expectedTotal: 5,
                adjustments: [],
                items: [
                  {
                    id: "line",
                    description: source.description,
                    receiptItemCode: source.itemCode,
                    quantity: 1,
                    unitPrice: 5,
                    category: "Food",
                    currentClassification: dairyClassification,
                  },
                ],
              },
              transactions: [],
            },
          }),
        "**/api/taxonomy/revisions/*": (route) =>
          route.fulfill({ json: taxonomyFixture }),
        "**/api/category-mappings/inspect": (route) =>
          route.fulfill({
            json: {
              source,
              sourceFingerprint: "fingerprint",
              taxonomyRevisionId: taxonomyFixture.id,
              disposition: mapping ? "approved" : "missing",
              mapping,
            },
          }),
        "**/api/category-mappings/changes": (route) => {
          const body = route.request().postDataJSON();
          writes.push(body);
          if (writes.length === 1) return route.abort("failed");
          mapping = {
            id: "mapping",
            ...body,
            taxonomyRevisionId: taxonomyFixture.id,
            actor: "Admin",
            recordedAt: "2026-10-26T12:00:00Z",
          };
          return route.fulfill({ json: mapping });
        },
      },
    });
    await signInAs(page, ADMIN_FIXTURE_USER);
    await page.goto("/receipts/saved");
    const control = page.getByRole("button", { name: "Remember category…" });
    await control.focus();
    await page.keyboard.press("Enter");
    await expect(page.getByText("Remembered category: None")).toBeVisible();
    expect(writes).toHaveLength(0);
    const picker = page.getByRole("combobox", {
      name: "Category for future matches",
    });
    await picker.focus();
    await page.keyboard.press("Enter");
    await page.getByPlaceholder("Search category paths…").fill("Dairy");
    await page.keyboard.press("ArrowDown");
    await page.keyboard.press("Enter");
    const dialog = page.getByRole("dialog", {
      name: "Remember category",
      exact: true,
    });
    const bounds = await dialog.boundingBox();
    expect(bounds).not.toBeNull();
    expect(bounds!.x).toBeGreaterThanOrEqual(0);
    expect(bounds!.y).toBeGreaterThanOrEqual(0);
    expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(width);
    expect(bounds!.y + bounds!.height).toBeLessThanOrEqual(900);
    expect(
      await dialog.evaluate(
        (element) => element.scrollWidth <= element.clientWidth,
      ),
    ).toBe(true);
    const approve = page.getByRole("button", {
      name: "Remember for future matches",
    });
    await approve.focus();
    await page.keyboard.press("Enter");
    await expect(
      page.getByRole("button", { name: "Retry same change" }),
    ).toBeVisible();
    await page.getByRole("button", { name: "Close", exact: true }).click();
    await control.click();
    await expect(
      page.getByRole("textbox", { name: /Reason for remembered/ }),
    ).toBeDisabled();
    await page.getByRole("button", { name: "Retry same change" }).click();
    await expect(page.getByText(/Remembered category updated/)).toBeVisible();
    expect(writes).toHaveLength(2);
    expect(writes[1]).toEqual(writes[0]);
    expect(writes[0]).toMatchObject({
      source,
      category: { id: dairyClassification.categoryId },
      expectedMappingRevisionId: null,
    });
    await expect(
      page.getByRole("button", { name: "Revoke remembered category" }),
    ).toBeVisible();
    await page.screenshot({
      path: `C:/Users/mggar/Source/receipts-017-artifacts/1049/mapping-ui-${width}.png`,
      fullPage: true,
    });
    expect(unexpected).toEqual([]);
  });
}
