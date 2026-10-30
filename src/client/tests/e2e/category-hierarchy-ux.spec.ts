import { expect, test } from "@playwright/test";
import type { components } from "../../src/generated/api";
import { taxonomyFixture } from "../../src/test/taxonomy-fixture";
import {
  ADMIN_FIXTURE_USER,
  installApiMocks,
  signInAs,
} from "../fixtures/api-mocks";

const output = "C:/Users/mggar/Source/receipts-017-artifacts/1075";
// Cold Vite transforms can exceed the default navigation budget on Windows.
// Interaction and assertion timeouts retain the shared defaults.
test.use({ navigationTimeout: 60_000, hasTouch: true });
for (const width of [1280, 375]) {
  test(`nested category creation and long path selection at ${width}px`, async ({
    page,
  }) => {
    await page.setViewportSize({ width, height: width === 375 ? 812 : 900 });
    const posted: components["schemas"]["TaxonomyChangeRequest"][] = [];
    const deep = Array.from({ length: 9 }, (_, i) => ({
      id: `deep-${i}`,
      name:
        i === 8
          ? "Z".repeat(180)
          : `Long category level ${i} with descriptive words`,
      parentId: i ? `deep-${i - 1}` : null,
      archived: false,
    }));
    let current = {
      ...taxonomyFixture,
      nodes: [
        ...taxonomyFixture.nodes,
        ...deep,
        { id: "plant", name: "Plant", archived: false },
        {
          id: "plant-dairy",
          name: "Dairy",
          parentId: "plant",
          archived: false,
        },
      ],
    };
    await installApiMocks(page, {
      user: ADMIN_FIXTURE_USER,
      freezeClock: false,
      overrides: {
        "**/api/taxonomy": (route) => route.fulfill({ json: current }),
        "**/api/taxonomy/changes": (route) => {
          const body = route
            .request()
            .postDataJSON() as components["schemas"]["TaxonomyChangeRequest"];
          posted.push(body);
          current = {
            ...current,
            id: "95000000-0000-4000-8000-000000000099",
            nodes: [
              ...current.nodes,
              {
                id: body.changes[0].categoryId,
                name: body.changes[0].name!,
                parentId: body.changes[0].parentId,
                archived: false,
              },
            ],
          };
          return route.fulfill({ json: current });
        },
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
    await signInAs(page, ADMIN_FIXTURE_USER);
    await page.goto("/categories");
    await page
      .getByRole("button", { name: "Expand Food", exact: true })
      .click();
    await expect(
      page.getByRole("treeitem", { name: "Food / Dairy", exact: true }),
    ).toBeVisible();
    const actions = page.getByRole("button", {
      name: "Category actions: Food / Dairy",
      exact: true,
    });
    if (width === 375) await actions.tap();
    else {
      await actions.focus();
      await actions.press("Enter");
    }
    const rename = page.getByRole("menuitem", {
      name: "Rename category: Food / Dairy",
    });
    if (width === 375) await expect(page.getByRole("menu")).toBeFocused();
    else await expect(rename).toBeFocused();
    await page.getByRole("menu").evaluate(async (element) => {
      await Promise.all(
        element.getAnimations().map((animation) => animation.finished),
      );
    });
    await page.screenshot({
      path: `${output}/actions-menu-${width}.png`,
      fullPage: true,
      animations: "disabled",
    });
    if (width === 375) {
      await rename.tap();
      await expect(page.getByRole("dialog")).toBeVisible();
      await page
        .getByRole("dialog")
        .getByRole("button", { name: "Cancel", exact: true })
        .tap();
    } else await page.getByRole("menu").press("Escape");
    await expect(actions).toBeFocused();
    await page.screenshot({
      path: `${output}/tree-${width}.png`,
      fullPage: true,
    });
    await page
      .getByRole("button", {
        name: "Add child category: Food / Dairy",
        exact: true,
      })
      .click();
    await expect(page.getByLabel("Parent", { exact: true })).toHaveValue(
      taxonomyFixture.nodes[1].id,
    );
    await page.getByLabel("Name", { exact: true }).fill("Cream");
    await page
      .getByLabel("Reason", { exact: true })
      .fill("A more precise category");
    await page.screenshot({
      path: `${output}/add-subcategory-${width}.png`,
      fullPage: true,
    });
    await page
      .getByRole("dialog")
      .getByRole("button", { name: "New category", exact: true })
      .click();
    await expect(page.getByRole("dialog")).toHaveCount(0);
    expect(posted).toHaveLength(1);
    expect(posted[0].changes[0]).toMatchObject({
      kind: "create",
      parentId: taxonomyFixture.nodes[1].id,
      name: "Cream",
    });
    expect(taxonomyFixture.nodes.map((n) => n.id)).not.toContain(
      posted[0].changes[0].categoryId,
    );
    await expect(
      page.getByRole("treeitem", { name: "Food / Dairy / Cream", exact: true }),
    ).toBeVisible();
    await page
      .getByRole("textbox", { name: "Search category paths" })
      .fill("Z".repeat(20));
    await expect(
      page.getByRole("treeitem", {
        name: deep.map((n) => n.name).join(" / "),
        exact: true,
      }),
    ).toHaveAttribute("aria-level", "9");
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= innerWidth,
      ),
    ).toBe(true);
    await page.screenshot({
      path: `${output}/deep-tree-${width}.png`,
      fullPage: true,
    });
    await page.goto("/receipts/new");
    await page.getByPlaceholder("Item description").fill("Preserve my draft");
    const picker = page.getByRole("combobox", { name: /^Category/ });
    await picker.click();
    // Browsing starts at the roots; duplicate leaf names are told apart by
    // their full path once the whole tree is searched.
    await expect(
      page.getByRole("option", { name: "Plant, 1 subcategory", exact: true }),
    ).toBeVisible();
    await page.getByPlaceholder("Search category paths…").fill("Dairy");
    await expect(
      page.getByRole("option", { name: "Food / Dairy", exact: true }),
    ).toBeVisible();
    await expect(
      page.getByRole("option", { name: "Plant / Dairy", exact: true }),
    ).toBeVisible();
    await page.getByPlaceholder("Search category paths…").fill("Z".repeat(20));
    const path = deep.map((n) => n.name).join(" / ");
    await expect(
      page.getByRole("option", { name: path, exact: true }),
    ).toBeVisible();
    // A popover on desktop, a bottom sheet on a phone.
    const popup = page.locator(
      width === 375
        ? '[data-slot="category-picker-sheet"]'
        : '[data-slot="popover-content"]',
    );
    await expect(popup).toBeVisible();
    await popup.evaluate(async (element) => {
      await Promise.all(
        element.getAnimations().map((animation) => animation.finished),
      );
    });
    const popupStyle = await popup.evaluate((element) => {
      const style = getComputedStyle(element);
      const canvas = document.createElement("canvas");
      canvas.width = canvas.height = 1;
      const context = canvas.getContext("2d")!;
      context.fillStyle = style.backgroundColor;
      context.fillRect(0, 0, 1, 1);
      const bounds = element.getBoundingClientRect();
      return {
        opacity: style.opacity,
        background: style.backgroundColor,
        alpha: context.getImageData(0, 0, 1, 1).data[3],
        left: bounds.left,
        right: bounds.right,
        top: bounds.top,
        bottom: bounds.bottom,
        height: innerHeight,
      };
    });
    expect(popupStyle.opacity).toBe("1");
    expect(popupStyle.alpha).toBe(255);
    expect(popupStyle.left).toBeGreaterThanOrEqual(0);
    expect(popupStyle.right).toBeLessThanOrEqual(width);
    expect(popupStyle.top).toBeGreaterThanOrEqual(0);
    expect(popupStyle.bottom).toBeLessThanOrEqual(popupStyle.height);
    await test.info().attach(`popup-style-${width}`, {
      body: JSON.stringify(popupStyle),
      contentType: "application/json",
    });
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= innerWidth,
      ),
    ).toBe(true);
    await page.screenshot({
      path: `${output}/deep-picker-${width}.png`,
      fullPage: true,
      animations: "disabled",
    });
    // The first result is active as soon as the search narrows.
    await page.getByPlaceholder("Search category paths…").press("Enter");
    await expect(picker).toHaveAttribute("title", path);
    await expect(picker).toContainText(deep[8].name);
    await expect(page.getByRole("option")).toHaveCount(0);
    await expect(page.getByPlaceholder("Item description")).toHaveValue(
      "Preserve my draft",
    );
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= innerWidth,
      ),
    ).toBe(true);
    await page.screenshot({
      path: `${output}/selected-deep-path-${width}.png`,
      fullPage: true,
    });
  });
}
