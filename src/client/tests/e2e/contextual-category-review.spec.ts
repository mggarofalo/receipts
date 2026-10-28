import { expect, test } from "@playwright/test";
import type { components } from "../../src/generated/api";
import { taxonomyFixture } from "../../src/test/taxonomy-fixture";
import {
  ADMIN_FIXTURE_USER,
  FIXTURE_USER,
  installApiMocks,
  signInAs,
} from "../fixtures/api-mocks";

const output = "C:/Users/mggar/Source/receipts-017-artifacts/1049";

for (const width of [1280, 375]) {
  test(`receipt category creation at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: width === 375 ? 812 : 900 });
    const posted: components["schemas"]["TaxonomyChangeRequest"][] = [];
    let current = taxonomyFixture;
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
        "**/api/receipts/locations*": (route) =>
          route.fulfill({ json: { locations: ["Market"] } }),
        "**/api/taxonomy": (route) => route.fulfill({ json: current }),
        "**/api/taxonomy/changes": (route) => {
          const body = route
            .request()
            .postDataJSON() as components["schemas"]["TaxonomyChangeRequest"];
          posted.push(body);
          current = {
            ...current,
            id: `95000000-0000-4000-8000-00000000000${posted.length + 2}`,
            nodes: [
              ...current.nodes,
              {
                id: body.changes[0].categoryId,
                name: body.changes[0].name,
                parentId: body.changes[0].parentId,
                archived: false,
              },
            ],
          };
          return route.fulfill({ json: current });
        },
      },
    });
    await signInAs(page, ADMIN_FIXTURE_USER);
    await page.goto("/receipts/new", { waitUntil: "domcontentloaded" });
    await page.getByPlaceholder("Item description").fill("Keep this draft");
    await expect(page.getByPlaceholder("Item description")).toHaveValue(
      "Keep this draft",
    );
    await page.getByRole("combobox", { name: /^Category/ }).click();
    await expect(page.getByPlaceholder("Item description")).toHaveValue(
      "Keep this draft",
    );
    await page.getByRole("button", { name: "Create category" }).click();
    await expect(page.getByPlaceholder("Item description")).toHaveValue(
      "Keep this draft",
    );
    const name = page.getByLabel("New category");
    await expect(name).toBeFocused();
    await name.fill("Pantry");
    await expect(page.getByLabel("Parent")).toHaveValue("");
    await page.screenshot({
      path: `${output}/create-${width}-root.png`,
      animations: "disabled",
    });
    await page.getByRole("button", { name: "Create and select" }).click();
    await expect(
      page.getByRole("combobox", { name: /^Category/ }),
    ).toContainText("Pantry");
    await expect(page.getByPlaceholder("Item description")).toHaveValue(
      "Keep this draft",
    );
    expect(posted[0].changes[0].parentId).toBeNull();
    await page.getByRole("combobox", { name: /^Category/ }).click();
    await page.getByRole("button", { name: "Create category" }).click();
    await expect(page.getByLabel("Parent")).toHaveValue(
      posted[0].changes[0].categoryId,
    );
    await page.getByLabel("New category").fill("Canned goods");
    await page.screenshot({
      path: `${output}/create-${width}-child.png`,
      animations: "disabled",
    });
    await page.getByRole("button", { name: "Create and select" }).click();
    await expect(
      page.getByRole("combobox", { name: /^Category/ }),
    ).toHaveAttribute("title", "Pantry / Canned goods");
    await expect(
      page.getByRole("combobox", { name: /^Category/ }),
    ).toContainText("Canned goods");
    await expect(page.getByPlaceholder("Item description")).toHaveValue(
      "Keep this draft",
    );
    expect(posted[1].changes[0].parentId).toBe(posted[0].changes[0].categoryId);
    expect(posted[1].expectedRevisionId).toBe(
      "95000000-0000-4000-8000-000000000003",
    );
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= innerWidth,
      ),
    ).toBe(true);
  });

  test(`reader cannot create receipt category at ${width}px`, async ({
    page,
  }) => {
    await page.setViewportSize({ width, height: width === 375 ? 812 : 900 });
    await installApiMocks(page, {
      freezeClock: false,
      user: FIXTURE_USER,
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
        "**/api/receipts/locations*": (route) =>
          route.fulfill({ json: { locations: ["Market"] } }),
      },
    });
    await signInAs(page, FIXTURE_USER);
    await page.goto("/receipts/new", { waitUntil: "domcontentloaded" });
    await page.getByRole("combobox", { name: /^Category/ }).click();
    await expect(
      page.getByRole("button", { name: "Create category" }),
    ).toHaveCount(0);
    await page.getByRole("option", { name: "Food / Dairy" }).click();
    await expect(
      page.getByRole("combobox", { name: /^Category/ }),
    ).toHaveAttribute("title", "Food / Dairy");
    await expect(
      page.getByRole("combobox", { name: /^Category/ }),
    ).toContainText("Dairy");
  });
}
